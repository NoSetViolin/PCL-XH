// Compiled only by this folder's Inject.targets. Does not touch real user data.
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using PCL.Core.App;
using PCL.Core.App.IoC;
using PCL.Core.App.Localization;
using PCL.Core.UI.Controls;
using PCL.Core.UI.Theme;

namespace PCL;
internal static class CardBlurSmoke
{
    static readonly string Root = Environment.GetEnvironmentVariable("PCL_CARD_QA_ROOT")
        ?? Path.Combine(Path.GetTempPath(),"pcl-card-blur-qa");
    [ModuleInitializer] internal static void Initialize()
    {
        Paths.Data=Path.Combine(Root,"instance");Paths.SharedData=Path.Combine(Root,"shared");
        Paths.SharedLocalData=Path.Combine(Root,"local");Paths.Temp=Path.Combine(Root,"temp");Paths.OldSharedData=Path.Combine(Root,"old");
        foreach(var path in new[]{Paths.Data,Paths.SharedData,Paths.SharedLocalData,Paths.Temp,Paths.OldSharedData})Directory.CreateDirectory(path);
        if(Environment.GetEnvironmentVariable("PCL_CARD_QA_ANIME_CACHE") is { Length: >0 } animeCache)
        {
            var cache=Path.Combine(Paths.Temp,"Cache","Background","anime.png");
            Directory.CreateDirectory(Path.GetDirectoryName(cache)!);File.Copy(animeCache,cache,true);
        }
        Lifecycle.When(LifecycleState.WindowCreated,()=>Application.Current.Dispatcher.BeginInvoke(async()=>await Run()));
    }
    sealed class Probe : LiquidGlassSurface
    {
        internal int Renders;
        protected override void OnRender(DrawingContext dc){Renders++;base.OnRender(dc);}
    }
    static object? Field(object target,string name)=>typeof(LiquidGlassSurface).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(target);
    static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
    static IEnumerable<DependencyObject> Items(DependencyObject root)
    {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);yield return child;foreach(var nested in Items(child))yield return nested;}
    }
    static async Task Run()
    {
        var report=new List<string>();Window? fixture=null;
        try
        {
            await Task.Delay(1300);
            var form=ModMain.frmMain;
            await ModBackground.CurrentTask;
            if(Environment.GetEnvironmentVariable("PCL_CARD_QA_ANIME")=="1")
                await CheckAnimeSidebar(form,report);
            // Keep fixtures deterministic and independent of wallpaper network availability.
            Config.Preference.Background.WallpaperSource=0;
            ModBackground.Refresh(0,false);await ModBackground.CurrentTask;
            form.VideoBack.Stop();form.VideoBack.Source=null;form.ImgBack.Background=null;
            Config.Preference.Blur.IsEnabled=true;Config.Preference.Blur.Radius=16;
            FluentTheme.Refresh();
            if(Environment.GetEnvironmentVariable("PCL_CARD_QA_SIDEBAR_ONLY")=="1")
            {
                await CheckSidebar(form,report);
                return;
            }
            var pattern=new DrawingGroup();
            using(var dc=pattern.Open())
            {
                dc.DrawRectangle(Brushes.White,null,new Rect(0,0,32,32));
                dc.DrawRectangle(Brushes.DarkSlateBlue,null,new Rect(0,0,8,32));
                dc.DrawRectangle(Brushes.Coral,null,new Rect(16,0,8,32));
            }
            pattern.Freeze();
            var wallpaper=new DrawingBrush(pattern){TileMode=TileMode.Tile,ViewportUnits=BrushMappingMode.Absolute,Viewport=new Rect(0,0,32,32),Stretch=Stretch.Fill};
            wallpaper.Freeze();form.LiquidBackdrop.Background=wallpaper;
            var root=new Grid();var source=new Grid{Background=wallpaper};root.Children.Add(source);
            var cards=Enumerable.Range(0,12).Select(i=>new Probe{Width=180,Height=120,Background=Brushes.Transparent,
                HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Top,
                Margin=new Thickness(24+(i%4)*210,24+(i/4)*160,0,0),CornerRadius=new CornerRadius(18),BlurRadius=12}).ToArray();
            foreach(var card in cards){LiquidGlassSurface.SetUseLightweightMaterial(card,true);root.Children.Add(card);}
            LiquidGlassSurface.SetBackdropSource(root,source);
            fixture=new Window{Width=920,Height=570,Content=root,ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.CenterScreen};fixture.Show();await Task.Delay(450);
            report.Add($"INFO tier={RenderCapability.Tier>>16}; ps3={RenderCapability.IsPixelShaderVersionSupported(3,0)}");
            Check(Field(cards[0],"_cardBackdrop") is not null,"Card blur cache missing");
            Check(cards.All(c=>ReferenceEquals(Field(c,"_cardBackdrop"),Field(cards[0],"_cardBackdrop"))),"Cards do not share their backdrop");
            Check(cards.All(c=>Field(c,"_sample")==null && Field(c,"_effect")==null),"Cards retain full glass graphs");
            var shared=Field(cards[0],"_cardBackdrop")!;
            var visual=(Border)shared.GetType().GetProperty("Visual",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(shared)!;
            Check(Math.Abs(visual.ActualWidth-source.ActualWidth*.5-32)<.01,"Backdrop is not half resolution with a bounded gutter");
            var blurred=Path.Combine(Root,"fixture-blurred.png");LiquidGlassSmoke.Capture(fixture,blurred);
            var blurredContrast=Contrast(blurred);
            foreach(var c in cards)c.BlurRadius=0;await Task.Delay(250);
            var flat=Path.Combine(Root,"fixture-flat.png");LiquidGlassSmoke.Capture(fixture,flat);
            var flatContrast=Contrast(flat);
            Check(blurredContrast<flatContrast*.65,$"Blur did not soften actual hardware pixels: blurred={blurredContrast}, flat={flatContrast}");
            report.Add($"PASS 12 cards share one half-resolution cache, no lens graphs; stripe edge contrast {flatContrast:F2} -> {blurredContrast:F2}");
            Check(cards.All(c=>Field(c,"_cardBackdrop")==null && Field(c,"_coordinateTracking") is false),"Disabling blur retains work");
            foreach(var c in cards)c.BlurRadius=12;await Task.Delay(250);
            var before=cards.Select(c=>c.Renders).ToArray();await Task.Delay(450);
            Check(cards.Select((c,i)=>c.Renders==before[i]).All(x=>x),"Idle cards keep invalidating");
            source.Background=Brushes.LimeGreen;await Task.Delay(250);
            var live=Path.Combine(Root,"fixture-live.png");LiquidGlassSmoke.Capture(fixture,live);
            var green=CenterPixel(live);
            Check(green[1]>green[2]+50 && green[1]>green[0]+50,"Shared cache froze live source changes");
            report.Add("PASS blur toggle releases cache/tracking, static cards stay idle, live source color propagates through cache");
            source.Background=wallpaper;
            cards[0].Margin=new Thickness(39,24,0,0);await Task.Delay(200);
            var crop=(MatrixTransform)Field(cards[0],"_cardTransform")!;
            var expected=cards[0].TransformToVisual(source).TransformBounds(new Rect(cards[0].RenderSize));
            Check(Math.Abs(crop.Transform(new Point(expected.X*.5+16,expected.Y*.5+16)).X)<.01,"Moving cards did not update the backdrop crop");
            cards[0].Margin=new Thickness(24,24,0,0);
            cards[0].BlurRadius=64;await Task.Delay(150);
            Check(ReferenceEquals(Field(cards[0],"_cardBackdrop"),Field(cards[1],"_cardBackdrop")),"Blur radius cap did not reuse the 12-DIP cache");
            cards[0].BlurRadius=12;
            cards[0].BlurKernelType=System.Windows.Media.Effects.KernelType.Box;await Task.Delay(150);
            Check(!ReferenceEquals(Field(cards[0],"_cardBackdrop"),Field(cards[1],"_cardBackdrop")),"Different blur kernels incorrectly share a cache");
            cards[0].BlurKernelType=System.Windows.Media.Effects.KernelType.Gaussian;
            var native=(HwndSource)PresentationSource.FromVisual(fixture);
            native.CompositionTarget.RenderMode=RenderMode.SoftwareOnly;
            foreach(var c in cards){c.BlurRadius=0;c.BlurRadius=12;}await Task.Delay(200);
            Check(cards.All(c=>Field(c,"_cardBackdrop")==null && Field(c,"_coordinateTracking") is false),"Software HWND retains card blur work");
            native.CompositionTarget.RenderMode=RenderMode.Default;
            foreach(var c in cards){c.BlurRadius=0;c.BlurRadius=12;}await Task.Delay(200);
            report.Add("PASS moving crop, capped radius cache reuse, kernel separation and real software-HWND fallback");
            foreach(var mode in new[]{"flat","shared","lens"})
            {
                foreach(var c in cards){LiquidGlassSurface.SetUseLightweightMaterial(c,mode!="lens");c.BlurRadius=mode=="flat"?0:12;}
                var offset=new TranslateTransform();source.RenderTransform=offset;
                offset.BeginAnimation(TranslateTransform.XProperty,new DoubleAnimation(0,24,TimeSpan.FromMilliseconds(600)){AutoReverse=true,RepeatBehavior=RepeatBehavior.Forever});
                await Task.Delay(400);
                using var process=Process.GetCurrentProcess();var cpu=process.TotalProcessorTime;var timer=Stopwatch.StartNew();
                var intervals=new List<double>();double last=0;EventHandler frame=(_,_)=>{var now=timer.Elapsed.TotalMilliseconds;if(last>0)intervals.Add(now-last);last=now;};
                CompositionTarget.Rendering+=frame;await Task.Delay(1600);CompositionTarget.Rendering-=frame;
                report.Add($"INFO 12 animated-background cards {mode}: CPU={(process.TotalProcessorTime-cpu).TotalMilliseconds:F1} ms / {timer.Elapsed.TotalMilliseconds:F1} ms, callbacks={intervals.Count}, max interval={intervals.DefaultIfEmpty().Max():F2} ms (not displayed FPS)");
                offset.BeginAnimation(TranslateTransform.XProperty,null);source.RenderTransform=null;
            }
            foreach(var c in cards){LiquidGlassSurface.SetUseLightweightMaterial(c,true);c.BlurRadius=12;}await Task.Delay(250);
            Check(cards.All(c=>Field(c,"_sample")==null && Field(c,"_effect")==null && Field(c,"_cardBackdrop")!=null),"Switching back from full lens retains old graphs");
            fixture.Width=1000;await Task.Delay(250);
            shared=Field(cards[0],"_cardBackdrop")!;visual=(Border)shared.GetType().GetProperty("Visual",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(shared)!;
            Check(Math.Abs(visual.ActualWidth-source.ActualWidth*.5-32)<.01,"Shared cache did not resize");
            foreach(var c in cards)c.GlassEnabled=false;await Task.Delay(200);
            Check(cards.All(c=>Field(c,"_cardBackdrop")==null),"Opaque fallback retains cache");
            foreach(var c in cards)c.GlassEnabled=true;await Task.Delay(200);
            foreach(var c in cards)root.Children.Remove(c);await Task.Delay(100);
            Check(cards.All(c=>Field(c,"_cardBackdrop")==null && Field(c,"_coordinateTracking") is false),"Unloaded cards retain caches/subscriptions");
            report.Add("PASS resize, opaque fallback and unload release/refresh cache correctly");
            await CheckFlatCards(fixture,root,source,report);
            fixture.Close();fixture=null;
            foreach(var language in new[]{"zh-CN","en-US"})
            foreach(var dark in new[]{false,true})
            foreach(var wide in new[]{false,true})
            {
                LocalizationService.Apply(language,save:false);Config.Preference.Theme.ColorMode=dark?ColorMode.Dark:ColorMode.Light;ThemeService.RefreshColorMode();FluentTheme.Refresh();
                form.LiquidBackdrop.Background=wallpaper;form.Width=wide?1350:810;form.Height=wide?825:470;
                foreach(var page in new[]{FormMain.PageType.Launch,FormMain.PageType.Setup,FormMain.PageType.Download})
                {
                    form.PageChange(page);await Task.Delay(500);
                    var active=Items(form).OfType<LiquidGlassSurface>().Where(c=>c.IsVisible && c.ActualWidth>0 && c.ActualHeight>0).ToArray();
                    Check(active.Where(c=>c.Material is LiquidGlassMaterial.BottomTabs or LiquidGlassMaterial.BottomTabIndicator).All(c=>Field(c,"_effect")!=null),"Dock glass graph lost");
                    Check(active.Where(c=>c.Material is LiquidGlassMaterial.Content or LiquidGlassMaterial.Chrome).Any(c=>Field(c,"_cardBackdrop")!=null),"Page has no blurred cards");
                    Check(active.Where(c=>c.Material is LiquidGlassMaterial.Content or LiquidGlassMaterial.Chrome).All(c=>c.Effect==null && Field(c,"_effect")==null),"Card foreground blurred or lens work retained");
                    var name=$"{language}-{(dark?"dark":"light")}-{form.Width}-{page}";
                    LiquidGlassSmoke.Capture(form,Path.Combine(Root,name+".png"));report.Add("PASS "+name+": shared card blur, clear foreground and full dock glass");
                }
            }
        }
        catch(Exception ex){report.Add("FAIL "+ex);}
        finally{fixture?.Close();File.WriteAllLines(Path.Combine(Root,"report.txt"),report);Application.Current.Shutdown();}
    }
    static async Task CheckFlatCards(Window window,Grid root,Grid source,List<string> report)
    {
        source.Background=Brushes.White;
        var card=new MyCard{Width=180,Height=120,Title="Flat blur",HorizontalAlignment=HorizontalAlignment.Left,
            VerticalAlignment=VerticalAlignment.Top,Margin=new Thickness(24,24,0,0),UseAnimation=false};
        LiquidGlassSurface.SetUseLightweightMaterial(card,true);root.Children.Add(card);
        foreach(var dark in new[]{false,true})
        {
            Config.Preference.Theme.ColorMode=dark?ColorMode.Dark:ColorMode.Light;
            ThemeService.RefreshColorMode();FluentTheme.Refresh();await Task.Delay(200);
            var stroke=(SolidColorBrush)Application.Current.Resources["Fluent.Stroke"];
            var brightness=stroke.Color.R+stroke.Color.G+stroke.Color.B;
            Check(stroke.Color.A<=50 && (dark?brightness>600:brightness<400),"Card stroke is not a faint contrasting gray");
            foreach(var state in new[]{"rest","hover","leave"})
            {
                if(state!="rest")typeof(MyCard).GetMethod(state=="hover"?"MyCard_MouseEnter":"MyCard_MouseLeave",BindingFlags.Instance|BindingFlags.NonPublic)!
                    .Invoke(card,new object[]{card,new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice,Environment.TickCount)});
                await Task.Delay(160);
                Check(card.MainChrome.Visibility==Visibility.Collapsed && card.MainChrome.Opacity==0,"Card shadow returned on hover");
                Check(Items(card).OfType<LiquidGlassSurface>().All(s=>s.Effect==null && Field(s,"_effect")==null),"Flat card has a foreground effect or lens");
                var path=Path.Combine(Root,$"flat-card-{dark}-{state}.png");LiquidGlassSmoke.Capture(window,path);
                GetWindowRect(new WindowInteropHelper(window).Handle,out var rect);
                foreach(var local in new[]{new Point(-2,60),new Point(182,60),new Point(90,-2),new Point(90,122)})
                {
                    var point=card.PointToScreen(local);var pixel=new byte[4];
                    Decode(path).CopyPixels(new Int32Rect((int)Math.Round(point.X)-rect.Left,(int)Math.Round(point.Y)-rect.Top,1,1),pixel,4,0);
                    Check(pixel[0]>=254 && pixel[1]>=254 && pixel[2]>=254,"Card casts a dark halo outside its bounds");
                }
                report.Add($"PASS flat card dark={dark} state={state}: collapsed shadow, no lens/effect, faint contrasting stroke, white exterior pixels");
            }
        }
        root.Children.Remove(card);
    }
    static async Task CheckSidebar(FormMain form,List<string> report)
    {
        foreach(var language in new[]{"zh-CN","en-US"})
        foreach(var dark in new[]{false,true})
        foreach(var wide in new[]{false,true})
        {
            LocalizationService.Apply(language,save:false);Config.Preference.Theme.ColorMode=dark?ColorMode.Dark:ColorMode.Light;
            ThemeService.RefreshColorMode();FluentTheme.Refresh();
            form.LiquidBackdrop.Background=new SolidColorBrush(Color.FromRgb(200,200,200));
            form.Width=wide?1350:810;form.Height=wide?825:470;
            form.PageChange(FormMain.PageType.InstanceSelect);await Task.Delay(600);
            var surface=form.CategorySurface;
            var scenario=$"sidebar-{language}-{dark}-{form.Width}";
            report.Add($"INFO {scenario}: lightweight={LiquidGlassSurface.GetUseLightweightMaterial(surface)}, material={surface.Material}, lens={Field(surface,"_effect")!=null}, shared={Field(surface,"_cardBackdrop")!=null}");
            var shared=Field(surface,"_cardBackdrop")!;
            var cachedVisual=(Border)shared.GetType().GetProperty("Visual",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(shared)!;
            var source=LiquidGlassSurface.GetBackdropSource(surface)!;
            report.Add($"INFO geometry source={source.RenderSize}, cache={cachedVisual.RenderSize}, cacheBounds={VisualTreeHelper.GetDescendantBounds(cachedVisual)}, crop={Field(surface,"_sourceRect")}, sourceBounds={VisualTreeHelper.GetDescendantBounds(source)}");
            LiquidGlassSmoke.Capture(form,Path.Combine(Root,scenario+".png"));
            var contentVisibility=form.PanMainLeft.Visibility;var actionsVisibility=form.CategoryActions.Visibility;
            form.PanMainLeft.Visibility=Visibility.Hidden;form.CategoryActions.Visibility=Visibility.Hidden;
            await Task.Delay(120);
            var path=Path.Combine(Root,scenario+"-surface.png");LiquidGlassSmoke.Capture(form,path);
            form.PanMainLeft.Visibility=contentVisibility;form.CategoryActions.Visibility=actionsVisibility;
            GetWindowRect(new WindowInteropHelper(form).Handle,out var rect);
            var points=new List<Point>{new(surface.ActualWidth/2,surface.ActualHeight/2)};
            foreach(var distance in new[]{4d,8d,12d,20d})
            {
                points.Add(new Point(surface.ActualWidth/2,distance));points.Add(new Point(surface.ActualWidth/2,surface.ActualHeight-distance));
                points.Add(new Point(distance,surface.ActualHeight/2));points.Add(new Point(surface.ActualWidth-distance,surface.ActualHeight/2));
            }
            var pixels=points.Select(local=>{var point=surface.PointToScreen(local);var pixel=new byte[4];Decode(path).CopyPixels(new Int32Rect((int)Math.Round(point.X)-rect.Left,(int)Math.Round(point.Y)-rect.Top,1,1),pixel,4,0);return pixel;}).ToArray();
            var difference=pixels.Skip(1).Max(p=>Enumerable.Range(0,3).Max(i=>Math.Abs(p[i]-pixels[0][i])));
            report.Add($"INFO {scenario}: uniform input interior={pixels[0][0]}, edge difference={difference}; samples="+string.Join(";",pixels.Select(p=>p[0])));
            Check(difference<=2,"Sidebar has a dark inner rim on uniform input: "+difference);
            Check(Field(surface,"_effect")==null && Field(surface,"_cardBackdrop")!=null,"Sidebar is not using flat blur");
            report.Add("PASS "+scenario+": actual folder-list sidebar has uniform interior and edges");
        }
    }
    static async Task CheckAnimeSidebar(FormMain form,List<string> report)
    {
        if(form.ImgBack.Background is not ImageBrush)
        {
            Config.Preference.Background.WallpaperSource=2;ModBackground.Refresh(2,false);
            await ModBackground.CurrentTask.WaitAsync(TimeSpan.FromSeconds(30));
        }
        Check(form.ImgBack.Background is ImageBrush,"Random anime background did not load: "+ModBackground.Status);
        report.Add("PASS random anime background loaded through the normal background service; status="+ModBackground.Status);
        foreach(var language in new[]{"zh-CN","en-US"})
        foreach(var dark in new[]{false,true})
        foreach(var wide in new[]{false,true})
        {
            LocalizationService.Apply(language,save:false);Config.Preference.Theme.ColorMode=dark?ColorMode.Dark:ColorMode.Light;
            ThemeService.RefreshColorMode();FluentTheme.Refresh();form.Width=wide?1350:810;form.Height=wide?825:470;
            foreach(var page in new[]{FormMain.PageType.InstanceSelect,FormMain.PageType.Launch,FormMain.PageType.Setup})
            {
                form.PageChange(page);await Task.Delay(500);
                var name=$"random-anime-{language}-{dark}-{form.Width}-{page}";
                LiquidGlassSmoke.Capture(form,Path.Combine(Root,name+".png"));
                if(page!=FormMain.PageType.Launch)
                    Check(Field(form.CategorySurface,"_effect")==null && Field(form.CategorySurface,"_cardBackdrop")!=null,"Anime sidebar is not flat blur");
                report.Add("PASS "+name+": actual anime backdrop, flat card/sidebar pipeline");
            }
        }
    }
    [StructLayout(LayoutKind.Sequential)] struct NativeRect{public int Left,Top,Right,Bottom;}
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr handle,out NativeRect rect);
    // Native captures are required: RenderTargetBitmap cannot validate hardware effects.
    static BitmapSource Decode(string path)=>BitmapDecoder.Create(new Uri(path),BitmapCreateOptions.None,BitmapCacheOption.OnLoad).Frames[0];
    static byte[] CenterPixel(string path)
    {
        var bytes=new byte[4];Decode(path).CopyPixels(new Int32Rect(105,110,1,1),bytes,4,0);return bytes;
    }
    static double Contrast(string path)
    {
        var image=Decode(path);var pixels=new byte[120*4];image.CopyPixels(new Int32Rect(50,110,120,1),pixels,pixels.Length,0);
        return Enumerable.Range(1,119).Average(i=>Math.Abs(pixels[i*4]-pixels[(i-1)*4]));
    }
}
