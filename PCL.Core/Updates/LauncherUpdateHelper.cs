using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Updates;

/// <summary>Runs in a copied single-file launcher without starting the UI or loading user data.</summary>
internal static class LauncherUpdateHelper
{
    internal static readonly string[] DisposableFiles = ["updater.exe", "helper.done", "new.exe", "request.json", "helper.ready", "helper.ready.tmp",
        "commit.ready", "commit.ready.tmp", "app.ready", "app.ready.tmp", "result.json", "result.json.tmp", "update.log", "update.log.tmp"];

    private sealed class Failure
    {
        public bool Success { get; set; }
        public string Error { get; set; } = "";
        public string Phase { get; set; } = "preflight";
        public int? NativeErrorCode { get; set; }
        public bool RolledBack { get; set; }
        public bool RestartedOld { get; set; }
        public string? RecoveryError { get; set; }
    }

    internal static int? NativeError(Exception error) => error is Win32Exception win32 ? win32.NativeErrorCode
        : error.InnerException is { } inner ? NativeError(inner) : null;

    internal static Process StartWithRetry(Func<Process?> start, TimeSpan timeout, Action<string>? log = null)
    {
        var clock = Stopwatch.StartNew();
        for (var attempt = 1; ; attempt++)
        {
            try { return start() ?? throw new IOException("Could not create the launcher process."); }
            catch (Exception ex)
            {
                var code = NativeError(ex);
                log?.Invoke($"attempt={attempt} nativeError={code} {ex.Message}");
                if (code is not (5 or 32 or 33 or 1224) || clock.Elapsed >= timeout) throw;
                Thread.Sleep(250);
            }
        }
    }

    private static void RetryFile(Action action, int seconds)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            try { action(); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && clock.Elapsed < TimeSpan.FromSeconds(seconds))
            { Thread.Sleep(200); }
        }
    }

    private static void WriteText(string path, string text)
    {
        File.WriteAllText(path + ".tmp", text);
        File.Move(path + ".tmp", path, true);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    internal static int Run(string? manifest)
    {
        string? directory = null;
        string? target = null;
        LauncherUpdateRequest? request = null;
        Process? child = null;
        var parentExited = false;
        var replaced = false;
        var validated = false;
        var phase = "preflight";
        void Log(string text)
        {
            try { if (directory is not null) File.AppendAllText(Path.Combine(directory, "update.log"), $"{DateTime.UtcNow:o} phase={phase} {text}{Environment.NewLine}"); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        Process StartLauncher(bool failed)
        {
            var info = new ProcessStartInfo(target!) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(target!)! };
            foreach (var key in new[] { AtomicLauncherUpdate.ManifestEnvironment, AtomicLauncherUpdate.RequestEnvironment,
                         AtomicLauncherUpdate.TokenEnvironment, AtomicLauncherUpdate.FailureEnvironment }) info.Environment.Remove(key);
            if (failed) info.Environment[AtomicLauncherUpdate.FailureEnvironment] = Path.Combine(directory!, "result.json");
            else
            {
                info.Environment[AtomicLauncherUpdate.RequestEnvironment] = manifest!;
                info.Environment[AtomicLauncherUpdate.TokenEnvironment] = request!.Token;
            }
            return StartWithRetry(() => Process.Start(info), TimeSpan.FromSeconds(30), Log);
        }
        try
        {
            Check(manifest is not null, "Missing updater manifest.");
            manifest = Path.GetFullPath(manifest!);
            var stage = Path.GetDirectoryName(manifest)!;
            Check(Path.GetFileName(manifest) == "request.json" &&
                string.Equals(Path.Combine(stage, "updater.exe"), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase), "Invalid updater workspace.");
            request = JsonSerializer.Deserialize<LauncherUpdateRequest>(File.ReadAllText(manifest));
            Check(request is not null, "Invalid update request.");
            target = Path.GetFullPath(request!.TargetExe);
            Check(AtomicLauncherUpdate.IsStageDirectory(stage, target), "Invalid update directory.");
            Check(request.Schema == 1 && Regex.IsMatch(request.Token, "^[0-9A-F]{64}$") &&
                Regex.IsMatch(request.OldSha256, "^[0-9A-F]{64}$") && Regex.IsMatch(request.NewSha256, "^[0-9A-F]{64}$") &&
                ReleaseVersion.TryParse(request.ReleaseTag, out _) && request.StartupTimeoutSeconds is >= 5 and <= 180 &&
                request.StartupGraceSeconds is >= 0 and <= 10 && Path.GetExtension(target).Equals(".exe", StringComparison.OrdinalIgnoreCase), "Invalid update request.");
            foreach (var path in new[] { stage, target, manifest, Environment.ProcessPath! })
                Check((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0, "Reparse targets are not supported.");
            directory = stage; // Only write diagnostics after validating the workspace.
            var candidate = Path.Combine(stage, "new.exe");
            var backup = Path.Combine(stage, "old.exe");
            Check(new FileInfo(candidate).LinkTarget is null && AtomicLauncherUpdate.HashFile(candidate) == request.NewSha256 &&
                AtomicLauncherUpdate.HashFile(target) == request.OldSha256 &&
                AtomicLauncherUpdate.HashFile(Environment.ProcessPath!) == request.OldSha256, "Update hash mismatch.");
            using (var signature = File.OpenRead(candidate)) Check(signature.ReadByte() == 'M' && signature.ReadByte() == 'Z', "Invalid executable.");
            using var parent = Process.GetProcessById(request.ParentProcessId);
            Check(parent.StartTime.ToUniversalTime().Ticks == request.ParentStartTicks &&
                string.Equals(Path.GetFullPath(parent.MainModule!.FileName), target, StringComparison.OrdinalIgnoreCase), "Parent process identity changed.");
            WriteText(Path.Combine(stage, "helper.ready"), request.Token);
            var clock = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(stage, "commit.ready")))
            {
                Check(!parent.HasExited && clock.Elapsed < TimeSpan.FromSeconds(30), "Update was not committed.");
                Thread.Sleep(50);
            }
            Check(File.ReadAllText(Path.Combine(stage, "commit.ready")).Trim() == request.Token, "Invalid commit token.");
            phase = "wait-parent";
            Check(parent.WaitForExit(90000), "The old launcher did not exit.");
            parentExited = true;
            Check(AtomicLauncherUpdate.HashFile(candidate) == request.NewSha256 && AtomicLauncherUpdate.HashFile(target) == request.OldSha256, "Files changed after verification.");
            phase = "replace";
            RetryFile(() => File.Replace(candidate, target, backup, true), 30);
            replaced = true;
            phase = "start-new";
            child = StartLauncher(false);
            phase = "validate-new";
            clock.Restart();
            var ready = Path.Combine(stage, "app.ready");
            while (!File.Exists(ready))
            {
                Check(!child.HasExited, "New launcher exited before startup completed.");
                Check(clock.Elapsed < TimeSpan.FromSeconds(request.StartupTimeoutSeconds), "New launcher startup timed out.");
                Thread.Sleep(100);
            }
            Check(File.ReadAllText(ready).Trim() == request.Token, "Invalid startup acknowledgement.");
            Check(!child.WaitForExit(request.StartupGraceSeconds * 1000), "New launcher exited during startup validation.");
            validated = true;
            phase = "receipt-and-cleanup";
            WriteText(AtomicLauncherUpdate.ReceiptPath(target), JsonSerializer.Serialize(new LauncherUpdateReceipt(request.ReleaseTag, request.NewSha256)));
            RetryFile(() => File.Delete(backup), 15);
            // The running helper image cannot delete itself. The confirmed new
            // launcher removes only our known files after this process releases it.
            WriteText(Path.Combine(stage, "helper.done"), request.Token);
            return 0;
        }
        catch (Exception ex)
        {
            Log(ex.ToString());
            var failure = new Failure { Success = validated, Error = ex.Message, Phase = phase, NativeErrorCode = NativeError(ex) };
            if (directory is not null && target is not null && request is not null)
            {
                var result = Path.Combine(directory, "result.json");
                try
                {
                    if (replaced && !validated)
                    {
                        phase = "rollback";
                        if (child is not null && !child.HasExited) { child.Kill(); child.WaitForExit(10000); }
                        RetryFile(() => File.Replace(Path.Combine(directory, "old.exe"), target, Path.Combine(directory, "failed-new.exe"), true), 30);
                        failure.RolledBack = true;
                    }
                    WriteText(result, JsonSerializer.Serialize(failure));
                    if (parentExited && AtomicLauncherUpdate.HashFile(target) == request.OldSha256)
                    {
                        phase = "restart-old";
                        using var old = StartLauncher(true);
                        failure.RestartedOld = true;
                    }
                }
                catch (Exception recovery)
                {
                    failure.RecoveryError = $"phase={phase} nativeError={NativeError(recovery)} {recovery.Message}";
                    Log(recovery.ToString());
                }
                try { WriteText(result, JsonSerializer.Serialize(failure)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            return 1;
        }
        finally { child?.Dispose(); }
    }

    internal static void CleanupAfterSuccess(string directory, LauncherUpdateRequest request, string executable)
    {
        _ = Task.Run(async () =>
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(45))
            {
                await Task.Delay(100).ConfigureAwait(false);
                try
                {
                    var done = Path.Combine(directory, "helper.done");
                    if (!File.Exists(done) || File.ReadAllText(done).Trim() != request.Token) continue;
                    if (AtomicLauncherUpdate.ReadInstalledReleaseTag(executable) != request.ReleaseTag || File.Exists(Path.Combine(directory, "old.exe"))) return;
                    // Delete the helper image first; retry until it has exited.
                    foreach (var name in DisposableFiles) File.Delete(Path.Combine(directory, name));
                    if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
                    return;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        });
    }
}
