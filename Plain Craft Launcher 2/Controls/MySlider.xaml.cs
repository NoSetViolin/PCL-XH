// WPF adapter for AndroidLiquidGlass LiquidSlider, pinned to 65ab177 (Apache-2.0).
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PCL.Core.UI.Effects;

namespace PCL;

public partial class MySlider
{
    public delegate void ChangeEventHandler(object sender, bool user);
    public delegate void PreviewChangeEventHandler(object sender, ModBase.RouteEventArgs e);
    public event ChangeEventHandler? Change;
    public event PreviewChangeEventHandler? PreviewChange;
    public Delegate? getHintText;
    public int Uuid = ModBase.GetUuid();
    public uint ValueByKey { get; set; } = 1U;
    private int _value, _maxValue = 100;
    private bool _dragging, _interactive, _rendering, _ending;
    private double _pointerX, _lastTime;
    private TimeSpan _lastRenderingTime = TimeSpan.MinValue;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _popupTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    internal LiquidSliderMotion Motion { get; } = new();
    internal bool IsRendering => _rendering;
    private readonly DrawingVisual _trackSample = new();
    private readonly VisualBrush _trackBrush;
    private readonly VisualBrush _trackOutput;
    private double Progress => MaxValue > 0 ? Value / (double)MaxValue : 0;
    private double TrackWidth => Math.Max(0, ActualWidth - 60);
    private bool Animate => IsLoaded && IsVisible && SystemParameters.ClientAreaAnimation
        && !SystemParameters.HighContrast && ModAnimation.AniControlEnabled == 0;

    public MySlider()
    {
        InitializeComponent();
        _trackBrush = new VisualBrush(TrackLayer) { ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
            Stretch = Stretch.Fill, AutoLayoutContent = false };
        _trackOutput = new VisualBrush(_trackSample) { ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
            Stretch = Stretch.Fill, AutoLayoutContent = false };
        ShapeDot.TabOverlay = _trackOutput;
        SizeChanged += (_, _) => Apply();
        Loaded += (_, _) => { Motion.Reset(Progress); Apply(); };
        Unloaded += (_, _) => ResetInteraction();
        IsVisibleChanged += (_, _) => { if (!IsVisible) ResetInteraction(); };
        IsEnabledChanged += (_, _) => { if (!IsEnabled) ResetInteraction(); RefreshColor(); };
        MouseEnter += (_, _) => Focus();
        MouseLeftButtonDown += DragStart;
        MouseMove += (_, _) => { if (_dragging) DragDoing(); };
        MouseLeftButtonUp += (_, e) => { if (_dragging) { DragStop(); e.Handled = true; } };
        LostMouseCapture += (_, _) => { if (_dragging) DragStop(); };
        KeyDown += OnKeyDown;
        _popupTimer.Tick += (_, _) => { _popupTimer.Stop(); Popup.IsOpen = false; };
        RefreshColor();
    }

    public int MaxValue
    {
        get => _maxValue;
        set
        {
            value = Math.Max(0, value);
            if (_maxValue == value) return;
            DragStop(); _maxValue = value;
            // Range changes must leave a valid value even when preview rejects a change.
            _value = Math.Clamp(_value, 0, value);
            Motion.Reset(Progress); StopRendering(); Apply();
        }
    }
    public int Value
    {
        get => _value;
        set
        {
            try
            {
                value = Math.Clamp(value, 0, MaxValue);
                if (_value == value) return;
                var old = _value;
                _value = value;
                if (ModAnimation.AniControlEnabled == 0)
                {
                    var args = new ModBase.RouteEventArgs();
                    PreviewChange?.Invoke(this, args);
                    if (args.handled)
                    {
                        _value = old; ResetInteraction(); return;
                    }
                }
                if (Animate) { Motion.SetValue(Progress, _interactive); StartRendering(); }
                else { Motion.Reset(Progress); StopRendering(); }
                Apply();
                if (ModAnimation.AniControlEnabled == 0) Change?.Invoke(this, false);
            }
            catch (Exception ex)
            {
                ResetInteraction();
                ModBase.Log(ex, "滑动条进度改变出错", ModBase.LogLevel.Hint,
                    userSummary: PCL.Core.App.Localization.Lang.Text("Application.Control.Error.OperationFailed"));
            }
        }
    }

    private void DragStart(object sender, MouseButtonEventArgs e)
    {
        BeginDrag(e.GetPosition(PanMain).X);
        e.Handled = true;
    }
    internal void BeginDrag(double pointerX, bool capture = true)
    {
        if (!IsEnabled || TrackWidth <= 0) return;
        Focus(); _popupTimer.Stop();
        if (ModMain.dragControl is { } other && !ReferenceEquals(other, this)) other.DragStop();
        _pointerX = pointerX;
        var center = ThumbOffset.X + 20;
        // Pressing the thumb retains its grab offset; clicking the track jumps to the pointer.
        var onThumb = Math.Abs(_pointerX - center) <= 20 * Motion.ThumbScaleX;
        _dragging = true; ModMain.dragControl = this;
        if (capture && !CaptureMouse()) { DragStop(); return; }
        if (Animate) Motion.BeginDrag();
        if (!onThumb) SetFromPointer(_pointerX);
        if (_dragging && Animate) StartRendering();
        if (_dragging) RefreshPopup();
    }
    private void SetFromPointer(double x)
    {
        if (TrackWidth <= 0) return;
        SetUserValue((int)Math.Round(Math.Clamp((x - 30) / TrackWidth, 0, 1) * MaxValue));
    }
    private bool SetUserValue(int value)
    {
        _interactive = true;
        try { Value = value; return Value == Math.Clamp(value, 0, MaxValue); }
        finally { _interactive = false; }
    }
    public void DragDoing()
    {
        DragTo(Mouse.GetPosition(PanMain).X);
    }
    internal void DragTo(double x)
    {
        if (!_dragging || !IsEnabled || TrackWidth <= 0) return;
        var delta = x - _pointerX; _pointerX = x;
        // Keep sub-step motion until it reaches the next integer setting value.
        _dragValue = Math.Clamp((_dragValue ?? Value) + delta / TrackWidth * MaxValue, 0, MaxValue);
        SetUserValue((int)Math.Round(_dragValue.Value));
        if (_dragging) RefreshPopup();
    }
    private double? _dragValue;
    public void DragStop()
    {
        if (_ending) return;
        _ending = true;
        try
        {
            var wasDragging = _dragging;
            _dragging = false; _dragValue = null;
            if (ReferenceEquals(ModMain.dragControl, this)) ModMain.dragControl = null;
            if (IsMouseCaptured) ReleaseMouseCapture();
            _popupTimer.Stop(); Popup.IsOpen = false;
            if (wasDragging && Animate) { Motion.EndDrag(); StartRendering(); }
            else if (!Animate) { Motion.Reset(Progress); StopRendering(); Apply(); }
        }
        finally { _ending = false; }
    }
    private void ResetInteraction()
    {
        DragStop(); Motion.Reset(Progress); StopRendering(); Apply();
    }
    public void RefreshPopup()
    {
        if (getHintText is null || !IsLoaded || !IsEnabled) return;
        TextHint.Text = getHintText.DynamicInvoke(Value)?.ToString() ?? "";
        var typeface = new Typeface(TextHint.FontFamily, TextHint.FontStyle, TextHint.FontWeight, TextHint.FontStretch);
        var text = new FormattedText(TextHint.Text, Thread.CurrentThread.CurrentCulture,
            TextHint.FlowDirection, typeface, TextHint.FontSize, TextHint.Foreground, ModBase.dpi);
        TextHint.Width = Math.Min(480, Math.Ceiling(text.Width));
        Popup.IsOpen = true;
    }
    private void RefreshColor()
    {
        LineFore.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty,
            IsEnabled ? "Fluent.AccentFill" : "ColorBrushGray5");
        ShapeDot.Opacity = IsEnabled ? 1 : .45;
    }
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (_dragging || !IsEnabled || e.Key is not (Key.Left or Key.Right)) return;
        var next = (long)Value + (e.Key == Key.Right ? (long)ValueByKey : -(long)ValueByKey);
        e.Handled = true;
        if (!SetUserValue((int)Math.Clamp(next, 0, MaxValue))) return;
        RefreshPopup(); _popupTimer.Stop();
        if (Popup.IsOpen) _popupTimer.Start();
    }
    private void StartRendering()
    {
        if (_rendering || !Animate) return;
        _lastTime = _clock.Elapsed.TotalSeconds;
        CompositionTarget.Rendering += OnRendering; _rendering = true;
    }
    private void StopRendering()
    {
        if (!_rendering) return;
        CompositionTarget.Rendering -= OnRendering; _rendering = false;
    }
    private void OnRendering(object? sender, EventArgs e)
    {
        if (!Animate) { ResetInteraction(); return; }
#if PCL_FRAME_DIAGNOSTICS
        if(!FramePacingProbe.DiagnosticBypassDedup)
#endif
        if(e is RenderingEventArgs frame)
        {
            if(frame.RenderingTime==_lastRenderingTime)return;
            _lastRenderingTime=frame.RenderingTime;
        }
        using var probe = PCL.Core.UI.Effects.FramePacingProbe.Log.Measure("slider.advance", e is RenderingEventArgs stamp ? stamp.RenderingTime.Ticks : -1);
        var now = _clock.Elapsed.TotalSeconds;
        Motion.Advance(now - _lastTime); _lastTime = now;
        Apply();
        if (!Motion.Active) StopRendering();
    }
    private void Apply()
    {
        if (ShapeDot is null || _trackBrush is null) return;
        var progress = Math.Clamp(Motion.Position.Value, 0, 1);
        LineFore.Width = TrackWidth * progress;
        ThumbScale.ScaleX = Motion.ThumbScaleX; ThumbScale.ScaleY = Motion.ThumbScaleY;
        var halfWidth = Math.Min(ActualWidth / 2, 20 * ThumbScale.ScaleX);
        var center = Math.Clamp(30 + TrackWidth * progress, halfWidth, Math.Max(halfWidth, ActualWidth - halfWidth));
        var left = center - 20;
        ThumbOffset.X = left;
        Popup.PlacementRectangle = new Rect(center, Math.Max(0, ActualHeight/2 - 12*ThumbScale.ScaleY), 0, 0);
        ShapeDot.PressProgress = Math.Clamp(Motion.Press.Value, 0, 1);
        // Only the sibling track is sampled here. The inherited backdrop is sampled
        // separately by LiquidGlassSurface, so neither source contains this thumb.
        var press = ShapeDot.PressProgress;
        var gutter = 24 + ShapeDot.BlurRadius;
        var expanded = new Rect(-gutter, -gutter, 40 + 2*gutter, 24 + 2*gutter);
        _trackSample.Clip = new RectangleGeometry(expanded);
        using (var dc = _trackSample.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Transparent, null, expanded);
            dc.PushTransform(new ScaleTransform(2d/3 + press/3, press, 20, 12));
            dc.DrawRectangle(_trackBrush, null, new Rect(30 - left, 9, TrackWidth, 6));
            dc.Pop();
        }

        ShapeDot.RefreshBackdropCoordinates();
    }
}
