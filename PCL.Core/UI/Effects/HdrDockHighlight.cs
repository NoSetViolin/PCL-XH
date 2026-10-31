using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PCL.Core.Logging;
using PCL.Core.UI.Controls;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;

namespace PCL.Core.UI.Effects;

/// <summary>
/// Optional soft HDR glass light above WPF's SDR scene. A DirectComposition visual has no HWND
/// or input surface, so mouse capture and WPF hit testing stay with the existing dock.
/// All calls and COM lifetimes belong to the window's dispatcher thread.
/// </summary>
public sealed class HdrDockHighlight : IDisposable
{
    private readonly Window _window;
    private readonly FrameworkElement _thumb;
    private readonly DispatcherTimer _monitorTimer;
    private HwndSource? _source;
    private IDXGIFactory1? _factory;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDCompositionDevice? _composition;
    private IDCompositionTarget? _target;
    private IDCompositionVisual3? _visual;
    private IDXGISwapChain1? _swapChain;
    private nint _monitor;
    private Rect _monitorBounds;
    private float _peakNits;
    private float _whiteScale = 1;
    private int _width, _height;
    private double _dpi, _progress;
    private bool _disposed, _allowed, _visible;
    private Matrix3x2 _lastTransform;
    private float _lastOpacity;
    private Rect _labelViewbox;
    private DateTime _nextProbe, _retryAfter;
    private Exception? _lastFailure;

    public HdrDockHighlight(Window window, FrameworkElement thumb)
    {
        _window = window;
        _thumb = thumb;
        _monitorTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            (_, _) => Update(_progress, _allowed), window.Dispatcher);
        _monitorTimer.Stop();
        window.LocationChanged += GeometryChanged;
        window.SizeChanged += GeometryChanged;
        window.StateChanged += GeometryChanged;
        thumb.LayoutUpdated += LayoutChanged;
        thumb.IsVisibleChanged += VisibilityChanged;
        thumb.Unloaded += Unloaded;
        window.Closed += Closed;
    }

    public void Update(double pressProgress, bool allowed)
    {
        if (_disposed) return;
        if (_progress < .001 && pressProgress >= .001) _labelViewbox = Rect.Empty;
        _progress = double.IsFinite(pressProgress) ? Math.Clamp(pressProgress, 0, 1) : 0;
        _allowed = allowed;
        try
        {
            if (!allowed || _progress < .001 || !_thumb.IsVisible || !_window.IsVisible
                || _window.WindowState == WindowState.Minimized || SystemParameters.HighContrast
                || !SystemParameters.ClientAreaAnimation)
            {
                Hide();
                return;
            }
            _monitorTimer.Start();
            var source = PresentationSource.FromVisual(_thumb) as HwndSource;
            if (source?.RootVisual is not Visual root || DateTime.UtcNow < _retryAfter) return;
            if (_source is null) { _source = source; _source.AddHook(WindowMessage); }
            var transform = _thumb.TransformToAncestor(root);
            var deviceTransform = source.CompositionTarget.TransformToDevice;
            var topLeft = deviceTransform.Transform(transform.Transform(new Point(0, 0)));
            var bottomRight = deviceTransform.Transform(transform.Transform(new Point(_thumb.ActualWidth, _thumb.ActualHeight)));
            var bounds = new Rect(topLeft, bottomRight);
            if (bounds.Width < 1 || bounds.Height < 1) { Hide(); return; }
            var screenOrigin = root.PointToScreen(new Point());
            var screenBounds = new Rect(screenOrigin.X + bounds.X, screenOrigin.Y + bounds.Y, bounds.Width, bounds.Height);
            var center = new NativePoint((int)(screenBounds.X + screenBounds.Width / 2), (int)(screenBounds.Y + screenBounds.Height / 2));
            var monitor = MonitorFromPoint(center, 2);
            if (monitor != _monitor || DateTime.UtcNow >= _nextProbe)
            {
                Probe(monitor);
                _nextProbe = DateTime.UtcNow.AddSeconds(1);
            }
            // Do not spill HDR pixels onto an SDR display when straddling two monitors.
            if (_peakNits == 0 || !_monitorBounds.Contains(screenBounds)) { Hide(false); return; }
            EnsureDevice(source.Handle);
            var dpi = deviceTransform.M11;
            var width = (int)Math.Ceiling(_thumb.ActualWidth * dpi);
            var height = (int)Math.Ceiling(_thumb.ActualHeight * deviceTransform.M22);
            var labelViewbox = (_thumb as LiquidGlassSurface)?.TabOverlay is VisualBrush labels ? labels.Viewbox : Rect.Empty;
            if (_swapChain is null || width != _width || height != _height || dpi != _dpi || labelViewbox != _labelViewbox)
                CreateImage(width, height, dpi);
            var matrix = new Matrix3x2((float)(bounds.Width / _width), 0, 0,
                (float)(bounds.Height / _height), (float)bounds.X, (float)bounds.Y);
            var opacity = (float)(_progress * _progress);
            if (_visible && matrix == _lastTransform && opacity == _lastOpacity) return;
            _visual!.SetTransform(matrix).CheckError();
            _visual.SetOpacity(opacity).CheckError();
            _composition!.Commit().CheckError();
            _lastTransform = matrix;
            _lastOpacity = opacity;
            _visible = true;
        }
        catch (Exception ex)
        {
            // Optional eye candy must never break input/startup. Retry after a device reset,
            // monitor change, or a short cooldown rather than throwing on every animation frame.
            ReleaseGraphics();
            _retryAfter = DateTime.UtcNow.AddSeconds(15);
            _lastFailure = ex;
            LogWrapper.Debug(ex, "HDR", "底栏 HDR 高光不可用，已回退至 SDR。");
        }
    }

    private void Probe(nint monitor)
    {
        // DXGI factories cache display topology and HDR state. Refresh for each capability probe.
        _factory?.Dispose();
        _factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        var peak = 0f;
        var whiteScale = 1f;
        var bounds = Rect.Empty;
        for (uint a = 0; _factory.EnumAdapters1(a, out var adapter).Success; a++)
        using (adapter)
        {
            for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
            using (output)
            {
                if (output.Description.Monitor != monitor) continue;
                using var advanced = output.QueryInterfaceOrNull<IDXGIOutput6>();
                if (advanced is null) continue;
                var description = advanced.Description1;
                // This is the active output colour space, not merely EDID HDR capability.
                if (description.ColorSpace != ColorSpaceType.RgbFullG2084NoneP2020) continue;
                peak = HdrHighlightImage.PeakNits(description.MaxLuminance);
                whiteScale = HdrDisplayWhiteLevel.GetScale(description.DeviceName);
                var r = description.DesktopCoordinates;
                bounds = new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            }
        }
        if (monitor != _monitor || peak != _peakNits || whiteScale != _whiteScale) ReleaseGraphics();
        _monitor = monitor;
        _peakNits = peak;
        _whiteScale = whiteScale;
        _monitorBounds = bounds;
    }

    private void EnsureDevice(nint hwnd)
    {
        if (_device is not null) return;
        _device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            FeatureLevel.Level_11_0);
        _context = _device.ImmediateContext;
        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        // Legacy DCompositionCreateDevice creates v1 visuals that cannot expose Visual3,
        // even on current Windows. Request the v3 device explicitly for HDR opacity.
        _composition = DComp.DCompositionCreateDevice3<IDCompositionDevice>(dxgiDevice);
        _composition.CreateTargetForHwnd(hwnd, true, out _target).CheckError();
        _composition.CreateVisual(out var visual).CheckError();
        using (visual) _visual = visual.QueryInterface<IDCompositionVisual3>();
        _target.SetRoot(_visual).CheckError();
    }

    private void CreateImage(int width, int height, double dpi)
    {
        if (width <= 0 || height <= 0 || width > 4096 || height > 1024)
            throw new InvalidOperationException("Invalid HDR reflection bounds.");
        var resized = _swapChain is null || width != _width || height != _height;
        if (resized)
        {
            using var dxgiDevice = _device!.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            using var factory = adapter.GetParent<IDXGIFactory2>();
            _visual!.SetContent(null).CheckError();
            _swapChain?.Dispose();
            _swapChain = null;
            _swapChain = factory.CreateSwapChainForComposition(_device, new SwapChainDescription1
            {
                Width = (uint)width, Height = (uint)height, Format = Format.R16G16B16A16_Float,
                BufferCount = 2, BufferUsage = Usage.RenderTargetOutput,
                SampleDescription = new SampleDescription(1, 0), Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipSequential, AlphaMode = Vortice.DXGI.AlphaMode.Premultiplied
            });
        }
        using var colorSwapChain = _swapChain!.QueryInterface<IDXGISwapChain3>();
        if ((colorSwapChain.CheckColorSpaceSupport(ColorSpaceType.RgbFullG10NoneP709)
            & SwapChainColorSpaceSupportFlags.Present) == 0)
            throw new NotSupportedException("scRGB composition is unavailable.");
        colorSwapChain.SetColorSpace1(ColorSpaceType.RgbFullG10NoneP709);
        var pixels = HdrHighlightImage.Create(width, height, 28 * dpi, 8 * dpi, _peakNits);
        if (_thumb is LiquidGlassSurface surface && surface.TabOverlay is VisualBrush overlay)
        {
            var drawing = new DrawingVisual();
            var gutter = 24 + surface.BlurRadius;
            using (var dc = drawing.RenderOpen())
                dc.DrawRectangle(overlay, null, new Rect(-gutter, -gutter,
                    width / dpi + 2 * gutter, height / dpi + 2 * gutter));
            var mask = new RenderTargetBitmap(width, height, 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
            mask.Render(drawing);
            var bgra = new byte[width * height * 4];
            mask.CopyPixels(bgra, width * 4, 0);
            var dark = surface.FallbackBrush is SolidColorBrush fallback && fallback.Color.R + fallback.Color.G + fallback.Color.B < 384;
            HdrHighlightImage.CompositeLabels(pixels, bgra, _whiteScale, dark ? 145f / 255 : 136f / 255);
            _labelViewbox = overlay.Viewbox;
        }
        using var buffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _context!.UpdateSubresource(pixels, buffer, 0, (uint)(width * 8));
        _swapChain.Present(0, PresentFlags.None).CheckError();
        _visual!.SetContent(_swapChain).CheckError();
        _width = width; _height = height; _dpi = dpi;
        _visible = false;
        // Reuse the swap chain. Stable holds only change composition opacity/transform.
        // While dragging, refresh the label content as it moves under the lens.
    }

    private void Hide(bool stopTimer = true)
    {
        if (stopTimer) _monitorTimer.Stop();
        if (!_visible) return;
        _visual?.SetOpacity(0).CheckError();
        _composition?.Commit().CheckError();
        _visible = false;
    }

    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message is 0x007E or 0x001A or 0x02E0) // display/settings/DPI changed
        {
            _nextProbe = DateTime.MinValue;
            _retryAfter = DateTime.MinValue;
            _window.Dispatcher.BeginInvoke(() => Update(_progress, _allowed), DispatcherPriority.Loaded);
        }
        return 0;
    }
    private void GeometryChanged(object? sender, EventArgs e) => Update(_progress, _allowed);
    private void LayoutChanged(object? sender, EventArgs e) { if (_progress >= .001) Update(_progress, _allowed); }
    private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => Update(_progress, _allowed);
    private void Unloaded(object sender, RoutedEventArgs e) { _progress = 0; Update(0, _allowed); }
    private void Closed(object? sender, EventArgs e) => Dispose();

    private void ReleaseGraphics()
    {
        _visible = false;
        // Detach before releasing so a failed device cannot leave a stuck bright reflection.
        _target?.SetRoot(null);
        _composition?.Commit();
        _visual?.Dispose(); _visual = null;
        _swapChain?.Dispose(); _swapChain = null;
        _target?.Dispose(); _target = null;
        _composition?.Dispose(); _composition = null;
        _context?.Dispose(); _context = null;
        _device?.Dispose(); _device = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _monitorTimer.Stop();
        _source?.RemoveHook(WindowMessage);
        _window.LocationChanged -= GeometryChanged;
        _window.SizeChanged -= GeometryChanged;
        _window.StateChanged -= GeometryChanged;
        _thumb.LayoutUpdated -= LayoutChanged;
        _thumb.IsVisibleChanged -= VisibilityChanged;
        _thumb.Unloaded -= Unloaded;
        _window.Closed -= Closed;
        ReleaseGraphics();
        _factory?.Dispose(); _factory = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativePoint(int X, int Y);
    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(NativePoint point, uint flags);
}
