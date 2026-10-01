using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
 Button? pause, begin, cancel, skip;
 double applyingAt;
 readonly List<(Image Image,string Key)> cameras=new();
 readonly Dictionary<string,ListBoxItem> nav=new();
 bool automatic=true;
 double settle=2.0;
 public StudioWindow(string root,bool preview=false)
 {
    InitializeComponent();this.preview=preview;session=new(root);
    session.Changed+=_=>Dispatcher.Invoke(()=>
    {
        SidebarStatus();
        if(session.SettingUp)SetupProgress();
        trackingRefresh?.Invoke();
        if(page=="Setup")setupRefresh?.Invoke();else if(page!="Tracking"&&busy&&session.Detail.Length>0)Error(session.Detail);
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
    Theme("System");InitializeUpdates();UseChanged(session.Use);Navigate("Tracking");SidebarStatus();RestorePlacement();
    SizeChanged+=(_,_)=>ResizeGuide();
    if(!preview)
    {
        tray=new(){Icon=System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!),Text="QFT+",Visible=true};
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
    KeyDown+=(_,e)=>
    {
        if(e.Key!=Key.Escape)return;
        if(recording&&cancel is {IsVisible:true,IsEnabled:true}){cancel.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));e.Handled=true;}
        else if(busy&&Equals(StartButton.Content,"Cancel")){session.CancelSetup();e.Handled=true;}
    };
    Closing+=(_,e)=>
    {
        StopManual();
        if(quitting||preview)return;
        SavePlacement();
        if(busy){e.Cancel=true;session.CancelSetup();return;}
        if(recording) { e.Cancel=true; Error("Finish or cancel calibration before closing this window.");return; }
        if(training||session.Running){e.Cancel=true;Hide();return;}
        quitting=true;tray?.Dispose();Application.Current.Shutdown();
    };
 }
 void RestorePlacement()
 {
    if(preview||session.Config["window"] is not JsonObject saved)return;
    double Get(string key)=>saved[key] is JsonValue value&&value.TryGetValue<double>(out var number)&&double.IsFinite(number)?number:double.NaN;
    var (left,top,width,height)=(Get("left"),Get("top"),Get("width"),Get("height"));
    var screen=new Rect(SystemParameters.VirtualScreenLeft,SystemParameters.VirtualScreenTop,SystemParameters.VirtualScreenWidth,SystemParameters.VirtualScreenHeight);
    if(double.IsNaN(left+top+width+height)||!screen.Contains(new Point(left+100,top+16)))return;
    WindowStartupLocation=WindowStartupLocation.Manual;Left=left;Top=top;Width=Math.Max(MinWidth,width);Height=Math.Max(MinHeight,height);
    if(saved["maximized"]?.GetValue<bool>()==true)WindowState=WindowState.Maximized;
 }
 void SavePlacement()
 {
    if(preview)return;
    var bounds=WindowState==WindowState.Normal?new Rect(Left,Top,ActualWidth,ActualHeight):RestoreBounds;
    if(bounds.IsEmpty)return;
    try{session.Save("window",new JsonObject{["left"]=bounds.Left,["top"]=bounds.Top,["width"]=bounds.Width,["height"]=bounds.Height,["maximized"]=WindowState==WindowState.Maximized&&restoreState is null});}
    catch(Exception error) when(error is IOException or UnauthorizedAccessException){}
 }
 void UseChanged(string use){foreach(var name in new[]{"Adjustments","Manual","Calibration","Cameras"})nav[name].Visibility=use=="hands"?Visibility.Collapsed:Visibility.Visible;}
 internal void Error(string message){Notice.Text=message;NoticeBox.Visibility=Visibility.Visible;SetupHelp.Visibility=session.HelpTarget is null?Visibility.Collapsed:Visibility.Visible;SetupHelp.Content=session.HelpCaption;}
 void HelpClick(object sender,RoutedEventArgs e){if(session.HelpTarget is {} target)Open(target);}
 void ClearNotice(){NoticeBox.Visibility=Visibility.Collapsed;}
 internal async Task Start(bool fromModule=false)
 {
    if(busy)return;busy=true;StartButton.Content="Cancel";ClearNotice();
    setupDone=false;setupProblem="";moduleMissing=false;setupStep=-1;
    trackingRefresh?.Invoke();
    try {await session.Start(fromModule);ClearNotice();}
    catch(OperationCanceledException){session.Notify("Ready");ClearNotice();}
    catch(Exception error){setupProblem=error.Message;moduleMissing=error is QproFaceTracking.Hub.ModuleNotInstalledException;if(page!="Tracking")Error(error.Message);session.Notify("Couldn’t start",error.Message);}
    finally {busy=false;StartButton.IsEnabled=true;SidebarStatus();trackingRefresh?.Invoke();setupRefresh?.Invoke();}
 }
 async void StartClick(object sender,RoutedEventArgs e)
 {
    if(busy){session.CancelSetup();return;}
    if(!session.Running){await Start();return;}
    if(recording||training){Error("Finish calibration before stopping tracking.");return;}
    StopManual();
    busy=true;StartButton.IsEnabled=false;
    try{await session.Stop();}catch(Exception error){Error(error.Message);}finally{busy=false;StartButton.IsEnabled=true;SidebarStatus();trackingRefresh?.Invoke();}
 }
 async Task Quit()
 {
    if(busy){Show();session.CancelSetup();Error("Finishing the current step before quitting.");return;}
    if(recording||training){Show();Error("Finish or cancel calibration before quitting.");return;}
    StartButton.IsEnabled=false;SavePlacement();
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
    if(recording){if(name!=page)Error("Finish or cancel calibration first.");return;}
    StopManual();setupRefresh=null;trackingRefresh=null;adjustmentRefresh=null;liveLog=null;updateRefresh=null;
    page=name;Page.Children.Clear();Actions.Children.Clear();cameras.Clear();ClearNotice();PageTitle.Text=name=="Manual"?"Test movements":name;Title=PageTitle.Text+" — QFT+";Subtitle.Visibility=Visibility.Collapsed;PageScroll.ScrollToTop();
    foreach(var item in nav){item.Value.IsSelected=item.Key==name;item.Value.FontWeight=item.Key==name?FontWeights.SemiBold:FontWeights.Normal;}
    Navigation.ScrollIntoView(nav[name]);
    switch(name){case "Tracking":Home();break;case "Adjustments":Adjustments();break;case "Manual":Manual();break;case "Calibration":Calibration();break;case "Cameras":Cameras();break;case "Setup":SetupOptions();break;case "Settings":Settings();break;}
    RefreshUpdates();
 }
 bool Problem=>!busy&&!session.Running&&(setupProblem.Length>0||session.State=="Tracking stopped");
 (string Glyph,string Brush) Mark()=>Problem?("","Ink"):session.State=="Connected"?("","Accent"):busy||session.Running?("","Accent"):("","Muted");
 void SidebarStatus()
 {
    var (glyph,brush)=Mark();ConnectionMark.Text=glyph;ConnectionMark.SetResourceReference(TextBlock.ForegroundProperty,brush);
    Connection.Text=Problem&&setupProblem.Length>0?"Couldn’t start":session.State;
    StartButton.Content=busy&&!session.Running?"Cancel":session.Running?"Stop":"Start";
 }
 static readonly FontFamily Symbols=new("Segoe Fluent Icons, Segoe MDL2 Assets");
 void Home()
 {
    var badge=new Border{Width=44,Height=44,CornerRadius=new(22),Margin=new(0,0,16,0),VerticalAlignment=VerticalAlignment.Top};
    var glyph=new TextBlock{FontFamily=Symbols,FontSize=20,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};badge.Child=glyph;
    var headline=Text("",18);headline.FontWeight=FontWeights.SemiBold;headline.Margin=new(0,0,0,4);AutomationProperties.SetHeadingLevel(headline,AutomationHeadingLevel.Level2);AutomationProperties.SetLiveSetting(headline,AutomationLiveSetting.Polite);
    var detail=Text("",14,true);AutomationProperties.SetLiveSetting(detail,AutomationLiveSetting.Polite);
    var bar=new ProgressBar{Minimum=0,Maximum=100,Height=5,Margin=new(0,4,0,4)};bar.SetResourceReference(ProgressBar.ForegroundProperty,"Accent");
    var action=new Button{HorizontalAlignment=HorizontalAlignment.Left};
    var view=Button("View cameras",()=>Navigate("Cameras"));
    var help=Button("",()=>{if(session.HelpTarget is {} target)Open(target);});
    var logs=Button("View logs",()=>{revealLog=session.State=="Tracking stopped"?session.ErrorLog:setupStep>=0&&!setupDone?"setup.log":"autostart.log";Navigate("Settings");});
    var buttons=new WrapPanel{Margin=new(0,12,0,-8)};foreach(var button in new[]{action,view,help,logs}){button.Margin=new(0,0,8,8);buttons.Children.Add(button);}
    var body=new StackPanel{VerticalAlignment=VerticalAlignment.Center};body.Children.Add(headline);body.Children.Add(detail);body.Children.Add(bar);body.Children.Add(buttons);
    var hero=new DockPanel();DockPanel.SetDock(badge,Dock.Left);hero.Children.Add(badge);hero.Children.Add(body);Page.Children.Add(Card(hero));
    var setUp=false;
    action.Click+=(_,_)=>{if(!setUp||Problem&&moduleMissing)Navigate("Setup");else StartClick(action,new RoutedEventArgs());};
    var use=session.Use;

     var config=session.Config;
    bool On(string key,bool fallback=false)=>config[key]?.GetValue<bool>()??fallback;
    bool Has(string key)=>config[key]?.GetValue<string>() is {Length:>0} path&&File.Exists(path);
    var eyeModel=File.Exists(System.IO.Path.Combine(session.Root,"models/eye/bolt-independent-axes.ptl"));
    var pupils=File.Exists(System.IO.Path.Combine(session.Root,"calibration/qpro-pupil-dilation.json"));
    var header=Text("Features",14,true);header.FontWeight=FontWeights.SemiBold;header.Margin=new(4,8,0,8);AutomationProperties.SetHeadingLevel(header,AutomationHeadingLevel.Level2);Page.Children.Add(header);
    var list=new StackPanel();
    (TextBlock Value,TextBlock Mark,Button Row,string Title) Row(string title,Action go)
    {
        var grid=new Grid();foreach(var width in new[]{new GridLength(1,GridUnitType.Star),GridLength.Auto,GridLength.Auto})grid.ColumnDefinitions.Add(new(){Width=width});
        var text=new StackPanel();var name=Text(title);name.Margin=new(0);text.Children.Add(name);
        var value=Text("",14,true);value.Margin=new(0,4,0,0);text.Children.Add(value);grid.Children.Add(text);
        var mark=new TextBlock{FontFamily=Symbols,Text="",Margin=new(12,0,0,0),VerticalAlignment=VerticalAlignment.Center,Visibility=Visibility.Collapsed};mark.SetResourceReference(TextBlock.FontSizeProperty,"Font13");Grid.SetColumn(mark,1);grid.Children.Add(mark);
        var chevron=new TextBlock{FontFamily=Symbols,Text="",Margin=new(12,0,0,0),VerticalAlignment=VerticalAlignment.Center};chevron.SetResourceReference(TextBlock.FontSizeProperty,"Font12");chevron.SetResourceReference(TextBlock.ForegroundProperty,"Muted");Grid.SetColumn(chevron,2);grid.Children.Add(chevron);
        var row=new Button{Content=grid};row.SetResourceReference(StyleProperty,"ListRow");row.Click+=(_,_)=>go();
        if(list.Children.Count>0){var line=new Border{Height=1,Margin=new(12,0,12,0)};line.SetResourceReference(Border.BackgroundProperty,"Line");list.Children.Add(line);}
        list.Children.Add(row);return (value,mark,row,title);
    }
    void Show((TextBlock Value,TextBlock Mark,Button Row,string Title) row,string value,bool problem=false)
    {
        row.Value.Text=value;row.Mark.Visibility=problem?Visibility.Visible:Visibility.Collapsed;AutomationProperties.SetName(row.Row,$"{row.Title}, {value}");
    }
    if(use=="hands")Show(Row("Face tracking",()=>Navigate("Settings")),"Off");
    else
    {
        var needsEyeSetup=session.IndependentGaze&&!eyeModel;
        Show(Row("Eye gaze",()=>Navigate(needsEyeSetup?"Setup":"Settings")),!session.IndependentGaze?"Standard":needsEyeSetup?"Needs setup":"Independent",needsEyeSetup);
        if(!session.Legacy)
        {
            var enrolled=Has("faceEnrollment");
            Show(Row("Cheeks, tongue and brows",()=>{kind="enroll";Navigate("Calibration");}),
                !On("extraFaceOutput",true)&&!On("tongueOutput",true)?"Off":enrolled?"Calibrated":"Standard");
        }
        else
        {
        Show(Row("Tongue",()=>{kind="tongue";Navigate("Calibration");}),!On("tongueOutput",true)?"Off":Has("tongueDirectionModelPath")?"Calibrated":"Standard model");
        var groups=QproFaceTracking.Hub.CalibrationSettings.FaceGroups;var calibratedGroups=groups.Count(group=>session.FaceCalibrated(group.Kind));var onGroups=groups.Count(group=>session.FaceOn(group.Kind));
        Show(Row("Extra expressions",()=>{kind=groups.FirstOrDefault(group=>!session.FaceCalibrated(group.Kind)).Kind??"puff";Navigate("Calibration");}),
            calibratedGroups==0?"Not calibrated":onGroups==0?"Off":onGroups<calibratedGroups?$"{onGroups} of {calibratedGroups} on":$"{calibratedGroups} of {groups.Length} calibrated");
        }
        Show(Row("Pupil dilation",()=>{kind="pupils";Navigate("Calibration");}),!pupils?"Not calibrated":On("pupilDilation")?"Calibrated":"Off");
    }
    var hybridStopped=false;
    var hybrid=Row("Hybrid hands",()=>{if(hybridStopped||session.HybridProblem.Length>0)revealLog="hybrid.log";Navigate("Settings");});
    var group=Card(list);group.Padding=new(8);Page.Children.Add(group);

    void Refresh()
    {
        setUp=busy||session.Running||string.Equals(session.Config["root"]?.GetValue<string>(),session.Root,StringComparison.OrdinalIgnoreCase);
        var failed=!busy&&!session.Running&&setupProblem.Length>0;
        var via=session.Detail.EndsWith("USB")?"USB":"Wi-Fi";
        var (title,about)=
            failed?("Couldn’t start tracking",setupProblem):
            session.State=="Tracking stopped"?("Tracking stopped",session.Detail):
            session.State=="Connected"&&use=="hands"?("Hand tracking is on",$"Connected over {via}. Hybrid hands and controllers are on in Virtual Desktop."):
            session.State=="Connected"?("Tracking is on",$"Connected over {via}. Expressions are going to VRCFaceTracking."+(session.HybridReady?" Hybrid hands are on.":"")):
            busy||session.Running?(session.State,session.Detail):
            !setUp?("Set up Quest Pro","Connect the headset to this PC with a USB data cable. Setup runs once and takes a few minutes."):
            ("Ready to track","Put on the headset, then start tracking.");
        var (symbol,brush)=Mark();glyph.Text=symbol;
        if(session.State=="Connected"&&!Problem){badge.SetResourceReference(Border.BackgroundProperty,"Accent");glyph.SetResourceReference(TextBlock.ForegroundProperty,"OnAccent");}
        else{badge.SetResourceReference(Border.BackgroundProperty,"Line");glyph.SetResourceReference(TextBlock.ForegroundProperty,brush);}
        headline.Text=title;detail.Text=about;detail.Visibility=about.Length>0?Visibility.Visible:Visibility.Collapsed;
        bar.Visibility=busy&&session.SettingUp||session.State=="Connecting"?Visibility.Visible:Visibility.Collapsed;
        bar.IsIndeterminate=!session.SettingUp;bar.Value=session.Progress;
        action.Content=busy&&!session.Running?"Cancel":session.Running?"Stop tracking":!setUp?"Set up…":Problem?(moduleMissing?"Open Setup":"Try again"):"Start tracking";
        action.IsEnabled=session.State!="Stopping";
        if(session.Running||busy)action.ClearValue(StyleProperty);else action.SetResourceReference(StyleProperty,"PrimaryButton");
        view.Visibility=session.Running?Visibility.Visible:Visibility.Collapsed;
        help.Visibility=failed&&session.HelpTarget is not null?Visibility.Visible:Visibility.Collapsed;help.Content=session.HelpCaption;
        logs.Visibility=Problem?Visibility.Visible:Visibility.Collapsed;
        hybridStopped=session.Hybrid is not null&&!Session.Alive(session.Hybrid);
        var hybridFailed=session.HybridProblem.Length>0;
        Show(hybrid,use=="face"?"Off":QproFaceTracking.Hub.SteamVr.IsSteamLink?"Requires Virtual Desktop":hybridFailed?"Couldn’t start":hybridStopped?"Stopped":"On",hybridStopped||hybridFailed);
    }
    trackingRefresh=Refresh;Refresh();
 }
 void TrackingOptions(List<UIElement> faceOnly,Action<string> useChanged)
 {
    if(!preview)session.DisableUncalibratedOutputs();
    var heading=Text("Tracking",18);heading.FontWeight=FontWeights.SemiBold;AutomationProperties.SetHeadingLevel(heading,AutomationHeadingLevel.Level2);Page.Children.Add(heading);
    var (useCard,useRefresh,_)=UseOptions(useChanged);Page.Children.Add(useCard);
    var eyes=Page.Children.Count;EyeOptions();faceOnly.Add(Page.Children[eyes]);
    var eyeRefresh=trackingRefresh;trackingRefresh=()=>{eyeRefresh?.Invoke();useRefresh();};
    var rows=new StackPanel();
    foreach(var (key,title) in new[]{("tongueOutput","Tongue"),("extraFaceOutput","Extra expressions"),("pupilDilation","Pupil dilation")})
    {
        var calibrated=session.Calibrated(key);
        var option=new CheckBox{Content=new TextBlock{Text=calibrated?title:title+" · Calibrate first"},IsEnabled=calibrated,IsChecked=calibrated&&(session.Config[key]?.GetValue<bool>()??(key=="tongueOutput"||key=="extraFaceOutput"&&!session.Legacy))};AutomationProperties.SetName(option,title);
        rows.Children.Add(option);
        if(key=="extraFaceOutput"){ExpressionGroups(rows,option);continue;}
        option.Click+=(_,_)=>{if(preview)return;session.Save(key,JsonValue.Create(option.IsChecked==true));if(session.Running)Send("reload");};
    }
    var faceRows=Card(rows);faceOnly.Add(faceRows);Page.Children.Add(faceRows);
 }
 void ExpressionGroups(Panel rows,CheckBox parent)
 {
    var groups=new List<(string Kind,CheckBox Box)>();
    foreach(var (kind,title) in QproFaceTracking.Hub.CalibrationSettings.FaceGroups.Where(g=>session.Legacy||Session.UniversalGroups.Contains(g.Kind)))
    {
        var calibrated=session.FaceCalibrated(kind);
        var box=new CheckBox{Content=new TextBlock{Text=calibrated?title:title+" · Calibrate first"},IsEnabled=calibrated,IsChecked=session.FaceOn(kind),Margin=new(28,0,0,0)};
        AutomationProperties.SetName(box,title);rows.Children.Add(box);
        if(calibrated)groups.Add((kind,box));
    }
    void Mixed(){var on=groups.Count(g=>g.Box.IsChecked==true);parent.IsChecked=on==0?false:on==groups.Count?true:null;}
    void Save(IEnumerable<(string Kind,CheckBox Box)> changed)
    {
        if(preview)return;
        foreach(var (kind,box) in changed)session.Save(QproFaceTracking.Hub.CalibrationSettings.FaceOutputKey(kind),JsonValue.Create(box.IsChecked==true));
        session.Save("extraFaceOutput",JsonValue.Create(groups.Any(g=>g.Box.IsChecked==true)));
        if(session.Running)Send("reload");
    }
    foreach(var group in groups)group.Box.Click+=(_,_)=>{Mixed();Save([group]);};
    parent.Click+=(_,_)=>{var on=!groups.All(g=>g.Box.IsChecked==true);foreach(var g in groups)g.Box.IsChecked=on;Mixed();Save(groups);};
    Mixed();
 }
 void Cameras()
 {
    Subtitle.Text=session.Running?"Waiting for cameras…":"Start tracking to view cameras.";Subtitle.Visibility=Visibility.Visible;
    var outline=new CheckBox{Content="Show pupil outlines",IsChecked=true};
    outline.Click+=(_,_)=>{for(var i=0;i<Math.Min(2,cameras.Count);i++)cameras[i]=(cameras[i].Image,(outline.IsChecked==true?"pupil":"camera")+i);};Page.Children.Add(outline);
    var grid=new UniformGrid{Columns=2};
    foreach(var (key,title) in new[]{("pupil0","Left eye"),("pupil1","Right eye"),("camera2","Left face"),("camera3","Right face"),("camera4","Brow")})
    {
        var stack=new StackPanel();stack.Children.Add(Text(title,14));var image=new Image{Height=180,Stretch=Stretch.Uniform};AutomationProperties.SetName(image,title+" camera");stack.Children.Add(image);cameras.Add((image,key));var card=Card(stack);card.Margin=new(0,8,12,8);grid.Children.Add(card);
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
    UpdateOptions();
    var faceOnly=new List<UIElement>();
    void ShowFace(string use){foreach(var element in faceOnly)element.Visibility=use=="hands"?Visibility.Collapsed:Visibility.Visible;UseChanged(use);}
    TrackingOptions(faceOnly,ShowFace);
    ThumbrestOptions();
    StartupOptions(faceOnly);var processing=Page.Children.Count;ProcessingOptions();faceOnly.Add(Page.Children[processing]);
    FaceModelOptions(faceOnly);
    TesterOptions(faceOnly);
    Diagnostics();
    UninstallOptions();
    ShowFace(session.Use);
 }
 void FaceModelOptions(List<UIElement> faceOnly)
 {
    var panel=new StackPanel();var heading=Text("Face calibration",16);heading.FontWeight=FontWeights.SemiBold;AutomationProperties.SetHeadingLevel(heading,AutomationHeadingLevel.Level2);panel.Children.Add(heading);
    panel.Children.Add(Text("Cheeks, tongue and brows work without calibration. Calibrating your face makes them more accurate.",13,true));
    var legacy=new CheckBox{Content=new TextBlock{Text="Use older calibrations",TextWrapping=TextWrapping.Wrap},IsChecked=session.Legacy,Margin=new(0,8,0,0)};
    const string legacyAbout="Calibrations made one area at a time, like cheeks, brows and lip pucker. Turn this on to keep using or redo them. The first time, it downloads about 140 MB.";
    AutomationProperties.SetHelpText(legacy,legacyAbout);
    var legacyStatus=Text("",13,true);legacyStatus.Visibility=Visibility.Collapsed;AutomationProperties.SetLiveSetting(legacyStatus,AutomationLiveSetting.Polite);
    var legacyProgress=new ProgressBar{IsIndeterminate=true,Height=3,Margin=new(0,0,0,8),Visibility=Visibility.Collapsed};legacyProgress.SetResourceReference(ProgressBar.ForegroundProperty,"Accent");
    legacy.Click+=async(_,_)=>
    {
        if(preview)return;
        var on=legacy.IsChecked==true;
        if(on&&!session.LegacyInstalled)
        {
            legacy.IsEnabled=false;legacyStatus.Visibility=legacyProgress.Visibility=Visibility.Visible;legacyStatus.Text="Downloading older calibration support (about 140 MB)…";
            try{await session.InstallLegacy(line=>{if(line.StartsWith("Downloading "))Dispatcher.BeginInvoke(()=>legacyStatus.Text=line.TrimEnd('.')+"…");});}
            catch(IOException error){legacy.IsChecked=false;legacy.IsEnabled=true;legacyProgress.Visibility=Visibility.Collapsed;legacyStatus.Text="Couldn’t turn on older calibrations. "+error.Message;return;}
            legacy.IsEnabled=true;legacyProgress.Visibility=Visibility.Collapsed;
        }
        session.Save("faceEngine",JsonValue.Create(on?"legacy":"universal"));Send("reload");
        var y=PageScroll.VerticalOffset;Navigate("Settings");Dispatcher.BeginInvoke(()=>PageScroll.ScrollToVerticalOffset(y),System.Windows.Threading.DispatcherPriority.Loaded);
    };
    panel.Children.Add(legacy);panel.Children.Add(Text(legacyAbout,13,true));panel.Children.Add(legacyStatus);panel.Children.Add(legacyProgress);
    var log=new CheckBox{Content=new TextBlock{Text="Save face-tracking logs on this PC (numbers only, no images)",TextWrapping=TextWrapping.Wrap},IsChecked=session.Config["faceLog"]?.GetValue<bool>()==true,Margin=new(0,8,0,0)};
    log.Click+=(_,_)=>{if(preview)return;session.Save("faceLog",JsonValue.Create(log.IsChecked==true));Send("reload");};
    panel.Children.Add(log);
    var card=Card(panel);Page.Children.Add(card);faceOnly.Add(card);
 }
 Border? testerCard;
 void TesterOptions(List<UIElement> faceOnly)
 {
    var panel=new StackPanel();var heading=Text("Help improve tracking",16);heading.FontWeight=FontWeights.SemiBold;AutomationProperties.SetHeadingLevel(heading,AutomationHeadingLevel.Level2);panel.Children.Add(heading);
    if(session.Tester)
    {
        var id=Text($"You’re a tester. Your tester ID is {session.TesterId}.",13,true);AutomationProperties.SetName(id,"Tester ID "+session.TesterId);panel.Children.Add(id);
        panel.Children.Add(Text("Record a benchmark, then send the file it makes to the QFT+ developer directly. Use your tester ID to ask for your recordings to be deleted.",13,true));
        var actions=new WrapPanel{Margin=new(0,8,0,0)};
        var record=new Button{Content="Record a benchmark",Margin=new(0,0,8,8)};record.SetResourceReference(StyleProperty,"PrimaryButton");
        record.Click+=(_,_)=>{kind="benchmark-quick";Navigate("Calibration");};
        var leave=new Button{Content="Stop being a tester",Margin=new(0,0,0,8)};
        AutomationProperties.SetHelpText(leave,"Benchmark is removed from Calibration. Recordings already on this PC stay there.");
        leave.Click+=(_,_)=>{if(preview)return;session.LeaveTesting();var y=PageScroll.VerticalOffset;Navigate("Settings");Dispatcher.BeginInvoke(()=>PageScroll.ScrollToVerticalOffset(y),System.Windows.Threading.DispatcherPriority.Loaded);};
        actions.Children.Add(record);
        if(DiscordProfile.Length>0){var message=new Button{Content="Message on Discord",Margin=new(0,0,8,8)};message.Click+=MessageOnDiscord;actions.Children.Add(message);}
        actions.Children.Add(leave);panel.Children.Add(actions);
    }
    else
    {
        panel.Children.Add(Text("Record a short session and send it to the QFT+ developer. Recordings from many different faces make tracking better for everyone.",13,true));
        var shared=new StackPanel();
        foreach(var line in new[]{
            "Small infrared images of your eyes, brows and mouth from the headset’s face cameras, and the expression values the headset reports.",
            "No audio, no color camera, and no name or account. Each recording carries a random tester ID instead.",
            "Recordings are used only to test and improve QFT+ face tracking.",
            "Nothing is sent automatically. You send recordings yourself, directly to the developer.",
            "You can ask for your recordings to be deleted at any time with your tester ID."})
            shared.Children.Add(Text("• "+line,13,true));
        panel.Children.Add(new Expander{Header="What’s shared",Content=shared,IsExpanded=true,Margin=new(0,8,0,0)});
        var agree=new CheckBox{Content=new TextBlock{Text="I agree to share the recordings I send",TextWrapping=TextWrapping.Wrap},Margin=new(0,8,0,0)};
        var join=new Button{Content="Become a tester",IsEnabled=false,HorizontalAlignment=HorizontalAlignment.Left,Margin=new(0,8,0,0)};join.SetResourceReference(StyleProperty,"PrimaryButton");
        agree.Click+=(_,_)=>join.IsEnabled=agree.IsChecked==true;
        join.Click+=(_,_)=>{if(preview||agree.IsChecked!=true)return;session.JoinTesting();var y=PageScroll.VerticalOffset;Navigate("Settings");Dispatcher.BeginInvoke(()=>PageScroll.ScrollToVerticalOffset(y),System.Windows.Threading.DispatcherPriority.Loaded);};
        panel.Children.Add(agree);panel.Children.Add(join);
    }
    var card=testerCard=Card(panel);Page.Children.Add(card);faceOnly.Add(card);
 }
 void ShareRecordings(object sender,RoutedEventArgs e){Navigate("Settings");Dispatcher.BeginInvoke(()=>testerCard?.BringIntoView(),System.Windows.Threading.DispatcherPriority.Loaded);}
 static readonly (string Id,string Title,string About)[] ThumbrestModes={
    ("native","Trackpad","A regular SteamVR trackpad. Games and your SteamVR bindings decide what it does."),
    ("joystick","Joystick","Drag from where your thumb lands to push a stick. Lift to recenter."),
    ("swipe","Swipe","Swipe speed pushes the stick and glides out, like scrolling on a phone."),
    ("mouse","Mouse","Moves the Windows cursor like a laptop touchpad. Press firmly to click.")};
 void ThumbrestOptions()
 {
    var panel=new StackPanel();var heading=Text("Thumbrest and trigger",16);heading.FontWeight=FontWeights.SemiBold;AutomationProperties.SetHeadingLevel(heading,AutomationHeadingLevel.Level2);panel.Children.Add(heading);
    panel.Children.Add(Text("How the Quest Pro controllers’ thumbrests act in SteamVR. The trigger also reports where your finger slides along it, as its own input you can bind in SteamVR.",13,true));
    var status=Text("",13,true);AutomationProperties.SetLiveSetting(status,AutomationLiveSetting.Polite);
    var saved=session.Config["thumbrest"] as JsonObject??new JsonObject();
    string Mode()=>saved["mode"]?.GetValue<string>()??"native";
    double Get(string key,double fallback)=>saved[key] is JsonValue value&&value.TryGetValue<double>(out var number)?number:fallback;
    var pending=new Dictionary<string,JsonValue>();
    var timer=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMilliseconds(400)};
    void Apply()
    {
        timer.Stop();
        if(preview||pending.Count==0){pending.Clear();return;}
        try
        {
            var live=QproFaceTracking.Hub.SteamVr.SetThumbrest(pending);
            foreach(var (key,value) in pending)saved[key]=value.DeepClone();
            session.Save("thumbrest",saved.DeepClone());
            status.Text=live?"Applied.":"Applies when SteamVR starts.";
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException){Error("Couldn’t change the thumbrest. "+error.Message);}
        pending.Clear();
    }
    timer.Tick+=(_,_)=>Apply();
    void Set(string key,JsonValue value,bool now=false){pending[key]=value;timer.Stop();if(now)Apply();else timer.Start();}
    void Number(string key,double value)=>Set(key,JsonValue.Create(Math.Round(value,3)));
    var options=new StackPanel();var details=new StackPanel{Margin=new(0,12,0,0)};var joystick=new StackPanel();var swipe=new StackPanel();var mouse=new StackPanel();
    var straighten=new CheckBox{Content=new TextBlock{Text="Straighten scrolling",TextWrapping=TextWrapping.Wrap},IsChecked=Get("railAngle",35)>0,Margin=new(0,0,0,16)};
    void Show(string mode)
    {
        joystick.Visibility=mode=="joystick"?Visibility.Visible:Visibility.Collapsed;
        swipe.Visibility=mode=="swipe"?Visibility.Visible:Visibility.Collapsed;
        mouse.Visibility=mode=="mouse"?Visibility.Visible:Visibility.Collapsed;
        straighten.Visibility=mode is "joystick" or "swipe"?Visibility.Visible:Visibility.Collapsed;
    }
    var driverOn=session.Config["steamvrDriver"]?.GetValue<bool>()==true;
    var driver=new Button{Content=driverOn?"Uninstall SteamVR driver":"Install SteamVR driver",HorizontalAlignment=HorizontalAlignment.Left,Margin=new(0,12,0,8)};
    AutomationProperties.SetHelpText(driver,"The QFT+ driver adds the thumbrest trackpad and trigger slide. Uninstall it to use plain Touch controllers.");
    driver.Click+=async(_,_)=>
    {
        if(preview)return;
        var on=!driverOn;driver.IsEnabled=false;
        try
        {
            var note=await Task.Run(()=>on?QproFaceTracking.Hub.SteamVrDriver.Register(System.IO.Path.Combine(session.Root,"steamvr","qftplus")):QproFaceTracking.Hub.SteamVrDriver.Unregister());
            session.Save("steamvrDriver",on);options.Visibility=on?Visibility.Visible:Visibility.Collapsed;
            if(!on)await session.StopThumbrest();
            driverOn=on;driver.Content=on?"Uninstall SteamVR driver":"Install SteamVR driver";
            status.Text=note??(on?"The driver is installed. Start tracking to use it.":"The driver is removed.");
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException)
        {Error("Couldn’t change the SteamVR driver. "+error.Message);}
        finally{driver.IsEnabled=true;}
    };
    panel.Children.Add(driver);
    foreach(var (id,title,about) in ThumbrestModes)
        options.Children.Add(Choice("thumbrest",title,about,Mode()==id,()=>{Set("mode",JsonValue.Create(id),true);Show(id);}));
    SliderRow(details,"Left thumbrest angle",-45,45,Get("leftRotation",-20),v=>Number("leftRotation",Math.Round(v)),"0°");
    SliderRow(details,"Right thumbrest angle",-45,45,Get("rightRotation",20),v=>Number("rightRotation",Math.Round(v)),"0°");
    details.Children.Add(Text("Turn until a straight up-and-down swipe reads as straight, like Steam Input’s trackpad rotation.",13,true));
    SliderRow(details,"Press sensitivity",0,1,(0.8-Get("forceLow",0.55))/0.5,v=>Number("forceLow",0.8-0.5*v),"0'%'",100);
    var resting=new CheckBox{Content=new TextBlock{Text="Ignore a resting thumb",TextWrapping=TextWrapping.Wrap},IsChecked=Get("restingSize",75)>0,Margin=new(0,0,0,8)};
    resting.Click+=(_,_)=>Set("restingSize",JsonValue.Create(resting.IsChecked==true?75.0:0.0),true);
    straighten.Click+=(_,_)=>Set("railAngle",JsonValue.Create(straighten.IsChecked==true?35.0:0.0),true);
    var reversed=new CheckBox{Content=new TextBlock{Text="Reverse trigger slide (normally, sliding toward the tip is up)",TextWrapping=TextWrapping.Wrap},IsChecked=Get("triggerSlideReversed",0)!=0,Margin=new(0,0,0,16)};
    reversed.Click+=(_,_)=>Set("triggerSlideReversed",JsonValue.Create(reversed.IsChecked==true?1.0:0.0),true);
    details.Children.Add(resting);details.Children.Add(straighten);details.Children.Add(reversed);
    SliderRow(joystick,"Drag for a full push",0.2,1,Get("joystickRange",0.5),v=>Number("joystickRange",v),"0'% of the pad'",50);
    SliderRow(swipe,"Swipe speed",0.1,1,Get("swipeGain",0.35),v=>Number("swipeGain",v),"0'%'",100/0.35);
    SliderRow(swipe,"Glide",50,1000,Get("swipeDecayMs",350),v=>Number("swipeDecayMs",Math.Round(v)),"0' ms'");
    SliderRow(mouse,"Pointer speed",300,3000,Get("mouseSpeed",1200),v=>Number("mouseSpeed",Math.Round(v)),"0' px'");
    foreach(var group in new[]{joystick,swipe,mouse})details.Children.Add(group);
    Show(Mode());
    options.Children.Add(details);options.Visibility=driverOn?Visibility.Visible:Visibility.Collapsed;
    panel.Children.Add(options);panel.Children.Add(status);Page.Children.Add(Card(panel));
 }
 static readonly (string Title,string File,string Empty)[] Logs={("Tracking (autostart.log)","autostart.log","Start tracking to create it."),("Eye tracking (autostart-eyes.log)","autostart-eyes.log","Start tracking with independent eye gaze on to create it."),("Face and tongue (autostart-tongue.log)","autostart-tongue.log","Start tracking to create it."),("Headset relay (questpro-live-relay.txt)","questpro-live-relay.txt","It’s written when a tracking session ends."),("Setup (setup.log)","setup.log","Run setup to create it."),("Components (studio.log)","studio.log","It’s created when calibration or hybrid hands install or train something."),("Hybrid hands (hybrid.log)","hybrid.log","Turn on hybrid hands, then start tracking to create it.")};
 Action? liveLog;
 string? revealLog;
 int logIndex;
 void Diagnostics()
 {
    var failed=setupProblem.Length>0||session.State=="Tracking stopped";
    if(revealLog is not null)logIndex=Math.Max(0,Array.FindIndex(Logs,l=>l.File==revealLog));
    var panel=new StackPanel();
    var pick=new ComboBox{ItemsSource=Logs.Select(l=>l.Title).ToArray(),SelectedIndex=logIndex,Margin=new(0,0,0,8)};Field(panel,"Log",pick);
    var caption=Text("",13,true);AutomationProperties.SetLiveSetting(caption,AutomationLiveSetting.Polite);panel.Children.Add(caption);
    var box=new TextBox{IsReadOnly=true,IsUndoEnabled=false,IsReadOnlyCaretVisible=true,Height=300,TextWrapping=TextWrapping.Wrap,VerticalContentAlignment=VerticalAlignment.Top,FontFamily=new("Cascadia Mono, Consolas"),VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
    box.SetResourceReference(TextBox.FontSizeProperty,"Font12");AutomationProperties.SetName(box,"Log contents");panel.Children.Add(box);
    var buttons=new WrapPanel{Margin=new(0,12,0,0)};panel.Children.Add(buttons);
    string Target()=>System.IO.Path.Combine(session.Root,Logs[logIndex].File);
    var copy=Button("Copy log",()=>{try{Clipboard.SetText(box.Text);caption.Text="Copied to the clipboard.";}catch(Exception error){Error("Couldn’t copy the log. "+error.Message);}});
    var open=Button("Open in editor",()=>Open(Target()));
    var captures=System.IO.Path.Combine(session.Root,"captures");var recordings=Button("Open recordings",()=>Open(captures));recordings.IsEnabled=Directory.Exists(captures);
    foreach(var button in new[]{copy,open,recordings}){button.Margin=new(0,0,8,8);buttons.Children.Add(button);}
    var expander=new Expander{Header="Diagnostics",Content=panel,Margin=new(0,16,0,0),IsExpanded=revealLog is not null};Page.Children.Add(expander);
    (DateTime Time,long Length) seen=default;
    void Update(bool force=false)
    {
        if(!expander.IsExpanded)return;
        var info=new FileInfo(Target());
        if(!info.Exists){seen=default;box.Text="";box.Visibility=Visibility.Collapsed;copy.IsEnabled=open.IsEnabled=false;caption.Text="Nothing logged yet. "+Logs[logIndex].Empty;return;}
        info.Refresh();if(!force&&seen==(info.LastWriteTimeUtc,info.Length))return;seen=(info.LastWriteTimeUtc,info.Length);
        var following=box.VerticalOffset+box.ViewportHeight>=box.ExtentHeight-4;var offset=box.VerticalOffset;
        try{box.Text=Tail(info.FullName);}catch(IOException error){caption.Text="Couldn’t read the log. "+error.Message;return;}
        if(following)box.ScrollToEnd();else box.ScrollToVerticalOffset(offset);
        box.Visibility=Visibility.Visible;copy.IsEnabled=open.IsEnabled=true;caption.Text=$"Updates live. Last changed at {info.LastWriteTime:T}"+(info.Length>LogTail?". Showing the most recent part.":".");
    }
    pick.SelectionChanged+=(_,_)=>{logIndex=Math.Max(0,pick.SelectedIndex);seen=default;box.Text="";Update(true);box.ScrollToEnd();};
    expander.Expanded+=(_,_)=>{Update(true);box.ScrollToEnd();};
    liveLog=()=>Update();
    if(revealLog is not null){revealLog=null;Dispatcher.BeginInvoke(()=>{Update(true);box.ScrollToEnd();PageScroll.ScrollToVerticalOffset(expander.TranslatePoint(new Point(),Page).Y);},DispatcherPriority.Loaded);}
 }
 const int LogTail=128*1024;
 static string Tail(string path)
 {
    using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
    var cut=stream.Length>LogTail;if(cut)stream.Seek(-LogTail,SeekOrigin.End);
    var text=new StreamReader(stream).ReadToEnd();
    return cut?text[(text.IndexOf('\n')+1)..]:text;
 }
 internal void SetTextScale(double scale)
 {
    scale=Math.Clamp(scale,1,2.25);FontSize=14*scale;SidebarColumn.Width=new(Math.Min(280,196*scale));
    foreach(var size in new[]{12,13,14,16,18,23,25,30})Resources["Font"+size]=Math.Max(13,size)*scale;
 }
 void PreferencesChanged(object sender,UserPreferenceChangedEventArgs args)=>Dispatcher.BeginInvoke(()=>
 {
    SetTextScale(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Accessibility","TextScaleFactor",100) is int scale?scale/100.0:1);
    if(!(recording&&kind=="pupils"))Theme("System");
 });
 internal void Theme(string name)
 {
    var dark=name=="Dark"||(name=="System"&&Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize","AppsUseLightTheme",1) is int value&&value==0);
#pragma warning disable WPF0001
    ThemeMode=dark?System.Windows.ThemeMode.Dark:System.Windows.ThemeMode.Light;
    Application.Current.ThemeMode=ThemeMode;
#pragma warning restore WPF0001
    var colors=dark?new[]{"#191A1E","#25262C","#202126","#F3F3F5","#ADB0BA","#383A43"}:new[]{"#F5F5F7","#FFFFFF","#EBEBEF","#202127","#626570","#E1E2E7"};
    var keys=new[]{"Canvas","Surface","Sidebar","Ink","Muted","Line"};
    for(var i=0;i<keys.Length;i++)Resources[keys[i]]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
    Resources["Accent"]=TryFindResource("AccentFillColorDefaultBrush") as Brush??new SolidColorBrush(dark?Color.FromRgb(0x36,0x8C,0xFF):Color.FromRgb(0,0x67,0xD9));
    Resources["OnAccent"]=TryFindResource("TextOnAccentFillColorPrimaryBrush") as Brush??(dark?Brushes.Black:Brushes.White);
    if(SystemParameters.HighContrast){Resources["Canvas"]=Resources["Surface"]=Resources["Sidebar"]=SystemColors.WindowBrush;Resources["Ink"]=Resources["Muted"]=SystemColors.WindowTextBrush;Resources["Accent"]=SystemColors.HighlightBrush;Resources["OnAccent"]=SystemColors.HighlightTextBrush;}
    Resources["ListBoxItemSelectedBackgroundThemeBrush"]=Resources["ListBoxItemSelectedBackgroundPointerOverThemeBrush"]=SystemParameters.HighContrast?SystemColors.HighlightBrush:Resources["Surface"];
    Resources["ListBoxItemSelectedForegroundThemeBrush"]=SystemParameters.HighContrast?SystemColors.HighlightTextBrush:Resources["Ink"];
    Resources["ListBoxItemUnselectedBackgroundPointerOverThemeBrush"]=Resources["Line"];
 }
 static void Open(string target)=>Process.Start(new ProcessStartInfo(target){UseShellExecute=true});
 internal void PreviewPage(string requested)
 {
    if(requested=="Guide"){kind="tongue";Navigate("Calibration");recording=true;RefreshCalibrationControls();poseTitle!.Text="Tongue left";poseDetail!.Text="Point toward your left.";cue!.Text="Hold";count!.Text="8 of 42";progress!.Value=19;state["targets"]=new JsonObject{["visibility"]=1,["extension"]=.75,["horizontal"]=-1};DrawFace(face!,state["targets"]!.AsObject(),"tongue");}
    else if(requested=="Cheeks"){kind="puff";Navigate("Calibration");recording=true;RefreshCalibrationControls();poseTitle!.Text="Left cheek · halfway";poseDetail!.Text="Fill halfway. Other cheek flat.";cue!.Text="Halfway · 2";count!.Text="3 of 21";progress!.Value=12;state["targets"]=new JsonObject{["CheekPuffLeft"]=.5,["CheekPuffRight"]=1};DrawFace(face!,state["targets"]!.AsObject(),"puff");}
    else if(requested.StartsWith("Pose:")){var parts=requested.Split(':',4);kind=parts[1];Navigate("Calibration");recording=true;RefreshCalibrationControls();poseTitle!.Text=parts[2];state["targets"]=JsonNode.Parse(parts[3])!.AsObject();DrawFace(face!,state["targets"]!.AsObject(),kind);}
    else if(requested=="Logs"){revealLog="autostart.log";Navigate("Settings");}
    else if(requested=="Busy"){Navigate("Tracking");session.Notify("Preparing components","One-time setup…");}
    else Navigate(requested=="Calibrate"?"Calibration":requested);
 }
 static readonly double[] NeutralMouth={32,170,1/3.0,14.67,14.67},SmileMouth={38,168,1/3.0,26.67,26.67},OpenMouth={22,176,1,-20,20},FlatMouth={34,176,1/3.0,0,0},PursedMouth={12,176,1/3.0,2,2};
 readonly double[] facePose=new double[37],faceSpeed=new double[37],faceGoal=new double[37];
 string faceType="";int faceIndex=-1,faceCount,faceSide=1,faceGoalSide=1;bool faceAnimating,faceShown;DateTime faceNeutralUntil,facePulse;TimeSpan faceClock;
 bool FaceMotion=>SystemParameters.ClientAreaAnimation&&!preview;
 void DrawFace(Canvas canvas,JsonObject targets,string type)
 {
    double Value(string key)=>double.TryParse(targets[key]?.ToString(),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var value)?value:0;
    var prompt=poseTitle?.Text.ToLowerInvariant()??"";var now=DateTime.UtcNow;faceType=type;
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
    if(right>0||left>0||Value("CheekSuckRight")>0||Value("CheekSuckLeft")>0)mouth=PursedMouth;
    else if(type=="puff"){}
    else if(Value("visibility")>0)
    {
        double h=Value("horizontal"),v=Value("vertical");
        mouth=FlatMouth;tongue=1;faceGoalSide=v>0?-1:1;angle=v>0?180+h*35:-h*35;length=(12+Value("extension")*16)*(v>0?.7:1-Math.Min(v,0)*.1);
    }
    else if(prompt.Contains("open"))mouth=OpenMouth;
    else if(Value("MouthSmile")>0||prompt.Contains("smil"))mouth=SmileMouth;
    else if(Value("LipSuck")>0)mouth=FlatMouth;
    var open=Math.Min(1,Value("MouthOpen"));if(open>0)mouth=mouth.Zip(OpenMouth,(a,b)=>a+open*(b-a)).ToArray();
    right+=.35*Value("TongueBulgeRight")-.5*Value("CheekSuckRight");left+=.35*Value("TongueBulgeLeft")-.5*Value("CheekSuckLeft");
    double Both(string name)=>(Value(name+"Left")+Value(name+"Right"))/2;
    var kiss=Math.Min(Both("LipPuckerUpper"),Both("LipPuckerLower"));var pout=Both("LipPuckerLower")-kiss;
    var m=(double[])mouth.Clone();
    void Shape(double amount,double width,double upper,double lower){m[0]+=amount*width;m[3]+=amount*upper;m[4]+=amount*lower;}
    Shape(Both("MouthCornerPull"),6,-4,12);
    Shape(Both("MouthCornerSlant"),2,4,4);
    Shape(Both("MouthUpperDeepen"),-2,-10,0);
    Shape(kiss,-18,-20.67,-6.67);
    Shape(pout,-4,-22,-16);
    Shape(Both("LipSuckCorner"),-6,-11,-11);
    Shape(Math.Max(Value("JawClench"),Value("JawMandibleRaise")),2,-8,-8);
    Shape(Value("JawBackward"),-4,-3,-3);
    m.CopyTo(faceGoal,0);faceGoal[5]=tongue;faceGoal[6]=angle;faceGoal[7]=length;faceGoal[8]=right;faceGoal[9]=left;
    var mouthMoves=new[]{"MouthCornerPull","MouthCornerSlant","MouthUpperDeepen","LipPuckerUpper","LipPuckerLower","LipSuckCorner"}.Sum(Both)
        +new[]{"JawClench","JawMandibleRaise","JawBackward","MouthUpperLeft","MouthUpperRight","MouthLowerLeft","MouthLowerRight"}.Sum(Value);
    var lift=3*Both("MouthCornerSlant")-2*pout-2*Value("JawBackward");
    new[]{
        0,lift,0,lift,
        6*(Value("MouthUpperRight")-Value("MouthUpperLeft")),6*(Value("MouthLowerRight")-Value("MouthLowerLeft")),
        Math.Min(1,mouthMoves),Both("MouthUpperDeepen"),0,0,
        8*Value("BrowLowererLeft"),8*Value("BrowLowererRight"),8*Value("BrowPinchLeft"),8*Value("BrowPinchRight"),
        10*Value("BrowInnerUpLeft"),10*Value("BrowInnerUpRight"),10*Value("BrowOuterUpLeft"),10*Value("BrowOuterUpRight"),
        Value("NasalDilationLeft")-Value("NasalConstrictLeft"),Value("NasalDilationRight")-Value("NasalConstrictRight"),
        0,0,Value("JawClench"),Value("JawMandibleRaise"),Value("JawBackward"),
        Math.Min(1,Math.Max(Value("EyeClosed"),.6*Value("EyeSquint"))),Value("LookUp")}.CopyTo(faceGoal,10);
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
    if(DateTime.UtcNow<faceNeutralUntil){NeutralMouth.CopyTo(goal,0);goal[5]=goal[8]=goal[9]=0;Array.Clear(goal,10,goal.Length-10);}
    if(facePose[5]<.05){faceSide=faceGoalSide;facePose[6]=goal[6];faceSpeed[6]=0;}
    if(faceSide!=faceGoalSide)goal[5]=0;
    if(goal[5]==0){goal[6]=facePose[6];goal[7]=facePose[7];}
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
    const string ink="Ink",accent="Accent";
    void Line(string data,string brush,Transform? at=null,double opacity=1,double width=8)
    {
        var shape=new System.Windows.Shapes.Path{Data=Geometry.Parse(data),StrokeThickness=width,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,StrokeLineJoin=PenLineJoin.Round,RenderTransform=at,Opacity=opacity};
        shape.SetResourceReference(Shape.StrokeProperty,brush);group.Children.Add(shape);
    }
    void Fill(string data,double opacity,Transform? at=null)
    {
        var shape=new System.Windows.Shapes.Path{Data=Geometry.Parse(data),Opacity=opacity,RenderTransform=at};
        shape.SetResourceReference(Shape.FillProperty,accent);group.Children.Add(shape);
    }
    if(faceType=="pupils"){Line("M 160,125 A 20,20 0 1 1 200,125 A 20,20 0 1 1 160,125 M 180,125 L 180,125",ink);return;}
    var p=facePose;string F(FormattableString value)=>FormattableString.Invariant(value);
    var chin=230-4*p[34];
    var head=F($"M 180,22 C 234,22 258,62 258,112 C {258+p[8]*30},190 220,{chin} 180,{chin} C 140,{chin} {102-p[9]*30},190 102,112 C 102,62 126,22 180,22 Z");
    Line(head,ink);if(since<.6)Line(head,accent,null,1-since/.6);
    var closed=Math.Clamp(p[35],0,1);var look=6*p[36];
    foreach(var x in new[]{142.0,218.0})
    {
        if(closed>.85)Line(F($"M {x-9},107 Q {x},112 {x+9},107"),accent);
        else Line(F($"M {x},{107-9*(1-closed)-look} V {107+9*(1-closed)-look}"),closed>.1||Math.Abs(look)>.5?accent:ink);
    }
    var flare=(p[28]+p[29])/2;
    if(faceType=="nose"||Math.Abs(flare)>.02)
    {
        Line("M 180,98 V 124",ink);
        Line(F($"M {171-4*flare},135 Q {173-3*flare},127 180,129 Q {187+3*flare},127 {189+4*flare},135"),Math.Abs(flare)>.05?accent:ink,null,1,6);
    }
    else Line("M 180,98 V 124 Q 180,134 170,134",ink);
    foreach(var (side,down,pinch,inner,outer) in new[]{(-1.0,p[20],p[22],p[24],p[26]),(1.0,p[21],p[23],p[25],p[27])})
    {
        double ix=180+side*(20-pinch),ox=180+side*(56-pinch*.3),iy=86+down-inner,oy=84+down-outer;
        Line(F($"M {ox},{oy} Q {(ix+ox)/2},{Math.Min(iy,oy)-5} {ix},{iy}"),down+pinch+inner+outer>.1?accent:ink);
    }
    if(p[17]>.01)Line("M 173,109 L 187,109 M 174,116 L 186,116",accent,null,Math.Min(1,p[17]),3);
    foreach(var (puff,x,outward) in new[]{(p[9],136.0,-1.0),(p[8],224.0,1.0)})
        if(puff<-.02)Line(F($"M {x+outward*8},148 Q {x-outward*(6+24*-puff)},168 {x+outward*8},188"),accent,null,Math.Min(1,-puff*3),5);
        else if(puff>.01){var rx=8+22*puff;var ry=6+16*puff;var cx=x+outward*6*puff;Fill(F($"M {cx-rx},168 A {rx},{ry} 0 1 0 {cx+rx},168 A {rx},{ry} 0 1 0 {cx-rx},168 Z"),.15+.3*Math.Min(puff,1));}
    if(p[5]>.01)
    {
        var at=new TransformGroup();at.Children.Add(new ScaleTransform(p[5],p[5],180,176));at.Children.Add(new RotateTransform(p[6],180,176));
        var lean=Math.Clamp(p[6]-180*Math.Round(p[6]/180),-60,60);var trim=15*Math.Tan(lean*Math.PI/180);var tongue=F($"M 165,{176+trim} V {176+p[7]} A 15,15 0 0 0 195,{176+p[7]} V {176-trim}");
        Fill(tongue+" Z",.2,at);Line(tongue,accent,at);
    }
    var shift=(p[14]+p[15])/2;
    double w=p[0],y=p[1],k=p[2]*p[0],leftX=180-w+p[10]+shift,leftY=y-p[11],rightX=180+w-p[12]+shift,rightY=y-p[13],tuck=0;
    var bite=p[32];
    if(bite>.05)
    {
        double h=7*bite,third=(rightX-leftX)/3;
        Line(F($"M {leftX},{y-h} H {rightX} V {y+h} H {leftX} Z M {leftX},{y} H {rightX} M {leftX+third},{y-h} V {y+h} M {rightX-third},{y-h} V {y+h}"),accent,null,1,5);
    }
    else Line(F($"M {leftX},{leftY} C {180-k+p[14]},{y+p[3]} {180+k+p[14]},{y+p[3]} {rightX},{rightY} C {180+k+p[15]},{y+p[4]-tuck} {180-k+p[15]},{y+p[4]-tuck} {leftX},{leftY} Z"),p[16]>.1?accent:ink);
 }
}
