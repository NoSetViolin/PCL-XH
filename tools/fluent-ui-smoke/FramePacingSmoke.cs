// Opt-in Release QA. Callback pacing and scroll response are NOT display/present latency.
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PCL.Core.App;
using PCL.Core.UI.Controls;
using PCL.Core.UI.Effects;
using PCL.Core.UI.Theme;

namespace PCL;

internal sealed class FramePacingSmoke : EventListener
{
    private record Sample(string Stage, long Start, long End, double Value, int Thread);
    private readonly List<Sample> _samples = new();
    private bool _collect;
    private int _dropped;
    protected override void OnEventSourceCreated(EventSource source)
    {
        if(source.Name=="PCL-FramePacing") EnableEvents(source,EventLevel.Informational);
    }
    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if(!_collect || e.EventId!=1 || e.Payload is not { Count: 4 } p)return;
        lock(_samples)
        {
            if(_samples.Count>=300000) { _dropped++;return; }
            _samples.Add(new((string)p[0]!,Convert.ToInt64(p[1]),Convert.ToInt64(p[2]),Convert.ToDouble(p[3]),Environment.CurrentManagedThreadId));
        }
    }
    private static double Milliseconds(long ticks)=>ticks*1000d/Stopwatch.Frequency;
    private static object Stats(IEnumerable<double> values)
    {
        var a=values.Order().ToArray();
        double P(double q)=>a.Length==0?0:a[(int)((a.Length-1)*q)];
        return new {count=a.Length,p50=P(.5),p95=P(.95),p99=P(.99),max=P(1)};
    }

    internal static async Task Run(FormMain form,string root,List<string> report)
    {
        var output=Path.Combine(root,"frame-pacing",DateTime.UtcNow.ToString("yyyyMMdd-HHmmss",CultureInfo.InvariantCulture));
        Directory.CreateDirectory(output);
        using var listener=new FramePacingSmoke();
        var baseline=File.Exists(Path.Combine(AppContext.BaseDirectory,"frame-pacing-baseline.flag"));
        FramePacingProbe.DiagnosticBypassDedup=baseline;
        var dc=GetDC(new WindowInteropHelper(form).Handle);
        int refresh;
        try {refresh=GetDeviceCaps(dc,116);}finally {ReleaseDC(new WindowInteropHelper(form).Handle,dc);}
        // VREFRESH is nominal GDI refresh; preserve unknown rather than invent a refresh rate.
        double? budget=refresh>1?1000d/refresh:null;
        var source=(HwndSource?)PresentationSource.FromVisual(form);
        File.WriteAllText(Path.Combine(output,"environment.json"),JsonSerializer.Serialize(new {
            configuration="Release (build command recorded separately)",pid=Environment.ProcessId,
            uiThread=Environment.CurrentManagedThreadId,stopwatchFrequency=Stopwatch.Frequency,
            nativeUiThread=GetCurrentThreadId(),
            refreshHz=refresh,refreshSource="GDI VREFRESH for window DC; nominal, not measured presentation cadence",
            dpi=VisualTreeHelper.GetDpi(form).DpiScaleX,tier=RenderCapability.Tier>>16,
            processRenderMode=RenderOptions.ProcessRenderMode.ToString(),windowRenderMode=source?.CompositionTarget.RenderMode.ToString(),
            disabledHardwareAcceleration=Config.System.DisableHardwareAcceleration,animationLimit=Config.System.AnimationFpsLimit,
            presentTelemetry="unavailable unless external ETW capture succeeds; no callback-to-present inference",
            layoutTiming="LayoutUpdated counts only; Render dispatcher duration is a proxy including other work, not exact layout duration",
            inputLatency="routed wheel event to first ScrollChanged with changed offset; not input-to-photon",
            wheelSource="WPF routed MouseWheel events through production handler; excludes OS HID delivery",
            cores=Environment.ProcessorCount
            ,diagnosticBypassDedup=baseline,wallpaper="fixed frozen DrawingBrush fixture; background video removed",theme="Dark"
        },new JsonSerializerOptions {WriteIndented=true}));
        report.Add("Frame pacing results: "+output);
        report.Add("Presentation telemetry unavailable in-process; exact layout timing/native stacks require external ETW. Routed wheel events exercise production buffering and easing, not HID delivery.");
        var originalWidth=form.Width;var originalHeight=form.Height;
        var oldBackground=form.ImgBack.Background;var oldVideoSource=form.VideoBack.Source;
        var oldBackgroundVisibility=form.ImgBack.Visibility;var oldBackgroundOpacity=form.ImgBack.Opacity;
        var oldColorMode=Config.Preference.Theme.ColorMode;
        var oldWallpaperSource=Config.Preference.Background.WallpaperSource;
        var wallpaper=new DrawingBrush(new GeometryDrawing(new LinearGradientBrush(Color.FromRgb(24,52,92),Color.FromRgb(174,95,66),35),null,
            Geometry.Parse("M0,0 L1280,0 1280,800 0,800Z M0,420 C260,110 620,690 1280,230 L1280,800 0,800Z"))){Stretch=Stretch.UniformToFill};
        wallpaper.Freeze();
        var snapshots=new Dictionary<Visual,Visual>();
        var dock=new[]{form.DockPanel,form.DockHighlight};
        var enabled=dock.Select(x=>x.GlassEnabled).ToArray();
        try
        {
            form.Width=1280;form.Height=800;
            Config.Preference.Theme.ColorMode=ColorMode.Dark;ThemeService.RefreshColorMode();FluentTheme.Refresh();
            // Startup can still be fetching an online wallpaper after WindowCreated.
            // Cancel it before installing the fixture so no asynchronous completion
            // replaces the scene midway through a trial.
            Config.Preference.Background.WallpaperSource=0;
            ModBackground.CancelPending();
            await ModBackground.CurrentTask;
            form.VideoBack.Source=null;
            form.ImgBack.Background=wallpaper;form.ImgBack.Visibility=Visibility.Visible;form.ImgBack.Opacity=1;
            if(File.Exists(Path.Combine(AppContext.BaseDirectory,"frame-pacing-verify-only.flag")))
            {
                form.PageChange(FormMain.PageType.Setup,FormMain.PageSubType.SetupLaunch);
                await Task.Delay(1800);
                if(!baseline)VerifyFrameDedup(form,report);
                await VerifyInteractionCompletion(form,report);
                return;
            }
            var modes=File.Exists(Path.Combine(AppContext.BaseDirectory,"frame-pacing-normal-only.flag"))
                ? new[]{"normal"}:new[]{"normal","frozen-dock-source","no-dock-effects"};
            foreach(var mode in modes)
            {
                snapshots.Clear();
                LiquidGlassSurface.DiagnosticBackdropOverride=mode=="frozen-dock-source"?Freeze:null;
                for(var i=0;i<dock.Length;i++){dock[i].GlassEnabled=mode=="no-dock-effects"?false:enabled[i];dock[i].InvalidateVisual();}
                foreach(var scenario in new[]{"direct-scroll","wheel-scroll","dock-drag","navigation"})
                for(var trial=1;trial<=3;trial++)
                {
                    form.PageChange(FormMain.PageType.Setup,FormMain.PageSubType.SetupLaunch);
                    await Task.Delay(1200);
                    var scroll=ModMain.frmSetupLaunch!.PanBack;
                    scroll.ScrollToVerticalOffset(scroll.ScrollableHeight/2);
                    await Task.Delay(500);
                    if(scenario.Contains("scroll") && scroll.ScrollableHeight<=0)throw new InvalidOperationException("No scroll range for "+scenario);
                    await listener.Measure(form,scroll,scenario,mode,trial,2,false,output,budget);
                    await listener.Measure(form,scroll,scenario,mode,trial,10,true,output,budget);
                    if(!ReferenceEquals(form.ImgBack.Background,wallpaper))throw new InvalidOperationException("Background fixture changed during measurement");
                    report.Add($"PASS sampled {mode}/{scenario}/{trial}");
                    File.WriteAllLines(Path.Combine(output,"progress.txt"),report);
                }
            }
            LiquidGlassSurface.DiagnosticBackdropOverride=null;
            for(var i=0;i<dock.Length;i++)dock[i].GlassEnabled=enabled[i];
            form.PageChange(FormMain.PageType.Setup,FormMain.PageSubType.SetupLaunch);
            await Task.Delay(800);
            LiquidGlassSmoke.Capture(form,Path.Combine(output,"restored.png"));
            report.Add("PASS diagnostic overrides restored; final screenshot saved");
            if(!baseline)VerifyFrameDedup(form,report);
            await VerifyInteractionCompletion(form,report);
        }
        finally
        {
            _ = FramePacingProbe.Log;
            LiquidGlassSurface.DiagnosticBackdropOverride=null;
            FramePacingProbe.DiagnosticBypassDedup=false;
            // Restore dynamic resource bindings, rather than leaving a local diagnostic value.
            foreach(var surface in dock)surface.SetResourceReference(LiquidGlassSurface.GlassEnabledProperty,"LiquidGlass.Enabled");
            form.Width=originalWidth;form.Height=originalHeight;
            form.ImgBack.Background=oldBackground;form.ImgBack.Visibility=oldBackgroundVisibility;form.ImgBack.Opacity=oldBackgroundOpacity;
            form.VideoBack.Source=oldVideoSource;
            Config.Preference.Theme.ColorMode=oldColorMode;ThemeService.RefreshColorMode();FluentTheme.Refresh();
            Config.Preference.Background.WallpaperSource=oldWallpaperSource;
        }
        Visual Freeze(Visual visual)
        {
            if(snapshots.TryGetValue(visual,out var cached))return cached;
            var size=((FrameworkElement)visual).RenderSize;var dpi=VisualTreeHelper.GetDpi(visual);
            var bitmap=new RenderTargetBitmap(Math.Max(1,(int)Math.Ceiling(size.Width*dpi.DpiScaleX)),Math.Max(1,(int)Math.Ceiling(size.Height*dpi.DpiScaleY)),96*dpi.DpiScaleX,96*dpi.DpiScaleY,PixelFormats.Pbgra32);
            bitmap.Render(visual);bitmap.Freeze();
            var drawing=new DrawingVisual();using(var context=drawing.RenderOpen())context.DrawImage(bitmap,new Rect(size));
            snapshots[visual]=drawing;return drawing;
        }
    }

    private async Task Measure(FormMain form,MyScrollViewer scroll,string scenario,string mode,int trial,int seconds,bool save,string output,double? budget)
    {
        lock(_samples){_samples.Clear();_dropped=0;}
        var timer=Stopwatch.StartNew();var intervals=new List<double>();var response=new List<double>();
        var inputTimes=new Queue<long>();var lastStamp=TimeSpan.MinValue;long previous=0;
        var raw=0;var layouts=0;var step=-1;var direction=1;
        var initialGc=Enumerable.Range(0,3).Select(GC.CollectionCount).ToArray();
        var dispatcherStarts=new Dictionary<DispatcherOperation,long>();var dispatcherDurations=new List<double>();
        DispatcherHookEventHandler start=(_,e)=>{if(e.Operation.Priority==DispatcherPriority.Render)dispatcherStarts[e.Operation]=Stopwatch.GetTimestamp();};
        DispatcherHookEventHandler end=(_,e)=>{if(dispatcherStarts.Remove(e.Operation,out var stamp))dispatcherDurations.Add(Milliseconds(Stopwatch.GetTimestamp()-stamp));};
        EventHandler layout=(_,_)=>layouts++;
        ScrollChangedEventHandler changed=(_,e)=>{
            if(e.VerticalChange==0)return;
            var now=Stopwatch.GetTimestamp();
            while(inputTimes.TryDequeue(out var sent))
            {
                response.Add(Milliseconds(now-sent));
                FramePacingProbe.Log.Sample("wheel.scroll-response",sent,now,scroll.VerticalOffset);
            }
            FramePacingProbe.Log.Sample("scroll.changed",now,now,scroll.VerticalOffset);
        };
        void Act()
        {
            var t=timer.Elapsed.TotalSeconds;var next=(int)(t/(scenario=="navigation"?.8:.09));
            if(scenario=="direct-scroll")scroll.ScrollToVerticalOffset((.5-.45*Math.Cos(t*2))*scroll.ScrollableHeight);
            else if(scenario=="wheel-scroll" && next!=step)
            {
                if(scroll.VerticalOffset>=scroll.ScrollableHeight-1)direction=-1;
                if(scroll.VerticalOffset<=1)direction=1;
                var now=Stopwatch.GetTimestamp();inputTimes.Enqueue(now);
                FramePacingProbe.Log.Sample("wheel.input",now,now,direction);
                scroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice,Environment.TickCount,-120*direction) {RoutedEvent=Mouse.MouseWheelEvent,Source=scroll});
            }
            else if(scenario=="dock-drag")
            {
                var dock=form.LiquidDock!;
                if(!dock.Motion.Dragging && !dock.BeginDrag(new Point(Canvas.GetLeft(form.DockHighlight)+form.DockHighlight.Width/2,32),false))
                    throw new InvalidOperationException("Diagnostic dock drag did not begin");
                dock.DragTo(form.PrimaryNavigation.ActualWidth*(.5+.32*Math.Sin(t*4)));
            }
            else if(scenario=="navigation" && next!=step)
            {
                using var probe=FramePacingProbe.Log.Measure("navigation.input",next%2);
                form.PageChange(next%2==0?FormMain.PageType.Setup:FormMain.PageType.Tools);
            }
            step=next;
        }
        EventHandler render=(_,e)=>{
            raw++;var stamp=((RenderingEventArgs)e).RenderingTime;
            if(stamp==lastStamp)return;lastStamp=stamp;
            var now=Stopwatch.GetTimestamp();if(previous!=0)intervals.Add(Milliseconds(now-previous));previous=now;
            FramePacingProbe.Log.Sample("render.unique",now,now,stamp.Ticks);
            if(scenario=="direct-scroll" || scenario=="dock-drag")Act();
        };
        // Wheel/navigation cadence is independent of render callbacks, so a stall does not suppress input generation.
        var inputTimer=new DispatcherTimer(DispatcherPriority.Input){Interval=TimeSpan.FromMilliseconds(15)};
        inputTimer.Tick+=(_,_)=>Act();
        _collect=save;form.LayoutUpdated+=layout;scroll.ScrollChanged+=changed;
        var hooks=form.Dispatcher.Hooks;hooks.OperationStarted+=start;hooks.OperationCompleted+=end;
        CompositionTarget.Rendering+=render;
        if(scenario=="wheel-scroll" || scenario=="navigation")inputTimer.Start();
        try {await Task.Delay(TimeSpan.FromSeconds(seconds));}
        finally
        {
            inputTimer.Stop();CompositionTarget.Rendering-=render;hooks.OperationStarted-=start;hooks.OperationCompleted-=end;
            form.LayoutUpdated-=layout;scroll.ScrollChanged-=changed;_collect=false;
            if(form.LiquidDock?.Motion.Dragging==true)form.LiquidDock.FinishDrag();
        }
        if(!save)return;
        Sample[] samples;lock(_samples)samples=_samples.ToArray();
        if(scenario=="wheel-scroll" && response.Count==0)throw new InvalidOperationException("Wheel test did not change the scroll offset");
        if(scenario=="dock-drag" && !samples.Any(s=>s.Stage=="dock.advance"))throw new InvalidOperationException("Dock test did not advance motion");
        var stem=$"{mode}-{scenario}-{trial}";
        using(var writer=new StreamWriter(Path.Combine(output,stem+".csv")))
        {
            writer.WriteLine("stage,startQpc,endQpc,value,managedThread");
            foreach(var s in samples)writer.WriteLine(FormattableString.Invariant($"{s.Stage},{s.Start},{s.End},{s.Value},{s.Thread}"));
        }
        var summary=new {mode,scenario,trial,elapsedMs=timer.Elapsed.TotalMilliseconds,rawCallbacks=raw,
            callbackIntervalsMs=Stats(intervals),overTwoRefreshPeriods=budget is {} b?(int?)intervals.Count(x=>x>2*b):null,
            layoutUpdatedCount=layouts,renderDispatcherProxyMs=Stats(dispatcherDurations),scrollResponseMs=Stats(response),unansweredWheelInputs=inputTimes.Count,
            gcCollections=Enumerable.Range(0,3).Select(g=>GC.CollectionCount(g)-initialGc[g]).ToArray(),droppedSamples=_dropped,
            stages=samples.GroupBy(s=>s.Stage).ToDictionary(g=>g.Key,g=>Stats(g.Select(s=>Milliseconds(s.End-s.Start)))),
            duplicateAnimationStamps=samples.Where(s=>s.Stage is "dock.advance" or "slider.advance").GroupBy(s=>(s.Stage,s.Value)).Sum(g=>Math.Max(0,g.Count()-1))};
        File.WriteAllText(Path.Combine(output,stem+".json"),JsonSerializer.Serialize(summary,new JsonSerializerOptions {WriteIndented=true}));
    }

    [DllImport("user32.dll")]private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")]private static extern int ReleaseDC(IntPtr hwnd,IntPtr dc);
    [DllImport("gdi32.dll")]private static extern int GetDeviceCaps(IntPtr dc,int index);
    [DllImport("kernel32.dll")]private static extern uint GetCurrentThreadId();

    private static void VerifyFrameDedup(FormMain form,List<string> report)
    {
        var dock=form.LiquidDock!;
        if(!dock.BeginDrag(new Point(Canvas.GetLeft(form.DockHighlight)+form.DockHighlight.Width/2,32),false))
            throw new InvalidOperationException("Cannot start frame dedup validation");
        try
        {
            Verify(dock,"Frame");
            report.Add("PASS dock advances once per RenderingTime and accepts the next timestamp");
        }
        finally {dock.FinishDrag();}
        var slider=Descendants(form).OfType<MySlider>().FirstOrDefault(x=>x.IsVisible && x.IsLoaded);
        if(slider is null)throw new InvalidOperationException("No visible slider for frame dedup validation");
        try
        {
            slider.Motion.SetValue(.8,false);
            slider.GetType().GetMethod("StartRendering",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(slider,null);
            Verify(slider,"OnRendering");
            report.Add("PASS slider advances once per RenderingTime and accepts the next timestamp");
        }
        finally {slider.GetType().GetMethod("ResetInteraction",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(slider,null);}
        static void Verify(object target,string method)
        {
            var frame=target.GetType().GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic)!;
            var time=target.GetType().GetField("_lastTime",BindingFlags.Instance|BindingFlags.NonPublic)!;
            var args=Args(TimeSpan.FromDays(1));
            frame.Invoke(target,new object?[]{null,args});var first=(double)time.GetValue(target)!;
            Thread.Sleep(3);
            frame.Invoke(target,new object?[]{null,args});
            if((double)time.GetValue(target)! != first)throw new InvalidOperationException(method+" advanced twice for the same rendering timestamp");
            // A page switch can stop and restart the subscription within the same WPF frame.
            target.GetType().GetMethod(method=="Frame"?"Stop":"StopRendering",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(target,null);
            target.GetType().GetMethod(method=="Frame"?"Start":"StartRendering",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(target,null);
            var restarted=(double)time.GetValue(target)!;
            Thread.Sleep(3);
            frame.Invoke(target,new object?[]{null,args});
            if((double)time.GetValue(target)! != restarted)throw new InvalidOperationException(method+" advanced the same frame after restarting its subscription");
            frame.Invoke(target,new object?[]{null,Args(TimeSpan.FromDays(1)+TimeSpan.FromMilliseconds(10))});
            if((double)time.GetValue(target)!<=restarted)throw new InvalidOperationException(method+" rejected the next rendering timestamp");
            static RenderingEventArgs Args(TimeSpan stamp)=>(RenderingEventArgs)Activator.CreateInstance(typeof(RenderingEventArgs),BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,new object[]{stamp},CultureInfo.InvariantCulture)!;
        }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
        {
            var child=VisualTreeHelper.GetChild(root,i);yield return child;
            foreach(var descendant in Descendants(child))yield return descendant;
        }
    }

    private static async Task VerifyInteractionCompletion(FormMain form,List<string> report)
    {
        await Task.Delay(1000);
        if(form.LiquidDock is not {} dock || dock.Motion.Active || dock.IsRendering)
            throw new InvalidOperationException("Dock did not settle and unsubscribe after release");
        var scroll=ModMain.frmSetupLaunch!.PanBack;
        scroll.PerformVerticalOffsetDelta(scroll.ScrollableHeight*2);
        await Task.Delay(500);
        if(Math.Abs(scroll.VerticalOffset-scroll.ScrollableHeight)>1)
            throw new InvalidOperationException("Scroll animation did not reach the lower boundary");
        scroll.PerformVerticalOffsetDelta(-scroll.ScrollableHeight*2);
        await Task.Delay(500);
        if(scroll.VerticalOffset>1)throw new InvalidOperationException("Scroll animation did not reach the upper boundary");
        report.Add("PASS dock release settles/unsubscribes and scroll easing reaches both clamped boundaries");
    }
}
