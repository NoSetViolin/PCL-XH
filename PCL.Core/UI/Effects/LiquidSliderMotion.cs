// Copyright 2025 Kyant. Apache-2.0; adapted from LiquidSlider/DampedDragAnimation
// at AndroidLiquidGlass 65ab177. See UI/Assets/Shaders/LiquidGlass.NOTICE.md.
using System;
using System.Collections.Generic;

namespace PCL.Core.UI.Effects;

/// <summary>Continuous slider springs, expressed in normalized range coordinates.</summary>
public sealed class LiquidSliderMotion
{
    public LiquidSpring Position { get; } = new(1, 1000, .0001);
    public LiquidSpring Velocity { get; } = new(.5, 300, .001);
    public LiquidSpring Press { get; } = new(1, 1000, .001);
    public LiquidSpring ScaleX { get; } = new(.6, 250, .001, 1);
    public LiquidSpring ScaleY { get; } = new(.7, 250, .001, 1);
    private readonly Queue<(double Time, double Value)> _positions = new();
    private double _time, _lastTrackedTime;
    private bool _releasePending, _releaseFrame;
    public bool Dragging { get; private set; }
    public bool Active => Dragging || _releasePending || !Position.Settled || !Velocity.Settled
        || !Press.Settled || !ScaleX.Settled || !ScaleY.Settled;
    public double ThumbScaleX => ScaleX.Value / (1 - Math.Clamp(Velocity.Value / 10 * .75, -.2, .2));
    public double ThumbScaleY => ScaleY.Value * (1 - Math.Clamp(Velocity.Value / 10 * .25, -.2, .2));
    public void Reset(double progress)
    {
        Dragging = _releasePending = _releaseFrame = false;
        _positions.Clear(); _time = _lastTrackedTime = 0;
        Position.Snap(Clamp(progress)); Velocity.Snap(0); Press.Snap(0);
        ScaleX.Snap(1); ScaleY.Snap(1);
    }
    private static double Clamp(double progress) => double.IsFinite(progress) ? Math.Clamp(progress, 0, 1) : 0;
    private void PressDown()
    {
        _releasePending = false; _positions.Clear();
        Press.To(1); ScaleX.To(1.5); ScaleY.To(1.5);
    }
    public void BeginDrag() { Dragging = true; PressDown(); }
    public void SetValue(double progress, bool interactive)
    {
        Position.To(Clamp(progress));
        if (interactive && !Dragging) { PressDown(); EndDrag(); }
    }
    public void EndDrag()
    {
        Dragging = false; _releasePending = _releaseFrame = true; Velocity.To(0);
    }
    public void Advance(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) return;
        _time += seconds;
        Position.Advance(seconds);
        // Compose VelocityTracker: quadratic fit over the last 100ms, at most 20 samples.
        if (_time - _lastTrackedTime > .04) _positions.Clear();
        _positions.Enqueue((_time, Position.Value)); _lastTrackedTime = _time;
        while (_positions.Count > 20 || (_positions.Count > 1 && _time - _positions.Peek().Time > .1)) _positions.Dequeue();
        Velocity.To(Position.Settled ? 0 : EstimateVelocity());
        Velocity.Advance(seconds); Press.Advance(seconds);
        ScaleX.Advance(seconds); ScaleY.Advance(seconds);
        if (!_releasePending) return;
        if (_releaseFrame) { _releaseFrame = false; return; }
        if (Math.Abs(Position.Value - Position.Target) >= .025) return;
        _releasePending = false; Press.To(0); ScaleX.To(1); ScaleY.To(1);
    }
    private double EstimateVelocity()
    {
        if(_positions.Count<3)return 0;
        double n=0,t=0,t2=0,t3=0,t4=0,y=0,ty=0,t2y=0;
        foreach(var sample in _positions)
        {
            var x=sample.Time-_time;var xx=x*x;n++;t+=x;t2+=xx;t3+=xx*x;t4+=xx*xx;
            y+=sample.Value;ty+=x*sample.Value;t2y+=xx*sample.Value;
        }
        var determinant=n*(t2*t4-t3*t3)-t*(t*t4-t2*t3)+t2*(t*t3-t2*t2);
        if(Math.Abs(determinant)<1e-18)return 0;
        return (n*(ty*t4-t3*t2y)-y*(t*t4-t2*t3)+t2*(t*t2y-ty*t2))/determinant;
    }
}
