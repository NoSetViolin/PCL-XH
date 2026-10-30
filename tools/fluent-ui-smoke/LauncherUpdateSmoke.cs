using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Windows;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.UI.Theme;
using PCL.Core.Updates;

namespace PCL;

internal static class LauncherUpdateSmoke
{
    public static async Task Run(FormMain form, string output, List<string> report)
    {
        void Require(bool value, string message) { if (!value) throw new Exception(message); }
        var bytes = new byte[] { 77, 90, 1, 2, 3, 4, 5, 6 };
        var digest = "sha256:" + Convert.ToHexString(SHA256.HashData(bytes));
        LauncherRelease Release(string tag) => new(tag, tag, "Update notes\n" + new string('x', 300), false,
            tag.Contains('-'), DateTimeOffset.UtcNow, new[] { new ReleaseAsset("xiaohanlaucher.exe",
                $"https://github.com/NoSetViolin/PCL-XH/releases/download/{tag}/xiaohanlaucher.exe", bytes.Length, digest) });
        var feed = new[] { Release("dev-26.01101"), Release("beta-26.0.1"), Release("26.0.1") };
        var error = false;
        using var client = new HttpClient(new Handler((request, _) => Task.FromResult(
            error ? new HttpResponseMessage(HttpStatusCode.Forbidden) : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = request.RequestUri!.Host == "api.github.com"
                    ? new StringContent(JsonSerializer.Serialize(feed)) : new ByteArrayContent(bytes)
            })));
        form.PageChange(FormMain.PageType.Setup, FormMain.PageSubType.SetupAbout);
        await Task.Delay(700);
        var page = ModMain.frmSetupAbout;
        Require(page is not null, "About/update page was not opened");
        page.LauncherUpdateClient = new GitHubReleaseUpdater(client);
        Require(page.ComboUpdateChannel.Items.Count == 3 && page.LabCurrentLauncherVersion.Text.Contains("26.01030"),
            "Current build or three-channel selector missing");
        foreach (var selected in new[] { 0, 1, 2 })
        {
            page.ComboUpdateChannel.SelectedIndex = selected;
            await page.CheckLauncherUpdateAsync();
            Require(page.BtnDownloadLauncherUpdate.IsEnabled && page.BtnInstallLauncherUpdate.IsEnabled, "Expected selected-channel update/download is disabled");
            var expected = selected == 0 ? "dev-26.01101" : selected == 1 ? "beta-26.0.1" : "26.0.1";
            Require(page.LabLatestLauncherVersion.Text.Contains(expected), "Wrong update channel selected");
        }
        var destination = Path.Combine(output, "mock-launcher-update.exe");
        await page.DownloadLauncherUpdateAsync(destination);
        Require(File.ReadAllBytes(destination).SequenceEqual(bytes) && page.BtnOpenLauncherUpdate.Visibility == Visibility.Visible,
            "UI download did not finish with a verified fixture");
        Require(page.BtnCheckLauncherUpdate.IsEnabled && page.LabLauncherUpdateStatus.Text.Contains(Path.GetFullPath(destination)), "UI download completion state incorrect: " + page.LabLauncherUpdateStatus.Text);
        report.Add("PASS current DeveloperBeta build, all three channel searches and verified EXE download through the page");
        feed = Array.Empty<LauncherRelease>();
        await page.CheckLauncherUpdateAsync();
        Require(!page.BtnDownloadLauncherUpdate.IsEnabled && page.LabLauncherUpdateStatus.Text == Lang.Text("Setup.Update.NoReleases", "Release"), "Empty feed state incorrect");
        error = true;
        await page.CheckLauncherUpdateAsync();
        Require(page.BtnCheckLauncherUpdate.IsEnabled && !page.BtnDownloadLauncherUpdate.IsEnabled && page.LabLauncherUpdateStatus.Text.Contains("GitHub"), "Rate limit did not leave a retryable state");
        report.Add("PASS empty repository and GitHub error states, with download disabled and retry available");
        error = false;
        var installedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Basics.ExecutablePath)));
        var sameExecutable = new ReleaseAsset("xiaohanlaucher.exe", "https://github.com/NoSetViolin/PCL-XH/releases/download/beta-26.0.1/xiaohanlaucher.exe", bytes.Length, "sha256:" + installedHash);
        feed = new[] { new LauncherRelease("beta-26.0.1", "Installed", "", false, true, DateTimeOffset.UtcNow, new[] { sameExecutable }) };
        page.ComboUpdateChannel.SelectedIndex = 1;
        await page.CheckLauncherUpdateAsync();
        feed = new[] { feed[0], new LauncherRelease("26.0.1", "Same bytes, other channel", "", false, false, DateTimeOffset.UtcNow, new[] { sameExecutable }) };
        await page.CheckLauncherUpdateAsync();
        Require(page.CurrentLauncherChannel == ReleaseChannel.PublicBeta, "Identical EXE in another channel changed the installed channel");
        report.Add("PASS installed channel identity is preserved when identical bytes appear in another channel");
        feed = new[] { Release("dev-26.01101"), Release("beta-26.0.1"), Release("26.0.1") };
        page.ComboUpdateChannel.SelectedIndex = 0;
        foreach (var language in new[] { "zh-CN", "en-US" })
        foreach (var dark in new[] { false, true })
        foreach (var width in new[] { 810d, 1280d })
        {
            LocalizationService.Apply(language, save: false);
            Config.Preference.Theme.ColorMode = dark ? ColorMode.Dark : ColorMode.Light;
            ThemeService.RefreshColorMode();
            form.Width = width; form.Height = width == 810 ? 470 : 800;
            await page.CheckLauncherUpdateAsync();
            Require(page.LabCurrentLauncherVersion.Text.StartsWith(language == "zh-CN" ? "当前版本" : "Current version"), "Current version label did not follow the page language");
            page.PanBack.ScrollToTop();
            await Task.Delay(500); form.UpdateLayout();
            var button = page.BtnCheckLauncherUpdate;
            var bounds = button.TransformToAncestor(form).TransformBounds(new Rect(button.RenderSize));
            var dockTop = form.PrimaryNavigation.TransformToAncestor(form).Transform(new Point()).Y;
            Require(bounds.Left >= 0 && bounds.Right <= form.ActualWidth && bounds.Bottom < dockTop,
                "Update action overlaps the dock or window edge");
            LiquidGlassSmoke.Capture(form, Path.Combine(output, $"launcher-update-{language}-{dark}-{width}.png"));
        }
        report.Add("PASS update card actions above the dock in Chinese/English, both themes and 810/1280 widths");
        Require(EventTypeMapper.TryParse("检查更新", out var type) && type == EventType.CheckUpdate, "Legacy update event still unsupported");
        report.Add("PASS legacy homepage check-update event recognized");
        var live = await new GitHubReleaseUpdater().GetReleasesAsync();
        report.Add($"PASS live repository Release API: {live.Count} releases returned");
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
