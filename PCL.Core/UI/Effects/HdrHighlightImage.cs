using System;

namespace PCL.Core.UI.Effects;

/// <summary>Soft central glass light in linear scRGB. scRGB 1.0 represents 80 nits.</summary>
internal static class HdrHighlightImage
{
    internal static float PeakNits(float displayPeak) =>
        float.IsFinite(displayPeak) && displayPeak > 80 ? Math.Min(displayPeak, 600) : 0;

    internal static Half[] Create(int width, int height, double radius, double featherWidth, float peakNits)
    {
        var pixels = new Half[checked(width * height * 4)];
        radius = Math.Clamp(radius, 0, Math.Min(width, height) / 2d);
        featherWidth = Math.Max(1, featherWidth);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var px = x + .5 - width / 2d;
            var py = y + .5 - height / 2d;
            var qx = Math.Abs(px) - (width / 2d - radius);
            var qy = Math.Abs(py) - (height / 2d - radius);
            var distance = Math.Sqrt(Math.Pow(Math.Max(qx, 0), 2) + Math.Pow(Math.Max(qy, 0), 2))
                + Math.Min(Math.Max(qx, qy), 0) - radius;
            // Broad centre light, falling continuously towards the boundary. No rim term.
            var feather = Math.Clamp(-distance / featherWidth, 0, 1);
            feather = feather * feather * (3 - 2 * feather);
            var nx = px / (width / 2d);
            var ny = py / (height / 2d);
            var alpha = (float)(Math.Exp(-3 * (nx * nx + ny * ny)) * feather);
            var linear = alpha * peakNits / 80;
            var i = (y * width + x) * 4;
            pixels[i] = pixels[i + 1] = pixels[i + 2] = (Half)linear;
            pixels[i + 3] = (Half)alpha;
        }
        return pixels;
    }

    /// <summary>Composite label colour over the light with its original coverage, without cutting dark holes.</summary>
    internal static void CompositeLabels(Half[] pixels, byte[] bgra, float whiteScale, float green)
    {
        var linearGreen = green <= .04045f ? green / 12.92f : MathF.Pow((green + .055f) / 1.055f, 2.4f);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            // The label source spans neighbouring tabs too. Never redraw their pixels
            // outside the rounded glass footprint (including its transparent corners).
            if (pixels[i + 3] == (Half)0) continue;
            var coverage = bgra[i + 3] / 255f;
            // Normal premultiplied source-over. No dilation, shadow, exclusion border,
            // or darkening of background lettering as the glass moves across it.
            pixels[i] = (Half)((float)pixels[i] * (1 - coverage));
            pixels[i + 1] = (Half)((float)pixels[i + 1] * (1 - coverage) + linearGreen * whiteScale * coverage);
            pixels[i + 2] = (Half)((float)pixels[i + 2] * (1 - coverage) + whiteScale * coverage);
            pixels[i + 3] = (Half)((float)pixels[i + 3] * (1 - coverage) + coverage);
        }
    }
}
