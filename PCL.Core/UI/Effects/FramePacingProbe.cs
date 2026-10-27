using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;

namespace PCL.Core.UI.Effects;

/// <summary>Opt-in timestamps shared by the isolated QA harness and EventPipe.</summary>
[EventSource(Name = "PCL-FramePacing")]
public sealed class FramePacingProbe : EventSource
{
    public static readonly FramePacingProbe Log = new();
    private FramePacingProbe() { }
#if PCL_FRAME_DIAGNOSTICS
    public static bool DiagnosticBypassDedup { get; set; }
#endif

    [Event(1, Level = EventLevel.Informational)]
    public void Sample(string stage, long start, long end, double value)
    {
        if (IsEnabled()) WriteEvent(1, stage, start, end, value);
    }

    [NonEvent]
    public Scope Measure(string stage, double value = 0) => new(IsEnabled() ? stage : null, value);

    public readonly struct Scope : IDisposable
    {
        private readonly string? _stage;
        private readonly long _start;
        private readonly double _value;
        internal Scope(string? stage, double value)
        { _stage = stage; _value = value; _start = stage is null ? 0 : Stopwatch.GetTimestamp(); }
        public void Dispose()
        {
            if (_stage is not null) Log.Sample(_stage, _start, Stopwatch.GetTimestamp(), _value);
        }
    }
}
