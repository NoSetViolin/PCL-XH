using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Input;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.UI;
using PCL.Core.Updates;
using PCL.Network;

namespace PCL;

public partial class PageSetupAbout
{
    internal GitHubReleaseUpdater LauncherUpdateClient { get; set; } = new();
    internal string CurrentLauncherVersion { get; private set; } = ModBase.versionBaseName;
    internal ReleaseChannel CurrentLauncherChannel { get; private set; } = ReleaseChannel.DeveloperBeta;
    private LauncherRelease? _latestLauncherRelease;
    private ReleaseAsset? _launcherUpdateAsset;
    private CancellationTokenSource? _launcherUpdateCancellation;
    private bool _launcherUpdateBusy;
    private bool _launcherChannelChanged;
    private bool _changingLauncherChannel;
    private bool _hasInstalledReleaseIdentity;
    private string? _downloadedLauncherPath;

    private ReleaseChannel SelectedUpdateChannel => ComboUpdateChannel.SelectedIndex switch
        { 0 => ReleaseChannel.DeveloperBeta, 1 => ReleaseChannel.PublicBeta, _ => ReleaseChannel.Release };
    private static string ChannelName(ReleaseChannel channel) => channel.ToString();
    private static int ChannelIndex(ReleaseChannel channel) => channel switch
        { ReleaseChannel.DeveloperBeta => 0, ReleaseChannel.PublicBeta => 1, _ => 2 };

    private void InitializeLauncherUpdate()
    {
        var formalVersion = AtomicLauncherUpdate.ReadInstalledReleaseTag(Basics.ExecutablePath) ?? Assembly.GetEntryAssembly()?.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "LauncherReleaseVersion")?.Value;
        if (ReleaseVersion.TryParse(formalVersion, out var parsed))
        {
            _hasInstalledReleaseIdentity = true;
            CurrentLauncherVersion = parsed!.Tag;
            CurrentLauncherChannel = parsed.ExplicitChannel ?? (parsed.IsDeveloperBuild ? ReleaseChannel.DeveloperBeta : ReleaseChannel.Release);
        }
        ComboUpdateChannel.SelectedIndex = ChannelIndex(CurrentLauncherChannel);
        ShowCurrentLauncherVersion();
        if (AtomicLauncherUpdate.StartupFailureReport is not null)
            LabLauncherUpdateStatus.Text = Lang.Text("Setup.Update.RolledBack");
        ComboUpdateChannel.SelectionChanged += (_, _) =>
        {
            if (_changingLauncherChannel || _launcherUpdateBusy) return;
            _launcherChannelChanged = true;
            _latestLauncherRelease = null;
            _launcherUpdateAsset = null;
            BtnDownloadLauncherUpdate.IsEnabled = false;
            BtnInstallLauncherUpdate.IsEnabled = false;
            LabLatestLauncherVersion.Visibility = Visibility.Collapsed;
            PanLauncherReleaseNotes.Visibility = Visibility.Collapsed;
            LabLauncherUpdateStatus.Text = Lang.Text("Setup.Update.Ready");
        };
        Unloaded += (_, _) => _launcherUpdateCancellation?.Cancel();
    }

    private void ShowCurrentLauncherVersion() => LabCurrentLauncherVersion.Text =
        CurrentLauncherChannel == ReleaseChannel.DeveloperBeta && CurrentLauncherVersion == ModBase.versionBaseName
            ? Lang.Text("Setup.Update.CurrentBeta", ModBase.versionBaseName)
            : Lang.Text("Setup.Update.Current", ChannelName(CurrentLauncherChannel), CurrentLauncherVersion, ModBase.versionBaseName);

    private void SetLauncherUpdateBusy(bool busy)
    {
        _launcherUpdateBusy = busy;
        BtnCheckLauncherUpdate.IsEnabled = !busy;
        ComboUpdateChannel.IsEnabled = !busy;
        BtnDownloadLauncherUpdate.IsEnabled = !busy && _launcherUpdateAsset is not null;
        BtnInstallLauncherUpdate.IsEnabled = !busy && _launcherUpdateAsset is not null;
        BtnCancelLauncherUpdate.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ProgressLauncherUpdate.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void BtnCheckLauncherUpdate_Click(object sender, MouseButtonEventArgs e) => await CheckLauncherUpdateAsync();

    internal async Task CheckLauncherUpdateAsync()
    {
        if (_launcherUpdateBusy) return;
        ShowCurrentLauncherVersion();
        _launcherUpdateAsset = null;
        _latestLauncherRelease = null;
        LabLatestLauncherVersion.Visibility = Visibility.Collapsed;
        PanLauncherReleaseNotes.Visibility = Visibility.Collapsed;
        SetLauncherUpdateBusy(true);
        ProgressLauncherUpdate.IsIndeterminate = true;
        LabLauncherUpdateStatus.Text = Lang.Text("Setup.Update.Checking");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        _launcherUpdateCancellation = cancellation;
        try
        {
            var releases = await LauncherUpdateClient.GetReleasesAsync(cancellation.Token);
            await using var file = new FileStream(Basics.ExecutablePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, true);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellation.Token));
            // A receipt or embedded tag is authoritative when identical bytes
            // have been published to multiple channels.
            var identityReleases = _hasInstalledReleaseIdentity
                ? GitHubReleaseUpdater.PublishedReleases(releases).Where(r => GitHubReleaseUpdater.ChannelOf(r) == CurrentLauncherChannel)
                : releases;
            var installed = GitHubReleaseUpdater.InstalledRelease(identityReleases, hash);
            if (installed is not null)
            {
                _hasInstalledReleaseIdentity = true;
                CurrentLauncherVersion = installed.Tag;
                CurrentLauncherChannel = GitHubReleaseUpdater.ChannelOf(installed);
                if (!_launcherChannelChanged)
                {
                    _changingLauncherChannel = true;
                    ComboUpdateChannel.SelectedIndex = ChannelIndex(CurrentLauncherChannel);
                    _changingLauncherChannel = false;
                }
                ShowCurrentLauncherVersion();
            }
            _latestLauncherRelease = GitHubReleaseUpdater.LatestRelease(releases, SelectedUpdateChannel);
            if (_latestLauncherRelease is null)
            {
                LabLauncherUpdateStatus.Text = Lang.Text("Setup.Update.NoReleases", ChannelName(SelectedUpdateChannel));
                return;
            }
            var latest = _latestLauncherRelease;
            LabLatestLauncherVersion.Text = Lang.Text("Setup.Update.Latest", latest.Tag, latest.PublishedAt!.Value.ToLocalTime().ToString("yyyy-MM-dd"));
            LabLatestLauncherVersion.Visibility = Visibility.Visible;
            LabLauncherReleaseNotes.Text = latest.Notes ?? "";
            PanLauncherReleaseNotes.Visibility = string.IsNullOrWhiteSpace(latest.Notes) ? Visibility.Collapsed : Visibility.Visible;
            ReleaseVersion.TryParse(latest.Tag, out var availableVersion);
            ReleaseVersion.TryParse(CurrentLauncherVersion, out var currentVersion);
            var changingChannel = SelectedUpdateChannel != CurrentLauncherChannel;
            if (!changingChannel && availableVersion!.CompareTo(currentVersion) <= 0)
            {
                LabLauncherUpdateStatus.Text = Lang.Text("Setup.Update.UpToDate");
                return;
            }
            _launcherUpdateAsset = GitHubReleaseUpdater.SelectExecutable(latest, RuntimeInformation.ProcessArchitecture);
            LabLauncherUpdateStatus.Text = _launcherUpdateAsset is null
                ? Lang.Text("Setup.Update.NoExecutable", latest.Tag)
                : Lang.Text(changingChannel ? "Setup.Update.SwitchAvailable" : "Setup.Update.Available", ChannelName(SelectedUpdateChannel), latest.Tag, _launcherUpdateAsset.Name);
        }
        catch (OperationCanceledException)
        {
            LabLauncherUpdateStatus.Text = Lang.Text("Setup.Update.Cancelled");
        }
        catch (Exception ex)
        {
            LabLauncherUpdateStatus.Text = Lang.Text("Setup.Update.CheckFailed", ex.Message);
            ModBase.Log(ex, "[Update] GitHub Release check failed");
        }
        finally
        {
            _launcherUpdateCancellation = null;
            SetLauncherUpdateBusy(false);
        }
    }

    private async void BtnDownloadLauncherUpdate_Click(object sender, MouseButtonEventArgs e)
    {
        if (_launcherUpdateBusy || _launcherUpdateAsset is null || _latestLauncherRelease is null) return;
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var path = SystemDialogs.SelectSaveFile(Lang.Text("Setup.Update.SaveTitle"), "xiaohanlaucher.exe",
            "Windows executable (*.exe)|*.exe", Directory.Exists(downloads) ? downloads : Basics.ExecutableDirectory);
        if (string.IsNullOrEmpty(path)) return;
        if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(Basics.ExecutablePath), StringComparison.OrdinalIgnoreCase))
        {
            LabLauncherUpdateStatus.Text = Lang.Text("Setup.Update.RunningFile");
            return;
        }
        await DownloadLauncherUpdateAsync(path);
    }

    private async void BtnInstallLauncherUpdate_Click(object sender, MouseButtonEventArgs e) => await InstallLauncherUpdateAsync();

    internal async Task InstallLauncherUpdateAsync()
    {
        if (_launcherUpdateBusy || _launcherUpdateAsset is null || _latestLauncherRelease is null) return;
        // Replacing an apphost without its adjacent DLLs is not an application update.
#pragma warning disable IL3000 // The empty single-file Location is the intended capability check.
        var isPackaged = string.IsNullOrEmpty(Assembly.GetEntryAssembly()?.Location);
#pragma warning restore IL3000
        if (!isPackaged)
        {
            LabLauncherUpdateStatus.Text = Lang.Text("Setup.Update.PackagedOnly");
            return;
        }
        SetLauncherUpdateBusy(true);
        ProgressLauncherUpdate.IsIndeterminate = false;
        ProgressLauncherUpdate.Value = 0;
        using var cancellation = new CancellationTokenSource();
        _launcherUpdateCancellation = cancellation;
        try
        {
            using var update = await AtomicLauncherUpdate.CreateAsync(Basics.ExecutablePath, cancellationToken: cancellation.Token);
            var progress = new Progress<double>(value =>
            {
                if (!ReferenceEquals(_launcherUpdateCancellation, cancellation)) return;
                ProgressLauncherUpdate.Value = value * 100;
                LabLauncherUpdateStatus.Text = Lang.Text("Setup.Update.Downloading", (value * 100).ToString("0"));
            });
            await LauncherUpdateClient.DownloadAsync(_launcherUpdateAsset, update.CandidatePath, progress, cancellation.Token);
            // Existing game/resource downloads should finish rather than be aborted by the restart.
            while (ModNet.HasDownloadingTask())
            {
                LabLauncherUpdateStatus.Text = Lang.Text("Setup.Update.WaitingTasks");
                await Task.Delay(500, cancellation.Token);
            }
            cancellation.Token.ThrowIfCancellationRequested();
            LabLauncherUpdateStatus.Text = Lang.Text("Setup.Update.Restarting");
            await update.CommitAsync(_latestLauncherRelease.Tag, cancellation.Token);
            _launcherUpdateCancellation = null;
            BtnCancelLauncherUpdate.Visibility = Visibility.Collapsed;
            FormMain.EndProgramForce(force: false);
        }
        catch (OperationCanceledException) { LabLauncherUpdateStatus.Text = Lang.Text("Setup.Update.Cancelled"); }
        catch (Exception ex)
        {
            LabLauncherUpdateStatus.Text = Lang.Text("Setup.Update.InstallFailed", ex.Message);
            ModBase.Log(ex, "[Update] Automatic launcher update failed");
        }
        finally
        {
            _launcherUpdateCancellation = null;
            if (!ModBase.isProgramEnded) SetLauncherUpdateBusy(false);
        }
    }

    internal async Task DownloadLauncherUpdateAsync(string path)
    {
        if (_launcherUpdateBusy || _launcherUpdateAsset is null) return;
        SetLauncherUpdateBusy(true);
        ProgressLauncherUpdate.IsIndeterminate = false;
        ProgressLauncherUpdate.Value = 0;
        using var cancellation = new CancellationTokenSource();
        _launcherUpdateCancellation = cancellation;
        try
        {
            var progress = new Progress<double>(value =>
            {
                if (!ReferenceEquals(_launcherUpdateCancellation, cancellation)) return;
                ProgressLauncherUpdate.Value = value * 100;
                LabLauncherUpdateStatus.Text = Lang.Text("Setup.Update.Downloading", (value * 100).ToString("0"));
            });
            _downloadedLauncherPath = await LauncherUpdateClient.DownloadAsync(_launcherUpdateAsset, path, progress, cancellation.Token);
            LabLauncherUpdateStatus.Text = Lang.Text("Setup.Update.Downloaded", _downloadedLauncherPath);
            BtnOpenLauncherUpdate.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            LabLauncherUpdateStatus.Text = Lang.Text("Setup.Update.Cancelled");
        }
        catch (Exception ex)
        {
            LabLauncherUpdateStatus.Text = Lang.Text("Setup.Update.DownloadFailed", ex.Message);
            ModBase.Log(ex, "[Update] GitHub Release download failed");
        }
        finally
        {
            _launcherUpdateCancellation = null;
            SetLauncherUpdateBusy(false);
        }
    }

    private void BtnCancelLauncherUpdate_Click(object sender, MouseButtonEventArgs e) => _launcherUpdateCancellation?.Cancel();
    private void BtnOpenLauncherUpdate_Click(object sender, MouseButtonEventArgs e)
    {
        if (_downloadedLauncherPath is not null) Basics.OpenPath(Path.GetDirectoryName(_downloadedLauncherPath)!);
    }
}
