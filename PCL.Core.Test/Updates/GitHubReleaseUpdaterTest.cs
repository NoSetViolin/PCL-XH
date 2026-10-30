using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Updates;

namespace PCL.Core.Test.Updates;

[TestClass]
public class GitHubReleaseUpdaterTest
{
    private const string DownloadUrl = "https://github.com/NoSetViolin/PCL-XH/releases/download/26.1/xiaohanlaucher.exe";
    private static LauncherRelease Release(string tag, bool draft = false, bool prerelease = false, params ReleaseAsset[] assets) =>
        new(tag, tag, "Notes", draft, prerelease, DateTimeOffset.Parse("2026-10-30T00:00:00Z"), assets);
    private static ReleaseVersion Version(string value)
    {
        Assert.IsTrue(ReleaseVersion.TryParse(value, out var version));
        return version!;
    }

    [TestMethod]
    public void NumericTagsFollowTheRequestedSequence()
    {
        var versions = new[] { "26", "26.0.1", "26.0.2", "26.1", "26.1.1", "26.1.2", "26.2", "26.10" };
        for (var i = 1; i < versions.Length; i++) Assert.IsTrue(Version(versions[i]).CompareTo(Version(versions[i - 1])) > 0);
        Assert.AreEqual(0, Version("v26.1.0").CompareTo(Version("26.1")));
        Assert.IsTrue(Version("26.999999999999999999999999").CompareTo(Version("26.2147483647")) > 0);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("26..1")]
    [DataRow("26.1-beta")]
    [DataRow("26.-1")]
    [DataRow("26. 1")]
    [DataRow("2026/10/30")]
    public void NonNumericTagsAreRejected(string tag) => Assert.IsFalse(ReleaseVersion.TryParse(tag, out _));

    [TestMethod]
    public void BetaBuildsAndFormalVersionsHaveSeparateChannels()
    {
        var releases = new[] { Release("26.1"), Release("26.01030"), Release("26.01101"), Release("26.2"),
            Release("27", draft: true), Release("beta-26.3", prerelease: true), Release("invalid") };
        Assert.AreEqual("26.2", GitHubReleaseUpdater.LatestRelease(releases)?.Tag);
        Assert.AreEqual("26.01101", GitHubReleaseUpdater.LatestRelease(releases, ReleaseChannel.DeveloperBeta)?.Tag);
        Assert.AreEqual(ReleaseChannel.DeveloperBeta, GitHubReleaseUpdater.ChannelOf(Release("26.01030")));
        Assert.AreEqual(ReleaseChannel.Release, GitHubReleaseUpdater.ChannelOf(Release("26.0.1")));
        Assert.AreEqual(ReleaseChannel.DeveloperBeta, GitHubReleaseUpdater.ChannelOf(Release("dev-26.01030", prerelease: true)));
        Assert.AreEqual(ReleaseChannel.PublicBeta, GitHubReleaseUpdater.ChannelOf(Release("beta-26.0.1", prerelease: true)));
        Assert.AreEqual("beta-26.3", GitHubReleaseUpdater.LatestRelease(releases, ReleaseChannel.PublicBeta)?.Tag);
        Assert.IsTrue(Version("beta-26.0.2").CompareTo(Version("beta-26.0.1")) > 0);
        Assert.IsTrue(Version("dev-26.01101").CompareTo(Version("dev-26.01030")) > 0);
        Assert.IsTrue(Version("26.01101").CompareTo(Version("26.01030")) > 0);
        Assert.IsNull(GitHubReleaseUpdater.LatestRelease(Array.Empty<LauncherRelease>()));
    }

    [TestMethod]
    public void OnlyAppropriateRepositoryExecutablesAreSelected()
    {
        var release = Release("26.1", assets: new[] {
            new ReleaseAsset("notes.zip", DownloadUrl, 10),
            new ReleaseAsset("xiaohanlaucher-arm64.exe", DownloadUrl, 10),
            new ReleaseAsset("xiaohanlaucher-x64.exe", DownloadUrl, 10),
            new ReleaseAsset("xiaohanlaucher-x86.exe", DownloadUrl, 10),
            new ReleaseAsset("xiaohanlaucher.exe", "https://github.com.example.com/NoSetViolin/PCL-XH/releases/download/26.1/a.exe", 10) });
        Assert.AreEqual("xiaohanlaucher-x64.exe", GitHubReleaseUpdater.SelectExecutable(release, Architecture.X64)?.Name);
        Assert.AreEqual("xiaohanlaucher-arm64.exe", GitHubReleaseUpdater.SelectExecutable(release, Architecture.Arm64)?.Name);
        Assert.AreEqual("xiaohanlaucher-x86.exe", GitHubReleaseUpdater.SelectExecutable(release, Architecture.X86)?.Name);
        Assert.IsNull(GitHubReleaseUpdater.SelectExecutable(Release("26.1"), Architecture.X64));
        Assert.IsFalse(GitHubReleaseUpdater.IsRepositoryAsset("http://github.com/NoSetViolin/PCL-XH/releases/download/26/a.exe"));
        Assert.IsFalse(GitHubReleaseUpdater.IsRepositoryAsset("https://github.com/other/repo/releases/download/26/a.exe"));
    }

    [TestMethod]
    public void PublishedExeDigestIdentifiesInstalledTagAndPreventsRepeatUpdates()
    {
        var asset = new ReleaseAsset("xiaohanlaucher.exe", DownloadUrl, 10, "sha256:ABC123");
        var releases = new[] { Release("26.01030", assets: new[] { asset }), Release("26.0.1", assets: new[] { asset }) };
        Assert.AreEqual("26.0.1", GitHubReleaseUpdater.InstalledRelease(releases, "abc123")?.Tag);
        Assert.IsNull(GitHubReleaseUpdater.InstalledRelease(releases, "different"));
    }

    [TestMethod]
    public async Task AllReleasePagesAreReadBeforeChoosingHighestTag()
    {
        var requests = 0;
        using var client = new HttpClient(new Handler((request, _) =>
        {
            requests++;
            Assert.IsTrue(request.Headers.UserAgent.Any());
            var batch = request.RequestUri!.Query.EndsWith("&page=1")
                ? Enumerable.Range(1, 100).Select(n => Release("26.0." + n)).ToArray()
                : new[] { Release("26.2") };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(batch)) });
        }));
        var releases = await new GitHubReleaseUpdater(client).GetReleasesAsync();
        Assert.AreEqual(2, requests);
        Assert.AreEqual("26.2", GitHubReleaseUpdater.LatestRelease(releases)?.Tag);
    }

    [TestMethod]
    public async Task EmptyFeedAndRateLimitHaveDistinctOutcomes()
    {
        using var empty = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") })));
        Assert.AreEqual(0, (await new GitHubReleaseUpdater(empty).GetReleasesAsync()).Count);
        using var limited = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden))));
        var error = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => new GitHubReleaseUpdater(limited).GetReleasesAsync());
        Assert.AreEqual(HttpStatusCode.Forbidden, error.StatusCode);
    }

    [TestMethod]
    public async Task ValidDownloadIsVerifiedBeforeReplacingDestination()
    {
        var bytes = new byte[] { 77, 90, 1, 2, 3, 4, 5 };
        var asset = new ReleaseAsset("xiaohanlaucher.exe", DownloadUrl, bytes.Length, "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)));
        var root = Path.Combine(Path.GetTempPath(), "pcl-release-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var target = Path.Combine(root, "xiaohanlaucher.exe");
            await File.WriteAllTextAsync(target, "existing download");
            using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) })));
            await new GitHubReleaseUpdater(client).DownloadAsync(asset, target);
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(target));
            Assert.AreEqual(1, Directory.GetFiles(root).Length);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    [DataRow("hash")]
    [DataRow("size")]
    [DataRow("html")]
    public async Task InvalidDownloadsPreserveOldFileAndRemovePartialFile(string failure)
    {
        var bytes = failure == "html" ? new byte[] { 60, 104, 116, 109, 108 } : new byte[] { 77, 90, 1, 2 };
        var asset = new ReleaseAsset("xiaohanlaucher.exe", DownloadUrl, bytes.Length + (failure == "size" ? 1 : 0),
            "sha256:" + (failure == "hash" ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(bytes))));
        var root = Path.Combine(Path.GetTempPath(), "pcl-release-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var target = Path.Combine(root, "xiaohanlaucher.exe");
            await File.WriteAllTextAsync(target, "keep this file");
            using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) })));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new GitHubReleaseUpdater(client).DownloadAsync(asset, target));
            Assert.AreEqual("keep this file", await File.ReadAllTextAsync(target));
            Assert.AreEqual(1, Directory.GetFiles(root).Length);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task CancelledDownloadLeavesNoOutput()
    {
        var root = Path.Combine(Path.GetTempPath(), "pcl-release-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var cancellation = new CancellationTokenSource();
            using var client = new HttpClient(new Handler(async (_, token) =>
            {
                cancellation.Cancel();
                await Task.Delay(1000, token);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }));
            try
            {
                await new GitHubReleaseUpdater(client).DownloadAsync(new("xiaohanlaucher.exe", DownloadUrl, 10), Path.Combine(root, "update.exe"), cancellationToken: cancellation.Token);
                Assert.Fail("Download should have been cancelled.");
            }
            catch (OperationCanceledException) { }
            Assert.AreEqual(0, Directory.GetFiles(root).Length);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
