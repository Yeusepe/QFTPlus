using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace QFTPlus;
public partial class StudioWindow : Window
{
 readonly Session session;
 readonly bool preview;
 readonly DispatcherTimer timer=new(){Interval=TimeSpan.FromMilliseconds(250)};
 readonly HttpClient http=new(){Timeout=TimeSpan.FromSeconds(1)};
 readonly System.Windows.Forms.NotifyIcon? tray;
 string page="Tracking", kind="tongue", runtimeId="", candidate="", prefix="";
 JsonObject state=new();
 long command;
 bool busy, polling, recording, training, quitting, reloadPending;
 DateTime heartbeat=DateTime.MinValue;
 TextBlock? poseTitle, poseDetail, cue, count;
 ProgressBar? progress;
 Canvas? face;
 Button? pause, apply, train, begin, cancel;
 double applyingAt;
 readonly List<(Image Image,string Key)> cameras=new();
 readonly Dictionary<string,ListBoxItem> nav=new();
 bool automatic=true;
 double settle=2.5;
 public StudioWindow(string root,bool preview=false)
 {
    InitializeComponent();this.preview=preview;session=new(root);
    session.Changed+=_=>Dispatcher.Invoke(()=>
    {
        Connection.Text=session.State;StartButton.Content=busy&&!session.Running?"Cancel":session.Running?"Stop":"Start";
        if(session.SettingUp)SetupProgress();
        // The Setup page shows each instruction beside its step; other pages use the notice.
        if(page=="Setup")setupRefresh?.Invoke();else if(busy&&session.Detail.Length>0)Error(session.Detail);
    });
    foreach(var name in new[]{"Tracking","Adjustments","Manual","Calibration","Cameras","Setup","Settings"})
    {
        var title=name=="Manual"?"Test movements":name;
        var item=new ListBoxItem{Content=new TextBlock{Text=title},Tag=name};
        AutomationProperties.SetName(item,title);TextSearch.SetText(item,title);
        if(name=="Setup")item.Margin=new(0,16,0,0);
        Navigation.Items.Add(item);nav.Add(name,item);
    }
    Navigation.SelectionChanged+=(_,_)=>{if(Navigation.SelectedItem is ListBoxItem item&&item.Tag is string name&&name!=page)Navigate(name);};
    SetTextScale(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Accessibility","TextScaleFactor",100) is int scale?scale/100.0:1);
    Theme(session.Config["theme"]?.GetValue<string>()??"System");Navigate("Tracking");
    SizeChanged+=(_,_)=>ResizeGuide();
    if(!preview)
    {
        tray=new(){Icon=System.Drawing.SystemIcons.Application,Text="QFT+",Visible=true};
        var menu=new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open QFT+",null,(_,_)=>Dispatcher.Invoke(()=>{Show();WindowState=WindowState.Normal;Activate();}));
        menu.Items.Add("Settings",null,(_,_)=>Dispatcher.Invoke(()=>{Show();Navigate("Settings");Activate();}));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Quit QFT+",null,async(_,_)=>await Quit());tray.ContextMenuStrip=menu;
        tray.DoubleClick+=(_,_)=>Dispatcher.Invoke(()=>{Show();Activate();});
        timer.Tick+=async(_,_)=>await Tick();timer.Start();
        SystemEvents.UserPreferenceChanged+=PreferencesChanged;
        Closed+=(_,_)=>SystemEvents.UserPreferenceChanged-=PreferencesChanged;
    }
    PreviewKeyDown+=(_,e)=>{if(Keyboard.Modifiers==ModifierKeys.Control&&e.Key==Key.OemComma){Navigate("Settings");e.Handled=true;}};
    Closing+=(_,e)=>
    {
        StopManual();
        if(quitting||preview)return;
        if(busy){e.Cancel=true;session.CancelSetup();return;}
        if(recording) { e.Cancel=true; Error("Finish or cancel calibration before closing this window.");return; }
        if(training||session.Running){e.Cancel=true;Hide();return;}
        quitting=true;tray?.Dispose();Application.Current.Shutdown();
    };
 }
 internal void Error(string message){Notice.Text=message;NoticeBox.Visibility=Visibility.Visible;SetupHelp.Visibility=session.HelpTarget is null?Visibility.Collapsed:Visibility.Visible;SetupHelp.Content=session.HelpCaption;}
 void HelpClick(object sender,RoutedEventArgs e){if(session.HelpTarget is {} target)Open(target);}
 void ClearNotice(){NoticeBox.Visibility=Visibility.Collapsed;}
 internal async Task Start(bool fromModule=false)
 {
    if(busy)return;busy=true;StartButton.Content="Cancel";ClearNotice();
    try {await session.Start(fromModule);ClearNotice();}
    catch(OperationCanceledException){session.Notify("Ready");ClearNotice();}
    catch(Exception error){Error(error.Message);session.Notify("Needs attention");}
    finally {busy=false;StartButton.IsEnabled=true;StartButton.Content=session.Running?"Stop":"Start";}
 }
 async void StartClick(object sender,RoutedEventArgs e)
 {
    if(busy){session.CancelSetup();return;}
    if(!session.Running){await Start();return;}
    if(recording||training){Error("Finish calibration before stopping tracking.");return;}
    StopManual();
    busy=true;StartButton.IsEnabled=false;
    try{await session.Stop();}catch(Exception error){Error(error.Message);}finally{busy=false;StartButton.IsEnabled=true;}
 }
 async Task Quit()
 {
    if(busy){Show();session.CancelSetup();Error("Finishing the current step before quitting.");return;}
    if(recording||training){Show();Error("Finish or cancel calibration before quitting.");return;}
    StartButton.IsEnabled=false;
    try{await session.Stop();quitting=true;timer.Stop();tray?.Dispose();http.Dispose();Application.Current.Shutdown();}
    catch(Exception error){Show();Error(error.Message);StartButton.IsEnabled=true;}
 }
 Button Button(string title,Action action,bool primary=false)
 {
    var button=new Button{Content=title,HorizontalAlignment=HorizontalAlignment.Left};
    if(primary)button.SetResourceReference(StyleProperty,"PrimaryButton");
    button.Click+=(_,_)=>action();return button;
 }
 Button AsyncButton(string title,Func<Task> action,bool primary=false)=>Button(title,async()=>{try{await action();}catch(Exception error){Error(error.Message);}},primary);
 TextBlock Text(string value,double size=14,bool muted=false)
 {
    var text=new TextBlock{Text=value,Margin=new(0,0,0,8)};text.SetResourceReference(TextBlock.FontSizeProperty,"Font"+size);if(muted)text.SetResourceReference(TextBlock.ForegroundProperty,"Muted");return text;
 }
 Border Card(UIElement child)
 {
    var card=new Border{Child=child,Padding=new(20),Margin=new(0,0,0,16)};card.SetResourceReference(Border.CornerRadiusProperty,"CardRadius");card.SetResourceReference(Border.BackgroundProperty,"Surface");return card;
 }
 void Navigate(string name)
 {
    if(recording||training){if(name!=page)Error("Finish or cancel calibration first.");return;}
    StopManual();setupRefresh=null;
    page=name;Page.Children.Clear();Actions.Children.Clear();cameras.Clear();ClearNotice();PageTitle.Text=name=="Manual"?"Test movements":name;Title=PageTitle.Text+" — QFT+";Subtitle.Visibility=Visibility.Collapsed;PageScroll.ScrollToTop();
    foreach(var item in nav){item.Value.IsSelected=item.Key==name;item.Value.FontWeight=item.Key==name?FontWeights.SemiBold:FontWeights.Normal;}
    Navigation.ScrollIntoView(nav[name]);
    switch(name){case "Tracking":Home();break;case "Adjustments":Adjustments();break;case "Manual":Manual();break;case "Calibration":Calibration();break;case "Cameras":Cameras();break;case "Setup":SetupOptions();break;case "Settings":Settings();break;}
 }
 void Home()
 {
    var rows=new StackPanel();
    foreach(var (key,title,calibration) in new[]{("tongueOutput","Tongue","tongue"),("extraFaceOutput","Independent cheek puff","puff"),("pupilDilation","Pupil dilation","pupils")})
    {
        var row=new DockPanel{Margin=new(0,8,0,8)};
        var calibrate=Button("Calibrate…",()=>{kind=calibration;Navigate("Calibration");});DockPanel.SetDock(calibrate,Dock.Right);
        AutomationProperties.SetName(calibrate,"Calibrate "+title.ToLowerInvariant());row.Children.Add(calibrate);
        var option=new CheckBox{Content=new TextBlock{Text=title},Margin=new(0,2,16,2),IsChecked=session.Config[key]?.GetValue<bool>()??key=="tongueOutput",VerticalAlignment=VerticalAlignment.Center};AutomationProperties.SetName(option,title);
        option.Click+=(_,_)=>{if(preview)return;session.Save(key,JsonValue.Create(option.IsChecked==true));if(session.Running)Send("reload");};
        row.Children.Add(option);rows.Children.Add(row);
    }
    Page.Children.Add(Card(rows));
    var hands=new StackPanel();var hybrid=new CheckBox{Content=new TextBlock{Text="Hybrid hands and controllers"},IsChecked=session.Config["hybridHands"]?.GetValue<bool>()==true};AutomationProperties.SetName(hybrid,"Hybrid hands and controllers");
    hybrid.Click+=async(_,_)=>{if(preview)return;hybrid.IsEnabled=false;busy=true;StartButton.IsEnabled=false;try{await session.SetHybrid(hybrid.IsChecked==true);}catch(Exception e){hybrid.IsChecked=false;Error(e.Message);}finally{hybrid.IsEnabled=true;busy=false;StartButton.IsEnabled=true;}};
    hands.Children.Add(hybrid);hands.Children.Add(Text("Pauses Virtual Desktop body tracking.",13,true));AutomationProperties.SetHelpText(hybrid,"Experimental. Pauses Virtual Desktop body tracking while enabled.");Page.Children.Add(Card(hands));
 }
 void Cameras()
 {
    Subtitle.Text=session.Running?"Waiting for cameras…":"Start tracking to view cameras.";Subtitle.Visibility=Visibility.Visible;
    var outline=new CheckBox{Content="Show pupil outlines",IsChecked=true};
    outline.Click+=(_,_)=>{for(var i=0;i<Math.Min(2,cameras.Count);i++)cameras[i]=(cameras[i].Image,(outline.IsChecked==true?"pupil":"camera")+i);};Page.Children.Add(outline);
    var grid=new UniformGrid{Columns=2};
    foreach(var (key,title) in new[]{("pupil0","Left eye"),("pupil1","Right eye"),("camera2","Left face"),("camera3","Right face"),("camera4","Brow")})
    {
        var stack=new StackPanel();stack.Children.Add(Text(title,14));var image=new Image{Height=180,Stretch=Stretch.Uniform};stack.Children.Add(image);cameras.Add((image,key));var card=Card(stack);card.Margin=new(0,8,12,8);grid.Children.Add(card);
    }
    Page.Children.Add(grid);
 }
 async Task UpdateCameras()
 {
    var available=false;
    foreach(var (image,key) in cameras.ToArray())
    {
        try
        {
            var data=await http.GetByteArrayAsync("http://127.0.0.1:8081/"+key+".jpg");
            var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.StreamSource=new MemoryStream(data);bitmap.EndInit();bitmap.Freeze();image.Source=bitmap;available=true;
        }
        catch(HttpRequestException){image.Source=null;}
        catch(TaskCanceledException){image.Source=null;}
    }
    if(page=="Cameras"){Subtitle.Text=session.Running?"Waiting for cameras…":"Start tracking to view cameras.";Subtitle.Visibility=available?Visibility.Collapsed:Visibility.Visible;}
 }
 void Settings()
 {
    var appearance=new StackPanel();var theme=new ComboBox{ItemsSource=new[]{"System","Light","Dark"},SelectedItem=session.Config["theme"]?.GetValue<string>()??"System"};
    Field(appearance,"Appearance",theme);
    theme.SelectionChanged+=(_,_)=>{Theme((string)theme.SelectedItem);if(!preview)session.Save("theme",JsonValue.Create((string)theme.SelectedItem));};
    Page.Children.Add(Card(appearance));
    StartupOptions();ProcessingOptions();
    var files=new StackPanel();
    foreach(var(name,path) in new[]{("Open recordings","captures"),("Open setup log","setup.log"),("Open tracking log","autostart.log"),("Open hybrid log","hybrid.log")})
    {
        var target=System.IO.Path.Combine(session.Root,path);var button=Button(name,()=>Open(target));button.IsEnabled=Directory.Exists(target)||File.Exists(target);button.Margin=new(0,5,0,5);files.Children.Add(button);
    }
    Page.Children.Add(new Expander{Header="Diagnostics",Content=files,Margin=new(0,16,0,0)});
 }
 internal void SetTextScale(double scale)
 {
    scale=Math.Clamp(scale,1,2.25);FontSize=14*scale;SidebarColumn.Width=new(Math.Min(280,196*scale));
    foreach(var size in new[]{12,13,14,16,18,23,25,30})Resources["Font"+size]=Math.Max(13,size)*scale;
 }
 void PreferencesChanged(object sender,UserPreferenceChangedEventArgs args)=>Dispatcher.BeginInvoke(()=>
 {
    SetTextScale(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Accessibility","TextScaleFactor",100) is int scale?scale/100.0:1);
    if(!(recording&&kind=="pupils"))Theme(session.Config["theme"]?.GetValue<string>()??"System");
 });
 internal void Theme(string name)
 {
    var dark=name=="Dark"||(name=="System"&&Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize","AppsUseLightTheme",1) is int value&&value==0);
#pragma warning disable WPF0001
    ThemeMode=dark?System.Windows.ThemeMode.Dark:System.Windows.ThemeMode.Light;
    Application.Current.ThemeMode=ThemeMode;
#pragma warning restore WPF0001
    var colors=dark?new[]{"#191A1E","#25262C","#202126","#F3F3F5","#ADB0BA","#383A43","#368CFF"}:new[]{"#F5F5F7","#FFFFFF","#EBEBEF","#202127","#626570","#E1E2E7","#0067D9"};
    var keys=new[]{"Canvas","Surface","Sidebar","Ink","Muted","Line","Accent"};
    for(var i=0;i<keys.Length;i++)Resources[keys[i]]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
    if(SystemParameters.HighContrast){Resources["Canvas"]=Resources["Surface"]=Resources["Sidebar"]=SystemColors.WindowBrush;Resources["Ink"]=Resources["Muted"]=SystemColors.WindowTextBrush;Resources["Accent"]=SystemColors.HighlightBrush;}
    Resources["ListBoxItemSelectedBackgroundThemeBrush"]=Resources["ListBoxItemSelectedBackgroundPointerOverThemeBrush"]=SystemParameters.HighContrast?SystemColors.HighlightBrush:Resources["Surface"];
    Resources["ListBoxItemSelectedForegroundThemeBrush"]=SystemParameters.HighContrast?SystemColors.HighlightTextBrush:Resources["Ink"];
    Resources["ListBoxItemUnselectedBackgroundPointerOverThemeBrush"]=Resources["Line"];
 }
 static void Open(string target)=>Process.Start(new ProcessStartInfo(target){UseShellExecute=true});
 internal void PreviewPage(string requested)
 {
    if(requested=="Guide"){kind="tongue";Navigate("Calibration");recording=true;RefreshCalibrationControls();poseTitle!.Text="Tongue left";poseDetail!.Text="Point toward your left.";cue!.Text="Hold";count!.Text="8 of 42";progress!.Value=19;state["targets"]=new JsonObject{["visibility"]=1,["extension"]=.75,["horizontal"]=-1};DrawFace(face!,state["targets"]!.AsObject(),"tongue");}
    else Navigate(requested=="Calibrate"?"Calibration":requested);
 }
 // Face guide pose: mouth width, corner y, control x fraction, upper and lower control offsets, tongue scale, angle, length, right and left cheek puff.
 static readonly double[] NeutralMouth={32,170,1/3.0,14.67,14.67},SmileMouth={38,168,1/3.0,26.67,26.67},OpenMouth={22,176,1,-20,20},FlatMouth={34,176,1/3.0,0,0},PursedMouth={12,176,1/3.0,2,2};
 readonly double[] facePose=new double[10],faceSpeed=new double[10],faceGoal=new double[10];
 string faceType="";int faceIndex=-1,faceCount,faceSide=1,faceGoalSide=1;bool faceAnimating,faceShown;DateTime faceNeutralUntil,facePulse;TimeSpan faceClock;
 // Motion is optional: honors Windows "Animation effects"; text still carries every instruction.
 bool FaceMotion=>SystemParameters.ClientAreaAnimation&&!preview;
 void DrawFace(Canvas canvas,JsonObject targets,string type)
 {
    double Value(string key)=>double.TryParse(targets[key]?.ToString(),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var value)?value:0;
    var prompt=poseTitle?.Text.ToLowerInvariant()??"";var now=DateTime.UtcNow;faceType=type;
    // Show relaxed, then morph into each new pose, and pop briefly on every capture.
    if(recording&&state["index"]?.GetValue<int>() is int index)
    {
        var captured=state["count"]?.GetValue<int>()??0;
        if(index!=faceIndex){if(faceIndex>=0)faceNeutralUntil=now.AddMilliseconds(350);faceIndex=index;}
        else if(captured>faceCount)facePulse=now;
        faceCount=captured;
    }
    else faceIndex=-1;
    double right=Value("CheekPuffRight"),left=Value("CheekPuffLeft"),tongue=0,angle=0,length=0;faceGoalSide=1;
    var mouth=NeutralMouth;
    if(type=="puff"){if(right>0||left>0)mouth=PursedMouth;}
    else if(Value("visibility")>0)
    {
        // ponytail: fixed 35° lean and linear length, tune here if the guide feels off.
        double h=Value("horizontal"),v=Value("vertical");
        mouth=FlatMouth;tongue=1;faceGoalSide=v>0?-1:1;angle=v>0?180-h*35:h*35;length=(12+Value("extension")*16)*(v>0?.7:1-Math.Min(v,0)*.1);
    }
    else if(prompt.Contains("open"))mouth=OpenMouth;
    else if(prompt.Contains("smil"))mouth=SmileMouth;
    mouth.CopyTo(faceGoal,0);faceGoal[5]=tongue;faceGoal[6]=angle;faceGoal[7]=length;faceGoal[8]=right;faceGoal[9]=left;
    if(!FaceMotion||!faceShown){faceGoal.CopyTo(facePose,0);faceSide=faceGoalSide;faceShown=true;}
    RenderFace(canvas);
    if(!faceAnimating&&type!="pupils"&&FaceMotion){faceAnimating=true;faceClock=default;CompositionTarget.Rendering+=FaceFrame;}
 }
 void FaceFrame(object? sender,EventArgs e)
 {
    var time=((RenderingEventArgs)e).RenderingTime;var dt=faceClock==default?1/60.0:Math.Min((time-faceClock).TotalSeconds,.05);
    if(faceClock!=default&&dt<=0)return;faceClock=time;
    if(StepFace(dt)){CompositionTarget.Rendering-=FaceFrame;faceAnimating=false;}
    if(face is not null)RenderFace(face);
 }
 bool StepFace(double dt)
 {
    var goal=(double[])faceGoal.Clone();
    if(DateTime.UtcNow<faceNeutralUntil){NeutralMouth.CopyTo(goal,0);goal[5]=goal[8]=goal[9]=0;}
    // The tongue retracts before switching between pointing down and up, then grows out the other way.
    if(facePose[5]<.05){faceSide=faceGoalSide;facePose[6]=goal[6];faceSpeed[6]=0;}
    if(faceSide!=faceGoalSide)goal[5]=0;
    // Critically damped spring (no overshoot, ~0.4 s response) that retargets smoothly mid-flight.
    const double omega=2*Math.PI/.4;var settled=true;
    for(var i=0;i<facePose.Length;i++)
    {
        faceSpeed[i]+=(omega*omega*(goal[i]-facePose[i])-2*omega*faceSpeed[i])*dt;facePose[i]+=faceSpeed[i]*dt;
        if(Math.Abs(goal[i]-facePose[i])>.01||Math.Abs(faceSpeed[i])>.01)settled=false;
    }
    if(!settled||DateTime.UtcNow<faceNeutralUntil||(DateTime.UtcNow-facePulse).TotalSeconds<.6)return false;
    goal.CopyTo(facePose,0);Array.Clear(faceSpeed);return true;
 }
 void RenderFace(Canvas canvas)
 {
    canvas.Children.Clear();var scale=Math.Min(canvas.Width/360,canvas.Height/250);
    var since=(DateTime.UtcNow-facePulse).TotalSeconds;var pop=FaceMotion&&since<.3?1+.04*Math.Sin(Math.PI*since/.3):1;
    var transform=new TransformGroup();transform.Children.Add(new ScaleTransform(pop,pop,180,130));transform.Children.Add(new ScaleTransform(scale,scale));
    var group=new Canvas{Width=360,Height=250,RenderTransform=transform};Canvas.SetLeft(group,(canvas.Width-360*scale)/2);canvas.Children.Add(group);
    // One stroke weight; mirrored, so your right is on screen left.
    const string ink="Ink",accent="Accent";
    void Line(string data,string brush,Transform? at=null,double opacity=1)
    {
        var shape=new System.Windows.Shapes.Path{Data=Geometry.Parse(data),StrokeThickness=8,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,StrokeLineJoin=PenLineJoin.Round,RenderTransform=at,Opacity=opacity};
        shape.SetResourceReference(Shape.StrokeProperty,brush);group.Children.Add(shape);
    }
    void Fill(string data,double opacity,Transform? at=null)
    {
        var shape=new System.Windows.Shapes.Path{Data=Geometry.Parse(data),Opacity=opacity,RenderTransform=at};
        shape.SetResourceReference(Shape.FillProperty,accent);group.Children.Add(shape);
    }
    if(faceType=="pupils"){Line("M 180,105 V 145 M 160,125 H 200",ink);return;}
    var p=facePose;string F(FormattableString value)=>FormattableString.Invariant(value);
    // Head and ears stay put as a stable frame; a puffed cheek bulges the jaw outward.
    var head=F($"M 180,22 C 234,22 258,62 258,112 C {258+p[9]*16},190 220,230 180,230 C 140,230 {102-p[8]*16},190 102,112 C 102,62 126,22 180,22 Z");
    Line(head,ink);if(since<.6)Line(head,accent,null,1-since/.6);
    Line("M 142,98 V 116 M 218,98 V 116 M 180,98 V 124 Q 180,134 170,134",ink);
    foreach(var (puff,x) in new[]{(p[8],136.0),(p[9],224.0)})
        if(puff>.01)Fill(F($"M {x-16-puff*5},168 A {16+puff*5},{12+puff*4} 0 1 0 {x+16+puff*5},168 A {16+puff*5},{12+puff*4} 0 1 0 {x-16-puff*5},168 Z"),.35*Math.Min(puff,1));
    if(p[5]>.01)
    {
        var at=new TransformGroup();at.Children.Add(new ScaleTransform(p[5],p[5],180,176));at.Children.Add(new RotateTransform(p[6],180,176));
        var trim=15*Math.Tan(p[6]*Math.PI/180);var tongue=F($"M 165,{176+trim} V {176+p[7]} A 15,15 0 0 0 195,{176+p[7]} V {176-trim}");
        Fill(tongue+" Z",.2,at);Line(tongue,accent,at);
    }
    double w=p[0],y=p[1],k=p[2]*p[0];
    Line(F($"M {180-w},{y} C {180-k},{y+p[3]} {180+k},{y+p[3]} {180+w},{y} C {180+k},{y+p[4]} {180-k},{y+p[4]} {180-w},{y} Z"),ink);
 }
}
