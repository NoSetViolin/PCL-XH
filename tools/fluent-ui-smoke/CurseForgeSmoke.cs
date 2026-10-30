using System.IO;
using System.Net.Http;
using System.Windows;
using PCL.Network;
using PCL.Network.Loaders;
using static PCL.ModComp;

namespace PCL;

// Only compiled by Inject.targets. All downloaded fixtures stay in the QA root.
internal static class CurseForgeSmoke
{
    public static async Task Run(FormMain form, string output, List<string> report)
    {
        void Require(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        foreach (var source in new[] { CompSourceType.CurseForge, CompSourceType.Modrinth, CompSourceType.Any })
        {
            var request = new CompProjectRequest(CompType.Mod, new(), 40) { source = source };
            Require((request.GetCurseForgeAddress() is not null) == source.HasFlag(CompSourceType.CurseForge), "Wrong CurseForge source selection");
            Require((request.GetModrinthAddress() is not null) == source.HasFlag(CompSourceType.Modrinth), "Wrong Modrinth source selection");
            request.storage.curseForgeTotal = request.storage.curseForgeOffset = 40;
            request.storage.modrinthTotal = request.storage.modrinthOffset = 40;
            Require(!request.CanContinue, "Exhausted search can continue");
        }
        var exclusive = new CompProjectRequest(CompType.Mod, new(), 40) { tag = "/utility" };
        Require(exclusive.GetCurseForgeAddress() is null && exclusive.GetModrinthAddress() is not null, "Modrinth-only category leaked to CurseForge");
        exclusive = new(CompType.Mod, new(), 40) { tag = "406/" };
        Require(exclusive.GetCurseForgeAddress() is not null && exclusive.GetModrinthAddress() is null, "CurseForge-only category leaked to Modrinth");
        foreach (var sort in Enum.GetValues<CompSortType>())
        {
            var request = new CompProjectRequest(CompType.Mod, new(), 40)
            {
                tag = "406/worldgen", gameVersion = "1.20.1", modLoader = CompLoaderType.Forge,
                searchText = "a&b", sort = sort
            };
            var address = request.GetCurseForgeAddress();
            Require(address.Contains("categoryId=406") && address.Contains("gameVersion=1.20.1") &&
                address.Contains("modLoaderType=1") && address.Contains("searchFilter=a%26b"), "CurseForge filters not encoded");
        }
        foreach (var url in new[] { "https://api.curseforge.com/v1/mods", "https://api.modrinth.com/v2/search",
                     "https://api.curseforge.com.example.org/v1/mods", "https://edge.forgecdn.net/test", "http://api.curseforge.com/v1/mods" })
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, url);
            var signed = message;
            RequestSigning.SecretHeadersSign(url, ref signed);
            Require(message.Headers.Contains("x-api-key") == (url == "https://api.curseforge.com/v1/mods"), "API key attached to incorrect host or protocol");
        }
        Require(TryExtractSingleResourceLink("https://www.curseforge.com/minecraft/mc-mods/appleskin") is not null, "CurseForge link not recognized");
        Require(TryExtractSingleResourceLink("https://evilcurseforge.com/minecraft/mc-mods/appleskin") is null, "Lookalike domain recognized");
        report.Add("PASS source selection, exhaustion, provider-only categories, filters, URL recognition and API key host scope");

        async Task<CompProjectRequest> Search(CompType type, CompSourceType source, string text = "", CompProjectStorage? storage = null, int count = 40)
        {
            var request = new CompProjectRequest(type, storage ?? new(), count) { source = source, searchText = text };
            var task = new ModLoader.LoaderTask<CompProjectRequest, int>("Resource QA " + type + " " + source, CompProjectsGet);
            task.Start(request);
            var deadline = DateTime.UtcNow.AddSeconds(70);
            while (task.State == ModBase.LoadState.Loading && DateTime.UtcNow < deadline) await Task.Delay(100);
            Require(task.State == ModBase.LoadState.Finished, $"Search failed: {type} {source}: {task.Error}");
            Require(request.storage.results.Count > 0, "Search returned no projects");
            return request;
        }

        foreach (var type in new[] { CompType.Mod, CompType.ModPack, CompType.ResourcePack, CompType.Shader, CompType.DataPack, CompType.World })
        {
            var request = await Search(type, CompSourceType.CurseForge);
            Require(request.storage.results.All(p => p.FromCurseForge && p.Type == type), "Invalid CurseForge project type");
            report.Add($"PASS live CurseForge {type}: {request.storage.results.Count} projects");
        }
        var onlyModrinth = await Search(CompType.Mod, CompSourceType.Modrinth, "appleskin");
        Require(onlyModrinth.storage.results.All(p => !p.FromCurseForge), "Modrinth-only selection includes CurseForge");
        var mixed = await Search(CompType.Mod, CompSourceType.Any);
        Require(mixed.storage.curseForgeOffset > 0 && mixed.storage.modrinthOffset > 0, "Both sources were not fetched");
        var firstOffset = mixed.storage.curseForgeOffset;
        var next = await Search(CompType.Mod, CompSourceType.CurseForge, storage: new(), count: 40);
        var firstIds = next.storage.results.Select(p => p.Id).ToHashSet();
        var nextPage = await Search(CompType.Mod, CompSourceType.CurseForge, storage: next.storage, count: 80);
        Require(nextPage.storage.curseForgeOffset > firstOffset && nextPage.storage.results.Count > firstIds.Count, "CurseForge pagination did not advance");
        report.Add("PASS live Modrinth-only, mixed-source search and CurseForge second-page retrieval");

        var projectId = await Task.Run(() => ResolveLinkToProjectId("https://www.curseforge.com/minecraft/mc-mods/appleskin"));
        Require(projectId == "248787", "CurseForge slug did not resolve to AppleSkin");
        var mrId = onlyModrinth.storage.results[0].Id;
        var projects = await CompRequest.GetCompProjectsByIdsAsync(new() { projectId, mrId, "531761" });
        Require(projects.Any(p => p.Id == projectId && p.FromCurseForge) && projects.Any(p => p.Id == mrId && !p.FromCurseForge) &&
                projects.Any(p => p.Id == "531761" && p.FromCurseForge), "Mixed project/dependency metadata batch failed");
        var expected = await Task.Run(() => ModDownload.DlModRequest<System.Text.Json.Nodes.JsonObject>(
            $"https://api.curseforge.com/v1/mods/{projectId}/files?pageSize=1"));
        var total = expected["pagination"]!["totalCount"]!.GetValue<int>();
        var files = await Task.Run(() => CompFilesGet(projectId, true));
        Require(total > 50 && files.Count == total && files.All(f => f.FromCurseForge), "CurseForge file pagination incomplete");
        report.Add($"PASS CurseForge link, mixed favorite/dependency lookup and all {files.Count} files across multiple API pages");

        var fixture = files.First(f => f.Available && !string.IsNullOrEmpty(f.Hash));
        var target = Path.Combine(output, "curseforge-fixture.jar");
        var download = new LoaderDownload("CurseForge QA download", new() { fixture.ToNetFile(target) });
        download.Start();
        var downloadDeadline = DateTime.UtcNow.AddSeconds(60);
        while (download.State == ModBase.LoadState.Loading && DateTime.UtcNow < downloadDeadline) await Task.Delay(100);
        Require(download.State == ModBase.LoadState.Finished && File.Exists(target), "CurseForge download failed: " + download.Error);
        Require(new ModBase.FileChecker(hash: fixture.Hash).Check(target) is null, "Downloaded fixture hash mismatch");
        report.Add($"PASS real CurseForge CDN download through LoaderDownload with hash verification: {new FileInfo(target).Length} bytes");

        form.PageChange(FormMain.PageType.Download, FormMain.PageSubType.DownloadMod);
        await Task.Delay(900);
        foreach (var width in new[] { 810d, 1280d })
        {
            form.Width = width;
            form.Height = width == 810 ? 470 : 800;
            await Task.Delay(600);
            form.UpdateLayout();
            var page = ((PageDownloadMod)form.PanMainRight.Child).Content;
            page.FilterOptions.IsExpanded = true;
            page.ComboSearchSource.SelectedIndex = 1;
            typeof(PageComp).GetMethod("StartNewSearch", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(page, null);
            var uiDeadline = DateTime.UtcNow.AddSeconds(40);
            while (page.loader.State == ModBase.LoadState.Loading && DateTime.UtcNow < uiDeadline) await Task.Delay(100);
            Require(page.loader.State == ModBase.LoadState.Finished && page.storage.results.All(p => p.FromCurseForge), "CurseForge UI search did not load the selected source");
            await Task.Delay(600);
            form.UpdateLayout();
            var inputMethod = typeof(PageComp).GetMethod("LoaderInput", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var request = (CompProjectRequest)inputMethod.Invoke(page, null)!;
            Require(request.source == CompSourceType.CurseForge, "UI selector not used by loader");
            Require(page.ComboSearchSource.ActualWidth > 100 && page.ComboSearchSource.IsVisible, "Source selector not visible");
            var bounds = page.ComboSearchSource.TransformToAncestor(form).TransformBounds(new Rect(page.ComboSearchSource.RenderSize));
            Require(bounds.Left >= 0 && bounds.Right <= form.ActualWidth && bounds.Bottom <= form.ActualHeight, "Source selector outside window");
            LiquidGlassSmoke.Capture(form, Path.Combine(output, $"curseforge-selector-{width}.png"));
        }
        report.Add("PASS visible source selector and live CurseForge UI search at 810 and 1280 widths");
        form.PageChange(FormMain.PageType.Download, FormMain.PageSubType.DownloadWorld);
        await Task.Delay(800);
        Require(form.PanMainRight.Child is PageDownloadWorld, "World navigation redirected away");
        var world = ((PageDownloadWorld)form.PanMainRight.Child).Content;
        Require(world.ComboSearchSource.SelectedIndex == 1 && !world.ComboSearchSource.IsEnabled, "World source is not fixed to CurseForge");
        Require(world.ComboSearchTag.Items.Count > 1, "World categories not restored");
        world.FilterOptions.IsExpanded = true;
        await Task.Delay(600);
        LiquidGlassSmoke.Capture(form, Path.Combine(output, "curseforge-world.png"));
        report.Add("PASS restored world navigation, CurseForge-only world selector and categories");
    }
}
