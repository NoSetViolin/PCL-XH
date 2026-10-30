using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.App;
using PCL.Core.IO.Net;

namespace PCL.Core.Updates;

public enum ReleaseChannel { Release, PublicBeta, DeveloperBeta }

public sealed record ReleaseAsset(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("browser_download_url")] string DownloadUrl,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("digest")] string? Digest = null);

public sealed record LauncherRelease(
    [property: JsonPropertyName("tag_name")] string Tag,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("body")] string? Notes,
    [property: JsonPropertyName("draft")] bool Draft,
    [property: JsonPropertyName("prerelease")] bool Prerelease,
    [property: JsonPropertyName("published_at")] DateTimeOffset? PublishedAt,
    [property: JsonPropertyName("assets")] ReleaseAsset[]? Assets);

public sealed class GitHubReleaseUpdater
{
    public const string ReleasesUrl = "https://github.com/NoSetViolin/PCL-XH/releases";
    public const string ApiUrl = "https://api.github.com/repos/NoSetViolin/PCL-XH/releases";
    private readonly HttpClient _client;
    public GitHubReleaseUpdater(HttpClient? client = null) => _client = client ?? NetworkService.GetClient();

    public async Task<IReadOnlyList<LauncherRelease>> GetReleasesAsync(CancellationToken cancellationToken = default)
    {
        var releases = new List<LauncherRelease>();
        for (var page = 1; ; page++)
        {
            using var request = CreateRequest($"{ApiUrl}?per_page=100&page={page}");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            // Let the UI distinguish a rate limit from a repository/network failure.
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                throw new HttpRequestException("GitHub API rate limit or access restriction. Try again later.", null, response.StatusCode);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var batch = await JsonSerializer.DeserializeAsync<LauncherRelease[]>(stream, cancellationToken: cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("GitHub returned an invalid release list.");
            releases.AddRange(batch);
            if (batch.Length < 100) return releases;
        }
    }

    public static IEnumerable<LauncherRelease> PublishedReleases(IEnumerable<LauncherRelease> releases) =>
        releases.Where(r => !r.Draft && r.PublishedAt.HasValue && ReleaseVersion.TryParse(r.Tag, out _));

    public static ReleaseChannel ChannelOf(LauncherRelease release)
    {
        var version = Parse(release.Tag);
        return version.ExplicitChannel ?? (version.IsDeveloperBuild ? ReleaseChannel.DeveloperBeta :
            release.Prerelease ? ReleaseChannel.PublicBeta : ReleaseChannel.Release);
    }

    public static LauncherRelease? LatestRelease(IEnumerable<LauncherRelease> releases, ReleaseChannel channel = ReleaseChannel.Release) =>
        PublishedReleases(releases).Where(r => ChannelOf(r) == channel).OrderByDescending(r => Parse(r.Tag)).FirstOrDefault();

    // GitHub's asset digest also identifies manually downloaded EXEs, even when
    // their embedded release version was not changed before publishing the tag.
    public static LauncherRelease? InstalledRelease(IEnumerable<LauncherRelease> releases, string sha256) =>
        PublishedReleases(releases)
            .Where(r => r.Assets?.Any(a => a.Name?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true && IsRepositoryAsset(a.DownloadUrl) &&
                string.Equals(a.Digest, "sha256:" + sha256, StringComparison.OrdinalIgnoreCase)) == true)
            .OrderBy(r => ChannelOf(r)).ThenByDescending(r => Parse(r.Tag)).FirstOrDefault();

    public static ReleaseAsset? SelectExecutable(LauncherRelease release, Architecture architecture)
    {
        var candidates = (release.Assets ?? Array.Empty<ReleaseAsset>()).Where(a =>
            a.Size > 0 && a.Name?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true && IsRepositoryAsset(a.DownloadUrl));
        return candidates.Select(a => (Asset: a, Score: AssetScore(a.Name, architecture)))
            .Where(a => a.Score >= 0).OrderByDescending(a => a.Score).ThenBy(a => a.Asset.Name, StringComparer.OrdinalIgnoreCase)
            .Select(a => a.Asset).FirstOrDefault();
    }

    private static int AssetScore(string name, Architecture architecture)
    {
        var lower = name.ToLowerInvariant();
        var tokens = lower.Split(new[] { '-', '_', '.', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var arm = tokens.Any(t => t is "arm64" or "aarch64" || t.EndsWith("arm64"));
        var x64 = tokens.Any(t => t is "x64" or "amd64" or "win64" || t.EndsWith("x64"));
        var x86 = tokens.Any(t => t is "x86" or "win32" or "i386" || t.EndsWith("x86"));
        if ((arm && architecture != Architecture.Arm64) || (x64 && architecture != Architecture.X64) ||
            (x86 && architecture != Architecture.X86)) return -1;
        var score = arm || x64 || x86 ? 100 : 0;
        if (lower.Contains("xiaohanlaucher") || lower.Contains("xiaohanlauncher")) score += 30;
        if (lower.Contains("portable")) score += 10;
        if (lower.Contains("uninstall") || lower.Contains("updater")) return -1;
        return score;
    }

    public static bool IsRepositoryAsset(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.StartsWith("/NoSetViolin/PCL-XH/releases/download/", StringComparison.OrdinalIgnoreCase);

    public async Task<string> DownloadAsync(ReleaseAsset asset, string destination,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!IsRepositoryAsset(asset.DownloadUrl) || !asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || asset.Size <= 0)
            throw new InvalidDataException("Invalid release executable.");
        var path = Path.GetFullPath(destination);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".download";
        try
        {
            using var request = CreateRequest(asset.DownloadUrl);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long length = 0;
            var buffer = new byte[65536];
            var signature = new byte[2];
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, true))
            {
                int count;
                while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    for (var i = 0; i < count && length + i < 2; i++) signature[(int)length + i] = buffer[i];
                    length += count;
                    if (length > asset.Size) throw new InvalidDataException("Release file size does not match GitHub metadata.");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    progress?.Report((double)length / asset.Size);
                }
            }
            var digest = Convert.ToHexString(hash.GetHashAndReset());
            if (length != asset.Size || signature[0] != 'M' || signature[1] != 'Z')
                throw new InvalidDataException("The downloaded file is incomplete or is not an executable.");
            if (!string.IsNullOrEmpty(asset.Digest) &&
                !string.Equals(asset.Digest, "sha256:" + digest, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Release SHA256 verification failed.");
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
            return path;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static HttpRequestMessage CreateRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("PCL-XH-Update/1.0");
        return request;
    }

    private static ReleaseVersion Parse(string tag)
    {
        if (!ReleaseVersion.TryParse(tag, out var version)) throw new ArgumentException("Invalid release tag.", nameof(tag));
        return version!;
    }
}
