using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Updates;

public sealed record LauncherUpdateRequest(int Schema, string TargetExe, int ParentProcessId,
    long ParentStartTicks, string OldSha256, string NewSha256, string ReleaseTag, string Token,
    int StartupTimeoutSeconds = 90, int StartupGraceSeconds = 3);

public sealed record LauncherUpdateStartup(string? RequestPath, string? Token, string? FailurePath);
public sealed record LauncherUpdateReceipt(string ReleaseTag, string Sha256);

/// <summary>Stages a verified replacement and hands it to a process outside the launcher.</summary>
public sealed class AtomicLauncherUpdate : IDisposable
{
    public const string RequestEnvironment = "PCL_XH_UPDATE_READY_REQUEST";
    public const string TokenEnvironment = "PCL_XH_UPDATE_READY_TOKEN";
    public const string FailureEnvironment = "PCL_XH_UPDATE_FAILURE";
    internal const string ManifestEnvironment = "PCL_XH_UPDATE_MANIFEST";
    internal const string HelperArgument = "--pcl-apply-update";
    public static string? StartupFailureReport { get; private set; }
    private readonly Process _parent;
    private Process? _helper;
    private bool _committed;
    public string DirectoryPath { get; }
    public string CandidatePath => Path.Combine(DirectoryPath, "new.exe");
    public string RequestPath => Path.Combine(DirectoryPath, "request.json");
    public string TargetPath { get; }
    private readonly string _oldHash;

    private AtomicLauncherUpdate(string target, Process parent, string directory, string oldHash)
    { TargetPath = target; _parent = parent; DirectoryPath = directory; _oldHash = oldHash; }

    public static async Task<AtomicLauncherUpdate> CreateAsync(string target, Process? parent = null,
        CancellationToken cancellationToken = default)
    {
        target = Path.GetFullPath(target);
        if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(target) || new FileInfo(target).LinkTarget is not null)
            throw new InvalidDataException("The installed launcher must be a regular EXE file.");
        parent ??= Process.GetCurrentProcess();
        if (parent.HasExited || !string.Equals(Path.GetFullPath(parent.MainModule!.FileName), target, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The update target is not the running launcher.");
        var directory = Path.Combine(Path.GetDirectoryName(target)!, ".pcl-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); // Fails before exit if the installation is not writable.
        try { return new(target, parent, directory, await HashFileAsync(target, cancellationToken).ConfigureAwait(false)); }
        catch { Directory.Delete(directory); throw; }
    }

    public async Task CommitAsync(string releaseTag, CancellationToken cancellationToken = default,
        int startupTimeoutSeconds = 90, int startupGraceSeconds = 3)
    {
        if (_helper is not null || _committed) throw new InvalidOperationException("This update was already handed off.");
        if (!ReleaseVersion.TryParse(releaseTag, out _) || startupTimeoutSeconds is < 5 or > 180 || startupGraceSeconds is < 0 or > 10)
            throw new InvalidDataException("Invalid update request.");
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var request = new LauncherUpdateRequest(1, TargetPath, _parent.Id, _parent.StartTime.ToUniversalTime().Ticks,
            _oldHash, await HashFileAsync(CandidatePath, cancellationToken).ConfigureAwait(false), releaseTag, token,
            startupTimeoutSeconds, startupGraceSeconds);
        await File.WriteAllTextAsync(RequestPath, JsonSerializer.Serialize(request), cancellationToken).ConfigureAwait(false);
        // A copy runs in helper mode before application/config initialization.
        // Avoid a dynamically encoded PowerShell child for a normal app update.
        var helperPath = Path.Combine(DirectoryPath, "updater.exe");
        File.Copy(TargetPath, helperPath);
        var start = new ProcessStartInfo(helperPath) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(TargetPath)! };
        start.ArgumentList.Add(HelperArgument);
        start.Environment[ManifestEnvironment] = RequestPath;
        _helper = Process.Start(start) ?? throw new IOException("Could not start the updater helper.");
        var until = DateTime.UtcNow.AddSeconds(20);
        var ready = Path.Combine(DirectoryPath, "helper.ready");
        while (!File.Exists(ready))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_helper.HasExited || DateTime.UtcNow >= until)
                throw new IOException("Updater helper did not become ready. The current launcher has been kept running.");
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
        if ((await File.ReadAllTextAsync(ready, cancellationToken).ConfigureAwait(false)).Trim() != token)
            throw new InvalidDataException("Invalid updater handshake.");
        cancellationToken.ThrowIfCancellationRequested();
        var commit = Path.Combine(DirectoryPath, "commit.ready");
        await File.WriteAllTextAsync(commit + ".tmp", token, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        File.Move(commit + ".tmp", commit);
        _committed = true;
    }

    /// <summary>Called before normal startup; null means this is not an updater process.</summary>
    public static int? RunHelperIfRequested()
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Length != 2 || args[1] != HelperArgument) return null;
        return LauncherUpdateHelper.Run(Environment.GetEnvironmentVariable(ManifestEnvironment));
    }

    public static LauncherUpdateStartup ConsumeStartupContext()
    {
        var context = new LauncherUpdateStartup(Environment.GetEnvironmentVariable(RequestEnvironment),
            Environment.GetEnvironmentVariable(TokenEnvironment), Environment.GetEnvironmentVariable(FailureEnvironment));
        foreach (var name in new[] { RequestEnvironment, TokenEnvironment, FailureEnvironment }) Environment.SetEnvironmentVariable(name, null);
        if (context.FailurePath is not null && Path.GetFileName(context.FailurePath) == "result.json" &&
            IsStageDirectory(Path.GetDirectoryName(Path.GetFullPath(context.FailurePath))!, Environment.ProcessPath!))
            StartupFailureReport = context.FailurePath;
        return context;
    }

    public static void AcknowledgeStartup(LauncherUpdateStartup context, string executable)
    {
        if (context.RequestPath is null || context.Token is null) return;
        var requestPath = Path.GetFullPath(context.RequestPath);
        var directory = Path.GetDirectoryName(requestPath)!;
        if (Path.GetFileName(requestPath) != "request.json" || !IsStageDirectory(directory, executable)) return;
        var request = JsonSerializer.Deserialize<LauncherUpdateRequest>(File.ReadAllText(requestPath));
        if (request is null || request.Schema != 1 || request.Token != context.Token ||
            !string.Equals(Path.GetFullPath(request.TargetExe), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(HashFile(executable), request.NewSha256, StringComparison.OrdinalIgnoreCase)) return;
        var ready = Path.Combine(directory, "app.ready");
        File.WriteAllText(ready + ".tmp", request.Token);
        File.Move(ready + ".tmp", ready, true);
        LauncherUpdateHelper.CleanupAfterSuccess(directory, request, executable);
    }

    public static string ReceiptPath(string executable) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(executable))!, "." + Path.GetFileName(executable) + ".update-version.json");

    public static string? ReadInstalledReleaseTag(string executable)
    {
        try
        {
            var receipt = JsonSerializer.Deserialize<LauncherUpdateReceipt>(File.ReadAllText(ReceiptPath(executable)));
            return receipt is not null && ReleaseVersion.TryParse(receipt.ReleaseTag, out _) &&
                string.Equals(receipt.Sha256, HashFile(executable), StringComparison.OrdinalIgnoreCase) ? receipt.ReleaseTag : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    internal static bool IsStageDirectory(string directory, string executable) =>
        Path.GetFileName(directory).StartsWith(".pcl-update-", StringComparison.Ordinal) &&
        Guid.TryParseExact(Path.GetFileName(directory)[12..], "N", out _) &&
        string.Equals(Path.GetDirectoryName(directory), Path.GetDirectoryName(Path.GetFullPath(executable)), StringComparison.OrdinalIgnoreCase);

    internal static string HashFile(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var input = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false));
    }

    public void Dispose()
    {
        if (_committed) { _helper?.Dispose(); return; }
        if (_helper is not null)
        {
            try { if (!_helper.HasExited) { _helper.Kill(); _helper.WaitForExit(5000); } }
            catch (InvalidOperationException) { }
            _helper.Dispose();
        }
        if (!IsStageDirectory(DirectoryPath, TargetPath)) return;
        foreach (var name in LauncherUpdateHelper.DisposableFiles)
        {
            var path = Path.Combine(DirectoryPath, name);
            if (File.Exists(path)) File.Delete(path);
        }
        // Never recursively delete: unknown files are left intact.
        if (Directory.Exists(DirectoryPath) && !Directory.EnumerateFileSystemEntries(DirectoryPath).Any()) Directory.Delete(DirectoryPath);
    }
}
