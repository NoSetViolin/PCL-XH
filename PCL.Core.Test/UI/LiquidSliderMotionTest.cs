using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.UI.Effects;

namespace PCL.Core.Test.UI;

[TestClass]
public class LiquidSliderMotionTest
{
    [TestMethod]
    public void PressUsesUpstreamScaleAndSettlesAfterClick()
    {
        var motion = new LiquidSliderMotion(); motion.Reset(0);
        motion.SetValue(1, true); motion.Advance(.02);
        Assert.AreEqual(1.5, motion.ScaleX.Target);
        Assert.AreEqual(1.5, motion.ScaleY.Target);
        Assert.AreEqual(1d, motion.Press.Target);
        Assert.IsTrue(motion.ThumbScaleX > 1);
        for (var i = 0; i < 1000; i++) motion.Advance(1d / 240);
        Assert.AreEqual(1d, motion.Position.Value);
        Assert.AreEqual(0d, motion.Press.Value);
        Assert.AreEqual(1d, motion.ThumbScaleX);
        Assert.AreEqual(1d, motion.ThumbScaleY);
        Assert.IsFalse(motion.Active);
    }
    [TestMethod]
    public void RapidReversalPreservesContinuousPositionAndHasNoFling()
    {
        var motion = new LiquidSliderMotion(); motion.Reset(.5); motion.BeginDrag();
        motion.SetValue(1, true);
        for (var i = 0; i < 12; i++) motion.Advance(1d / 240);
        var before = motion.Position.Value;
        motion.SetValue(0, true);
        Assert.AreEqual(before, motion.Position.Value);
        for (var i = 0; i < 12; i++) motion.Advance(1d / 240);
        Assert.IsTrue(double.IsFinite(motion.ThumbScaleX));
        Assert.IsTrue(double.IsFinite(motion.ThumbScaleY));
        motion.EndDrag();
        for (var i = 0; i < 1000; i++) motion.Advance(1d / 240);
        Assert.AreEqual(0d, motion.Position.Value);
        Assert.IsFalse(motion.Active);
    }
    [TestMethod]
    public void ResetCancelsPressAndInvalidInputRemainsFinite()
    {
        var motion = new LiquidSliderMotion(); motion.Reset(1);
        motion.BeginDrag(); motion.SetValue(-20, true); motion.Advance(.05);
        Assert.AreEqual(0d, motion.Position.Target);
        motion.Reset(double.NaN); motion.Advance(double.PositiveInfinity);
        Assert.AreEqual(0d, motion.Position.Value); Assert.IsFalse(motion.Active);
        motion.SetValue(20, false); Assert.AreEqual(1d, motion.Position.Target);
        Assert.AreEqual(0d, motion.Press.Target);
    }
}
