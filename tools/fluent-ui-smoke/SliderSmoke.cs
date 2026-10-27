// Only compiled into the isolated QA executable.
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Reflection;
using PCL.Core.App;
using PCL.Core.App.Configuration;
using PCL.Core.UI.Controls;
using PCL.Core.UI.Theme;

namespace PCL;

internal static class SliderSmoke
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("Slider: " + message);
    }
    private static void SaveBrush(Brush brush, string path)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) dc.DrawRectangle(brush, null, new Rect(0, 0, 400, 200));
        var bitmap = new RenderTargetBitmap(400, 200, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[400 * 200 * 4]; bitmap.CopyPixels(pixels, 400 * 4, 0);
        Check(Enumerable.Range(0, 400 * 200).Any(i => pixels[i * 4 + 3] > 0), "empty independent track sample");
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
    private static async Task CheckPageControls(Window window, Grid root, List<string> report)
    {
        MyPageRight[] pages;
        ModAnimation.AniControlEnabled++;
        try
        {
            pages = [new PageSetupUI(), new PageSetupLaunch(), new PageSetupGameManage(),
                new PageSetupLauncherMisc(), new PageInstanceSetup(), new PageLogRight()];
        }
        finally { ModAnimation.AniControlEnabled--; }
        var count = 0;
        foreach (var page in pages)
        {
            var sliders = page.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(f => f.FieldType == typeof(MySlider)).Select(f => (MySlider)f.GetValue(page)!).ToArray();
            count += sliders.Length;
            foreach (var slider in sliders)
            {
                Check(slider.ShapeDot.Material == LiquidGlassMaterial.SliderThumb, "page slider was not replaced");
                if (page is PageInstanceSetup || page is PageLogRight || slider.Tag is not string key) continue;
                Check(ConfigService.TryGetConfigItemNoType(key, out var item), "unknown setting key " + key);
                var priorConfig = item!.GetValueNoType(); var priorValue = slider.Value;
                try
                {
                    slider.Value = priorValue == 0 ? 1 : priorValue - 1;
                    Check(Convert.ToInt32(item.GetValueNoType()) == slider.Value, "setting callback " + key);
                }
                finally { slider.Value = priorValue; item.SetValueNoType(priorConfig); }
            }
        }
        Check(count == 20, $"expected 20 page sliders, found {count}");
        var log = (PageLogRight)pages.Single(p => p is PageLogRight);
        var savedLog = Config.System.MaxGameLog;
        try { log.SliderMaxLog.Value = 12; Check(Config.System.MaxGameLog == 12, "game log setting callback"); }
        finally { Config.System.MaxGameLog = savedLog; }
        // Attach the original memory/log grids without loading the owning instance page,
        // which requires a real Minecraft profile and starts its periodic RAM refresh.
        foreach (var page in pages.Where(p => p is PageInstanceSetup or PageLogRight))
        {
            var slider = page is PageInstanceSetup instance ? instance.SliderRamCustom : ((PageLogRight)page).SliderMaxLog;
            var grid = (Grid)slider.Parent;
            ((Panel)grid.Parent).Children.Remove(grid);
            grid.Margin = new Thickness(25); grid.VerticalAlignment = VerticalAlignment.Center;
            root.Children.Add(grid);
            foreach (var width in new[] { 810d, 1280d })
            {
                window.Width = width; window.Height = 380; window.UpdateLayout(); await Task.Delay(50);
                foreach (var control in grid.Children.OfType<MySlider>())
                    Check(control.ActualHeight >= 48 && control.ActualWidth >= 80, "memory/log slider clipped");
            }
            root.Children.Remove(grid);
        }
        report.Add("PASS all 20 controls on 6 pages use liquid thumbs; settings callbacks save/restore values; instance RAM and log grids fit 810/1280 DIP hosts");
    }
    internal static async Task Run(FormMain form, string output, List<string> report)
    {
        var root = new Grid();
        var background = new Border { Background = new LinearGradientBrush(Colors.Orange, Colors.CornflowerBlue, 0) };
        root.Children.Add(background);
        var slider = new MySlider { Width = 320, MaxValue = 100, Value = 50 };
        root.Children.Add(slider);
        LiquidGlassSurface.SetBackdropSource(root, background);
        LiquidGlassSurface.SetUseLightweightMaterial(root, true);
        var window = new Window { Width = 420, Height = 180, Content = root, Owner = form,
            ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        window.Show();
        try
        {
            await Task.Delay(200); window.UpdateLayout();
            var changes = 0;
            slider.Change += (_, _) => changes++;
            slider.getHintText = (Func<int, string>)(v => $"Value: {v}");
            slider.Value = 1000; Check(slider.Value == 100, "upper bound");
            slider.Value = -1; Check(slider.Value == 0, "lower bound");
            slider.ValueByKey = 10;
            slider.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(slider), 0, Key.Right)
                { RoutedEvent = Keyboard.KeyDownEvent });
            Check(slider.Value == 10, "keyboard step");
            slider.ValueByKey = uint.MaxValue;
            slider.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(slider), 0, Key.Right)
                { RoutedEvent = Keyboard.KeyDownEvent });
            Check(slider.Value == 100, "large keyboard step must not overflow");
            slider.DragStop();
            MySlider.PreviewChangeEventHandler reject = (_, e) => e.handled = true;
            slider.PreviewChange += reject;
            var before = changes; slider.Value = 25;
            Check(slider.Value == 100 && changes == before, "preview rejection");
            slider.BeginDrag(30, false);
            Check(ModMain.dragControl is null && !slider.IsRendering && !slider.Popup.IsOpen, "rejected pointer cleanup");
            slider.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(slider), 0, Key.Left)
                { RoutedEvent = Keyboard.KeyDownEvent });
            Check(slider.Value == 100 && !slider.Popup.IsOpen, "rejected keyboard change reopened hint");
            slider.PreviewChange -= reject;
            slider.Value = 50; await Task.Delay(1000);
            slider.BeginDrag(160, false); slider.DragTo(290);
            Check(slider.Value == 100, "drag to end");
            slider.DragTo(30); Check(slider.Value == 0, "drag reversal to start");
            slider.IsEnabled = false;
            Check(ModMain.dragControl is null && !slider.IsRendering && !slider.Popup.IsOpen, "disable cleanup");
            slider.IsEnabled = true; slider.MaxValue = 0;
            slider.BeginDrag(30, false); slider.DragTo(290); slider.DragStop();
            Check(slider.Value == 0 && double.IsFinite(slider.Motion.Position.Value), "zero range");
            slider.MaxValue = 100; slider.Value = 100;
            slider.BeginDrag(290, false); slider.MaxValue = 40;
            Check(slider.Value == 40 && !slider.IsRendering && ModMain.dragControl is null, "range change during drag");
            slider.BeginDrag(290); slider.ReleaseMouseCapture();
            Check(ModMain.dragControl is null && !slider.Popup.IsOpen, "lost capture cleanup");
            slider.MaxValue = 100; slider.Value = 50; slider.Width = 80;
            window.UpdateLayout(); slider.BeginDrag(30, false); slider.DragTo(50); slider.DragStop();
            Check(slider.Value == 100, "minimum width");
            slider.Width = 320; window.UpdateLayout();
            foreach (var dark in new[] { false, true })
            {
                PCL.Core.App.Config.Preference.Theme.ColorMode = dark ? ColorMode.Dark : ColorMode.Light;
                ThemeService.RefreshColorMode(); FluentTheme.Refresh();
                slider.Value = 50; await Task.Delay(1000);
                LiquidGlassSmoke.Capture(window, Path.Combine(output, $"slider-{dark}-rest.png"));
                slider.BeginDrag(160, false);
                // FormMain's real-pointer watchdog ends synthetic drags when no OS button
                // is held; keep this fixture local while awaiting compositor frames.
                ModMain.dragControl = null;
                await Task.Delay(300);
                Check(slider.ShapeDot.Material == LiquidGlassMaterial.SliderThumb, "dedicated glass material");
                Check(Math.Abs(slider.Popup.PlacementRectangle.X - slider.ThumbOffset.X - 20) < .01, "hint does not follow thumb");
                Check(slider.ThumbScale.ScaleX > 1.4 || !SystemParameters.ClientAreaAnimation, "press scale");
                LiquidGlassSmoke.Capture(window, Path.Combine(output, $"slider-{dark}-pressed.png"));
                SaveBrush(slider.ShapeDot.TabOverlay, Path.Combine(output, $"slider-{dark}-track-sample.png"));
                slider.ShapeDot.RefractionStrength = 0;
                await Task.Delay(100);
                LiquidGlassSmoke.Capture(window, Path.Combine(output, $"slider-{dark}-flat.png"));
                slider.ShapeDot.RefractionStrength = 1;
                slider.ShapeDot.GlassEnabled = false;
                await Task.Delay(100);
                LiquidGlassSmoke.Capture(window, Path.Combine(output, $"slider-{dark}-fallback.png"));
                slider.ShapeDot.SetResourceReference(LiquidGlassSurface.GlassEnabledProperty, "LiquidGlass.Enabled");
                slider.DragStop(); await Task.Delay(1400);
                Check(!slider.IsRendering, "idle rendering subscription");
                foreach (var scale in new[] { 1d, 1.5, 2 })
                {
                    var raster = new RenderTargetBitmap((int)(root.ActualWidth * scale), (int)(root.ActualHeight * scale),
                        96 * scale, 96 * scale, PixelFormats.Pbgra32);
                    raster.Render(root);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(raster));
                    using var file = File.Create(Path.Combine(output, $"slider-layout-{dark}-{scale}.png")); encoder.Save(file);
                }
            }
            slider.BeginDrag(160, false); root.Children.Remove(slider); await Task.Delay(50);
            Check(!slider.IsRendering && ModMain.dragControl is null && !slider.Popup.IsOpen, "unload cleanup");
            await CheckPageControls(window, root, report);
            report.Add("PASS slider bounds, keyboard, preview rejection, drag, reversal, disabled/zero/narrow range, unload and idle cleanup; light/dark hardware and 1/1.5/2x layout captures");
        }
        finally { slider.DragStop(); window.Close(); }
    }
}
