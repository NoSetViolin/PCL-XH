// Compiled only into the isolated QA executable via Inject.targets.
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.UI.Theme;

namespace PCL;

internal static class CardInteractionSmoke
{
    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception("Card interaction: " + message);
    }

    internal static async Task Run(FormMain form, string output, List<string> report)
    {
        var scroll = new MyScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var panel = new StackPanel { Margin = new Thickness(25) };
        scroll.Content = panel;
        var window = new Window { Owner = form, Content = scroll, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        window.Show();
        try
        {
            foreach (var language in new[] { "zh-CN", "en-US" })
            foreach (var dark in new[] { false, true })
            foreach (var width in new[] { 810d, 1280d })
            {
                LocalizationService.Apply(language, save: false);
                Config.Preference.Theme.ColorMode = dark ? ColorMode.Dark : ColorMode.Light;
                ThemeService.RefreshColorMode(); FluentTheme.Refresh();
                window.Width = width; window.Height = width == 810 ? 470 : 800;
                panel.Children.Clear(); scroll.ScrollToHome();
                var installs = 0;
                var cards = new List<MyCard>();
                foreach (var loader in new[] { "Fabric", "Forge", "NeoForge" })
                {
                    var stack = new StackPanel { Margin = new Thickness(20, MyCard.SwapedHeight, 18, 0), Tag = true };
                    var card = new MyCard { Title = loader + " 1.21.1", Margin = new Thickness(0,0,0,15),
                        SwapControl = stack, InstallMethod = target =>
                        {
                            installs++;
                            target.Children.Add(new MyListItem { Title = "Jade 15.10.3", Height = 42 });
                            target.Children.Add(new MyListItem { Title = "Jade 15.10.2", Height = 42 });
                        } };
                    card.Children.Add(stack); card.IsSwapped = true;
                    panel.Children.Add(card); cards.Add(card);
                }
                await Task.Delay(100); window.UpdateLayout();
                foreach (var card in cards)
                {
                    // Reproduce the old failure by removing the card's hit-test surface.
                    var brush = card.Background; card.Background = null;
                    await Task.Delay(60);
                    var missingHit = card.InputHitTest(new Point(80,20));
                    Check(missingHit is null, "negative control did not reproduce missing header input: " + missingHit?.GetType().FullName);
                    card.Background = brush;
                    await Task.Delay(60);
                    foreach (var x in new[] { 5d, 30d, card.ActualWidth / 2, card.ActualWidth - 20 })
                        Check(card.InputHitTest(new Point(x,20)) is not null, "unreachable header at " + x);
                    var target = (UIElement)card.InputHitTest(new Point(80,20))!;
                    target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,0,MouseButton.Left)
                        { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
                    target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,0,MouseButton.Left)
                        { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
                    window.UpdateLayout(); await Task.Delay(200); window.UpdateLayout();
                    Check(!card.IsSwapped && card.ActualHeight > MyCard.SwapedHeight + 42,
                        "routed header click failed to expand files");
                    var stack = (StackPanel)card.SwapControl;
                    var row = stack.Children.OfType<MyListItem>().First();
                    var hit = card.InputHitTest(row.TranslatePoint(new Point(30,20),card)) as DependencyObject;
                    while (hit is not null && hit != row) hit = VisualTreeHelper.GetParent(hit);
                    Check(hit == row, "expanded file item is not clickable");
                }
                Check(installs == 3, "lazy file list was not installed exactly once");
                LiquidGlassSmoke.Capture(window, Path.Combine(output,$"card-{language}-{dark}-{width}.png"));
                foreach (var card in cards)
                {
                    card.IsSwapped = true; window.UpdateLayout(); await Task.Delay(200);
                    Check(card.ActualHeight == MyCard.SwapedHeight, "collapse height");
                    card.IsSwapped = false;
                }
                Check(installs == 3, "reopening duplicated file rows");
                report.Add($"PASS {language}, dark={dark}, width={width}: negative control; full header hit tests; routed expand; clickable files; collapse/reopen; lazy load once");
            }
        }
        finally { window.Close(); }
    }
}
