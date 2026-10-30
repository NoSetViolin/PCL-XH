using System.Text.Json;
using System.Text.Json.Nodes;
using PCL.Core.Utils.Exts;
using static PCL.ModComp;

namespace PCL;

public static partial class ModLocalComp
{
    internal delegate JsonNode LocalCompMetadataRequest(string url, string method, string? body);

    private static JsonNode RequestLocalCompMetadata(string url, string method, string? body) =>
        method == "GET" ? ModDownload.DlModRequest<JsonNode>(url) :
            ModDownload.DlModRequest<JsonNode>(url, method, body!, "application/json");

    // Null means cancelled; false means at least one source failed and the cache
    // must be retried. Successful results from the other source are still applied.
    internal static bool? LoadLocalCompSources(List<LocalCompFile> mods, CompType type,
        List<CompLoaderType> loaders, string mcVersion, Func<bool> isAborted,
        LocalCompMetadataRequest? request = null)
    {
        var modrinth = mods.Select(m => new LocalCompFile(m.path)).ToList();
        var curseForge = mods.Select(m => new LocalCompFile(m.path)).ToList();
        var failed = 0;
        var ended = 0;
        void StartSource(string source, Action fetch)
        {
            ModBase.RunInNewThread(() =>
            {
                try { fetch(); }
                catch (Exception ex)
                {
                    Interlocked.Exchange(ref failed, 1);
                    ModBase.Log(ex, $"从 {source} 获取本地 Mod 信息失败");
                }
                finally { Interlocked.Increment(ref ended); }
            }, "Mod List Detail Loader " + source);
        }
        // Providers never mutate the active list before both snapshots are ready.
        StartSource("Modrinth", () => LoadModrinthLocalInfo(modrinth, type, loaders, mcVersion, isAborted, request));
        StartSource("CurseForge", () => LoadCurseForgeLocalInfo(curseForge, type, loaders, mcVersion, isAborted, request));
        while (Volatile.Read(ref ended) < 2)
        {
            if (isAborted()) return null;
            Thread.Sleep(10);
        }
        if (isAborted()) return null;
        for (var i = 0; i < mods.Count; i++)
        {
            if (isAborted()) return null;
            MergeLocalCompSources(mods[i], modrinth[i], curseForge[i]);
        }
        return Volatile.Read(ref failed) == 0;
    }

    internal static bool IsCompatibleLocalUpdate(CompFile file, CompType type,
        IReadOnlyCollection<CompLoaderType> loaders, string mcVersion) =>
        file.Available && file.Type == type &&
        file.RawGameVersions.Contains(mcVersion.ToLowerInvariant()) &&
        (type != CompType.Mod || file.ModLoaders.Intersect(loaders).Any());

    private static bool HasSameFileHash(CompFile first, CompFile second) =>
        !string.IsNullOrWhiteSpace(first.Hash) &&
        string.Equals(first.Hash, second.Hash, StringComparison.OrdinalIgnoreCase);

    private static void SetLocalUpdate(LocalCompFile entry, CompFile update, string changelog)
    {
        if (entry.compFile is null || update.ReleaseDate <= entry.compFile.ReleaseDate ||
            HasSameFileHash(entry.compFile, update)) return;
        if (entry.UpdateFile is not null && HasSameFileHash(entry.UpdateFile, update))
        {
            entry.UpdateFile.DownloadUrls = entry.UpdateFile.DownloadUrls.Union(update.DownloadUrls).ToList();
            if (!entry.changelogUrls.Contains(changelog)) entry.changelogUrls.Add(changelog);
        }
        else if (entry.UpdateFile is null || update.ReleaseDate > entry.UpdateFile.ReleaseDate ||
                 (update.ReleaseDate == entry.UpdateFile.ReleaseDate && update.Status < entry.UpdateFile.Status))
        {
            entry.changelogUrls = new() { changelog };
            entry.UpdateFile = update;
        }
    }

    internal static void MergeLocalCompSources(LocalCompFile target, LocalCompFile modrinth, LocalCompFile curseForge)
    {
        if (modrinth.compFile is null && curseForge.compFile is null) return;
        var candidates = new[] { modrinth, curseForge }.Where(m => m.UpdateFile is not null)
            .OrderByDescending(m => m.UpdateFile.ReleaseDate).ToList();
        target.Comp = modrinth.Comp ?? curseForge.Comp ?? target.Comp;
        target.compFile = modrinth.compFile ?? curseForge.compFile;
        target.changelogUrls = candidates.Count == 0 ? new() : candidates
            .Where(m => ReferenceEquals(m, candidates[0]) || HasSameFileHash(m.UpdateFile, candidates[0].UpdateFile))
            .SelectMany(m => m.changelogUrls).Distinct().ToList();
        if (candidates.Count > 0)
        {
            var selected = candidates[0].UpdateFile;
            selected.DownloadUrls = candidates
                .Where(m => ReferenceEquals(m.UpdateFile, selected) || HasSameFileHash(m.UpdateFile, selected))
                .SelectMany(m => m.UpdateFile.DownloadUrls).Distinct().ToList();
            target.UpdateFile = selected;
        }
        else target.UpdateFile = null!;
    }

    internal static void LoadModrinthLocalInfo(List<LocalCompFile> mods, CompType type,
        List<CompLoaderType> loaders, string mcVersion, Func<bool> isAborted,
        LocalCompMetadataRequest? request = null)
    {
        request ??= RequestLocalCompMetadata;
        foreach (var batch in mods.Chunk(500))
        {
            if (isAborted()) return;
            var hashes = batch.Select(m => m.ModrinthHash).Distinct().ToArray();
            var matches = request("https://api.modrinth.com/v2/version_files", "POST",
                JsonSerializer.Serialize(new { hashes, algorithm = "sha1" })).AsObject();
            if (isAborted()) return;
            var mapping = new Dictionary<string, List<LocalCompFile>>();
            foreach (var entry in batch)
            {
                if (matches[entry.ModrinthHash] is not JsonObject version) continue;
                var matchedFile = version["files"]!.AsArray().FirstOrDefault(f =>
                    string.Equals(f?["hashes"]?["sha1"]?.ToString(), entry.ModrinthHash, StringComparison.OrdinalIgnoreCase));
                if (matchedFile is null) continue;
                var installed = (JsonObject)version.DeepClone();
                installed["files"] = new JsonArray(matchedFile.DeepClone());
                entry.compFile = new CompFile(installed, type);
                var projectId = version["project_id"]!.ToString();
                if (!mapping.TryGetValue(projectId, out var entries)) mapping[projectId] = entries = new();
                entries.Add(entry);
            }
            if (mapping.Count == 0) continue;
            var ids = Uri.EscapeDataString(JsonSerializer.Serialize(mapping.Keys));
            var projects = request("https://api.modrinth.com/v2/projects?ids=" + ids, "GET", null).AsArray();
            if (isAborted()) return;
            foreach (var json in projects)
            {
                var project = new CompProject((JsonObject)json!);
                if (mapping.TryGetValue(project.Id, out var entries))
                    foreach (var entry in entries) entry.Comp = project;
            }
            var targetLoaders = type == CompType.DataPack ? new[] { "datapack" } :
                loaders.Select(l => l.ToString().ToLowerInvariant()).ToArray();
            var updates = request("https://api.modrinth.com/v2/version_files/update", "POST",
                JsonSerializer.Serialize(new { hashes, algorithm = "sha1", loaders = targetLoaders,
                    game_versions = new[] { mcVersion } })).AsObject();
            if (isAborted()) return;
            foreach (var entry in batch)
            {
                if (entry.compFile is null || updates[entry.ModrinthHash] is not JsonObject json) continue;
                var update = new CompFile(json, type);
                if (update.ProjectId != entry.compFile.ProjectId || !IsCompatibleLocalUpdate(update, type, loaders, mcVersion)) continue;
                SetLocalUpdate(entry, update, $"https://modrinth.com/{type.ToString().ToLowerInvariant()}/{update.ProjectId}/changelog?g={Uri.EscapeDataString(mcVersion)}");
            }
        }
        ModBase.Log("[Mod] 从 Modrinth 获取本地 Mod 信息结束");
    }

    internal static void LoadCurseForgeLocalInfo(List<LocalCompFile> mods, CompType type,
        List<CompLoaderType> loaders, string mcVersion, Func<bool> isAborted,
        LocalCompMetadataRequest? request = null)
    {
        request ??= RequestLocalCompMetadata;
        foreach (var batch in mods.Chunk(500))
        {
            if (isAborted()) return;
            var byFingerprint = batch.GroupBy(m => m.CurseForgeHash).ToDictionary(g => g.Key, g => g.ToList());
            if (isAborted()) return;
            var matches = request("https://api.curseforge.com/v1/fingerprints/432", "POST",
                JsonSerializer.Serialize(new { fingerprints = byFingerprint.Keys })).AsObject()["data"]!["exactMatches"]!.AsArray();
            if (isAborted()) return;
            var mapping = new Dictionary<string, List<LocalCompFile>>();
            foreach (var match in matches)
            {
                var json = (JsonObject)match!["file"]!;
                if (!byFingerprint.TryGetValue(json["fileFingerprint"]!.GetValue<uint>(), out var entries)) continue;
                var file = new CompFile(json, type);
                if (file.ProjectId != match["id"]!.ToString()) continue;
                if (!mapping.TryGetValue(file.ProjectId, out var projectEntries)) mapping[file.ProjectId] = projectEntries = new();
                foreach (var entry in entries)
                {
                    entry.compFile = file;
                    projectEntries.Add(entry);
                }
            }
            ModBase.Log($"[Mod] CurseForge 精确匹配 {mapping.Count} 个本地资源工程");
            if (mapping.Count == 0) continue;
            var projects = request("https://api.curseforge.com/v1/mods", "POST",
                JsonSerializer.Serialize(new { modIds = mapping.Keys.Select(int.Parse) }))["data"]!.AsArray();
            if (isAborted()) return;
            var fileProjects = new Dictionary<string, CompProject>();
            // A Quilt instance intentionally includes both Fabric and Quilt. Other
            // multi-loader lists indicate an unknown loader, so do not guess upgrades.
            var canCheckMod = loaders.Count == 1 || loaders.ToHashSet().SetEquals(new[] { CompLoaderType.Fabric, CompLoaderType.Quilt });
            foreach (var json in projects.OfType<JsonObject>())
            {
                if (json["isAvailable"]?.GetValue<bool>() == false) continue;
                var project = new CompProject(json);
                if (!mapping.TryGetValue(project.Id, out var entries)) continue;
                foreach (var entry in entries) entry.Comp = project;
                if (type == CompType.Mod && !canCheckMod) continue;
                foreach (var index in (json["latestFilesIndexes"] as JsonArray ?? new()))
                {
                    if (index?["gameVersion"]?.ToString() != mcVersion) continue;
                    if (type == CompType.Mod && (index["modLoader"] is null ||
                        !loaders.Contains((CompLoaderType)index["modLoader"]!.GetValue<int>()))) continue;
                    fileProjects[index!["fileId"]!.ToString()] = project;
                }
            }
            foreach (var ids in fileProjects.Keys.Chunk(500))
            {
                if (isAborted()) return;
                var files = request("https://api.curseforge.com/v1/mods/files", "POST",
                    JsonSerializer.Serialize(new { fileIds = ids.Select(int.Parse) }))["data"]!.AsArray();
                if (isAborted()) return;
                foreach (var json in files.OfType<JsonObject>())
                {
                    if (json["isAvailable"]?.GetValue<bool>() == false) continue;
                    var file = new CompFile(json, type);
                    if (!fileProjects.TryGetValue(file.Id, out var project) || file.ProjectId != project.Id ||
                        !IsCompatibleLocalUpdate(file, type, loaders, mcVersion)) continue;
                    var category = type switch { CompType.ResourcePack => "texture-packs", CompType.Shader => "shaders",
                        CompType.DataPack => "data-packs", _ => "mc-mods" };
                    var changelog = $"https://www.curseforge.com/minecraft/{category}/{project.Slug}/files/{file.Id}";
                    foreach (var entry in mapping[project.Id]) SetLocalUpdate(entry, file, changelog);
                }
            }
        }
        ModBase.Log("[Mod] 从 CurseForge 获取本地 Mod 信息结束");
    }
}
