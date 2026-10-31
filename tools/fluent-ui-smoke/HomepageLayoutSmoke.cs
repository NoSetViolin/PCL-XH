// Included only in the isolated diagnostic build by Inject.targets.
using System.Diagnostics;
using System.IO;
using System.Windows;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.UI.Theme;

namespace PCL;

internal static class HomepageLayoutSmoke
{
    public static async Task Run(FormMain form, string root, List<string> report)
    {
        form.PageChange(FormMain.PageType.Launch);
        await Task.Delay(900);
        var page = ModMain.frmLaunchRight!;
        if (page.RecentCard.UseAnimation || page.TasksCard.UseAnimation)
            throw new Exception("Homepage summary cards still animate parent-assigned heights.");
        var raw = new List<string> { "scenario,elapsedMs,recentHeight,tasksHeight,customTop" };
        var text = page.RecentEmpty.Text;
        try
        {
            // Exercise the old height animation on the actual production controls,
            // using a deterministic content change instead of news/network timing.
            form.Width = 1280; form.Height = 800;
            page.RecentCard.UseAnimation = page.TasksCard.UseAnimation = true;
            ResetHeights();
            await Task.Delay(300);
            page.RecentEmpty.Text = text + "\nAdditional instance details\nAnother detail line";
            var baseline = await Sample("old-height-animation");
            report.Add($"INFO old behavior after content growth: height/position range={baseline:F3} DIP");
            if (SystemParameters.ClientAreaAnimation && baseline <= .1)
                throw new Exception("Positive control did not exercise the old height animation.");
            var idleBaseline = await Sample("old-idle");
            report.Add($"INFO old behavior settled-idle range={idleBaseline:F3} DIP; isolated configuration may not reproduce sustained oscillation");

            page.RecentCard.UseAnimation = page.TasksCard.UseAnimation = false;
            page.RecentEmpty.Text = text;
            ResetHeights();
            foreach (var width in new[] { 810d, 1280d })
            {
                form.Width = width;
                await Stable($"width-{width}");
                // Change natural height after loading, then return to the original
                // height; both cards must remain equal without moving news repeatedly.
                page.RecentEmpty.Text = text + "\nAdditional instance details\nAnother detail line";
                await Stable($"content-growth-{width}");
                page.RecentEmpty.Text = text;
                await Stable($"content-shrink-{width}");
                form.PageChange(FormMain.PageType.Setup);
                await Task.Delay(500);
                form.PageChange(FormMain.PageType.Launch);
                await Stable($"reenter-{width}");
            }
            foreach (var language in new[] { "zh-CN", "en-US" })
            foreach (var dark in new[] { false, true })
            foreach (var width in new[] { 810d, 1280d })
            {
                LocalizationService.Apply(language,save:false);
                Config.Preference.Theme.ColorMode = dark ? ColorMode.Dark : ColorMode.Light;
                ThemeService.RefreshColorMode(); FluentTheme.Refresh();
                form.Width = width;
                var scenario = $"{language}-{(dark ? "dark" : "light")}-{width}";
                await Stable(scenario);
                LiquidGlassSmoke.Capture(form,Path.Combine(root,"screenshots",scenario+".png"));
            }
        }
        finally
        {
            page.RecentCard.UseAnimation = page.TasksCard.UseAnimation = false;
            page.RecentEmpty.Text = text;
            ResetHeights();
            File.WriteAllLines(Path.Combine(root,"homepage-layout-samples.csv"),raw);
        }

        void ResetHeights()
        {
            page.RecentCard.TriggerForceResize();
            page.TasksCard.TriggerForceResize();
        }

        async Task Stable(string scenario)
        {
            await Task.Delay(900); // Exclude the normal page entrance animation.
            var range = await Sample(scenario);
            if (range > .1 || Math.Abs(page.RecentCard.ActualHeight-page.TasksCard.ActualHeight) > .1)
                throw new Exception($"Unstable or unequal summary cards: {scenario}, range={range:F3} DIP");
            report.Add($"PASS {scenario}: height/position range={range:F3} DIP, equal card heights");
        }

        async Task<double> Sample(string scenario)
        {
            var values = new List<(double Recent,double Tasks,double Top)>();
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 1200)
            {
                var top = page.PanCustom.TransformToAncestor(page).Transform(new Point()).Y;
                values.Add((page.RecentCard.ActualHeight,page.TasksCard.ActualHeight,top));
                raw.Add(FormattableString.Invariant($"{scenario},{clock.Elapsed.TotalMilliseconds:F3},{page.RecentCard.ActualHeight:F4},{page.TasksCard.ActualHeight:F4},{top:F4}"));
                await Task.Delay(20);
            }
            return new[] { values.Max(v=>v.Recent)-values.Min(v=>v.Recent),
                values.Max(v=>v.Tasks)-values.Min(v=>v.Tasks),
                values.Max(v=>v.Top)-values.Min(v=>v.Top) }.Max();
        }
    }
}
