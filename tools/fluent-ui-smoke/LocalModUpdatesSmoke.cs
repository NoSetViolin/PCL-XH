using System.IO;
using System.Text.Json.Nodes;
using PCL.Network.Loaders;
using static PCL.ModComp;
using static PCL.ModLocalComp;

namespace PCL;

internal static class LocalModUpdatesSmoke
{
    private static void Require(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    public static async Task Run(string output, List<string> report)
    {
        Directory.CreateDirectory(output);
        var path = Path.Combine(output, "synthetic-installed.jar");
        File.WriteAllText(path, "local update regression fixture");
        var fingerprint = new LocalCompFile(path).CurseForgeHash;
        var sha1 = new LocalCompFile(path).ModrinthHash;
        JsonObject FileInfo(bool cf, bool update, string hash, string version = "1.20.1", string loader = "Forge") => cf
            ? new JsonObject
            {
                ["id"] = update ? 9000002 : 9000001, ["gameId"] = 432, ["modId"] = 800000,
                ["fileFingerprint"] = fingerprint, ["displayName"] = "fixture", ["fileName"] = "fixture.jar",
                ["downloadCount"] = 0, ["releaseType"] = 1, ["fileDate"] = update ? "2024-02-01T00:00:00Z" : "2024-01-01T00:00:00Z",
                ["downloadUrl"] = "https://edge.forgecdn.net/fixture.jar", ["isAvailable"] = true,
                ["hashes"] = new JsonArray(new JsonObject { ["algo"] = 1, ["value"] = hash }),
                ["gameVersions"] = new JsonArray(version, loader)
            }
            : new JsonObject
            {
                ["id"] = update ? "new-version" : "installed-version", ["project_id"] = "mock-project",
                ["name"] = "fixture", ["version_number"] = "1", ["version_type"] = "release", ["downloads"] = 0,
                ["date_published"] = update ? "2024-02-01T00:00:00Z" : "2024-01-01T00:00:00Z",
                ["files"] = new JsonArray(new JsonObject { ["filename"] = "fixture.jar", ["url"] = "https://cdn.modrinth.com/fixture.jar",
                    ["hashes"] = new JsonObject { ["sha1"] = hash } }),
                ["game_versions"] = new JsonArray(version), ["loaders"] = new JsonArray(loader.ToLowerInvariant())
            };
        var indexLoader = 1;
        JsonObject Project(bool cf) => new()
        {
            ["DataSource"] = cf ? "CurseForge" : "Modrinth", ["Type"] = 0, ["Id"] = cf ? "800000" : "mock-project",
            ["Slug"] = "update-fixture", ["RawName"] = "Update fixture", ["Description"] = "QA", ["DownloadCount"] = 1,
            ["Website"] = cf ? "https://www.curseforge.com/minecraft/mc-mods/update-fixture" : "https://modrinth.com/mod/update-fixture",
            ["Tags"] = new JsonArray(), ["latestFilesIndexes"] = new JsonArray(
                new JsonObject { ["gameVersion"] = "1.20.1", ["modLoader"] = indexLoader, ["fileId"] = 9000002 })
        };
        string? fail = null;
        var cfVersion = "1.20.1";
        var cfLoader = "Forge";
        var sameInstalled = false;
        var cfUpdateHash = "next-hash";
        LocalCompMetadataRequest fake = (url, method, body) =>
        {
            var cf = new Uri(url).Host == "api.curseforge.com";
            if (fail == (cf ? "CF" : "MR")) throw new IOException("Injected provider failure");
            if (url.EndsWith("/fingerprints/432"))
            {
                Require(method == "POST" && JsonNode.Parse(body!)!["fingerprints"]!.AsArray().Any(h => h!.GetValue<uint>() == fingerprint), "Fingerprint request incorrect");
                return new JsonObject { ["data"] = new JsonObject { ["exactMatches"] = new JsonArray(
                    new JsonObject { ["id"] = 800000, ["file"] = FileInfo(true, false, sha1) }) } };
            }
            if (cf && url.EndsWith("/mods")) return new JsonObject { ["data"] = new JsonArray(Project(true)) };
            if (cf && url.EndsWith("/mods/files")) return new JsonObject { ["data"] = new JsonArray(FileInfo(true, true,
                sameInstalled ? sha1 : cfUpdateHash, cfVersion, cfLoader)) };
            if (url.Contains("/projects?")) return new JsonArray(Project(false));
            return new JsonObject { [sha1] = FileInfo(false, url.EndsWith("/update"),
                url.EndsWith("/update") && !sameInstalled ? "next-hash" : sha1) };
        };
        async Task<(LocalCompFile File, bool? Success)> Scan()
        {
            var entry = new LocalCompFile(path);
            var success = await Task.Run(() => LoadLocalCompSources(new() { entry }, CompType.Mod,
                new() { CompLoaderType.Forge }, "1.20.1", () => false, fake));
            return (entry, success);
        }
        var both = await Scan();
        Require(both.Success == true && both.File.UpdateFile is not null && !both.File.Comp.FromCurseForge,
            "Both-source scan did not preserve preferred Modrinth metadata");
        Require(both.File.UpdateFile.DownloadUrls.Count == 2 && both.File.changelogUrls.Count == 2,
            "Equivalent updates did not merge both CDN URLs and changelogs");
        var restored = new LocalCompFile(path);
        both.File.compLoaded = true;
        restored.FromJson(both.File.ToJson());
        Require(restored.CanUpdate && restored.UpdateFile.Hash == "next-hash" && restored.UpdateFile.DownloadUrls.Count == 2,
            "Dual-source update cache round trip lost metadata");
        report.Add("PASS dual-source merge, deterministic primary provider and cached update metadata");
        fail = "MR";
        var onlyCf = await Scan();
        Require(onlyCf.Success == false && onlyCf.File.Comp.FromCurseForge && onlyCf.File.UpdateFile.FromCurseForge,
            "Modrinth outage suppressed CurseForge updates or was cached as complete");
        fail = "CF";
        var onlyMr = await Scan();
        Require(onlyMr.Success == false && !onlyMr.File.UpdateFile.FromCurseForge,
            "CurseForge outage suppressed Modrinth updates");
        report.Add("PASS either provider may fail while the other still supplies an update; failed scans require retry");
        fail = "MR";
        cfVersion = "1.21.1";
        Require((await Scan()).File.UpdateFile is null, "Wrong Minecraft version offered");
        cfVersion = "1.20.1"; cfLoader = "Fabric";
        Require((await Scan()).File.UpdateFile is null, "Wrong loader offered");
        cfLoader = "Forge"; sameInstalled = true;
        Require((await Scan()).File.UpdateFile is null, "Identical installed hash offered as update");
        sameInstalled = false;
        report.Add("PASS CurseForge rejects incompatible Minecraft versions, wrong loaders and identical installed files");
        cfLoader = "Fabric"; indexLoader = 4;
        var quilt = new LocalCompFile(path);
        await Task.Run(() => LoadCurseForgeLocalInfo(new() { quilt }, CompType.Mod,
            new() { CompLoaderType.Fabric, CompLoaderType.Quilt }, "1.20.1", () => false, fake));
        Require(quilt.UpdateFile is not null, "Quilt instance did not accept its compatible Fabric update");
        var unknownLoader = new LocalCompFile(path);
        await Task.Run(() => LoadCurseForgeLocalInfo(new() { unknownLoader }, CompType.Mod,
            new() { CompLoaderType.Forge, CompLoaderType.Fabric }, "1.20.1", () => false, fake));
        Require(unknownLoader.Comp is not null && unknownLoader.UpdateFile is null, "Unknown loader scan guessed an upgrade");
        cfLoader = "Forge"; indexLoader = 1;
        report.Add("PASS Quilt/Fabric compatibility and conservative update selection for unknown instance loaders");
        fail = null;
        var old = FileInfo(true, true, "cf-different"); old["fileDate"] = "2024-03-01T00:00:00Z";
        var cf = new LocalCompFile(path) { Comp = new CompProject(Project(true)), compFile = new CompFile(FileInfo(true, false, sha1), CompType.Mod),
            UpdateFile = new CompFile(old, CompType.Mod), changelogUrls = new() { "cf-changelog" } };
        var merged = new LocalCompFile(path);
        MergeLocalCompSources(merged, both.File, cf);
        Require(merged.UpdateFile.Hash == "cf-different" && merged.changelogUrls.SequenceEqual(new[] { "cf-changelog" }),
            "Newer distinct CurseForge update did not replace older Modrinth update");
        report.Add("PASS newest distinct compatible release wins across providers");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var cancelled = 0;
        LocalCompMetadataRequest delayed = (url, method, body) =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Fixture gate timed out");
            return fake(url, method, body);
        };
        var untouched = new LocalCompFile(path);
        var scan = Task.Run(() => LoadLocalCompSources(new() { untouched }, CompType.Mod, new() { CompLoaderType.Forge },
            "1.20.1", () => Volatile.Read(ref cancelled) != 0, delayed));
        Require(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5))), "Cancelled scan never requested metadata");
        Interlocked.Exchange(ref cancelled, 1);
        Require(await scan is null, "Cancelled scan did not exit");
        release.Set();
        await Task.Delay(200);
        Require(untouched.Comp is null && untouched.compFile is null && untouched.UpdateFile is null, "Late cancelled responses mutated the active list");
        report.Add("PASS cancellation isolates late responses from the active mod list");

        var oldJson = await Task.Run(() => PCL.ModDownload.DlModRequest<JsonObject>("https://api.curseforge.com/v1/mods/248787/files/4605078"));
        var currentJson = await Task.Run(() => PCL.ModDownload.DlModRequest<JsonObject>("https://api.curseforge.com/v1/mods/248787/files/4770828"));
        var installed = new CompFile((JsonObject)oldJson["data"]!, CompType.Mod);
        var latest = new CompFile((JsonObject)currentJson["data"]!, CompType.Mod);
        async Task<string> Download(CompFile file, string name)
        {
            var target = Path.Combine(output, name);
            var download = new LoaderDownload("Installed mod update QA fixture", new() { file.ToNetFile(target) });
            download.Start();
            var until = DateTime.UtcNow.AddSeconds(60);
            while (download.State == ModBase.LoadState.Loading && DateTime.UtcNow < until) await Task.Delay(100);
            Require(download.State == ModBase.LoadState.Finished && new ModBase.FileChecker(hash: file.Hash).Check(target) is null,
                "Fixture download or hash verification failed: " + download.Error);
            return target;
        }
        var oldPath = await Download(installed, "appleskin-2.5.0.jar");
        var liveCf = new LocalCompFile(oldPath);
        await Task.Run(() => LoadCurseForgeLocalInfo(new() { liveCf }, CompType.Mod,
            new() { CompLoaderType.Forge }, "1.20.1", () => false));
        Require(liveCf.Comp?.Id == "248787" && liveCf.compFile.Id == "4605078" && liveCf.UpdateFile?.Id == "4770828",
            "Live CurseForge fingerprint/update chain did not find AppleSkin 2.5.0 -> 2.5.1");
        var disabled = oldPath + ".disabled";
        File.Copy(oldPath, disabled, true);
        var entries = new List<LocalCompFile> { new(oldPath), new(disabled) };
        var completed = await Task.Run(() => LoadLocalCompSources(entries, CompType.Mod, new() { CompLoaderType.Forge }, "1.20.1", () => false));
        Require(completed == true && entries.All(e => e.UpdateFile?.Hash == latest.Hash && e.changelogUrls.Any(u => u.Contains("curseforge.com"))),
            "Live dual-source or disabled-mod update detection failed");
        var newPath = await Download(entries[0].UpdateFile, "appleskin-2.5.1.jar");
        var upToDate = new LocalCompFile(newPath);
        await Task.Run(() => LoadCurseForgeLocalInfo(new() { upToDate }, CompType.Mod,
            new() { CompLoaderType.Forge }, "1.20.1", () => false));
        Require(upToDate.Comp?.Id == "248787" && upToDate.UpdateFile is null, "Latest version received a false upgrade");
        report.Add("PASS live CurseForge fingerprint matching: AppleSkin Forge 1.20.1 2.5.0 -> 2.5.1; latest version has no update");
        report.Add("PASS real dual-source scan of enabled/disabled mods and download of selected update with hash verification");
    }
}
