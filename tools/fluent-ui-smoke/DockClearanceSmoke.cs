// Source-XAML geometry fixtures only; compiled by Inject.targets, never in a normal build.
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.UI.Theme;

namespace PCL;

internal static class DockClearanceSmoke
{
    private static IEnumerable<FrameworkElement> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement element) yield return element;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    // Load production layout without page code-behind: no game, log process, or online loader.
    private static MyPageRight Load(string path)
    {
        var xml = XDocument.Load(path);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        xml.Root!.Attribute(x + "Class")!.Remove();
        foreach (var element in xml.Descendants().Where(e => e.Name.NamespaceName == "clr-namespace:PCL"))
            element.Name = XName.Get(element.Name.LocalName, "clr-namespace:PCL;assembly=Plain Craft Launcher 2");
        xml.Root.SetAttributeValue(XNamespace.Xmlns + "local", "clr-namespace:PCL;assembly=Plain Craft Launcher 2");
        foreach (var attribute in xml.Descendants().Attributes().Where(a =>
                     a.Name.LocalName is "Click" or "Change" or "Check" or "Loaded" or "Unloaded").ToArray())
            attribute.Remove();
        return (MyPageRight)XamlReader.Parse(xml.ToString());
    }

    private static Rect Bounds(FrameworkElement element, Visual relative) =>
        element.TransformToAncestor(relative).TransformBounds(new Rect(element.RenderSize));

    private static void Check(bool pass, string message)
    {
        if (!pass) throw new Exception("Dock clearance: " + message);
    }

    internal static async Task Run(FormMain form, string output, List<string> report)
    {
        var source = Environment.GetEnvironmentVariable("PCL_LAYOUT_SOURCE_ROOT")
            ?? throw new Exception("Set PCL_LAYOUT_SOURCE_ROOT to the repository root");
        var fixtures = new[]
        {
            ("PageInstance/PageInstanceExport.xaml", "BtnExport"),
            ("PageInstance/PageInstanceCompResource.xaml", "CardSelect"),
            ("PageInstance/PageInstanceSaves/PageInstanceSavesDatapack.xaml", "CardSelect"),
            ("PageDownload/PageDownloadCompFavorites.xaml", "CardSelect"),
            ("PageLogRight.xaml", "CardOperation")
        };
        form.PageChange(FormMain.PageType.Setup);
        await Task.Delay(600);
        var previous = form.PanMainRight.Child;
        try
        {
            foreach (var language in new[] { "zh-CN", "en-US" })
            foreach (var dark in new[] { false, true })
            foreach (var width in new[] { 810d, 1280d })
            {
                LocalizationService.Apply(language, save: false);
                Config.Preference.Theme.ColorMode = dark ? ColorMode.Dark : ColorMode.Light;
                ThemeService.RefreshColorMode(); FluentTheme.Refresh();
                form.Width = width; form.Height = width == 810 ? 470 : 800;
                foreach (var (file, target) in fixtures)
                {
                    var page = Load(Path.Combine(source, "Plain Craft Launcher 2/Pages", file));
                    form.PanMainRight.Child = page;
                    var action = (FrameworkElement)page.FindName(target);
                    action.Visibility = Visibility.Visible; action.Opacity = 1;
                    if (page.FindName("TransSelect") is TranslateTransform translation) translation.Y = -25;
                    if (page.FindName("LabSelect") is TextBlock label) label.Text = "3 selected";
                    // Hide fixture empty/loading overlays, making the actual action hit areas available.
                    foreach (var name in new[] { "PanLoad", "PanEmpty", "CardNoContent", "PanSchematicUnavailable" })
                        if (page.FindName(name) is FrameworkElement empty) empty.Visibility = Visibility.Collapsed;
                    await Task.Delay(150); form.UpdateLayout();
                    var dock = Bounds(form.PrimaryNavigation, form);
                    var area = Bounds(action, form);
                    Check(area.Bottom <= dock.Top - 1, $"{file} action overlaps dock: {area}, dock={dock}");
                    Check(area.Top >= Bounds(page, form).Top, $"{file} action clipped above page");
                    var buttons = Descendants(action).Prepend(action).Where(e => e.IsVisible &&
                        e is MyButton or MyIconTextButton or MyCheckBox or MySlider).ToArray();
                    Check(buttons.Length > 0, file + " missing actions");
                    foreach (var button in buttons)
                    {
                        var rect = Bounds(button, form);
                        Check(rect.Width > 0 && rect.Height > 0 && Bounds(page, form).Contains(rect),
                            $"{file}/{button.Name} outside page: {rect}");
                        foreach (var fraction in new[] { .2, .5, .8 })
                        {
                            var point = button.TranslatePoint(new Point(button.ActualWidth * fraction, button.ActualHeight / 2), form);
                            var hit = form.InputHitTest(point) as DependencyObject;
                            while (hit is not null && hit != button) hit = VisualTreeHelper.GetParent(hit);
                            Check(hit == button, $"{file}/{button.Name} hit area blocked at {fraction}");
                        }
                        if (button is MyButton textButton)
                            Check(textButton.LabText.DesiredSize.Width <= button.ActualWidth + 1,
                                file + " clipped button label");
                    }
                    if (target == "BtnExport")
                    {
                        var version = (FrameworkElement)page.FindName("TextExportVersion");
                        var versionBounds = Bounds(version, form);
                        Check(area.Left >= versionBounds.Right + 8 && Math.Abs(area.Top + area.Height / 2 - versionBounds.Top - versionBounds.Height / 2) < 2,
                            "Export must be right of version input on the same row");
                        var scroll = (MyScrollViewer)page.FindName("PanBack");
                        scroll.ScrollToEnd(); form.UpdateLayout(); await Task.Delay(80);
                        Check(Bounds(action, form) == area, "Export header moved when scrolling");
                    }
                    LiquidGlassSmoke.Capture(form, Path.Combine(output, $"dock-{Path.GetFileNameWithoutExtension(file)}-{language}-{dark}-{width}.png"));
                    report.Add($"PASS {file} {language} dark={dark} {width}: {buttons.Length} action hit areas, page bounds, dock clearance {dock.Top - area.Bottom:0.0} DIP");
                }
            }
            report.Add("INFO Source-XAML fixtures on real launcher content host/dock; no real game, export, deletion, or hardware mouse action performed.");
        }
        finally { form.PanMainRight.Child = previous; }
    }
}
