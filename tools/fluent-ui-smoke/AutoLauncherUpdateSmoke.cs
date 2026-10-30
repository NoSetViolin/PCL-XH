using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using PCL.Core.App;
using PCL.Core.App.Configuration;
using PCL.Core.Updates;

namespace PCL;

internal static class AutoLauncherUpdateSmoke
{
    public static async Task Run(FormMain form, string root, List<string> report)
    {
        var fixture = Environment.GetEnvironmentVariable("PCL_AUTOUPDATE_NEW_EXE")
            ?? throw new Exception("PCL_AUTOUPDATE_NEW_EXE is required");
        var marker = Path.Combine(root, "auto-update-armed");
        if (File.Exists(marker))
        {
            // The actual new launcher must acknowledge Running and keep its UI
            // alive while the detached helper verifies startup and removes backup.
            var until = DateTime.UtcNow.AddSeconds(20);
            while (Directory.GetDirectories(Basics.ExecutableDirectory, ".pcl-update-*").Length > 0)
            {
                if (DateTime.UtcNow >= until) throw new Exception("The updater helper did not finish cleanup");
                await Task.Delay(100);
            }
            var actualHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Basics.ExecutablePath)));
            var expectedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixture)));
            if (actualHash != expectedHash || AtomicLauncherUpdate.ReadInstalledReleaseTag(Basics.ExecutablePath) != "dev-26.01101")
                throw new Exception("Wrong executable/tag after automatic restart");
            if (AtomicLauncherUpdate.StartupFailureReport is not null) throw new Exception("Unexpected update rollback");
            report.Add("PASS real published launcher downloaded, replaced itself, restarted and acknowledged Running");
            report.Add("PASS same executable path, channel/version receipt, old EXE deletion and staging cleanup");
            File.WriteAllLines(Path.Combine(root, "auto-launcher-update-report.txt"), report);
            return;
        }
        var bytes = File.ReadAllBytes(fixture);
        var release = new LauncherRelease("dev-26.01101", "Automatic update fixture", "QA", false, true, DateTimeOffset.UtcNow,
            new[] { new ReleaseAsset("xiaohanlaucher.exe", "https://github.com/NoSetViolin/PCL-XH/releases/download/dev-26.01101/xiaohanlaucher.exe",
                bytes.Length, "sha256:" + Convert.ToHexString(SHA256.HashData(bytes))) });
        using var client = new HttpClient(new Handler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = request.RequestUri!.Host == "api.github.com"
                ? new StringContent(JsonSerializer.Serialize(new[] { release })) : new ByteArrayContent(bytes)
        })));
        form.PageChange(FormMain.PageType.Setup, FormMain.PageSubType.SetupAbout);
        await Task.Delay(500);
        var page = ModMain.frmSetupAbout;
        page.LauncherUpdateClient = new GitHubReleaseUpdater(client);
        await page.CheckLauncherUpdateAsync();
        if (!page.BtnInstallLauncherUpdate.IsEnabled) throw new Exception("One-click update action is disabled");
        File.WriteAllText(marker, Basics.ExecutablePath);
        await page.InstallLauncherUpdateAsync();
        if (!ModBase.isProgramEnded) throw new Exception("Auto update did not hand off and request graceful shutdown: " + page.LabLauncherUpdateStatus.Text);
        // Normal lifecycle shutdown terminates this process. Do not let the smoke
        // harness finally block replace that graceful shutdown with a forced exit.
        await Task.Delay(Timeout.Infinite);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
