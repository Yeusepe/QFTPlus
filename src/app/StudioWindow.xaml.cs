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
    Theme(session.Config["theme"]?.GetValue<string>()??"System");InitializeUpdates();UseChanged(session.Use);Navigate("Tracking");SidebarStatus();
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
    if(recording){if(name!=page)Error("Finish or cancel calibration first.");return;}
    StopManual();setupRefresh=null;trackingRefresh=null;liveLog=null;updateRefresh=null;
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
        Show(Row("Tongue",()=>{kind="tongue";Navigate("Calibration");}),!On("tongueOutput",true)?"Off":Has("tongueModelPath")?"Calibrated":"Standard model");
        var groups=QproFaceTracking.Hub.CalibrationSettings.FaceGroups;var calibratedGroups=groups.Count(group=>session.FaceCalibrated(group.Kind));var onGroups=groups.Count(group=>session.FaceOn(group.Kind));
        Show(Row("Extra expressions",()=>{kind=groups.FirstOrDefault(group=>!session.FaceCalibrated(group.Kind)).Kind??"puff";Navigate("Calibration");}),
            calibratedGroups==0?"Not calibrated":onGroups==0?"Off":onGroups<calibratedGroups?$"{onGroups} of {calibratedGroups} on":$"{calibratedGroups} of {groups.Length} calibrated");
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
        if(session.State=="Connected"&&!Problem){badge.SetResourceReference(Border.BackgroundProperty,"Accent");glyph.Foreground=Brushes.White;}
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
    var heading=Text("Tracking",18);heading.FontWeight=FontWeights.SemiBold;Page.Children.Add(heading);
    var (useCard,useRefresh,_)=UseOptions(useChanged);Page.Children.Add(useCard);
    var eyes=Page.Children.Count;EyeOptions();faceOnly.Add(Page.Children[eyes]);
    var eyeRefresh=trackingRefresh;trackingRefresh=()=>{eyeRefresh?.Invoke();useRefresh();};
    var rows=new StackPanel();
    foreach(var (key,title) in new[]{("tongueOutput","Tongue"),("extraFaceOutput","Extra expressions"),("pupilDilation","Pupil dilation")})
    {
        var calibrated=session.Calibrated(key);
        var option=new CheckBox{Content=new TextBlock{Text=calibrated?title:title+" · Calibrate first"},IsEnabled=calibrated,IsChecked=calibrated&&(session.Config[key]?.GetValue<bool>()??key=="tongueOutput")};AutomationProperties.SetName(option,title);
        rows.Children.Add(option);
        if(key=="extraFaceOutput"){ExpressionGroups(rows,option);continue;}
        option.Click+=(_,_)=>{if(preview)return;session.Save(key,JsonValue.Create(option.IsChecked==true));if(session.Running)Send("reload");};
    }
    var faceRows=Card(rows);faceOnly.Add(faceRows);Page.Children.Add(faceRows);
 }
 void ExpressionGroups(Panel rows,CheckBox parent)
 {
    var groups=new List<(string Kind,CheckBox Box)>();
    foreach(var (kind,title) in QproFaceTracking.Hub.CalibrationSettings.FaceGroups)
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
    UpdateOptions();
    var faceOnly=new List<UIElement>();
    void ShowFace(string use){foreach(var element in faceOnly)element.Visibility=use=="hands"?Visibility.Collapsed:Visibility.Visible;UseChanged(use);}
    TrackingOptions(faceOnly,ShowFace);
    var appearance=new StackPanel();var theme=new ComboBox{ItemsSource=new[]{"System","Light","Dark"},SelectedItem=session.Config["theme"]?.GetValue<string>()??"System"};
    Field(appearance,"Appearance",theme);
    theme.SelectionChanged+=(_,_)=>{Theme((string)theme.SelectedItem);if(!preview)session.Save("theme",JsonValue.Create((string)theme.SelectedItem));};
    Page.Children.Add(Card(appearance));
    StartupOptions(faceOnly);var processing=Page.Children.Count;ProcessingOptions();faceOnly.Add(Page.Children[processing]);
    Diagnostics();
    ShowFace(session.Use);
 }
 static readonly (string Title,string File,string Empty)[] Logs={("Tracking (autostart.log)","autostart.log","Start tracking to create it."),("Eye tracking (autostart-eyes.log)","autostart-eyes.log","Start tracking with independent eye gaze on to create it."),("Face and tongue (autostart-tongue.log)","autostart-tongue.log","Start tracking to create it."),("Setup (setup.log)","setup.log","Run setup to create it."),("Components (studio.log)","studio.log","It’s created when calibration or hybrid hands install or train something."),("Hybrid hands (hybrid.log)","hybrid.log","Turn on hybrid hands, then start tracking to create it.")};
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
    else if(requested=="Cheeks"){kind="puff";Navigate("Calibration");recording=true;RefreshCalibrationControls();poseTitle!.Text="Left cheek · halfway";poseDetail!.Text="Fill halfway. Other cheek flat.";cue!.Text="Halfway · 2";count!.Text="3 of 21";progress!.Value=12;state["targets"]=new JsonObject{["CheekPuffLeft"]=.5,["CheekPuffRight"]=1};DrawFace(face!,state["targets"]!.AsObject(),"puff");}
    else if(requested.StartsWith("Pose:")){var parts=requested.Split(':',4);kind=parts[1];Navigate("Calibration");recording=true;RefreshCalibrationControls();poseTitle!.Text=parts[2];state["targets"]=JsonNode.Parse(parts[3])!.AsObject();DrawFace(face!,state["targets"]!.AsObject(),kind);}
    else if(requested=="Logs"){revealLog="autostart.log";Navigate("Settings");}
    else if(requested=="Busy"){Navigate("Tracking");session.Notify("Preparing components","One-time setup…");}
    else Navigate(requested=="Calibrate"?"Calibration":requested);
 }
 static readonly double[] NeutralMouth={32,170,1/3.0,14.67,14.67},SmileMouth={38,168,1/3.0,26.67,26.67},OpenMouth={22,176,1,-20,20},FlatMouth={34,176,1/3.0,0,0},PursedMouth={12,176,1/3.0,2,2};
 readonly double[] facePose=new double[35],faceSpeed=new double[35],faceGoal=new double[35];
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
    if(type=="puff"){if(right>0||left>0)mouth=PursedMouth;}
    else if(Value("visibility")>0)
    {
        double h=Value("horizontal"),v=Value("vertical");
        mouth=FlatMouth;tongue=1;faceGoalSide=v>0?-1:1;angle=v>0?180+h*35:-h*35;length=(12+Value("extension")*16)*(v>0?.7:1-Math.Min(v,0)*.1);
    }
    else if(prompt.Contains("open"))mouth=OpenMouth;
    else if(prompt.Contains("smil"))mouth=SmileMouth;
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
        0,0,Value("JawClench"),Value("JawMandibleRaise"),Value("JawBackward")}.CopyTo(faceGoal,10);
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
    if(DateTime.UtcNow<faceNeutralUntil){NeutralMouth.CopyTo(goal,0);goal[5]=goal[8]=goal[9]=0;Array.Clear(goal,10,25);}
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
    Line("M 142,98 V 116 M 218,98 V 116",ink);
    if(faceType=="nose")
    {
        var flare=(p[28]+p[29])/2;
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
        if(puff>.01){var rx=8+22*puff;var ry=6+16*puff;var cx=x+outward*6*puff;Fill(F($"M {cx-rx},168 A {rx},{ry} 0 1 0 {cx+rx},168 A {rx},{ry} 0 1 0 {cx-rx},168 Z"),.15+.3*Math.Min(puff,1));}
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
