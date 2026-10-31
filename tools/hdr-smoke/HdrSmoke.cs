// Isolated diagnostic build only. No switches, forced HDR or reflection in production.
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PCL.Core.App;
using PCL.Core.App.IoC;
using PCL.Core.App.Localization;
using PCL.Core.UI.Effects;
using PCL.Core.UI.Theme;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;

namespace PCL;

internal static class HdrSmoke
{
    private static readonly string Root = Environment.GetEnvironmentVariable("PCL_HDR_QA_ROOT")
        ?? throw new InvalidOperationException("Set an isolated PCL_HDR_QA_ROOT before starting the HDR harness.");
    [ModuleInitializer]
    internal static void Initialize()
    {
        Paths.Data = Path.Combine(Root, "instance");
        Paths.SharedData = Path.Combine(Root, "shared");
        Paths.SharedLocalData = Path.Combine(Root, "local");
        Paths.Temp = Path.Combine(Root, "temp");
        Paths.OldSharedData = Path.Combine(Root, "old");
        foreach (var path in new[] { Paths.Data, Paths.SharedData, Paths.SharedLocalData, Paths.Temp, Paths.OldSharedData })
            Directory.CreateDirectory(path);
        Lifecycle.When(LifecycleState.WindowCreated, () => Application.Current.Dispatcher.BeginInvoke(async () => await Run()));
    }

    private static object? Field(object obj, string name) => obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(obj);
    private static void Set(object obj, string name, object value) => obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(obj, value);
    private static void Call(object obj, string name, params object[] args) => obj.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(obj, args);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    private static async Task Run()
    {
        var report = new List<string>();
        DispatcherTimer? dialogs = null;
        try
        {
            await Task.Delay(2000);
            foreach (var path in new[] { Paths.Data, Paths.SharedData, Paths.SharedLocalData, Paths.Temp, Paths.OldSharedData,
                PCL.Core.App.Configuration.ConfigService.SharedConfigPath, PCL.Core.App.Configuration.ConfigService.LocalConfigPath })
                Check(Path.GetFullPath(path).StartsWith(Path.GetFullPath(Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "QA paths escaped isolation");
            report.Add("PASS config/cache/legacy migration paths isolated");
            var form = ModMain.frmMain;
            dialogs = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            dialogs.Tick += (_, _) =>
            {
                foreach (var dialog in form.PanMsg.Children.OfType<MyMsgText>().ToArray())
                    if (dialog.LabTitle.Text == Lang.Text("Application.EnvironmentWarning.Title") || dialog.LabCaption.Text.Contains("HttpRequestException") || dialog.LabCaption.Text.Contains("SocketException"))
                        dialog.Btn1_Click();
            };
            dialogs.Start();
            Config.Preference.Background.WallpaperSource = 0;
            ModBackground.CancelPending();
            await ModBackground.CurrentTask;
            form.ImgBack.Background = new SolidColorBrush(Color.FromRgb(65, 90, 130));
            var output = Path.Combine(Root, "screenshots"); Directory.CreateDirectory(output);
            form.PageChange(FormMain.PageType.Launch);
            await Task.Delay(600);
            var controller = form.LiquidDock!;
            var hdr = (HdrDockHighlight)Field(controller, "_hdrHighlight")!;
            // Exercise the actual native code on this GPU even if the desktop is SDR. The
            // forced fixture stays invisible, never changes OS HDR, and is detached afterwards.
            Set(hdr, "_peakNits", 600f);
            Call(hdr, "EnsureDevice", new WindowInteropHelper(form).Handle);
            Call(hdr, "CreateImage", 120, 56, 1d);
            var swapChain = (IDXGISwapChain1)Field(hdr, "_swapChain")!;
            Check(swapChain.Description1.Format == Format.R16G16B16A16_Float, "Not FP16");
            var device = (ID3D11Device)Field(hdr, "_device")!;
            var context = (ID3D11DeviceContext)Field(hdr, "_context")!;
            using (var source = swapChain.GetBuffer<ID3D11Texture2D>(0))
            {
                // Present rotates buffers. Upload the same production image into the next
                // back buffer and read it BEFORE Present; the previously presented buffer
                // is not accessible by assuming GetBuffer(0) still names the front image.
                var image = typeof(HdrDockHighlight).Assembly.GetType("PCL.Core.UI.Effects.HdrHighlightImage")!;
                var pixels = (Half[])image.GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, new object[] { 120, 56, 28d, 2.5d, 600f })!;
                context.UpdateSubresource(pixels, source, 0, 120 * 8);
                var desc = source.Description;
                desc.Usage = ResourceUsage.Staging; desc.BindFlags = BindFlags.None;
                desc.CPUAccessFlags = CpuAccessFlags.Read; desc.MiscFlags = ResourceOptionFlags.None;
                using var staging = device.CreateTexture2D(desc);
                context.CopyResource(staging, source);
                var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    var row = new short[120 * 4]; var peak = 0f;
                    for (var y = 0; y < 56; y++)
                    {
                        System.Runtime.InteropServices.Marshal.Copy(mapped.DataPointer + y * (int)mapped.RowPitch, row, 0, row.Length);
                        for (var x = 0; x < 120; x++) peak = Math.Max(peak, (float)BitConverter.Int16BitsToHalf(row[x * 4]));
                    }
                    Check(peak > 6, "Native texture lost HDR range");
                    report.Add($"PASS actual D3D11 FP16 pre-Present texture readback peak={peak:F3} scRGB; production swap chain created/presented separately");
                }
                finally { context.Unmap(staging, 0); }
            }
            Call(hdr, "ReleaseGraphics");
            Set(hdr, "_nextProbe", DateTime.MinValue);
            hdr.Update(1, true);
            var hdrActive = (float)Field(hdr, "_peakNits")! > 0;
            report.Add($"INFO actual output HDR active={hdrActive}; reported target={Field(hdr, "_peakNits")} nits; SDR white scale={Field(hdr, "_whiteScale")}");
            if (!hdrActive) Check(Field(hdr, "_device") is null, "SDR fallback allocated a D3D device");
            Check((DateTime)Field(hdr, "_retryAfter")! == DateTime.MinValue, "Production path failed: " + Field(hdr, "_lastFailure"));
            hdr.Update(0, true);
            report.Add("PASS actual display capability detection and no-press fallback");
            Call(hdr, "ReleaseGraphics");
            Set(hdr, "_peakNits", 0f); Set(hdr, "_nextProbe", DateTime.UtcNow.AddMinutes(1));
            hdr.Update(1, true);
            Check(Field(hdr, "_device") is null && !(bool)Field(hdr, "_visible")!, "SDR capability gate rendered HDR");
            Set(hdr, "_nextProbe", DateTime.MinValue);
            hdr.Update(0, true);
            report.Add("PASS injected SDR capability result creates no device (capability-gate test, not physical SDR-monitor E2E)");
            Check(form.PrimaryNavigation.CaptureMouse(), "Could not capture WPF navigation for synchronous check");
            hdr.Update(1, true);
            Check(ReferenceEquals(Mouse.Captured, form.PrimaryNavigation), "HDR update changed WPF mouse capture");
            hdr.Update(1, false);
            Check(ReferenceEquals(Mouse.Captured, form.PrimaryNavigation), "HDR opt-out changed WPF mouse capture");
            form.PrimaryNavigation.ReleaseMouseCapture();
            hdr.Update(0, true);
            report.Add("PASS synchronous native HDR update/opt-out preserve WPF mouse capture");

            foreach (var language in new[] { "zh-CN", "en-US", "zh-TW", "en-GB" })
            foreach (var dark in new[] { false, true })
            foreach (var width in new[] { 810d, 1280d })
            {
                LocalizationService.Apply(language, save: false);
                Config.Preference.Theme.ColorMode = dark ? ColorMode.Dark : ColorMode.Light;
                ThemeService.RefreshColorMode(); FluentTheme.Refresh();
                form.Width = width; form.Height = width == 810 ? 470 : 800;
                await Task.Delay(250); form.UpdateLayout();
                controller.Refresh(false);
                var point = new Point(Canvas.GetLeft(form.DockHighlight) + form.DockHighlight.Width / 2, 32);
                // Do not hold the user's real mouse capture across async waits. External
                // pointer movement/focus legitimately cancels a drag and corrupts fixtures.
                Check(controller.BeginDrag(point, capture: false), "Cannot press actual dock thumb");
                await Task.Delay(350);
                var hit = form.InputHitTest(form.PrimaryNavigation.TranslatePoint(point, form)) as DependencyObject;
                while (hit is Visual && !ReferenceEquals(hit, form.PrimaryNavigation)) hit = VisualTreeHelper.GetParent(hit);
                Check(ReferenceEquals(hit, form.PrimaryNavigation), "Pressed thumb is outside WPF navigation hit region");
                var bounds = form.DockHighlight.TransformToAncestor(form).TransformBounds(new Rect(form.DockHighlight.RenderSize));
                Check(new Rect(form.RenderSize).Contains(bounds), "Pressed thumb outside client area");
                hdr.Update(1, false);
                Check(!(bool)Field(hdr, "_visible")!, "Hardware acceleration opt-out did not hide HDR");
                hdr.Update(controller.Motion.Press.Value, true);
                var name = $"{language}-{(dark ? "dark" : "light")}-{width}";
                LiquidGlassSmoke.Capture(form, Path.Combine(output, name + ".png"));
                controller.DragTo(point.X + 15);
                controller.FinishDrag();
                await Task.Delay(450);
                Check(Mouse.Captured is null, "Mouse capture not released");
                Check((DateTime)Field(hdr, "_retryAfter")! == DateTime.MinValue, "HDR encountered an exception");
                report.Add($"PASS {name}: synthetic press/drag/release, hit region, opt-out, thumb inside window; screenshot is SDR layout evidence only");
            }
            hdr.Update(1, true);
            form.WindowState = WindowState.Minimized;
            Check(!(bool)Field(hdr, "_visible")!, "Minimized highlight visible");
            form.WindowState = WindowState.Normal;
            hdr.Dispose(); hdr.Dispose();
            Check(Field(hdr, "_device") is null && Field(hdr, "_composition") is null, "COM resources leaked after dispose");
            report.Add("PASS minimize and idempotent disposal release graphics");
            report.Add("LIMIT actual HDR peak luminance, mixed HDR/SDR monitor moves, OS HDR toggling, and GPU device-loss recovery require corresponding hardware/manual verification; not simulated as E2E passes.");
        }
        catch (Exception ex) { report.Add("FAIL " + ex); }
        finally
        {
            dialogs?.Stop();
            File.WriteAllLines(Path.Combine(Root, "hdr-report.txt"), report);
            Application.Current.Shutdown();
        }
    }
}
