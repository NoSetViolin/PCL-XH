using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace PCL.Core.UI.Controls;

/// <summary>A live, reduced-resolution background shared by cards sampling the same visual.</summary>
internal sealed class SharedCardBackdrop
{
    internal const double Scale = .5;
    // Include the blur support plus texture filtering, including fractional DPI sizes.
    internal const double Gutter = 16;
    private static readonly ConditionalWeakTable<FrameworkElement,
        Dictionary<(double Radius, KernelType Kernel), WeakReference<SharedCardBackdrop>>> Sources = new();

    internal Border Visual { get; }
    internal BitmapCacheBrush Brush { get; }
    private Size _size;
    private readonly FrameworkElement _source;
    private readonly Border _sample;

    private SharedCardBackdrop(FrameworkElement source, double radius, KernelType kernel)
    {
        _source = source;
        _sample = new Border
        {
            Effect = new BlurEffect
            {
                Radius = radius * Scale,
                KernelType = kernel,
                RenderingBias = RenderingBias.Performance
            }
        };
        Visual = new Border
        {
            // The cache contains only the wallpaper/video, never the foreground card content.
            CacheMode = new BitmapCache { EnableClearType = false },
            // Effects are outside an element's own bitmap cache. Cache a parent so
            // BitmapCacheBrush reads the blurred child rather than the sharp input.
            Child = _sample,
            ClipToBounds = true,
            IsHitTestVisible = false
        };
        // No brush-local BitmapCache: all consumers use the target visual's cache.
        Brush = new BitmapCacheBrush(Visual);
        RenderOptions.SetBitmapScalingMode(Visual, BitmapScalingMode.LowQuality);
        UpdateSize(source.RenderSize);
    }

    internal static SharedCardBackdrop Get(FrameworkElement source, double radius, KernelType kernel)
    {
        // UI-thread only. Weak values release old GPU caches when cards unload or settings change.
        var entries = Sources.GetOrCreateValue(source);
        var key = (Math.Min(12, radius), kernel);
        if (!entries.TryGetValue(key, out var weak) || !weak.TryGetTarget(out var backdrop))
        {
            backdrop = new SharedCardBackdrop(source, key.Item1, kernel);
            entries[key] = new WeakReference<SharedCardBackdrop>(backdrop);
        }
        backdrop.UpdateSize(source.RenderSize);
        return backdrop;
    }

    private void UpdateSize(Size size)
    {
        if (_size == size) return;
        _size = size;
        var reduced = new Size(Math.Max(1, size.Width * Scale), Math.Max(1, size.Height * Scale));
        var padded = new Size(reduced.Width + 2 * Gutter, reduced.Height + 2 * Gutter);
        // Clamp the source edges before blurring. Transparent effect bounds must
        // never reveal the dark fallback, even for cards close to a window edge.
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            var edgeX = Math.Min(1, size.Width);
            var edgeY = Math.Min(1, size.Height);
            var x = new[] { 0d, Gutter, Gutter + reduced.Width, padded.Width };
            var y = new[] { 0d, Gutter, Gutter + reduced.Height, padded.Height };
            for (var row = 0; row < 3; row++)
            for (var column = 0; column < 3; column++)
            {
                // Sample a texel inside the source for stretched edge strips;
                // texture filtering at the outermost texel can itself add alpha.
                var insetX = Math.Min(edgeX, Math.Max(0, size.Width - edgeX));
                var insetY = Math.Min(edgeY, Math.Max(0, size.Height - edgeY));
                var sourceRect = new Rect(column == 2 ? size.Width - edgeX - insetX : column == 0 ? insetX : 0,
                    row == 2 ? size.Height - edgeY - insetY : row == 0 ? insetY : 0,
                    column == 1 ? size.Width : edgeX, row == 1 ? size.Height : edgeY);
                var brush = new VisualBrush(_source)
                {
                    AutoLayoutContent = false,
                    ViewboxUnits = BrushMappingMode.Absolute,
                    Viewbox = sourceRect,
                    Stretch = Stretch.Fill
                };
                dc.DrawRectangle(brush, null, new Rect(x[column], y[row], x[column + 1] - x[column], y[row + 1] - y[row]));
            }
        }
        _sample.Background = new DrawingBrush(drawing)
        {
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(padded),
            Stretch = Stretch.Fill
        };
        Visual.Width = padded.Width;
        Visual.Height = padded.Height;
        Visual.Measure(padded);
        Visual.Arrange(new Rect(padded));
    }
}
