using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Updates;

namespace PCL.Core.Test.Updates;

[TestClass]
public class AtomicLauncherUpdateTest
{
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [TestMethod]
    public async Task RejectsAnUpdateTargetThatIsNotTheParentExecutable()
    {
        var root = NewRoot();
        try
        {
            var target = Path.Combine(root, "other.exe");
            await File.WriteAllBytesAsync(target, new byte[] { 77, 90, 1 });
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => AtomicLauncherUpdate.CreateAsync(target));
            Assert.AreEqual(0, Directory.GetDirectories(root).Length);
        }
        finally { DeleteRoot(root); }
    }

    [TestMethod]
    public void ReleaseReceiptMustMatchTheInstalledBytes()
    {
        var root = NewRoot();
        try
        {
            var exe = Path.Combine(root, "xiaohanlaucher.exe");
            File.WriteAllText(exe, "original executable bytes");
            File.WriteAllText(AtomicLauncherUpdate.ReceiptPath(exe), JsonSerializer.Serialize(new LauncherUpdateReceipt("beta-26.0.1", Hash(exe))));
            Assert.AreEqual("beta-26.0.1", AtomicLauncherUpdate.ReadInstalledReleaseTag(exe));
            File.WriteAllText(exe, "another executable");
            Assert.IsNull(AtomicLauncherUpdate.ReadInstalledReleaseTag(exe));
        }
        finally { DeleteRoot(root); }
    }

    [TestMethod]
    [DataRow("success")]
    [DataRow("crash")]
    [DataRow("timeout")]
    [DataRow("tamper")]
    [DataRow("cancel")]
    public async Task ReplacementRestartCleanupAndRollbackUseRealProcesses(string scenario)
    {
        var oldFixture = Environment.GetEnvironmentVariable("PCL_SELFUPDATE_FIXTURE_OLD");
        var newFixture = Environment.GetEnvironmentVariable("PCL_SELFUPDATE_FIXTURE_NEW");
        if (oldFixture is null || newFixture is null) Assert.Inconclusive("Publish tools/self-update-smoke fixtures and set PCL_SELFUPDATE_FIXTURE_OLD/NEW.");
        // Includes spaces, brackets, quotes and shell metacharacters: none are code.
        var root = NewRoot();
        Process? parent = null;
        string? stage = null;
        try
        {
            var target = Path.Combine(root, "xiaohanlaucher.exe");
            File.Copy(oldFixture, target);
            File.WriteAllText(Path.Combine(root, "settings.ini"), "user configuration must survive");
            if (scenario == "crash") File.WriteAllText(Path.Combine(root, "crash-new"), "");
            if (scenario == "timeout") File.WriteAllText(Path.Combine(root, "hang-new"), "");
            var start = new ProcessStartInfo(target) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = root };
            start.ArgumentList.Add("--wait");
            parent = Process.Start(start)!;
            await WaitFor(() => File.Exists(Path.Combine(root, "parent.ready")), "Fixture parent startup");
            using (var update = await AtomicLauncherUpdate.CreateAsync(target, parent))
            {
                stage = update.DirectoryPath;
                File.Copy(newFixture, update.CandidatePath);
                if (scenario != "cancel")
                {
                    await update.CommitAsync("beta-26.0.1", startupTimeoutSeconds: 5, startupGraceSeconds: 1);
                    Assert.AreEqual(Hash(oldFixture), Hash(target), "The still-running parent must not be replaced.");
                    if (scenario == "tamper") await File.AppendAllTextAsync(update.CandidatePath, "tamper after handoff");
                }
            }
            if (scenario == "cancel")
            {
                Assert.IsFalse(Directory.Exists(stage));
                Assert.IsFalse(parent.HasExited);
                Assert.AreEqual(Hash(oldFixture), Hash(target));
                return;
            }
            parent.Kill(); // Only this test's disposable parent; production waits for graceful exit.
            await parent.WaitForExitAsync();
            if (scenario == "success")
            {
                await WaitFor(() => !Directory.Exists(stage), "Successful update cleanup", 25);
                Assert.IsTrue(File.Exists(Path.Combine(root, "started-new")));
                Assert.AreEqual(Hash(newFixture), Hash(target));
                Assert.AreEqual("beta-26.0.1", AtomicLauncherUpdate.ReadInstalledReleaseTag(target));
            }
            else
            {
                await WaitFor(() => File.Exists(Path.Combine(root, "started-old")), "Rollback restart", 30);
                Assert.AreEqual(Hash(oldFixture), Hash(target));
                Assert.IsTrue(File.Exists(Path.Combine(stage, "result.json")));
                Assert.IsNull(AtomicLauncherUpdate.ReadInstalledReleaseTag(target));
            }
            Assert.AreEqual("user configuration must survive", File.ReadAllText(Path.Combine(root, "settings.ini")));
        }
        finally
        {
            File.WriteAllText(Path.Combine(root, "stop"), "");
            if (parent is not null)
            {
                if (!parent.HasExited) { parent.Kill(); await parent.WaitForExitAsync(); }
                parent.Dispose();
            }
            foreach (var name in new[] { "started-new", "started-old" })
            {
                var marker = Path.Combine(root, name);
                if (!File.Exists(marker) || !int.TryParse(File.ReadAllText(marker), out var id)) continue;
                try
                {
                    using var child = Process.GetProcessById(id);
                    if (!child.HasExited && Path.GetFullPath(child.MainModule!.FileName).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!child.WaitForExit(5000)) { child.Kill(); await child.WaitForExitAsync(); }
                    }
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
            // Helper may still be exiting after publishing the final marker.
            await Task.Delay(500);
            DeleteRoot(root);
        }
    }

    private static async Task WaitFor(Func<bool> condition, string name, int seconds = 15)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow >= until) Assert.Fail(name + " timed out");
            await Task.Delay(100);
        }
    }

    private static string NewRoot()
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "pcl-self-update-tests", "space & [literal] ' $ test-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteRoot(string root)
    {
        var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "pcl-self-update-tests")) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(root);
        if (!full.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Test cleanup escaped its root.");
        Directory.Delete(full, true);
    }
}
