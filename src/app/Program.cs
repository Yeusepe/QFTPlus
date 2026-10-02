using System.Windows;
using Velopack;

namespace QFTPlus;
internal static class Program
{
 [STAThread] static void Main(string[] args)
 {
    VelopackApp.Build().OnBeforeUninstallFastCallback(_=>Uninstall.Run(WorkingCopy.Folder)).Run();
    System.Windows.Forms.Application.EnableVisualStyles();
    var rootIndex=Array.IndexOf(args,"--root");
    using var mutex=new Mutex(true, "Local\\QFTPlus",out var first);
    using var show=new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\QFTPlus.Show");
    if(!first) { if(!args.Contains("--from-vrcft")) show.Set(); return; }
    string root;
    try
    {
        var installed=WorkingCopy.Installed();
        var working=installed is null?null:WorkingCopy.Prepare(installed);
        root=Path.GetFullPath(rootIndex>=0?args[rootIndex+1]:working
            ??(File.Exists(Path.Combine(AppContext.BaseDirectory,"release-manifest.json"))?AppContext.BaseDirectory:Directory.GetCurrentDirectory()));
    }
    catch(Exception error) when(error is IOException or UnauthorizedAccessException)
    {
        System.Windows.Forms.TaskDialog.ShowDialog(new System.Windows.Forms.TaskDialogPage{Caption="QFT+",Heading="QFT+ couldn’t open its folder",
            Text=(SteamVrDriver.Loaded()?"SteamVR is using the QFT+ driver from it. Close SteamVR, then open QFT+ again.":"Close other QFT+ windows, then open QFT+ again.")+"\n\n"+error.Message});
        return;
    }
    try{CalibrationSettings.RemoveLegacy(root,SetupService.AutoPath);}
    catch(Exception error) when(error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException){System.Diagnostics.Trace.WriteLine("Removing older calibrations: "+error.Message);}
    var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
#pragma warning disable WPF0001
    app.ThemeMode=ThemeMode.System;
#pragma warning restore WPF0001
    var window=new StudioWindow(root);
    app.MainWindow=window;
    var registration=ThreadPool.RegisterWaitForSingleObject(show,(_,_)=>window.Dispatcher.BeginInvoke(()=>{window.Show();window.WindowState=WindowState.Normal;window.Activate();}),null,-1,false);
    app.Startup+=async(_,_)=>
    {
        window.Show();
        if(args.Contains("--start")||args.Contains("--from-vrcft")) await window.Start(args.Contains("--from-vrcft"));
    };
    app.DispatcherUnhandledException+=(_,e)=>{Session.Log(Path.Combine(root,"studio.log"),$"{DateTimeOffset.Now:O} Unhandled: {e.Exception}");window.Error(e.Exception.Message);e.Handled=true;};
    app.Run();registration.Unregister(null);
    Adb.Stop();
 }
}
