using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.UI.Effects;

namespace PCL.Core.Test.UI;

[TestClass]
public class HdrHighlightTests
{
    [TestMethod]
    public void RejectsUnknownPeakAndCapsReportedLuminance()
    {
        foreach (var invalid in new[] { float.NaN, float.PositiveInfinity, -1f, 0f, 80f })
            Assert.AreEqual(0f, HdrHighlightImage.PeakNits(invalid));
        Assert.AreEqual(300f, HdrHighlightImage.PeakNits(300));
        Assert.AreEqual(600f, HdrHighlightImage.PeakNits(2000));
    }

    [TestMethod]
    public void CentreIsBrightestAndFallsTowardsTransparentEdges()
    {
        foreach (var dpi in new[] { 1d, 1.5, 2 })
        {
            var width = (int)(120 * dpi);
            var height = (int)(56 * dpi);
            var pixels = HdrHighlightImage.Create(width, height, 28 * dpi, 8 * dpi, 600);
            var brightest = 0f;
            for (var i = 0; i < pixels.Length; i += 4)
            {
                var rgb = (float)pixels[i];
                var alpha = (float)pixels[i + 3];
                Assert.IsTrue(float.IsFinite(rgb) && rgb >= 0 && rgb <= 7.5f);
                Assert.IsTrue(alpha >= 0 && alpha <= 1);
                Assert.AreEqual(rgb, (float)pixels[i + 1]);
                Assert.AreEqual(rgb, (float)pixels[i + 2]);
                Assert.AreEqual(alpha * 7.5f, rgb, .005f, "Premultiplied linear scRGB");
                brightest = Math.Max(brightest, rgb);
            }
            Assert.IsTrue(brightest > 7, "The centre must exceed SDR white.");
            var centre = (float)pixels[((height / 2) * width + width / 2) * 4];
            Assert.AreEqual(brightest, centre, .005f);
            var previous = centre;
            for (var x = width / 2 + 1; x < width; x++)
            {
                var value = (float)pixels[((height / 2) * width + x) * 4];
                Assert.IsTrue(value <= previous, "Central light must fall off without a bright rim.");
                previous = value;
            }
            Assert.IsTrue(previous < .01);
            Assert.AreEqual((Half)0, pixels[3], "Rounded corners must stay transparent.");
        }
    }

    [TestMethod]
    public void LabelsUseNormalColourCompositingWithoutADarkExclusionBorder()
    {
        var image = HdrHighlightImage.Create(120, 56, 28, 8, 600);
        var original = (Half[])image.Clone();
        var mask = new byte[120 * 56 * 4];
        mask[3] = 255;
        mask[(28 * 120 + 60) * 4 + 3] = 255;
        HdrHighlightImage.CompositeLabels(image, mask, 2, 145f / 255);
        Assert.AreEqual((Half)2, image[(28 * 120 + 60) * 4 + 2], "Opaque label is blue, not a dark hole.");
        Assert.AreEqual((Half)1, image[(28 * 120 + 60) * 4 + 3]);
        Assert.AreEqual(original[(28 * 120 + 61) * 4], image[(28 * 120 + 61) * 4], "No outline around glyph.");
        Assert.AreEqual(original[(28 * 120 + 65) * 4], image[(28 * 120 + 65) * 4]);
        Assert.AreEqual((Half)0, image[3], "Neighbouring labels outside the glass must stay untouched.");
    }
}
