using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace QFTPlus;
internal static class Program
{
 [STAThread] static void Main(string[] args)
 {
    var rootIndex=Array.IndexOf(args,"--root");
    var root=Path.GetFullPath(rootIndex>=0?args[rootIndex+1]:File.Exists(Path.Combine(AppContext.BaseDirectory,"release-manifest.json"))?AppContext.BaseDirectory:Directory.GetCurrentDirectory());
    var previewIndex=Array.IndexOf(args,"--render-preview");
    var preview=previewIndex>=0;
    using var mutex=new Mutex(true, "Local\\QFTPlus",out var first);
    using var show=new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\QFTPlus.Show");
    if(!first&&!preview) { if(!args.Contains("--from-vrcft")) show.Set(); return; }
    var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
#pragma warning disable WPF0001
    app.ThemeMode=ThemeMode.System;
#pragma warning restore WPF0001
    var window=new StudioWindow(root,preview);
    app.MainWindow=window;
    var registration=ThreadPool.RegisterWaitForSingleObject(show,(_,_)=>window.Dispatcher.BeginInvoke(()=>{window.Show();window.WindowState=WindowState.Normal;window.Activate();}),null,-1,false);
    app.Startup+=async(_,_)=>
    {
        window.Show();
        if(preview)
        {
            var pageIndex=Array.IndexOf(args,"--preview-page");
            if(pageIndex>=0) window.PreviewPage(args[pageIndex+1]);
            if(args.Contains("--preview-dark")) window.Theme("Dark");
            if(args.Contains("--preview-light")) window.Theme("Light");
            if(args.Contains("--preview-small")){window.Width=800;window.Height=610;}
            if(args.Contains("--preview-large-text")) window.SetTextScale(2);
            await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            var content=(FrameworkElement)window.Content;
            var bitmap=new RenderTargetBitmap((int)content.ActualWidth,(int)content.ActualHeight,96,96,PixelFormats.Pbgra32);
            bitmap.Render(content);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[previewIndex+1]))!);
            using(var stream=File.Create(args[previewIndex+1])) encoder.Save(stream);
            app.Shutdown();return;
        }
        if(args.Contains("--start")||args.Contains("--from-vrcft")) await window.Start(args.Contains("--from-vrcft"));
    };
    app.DispatcherUnhandledException+=(_,e)=>{window.Error(e.Exception.Message);e.Handled=true;};
    app.Run();registration.Unregister(null);
 }
}
