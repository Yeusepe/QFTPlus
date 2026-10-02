using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;

namespace QFTPlus;
public partial class StudioWindow
{
 ComboBox? calibrationModes;
 CheckBox? slowMode;
 bool slow;
 Button? review;
 StackPanel? calibrationContent;
 (bool Passed,string Message)? calibrationResult;
 long recordingCommand;
 bool cancelPending;
 WindowState? restoreState;
 static string Intro(string kind)=>(kind switch
 {
    "enroll"=>"Hold a few expressions for a few seconds each, so tracking learns your relaxed face and your full range.",
    "benchmark-quick"=>"For testers. Short prompts and a little reading that check how well tracking works. Your settings don't change.",
    "benchmark"=>"For testers. Prompts, reading and talking that check how often tracking reacts when it shouldn't. Your settings don't change.",
    _=>"Lets your avatar's pupils follow yours. Dim the room lights (or attach the light blockers) a few minutes before you start."
 })+" To see this window in the headset, open the desktop in the Steam overlay.";
 void ResizeGuide()
 {
    if(page!="Calibration"||face is null)return;
    face.Height=Math.Clamp(ActualHeight-550,100,230);
    DrawFace(face,recording?state["targets"]?.AsObject()??new():new(),kind);
 }
 void RefreshCalibrationControls()
 {
    foreach(var button in nav.Values)button.IsEnabled=!recording;
    if(calibrationModes is not null)calibrationModes.IsEnabled=!recording&&!training;
    if(slowMode is not null)slowMode.Visibility=recording||training?Visibility.Collapsed:Visibility.Visible;
    if(begin is not null)begin.Visibility=recording||training?Visibility.Collapsed:Visibility.Visible;
    if(pause is not null)pause.Visibility=recording&&kind!="pupils"?Visibility.Visible:Visibility.Collapsed;
    if(skip is not null)skip.Visibility=recording&&kind!="pupils"?Visibility.Visible:Visibility.Collapsed;
    if(cancel is not null)cancel.Visibility=recording?Visibility.Visible:Visibility.Collapsed;
    if(progress is not null)progress.Visibility=recording||training?Visibility.Visible:Visibility.Collapsed;
    StartButton.IsEnabled=!recording;
 }
 void Calibration()
 {
    if(training)kind=trainingKind;
    var modes=calibrationModes=new ComboBox{Margin=new(0,0,0,16)};
    var testers=session.Config["benchmarkMode"]?.GetValue<bool>()==true?new[]{("benchmark-quick","Short benchmark"),("benchmark","Full benchmark")}:[];
    foreach(var (id,title) in new[]{("enroll","Face"),("pupils","Pupils")}.Concat(testers))
    {var item=new ComboBoxItem{Content=title,Tag=id,IsSelected=kind==id};modes.Items.Add(item);}
    modes.SelectionChanged+=(_,_)=>{if(recording||training||modes.SelectedItem is not ComboBoxItem item)return;var focus=modes.IsKeyboardFocusWithin;kind=(string)item.Tag;candidate=prefix="";calibrationResult=null;CalibrationReset();if(focus)Dispatcher.BeginInvoke(()=>calibrationModes?.Focus(),System.Windows.Threading.DispatcherPriority.Input);};
    Field(Page,"Tracking area",modes);
    var content=calibrationContent=new StackPanel();
    poseTitle=Text("Calibrate "+GroupTitle(kind).ToLowerInvariant(),23);poseTitle.TextAlignment=TextAlignment.Center;content.Children.Add(poseTitle);
    poseDetail=Text(Intro(kind),14,true);poseDetail.TextAlignment=TextAlignment.Center;content.Children.Add(poseDetail);
    cue=Text(kind switch{"pupils"=>"About 80 seconds","benchmark"=>"About 18 minutes","benchmark-quick"=>"About 5 minutes",_=>EnrollLength},25);cue.FontWeight=FontWeights.SemiBold;cue.TextAlignment=TextAlignment.Center;cue.Margin=new(0,4,0,0);content.Children.Add(cue);
    face=new Canvas{Width=360,Height=250};DrawFace(face,new JsonObject(),kind);
    content.Children.Add(new Viewbox{Stretch=Stretch.Uniform,StretchDirection=StretchDirection.DownOnly,Child=face});
    ResizeGuide();
    progress=new ProgressBar{Height=5,Minimum=0,Maximum=100,Margin=new(0,16,0,10),Foreground=(Brush)FindResource("Accent"),Visibility=Visibility.Collapsed};content.Children.Add(progress);
    count=Text(kind=="pupils"?"":"Recordings stay on this PC.",13,true);count.TextAlignment=TextAlignment.Center;count.Visibility=kind=="pupils"?Visibility.Collapsed:Visibility.Visible;content.Children.Add(count);
    Page.Children.Add(Card(content));
    slowMode=null;
    if(kind=="enroll")
    {
        var option=slowMode=new CheckBox{Content=new TextBlock{Text="More time for each pose",TextWrapping=TextWrapping.Wrap},IsChecked=slow,Margin=new(0,12,0,0),Visibility=recording||training?Visibility.Collapsed:Visibility.Visible};
        option.Click+=(_,_)=>{if(recording||training)return;slow=option.IsChecked==true;cue!.Text=EnrollLength;};
        Page.Children.Add(option);
    }
    var actions=Actions;actions.Children.Clear();
    begin=Button("Start calibration",Begin,true);actions.Children.Add(begin);
    pause=Button("Pause",()=>Send("pause"));pause.Visibility=Visibility.Collapsed;actions.Children.Add(pause);
    skip=Button("Skip pose",()=>Send("skip"));skip.Margin=new(8,0,0,0);skip.Visibility=Visibility.Collapsed;actions.Children.Add(skip);
    cancel=Button("Cancel",()=>{cancelPending=true;cancel!.IsEnabled=false;cue!.Text="Stopping calibration…";});cancel.Margin=new(12,0,0,0);cancel.Visibility=Visibility.Collapsed;actions.Children.Add(cancel);
    review=Button("Review video",()=>Open(prefix+".avi"));review.Visibility=Visibility.Collapsed;review.Margin=new(12,0,0,0);actions.Children.Add(review);
    if(training){RefreshCalibrationControls();ShowTraining();}
    else if(calibrationResult is {} result&&kind==trainingKind)Finished(result.Passed,result.Message);
 }
 string EnrollLength=>slow?"About 2 minutes":"About 1 minute";
 static string GroupTitle(string kind)=>kind=="pupils"?"Pupils":kind.StartsWith("benchmark")?"Benchmark":"Face";
 void CalibrationReset(){Page.Children.Clear();Calibration();}
 void Begin()
 {
    if(recording||training)return;
    if(!session.Running){Error("Start tracking, then start calibration.");return;}
    if(string.IsNullOrEmpty(runtimeId)||session.State!="Connected"||!FreshState()){Error("Waiting for the headset cameras. Make sure the headset is awake and connected.");return;}
    ClearNotice();candidate="";prefix="";
    if(!Send("begin",new JsonObject{["kind"]=kind,["slow"]=slow}))return;
    recordingCommand=command;cancelPending=false;
    recording=true;calibrationResult=null;CalibrationReset();count!.Visibility=Visibility.Visible;RefreshCalibrationControls();
    if(kind=="pupils"&&WindowState!=WindowState.Maximized){restoreState=WindowState;WindowState=WindowState.Maximized;}
 }
 bool Send(string action,JsonObject? fields=null)
 {
    if(string.IsNullOrEmpty(runtimeId))return false;
    if(state["ack"]?.GetValue<long>()<command){if(action=="reload")reloadPending=true;else if(action!="heartbeat")Error("Waiting for the previous action…");return false;}
    var data=fields??new JsonObject();data["id"]=++command;data["runtimeId"]=runtimeId;data["action"]=action;
    try{CalibrationSettings.WriteJson(Path.Combine(session.Root,"studio.command.json"),data);heartbeat=DateTime.UtcNow;return true;}
    catch{command--;throw;}
 }
 string trainingKind="";
 async Task Enroll()
 {
    if(training||string.IsNullOrEmpty(prefix))return;
    training=true;trainingKind=kind;ClearNotice();RefreshCalibrationControls();ShowTraining();
    try
    {
        candidate=await session.Enroll(prefix);
        Apply(trainingKind);
        training=false;Finished(true,"Face calibration is on.");
    }
    catch(Exception error){training=false;Finished(false,error.Message);}
    finally{training=false;RefreshCalibrationControls();}
 }
 void Apply(string which)
 {
    if(candidate.Length==0)return;
    session.Apply(which,candidate);Send("reload");
 }
 void ShowTraining()
 {
    if(page!="Calibration"||!training||poseTitle is null||trainingKind.StartsWith("benchmark"))return;
    poseTitle.Text="Creating your calibration";poseDetail!.Text="This takes a few seconds. Tracking stays on.";
    cue!.Text="";progress!.IsIndeterminate=true;progress.Visibility=Visibility.Visible;count!.Visibility=Visibility.Collapsed;
 }
 void Finished(bool passed,string message)
 {
    calibrationResult=(passed,message);
    if(page!="Calibration"||kind!=trainingKind||poseTitle is null){Error(passed?message:GroupTitle(trainingKind)+" calibration wasn't saved. Open Calibration for details.");return;}
    calibrationContent!.Children.Clear();
    poseTitle.Text=passed?"Calibration is on":"Calibration wasn't saved";poseTitle.TextAlignment=TextAlignment.Left;
    AutomationProperties.SetHeadingLevel(poseTitle,AutomationHeadingLevel.Level2);AutomationProperties.SetLiveSetting(poseTitle,AutomationLiveSetting.Polite);
    calibrationContent.Children.Add(poseTitle);
    poseDetail!.Text=passed?"Check your avatar to see the difference.":"Your current calibration hasn't changed.";poseDetail.TextAlignment=TextAlignment.Left;
    if(passed&&trainingKind.StartsWith("benchmark")){poseTitle.Text="Benchmark recorded";poseDetail.Text=message;}
    calibrationContent.Children.Add(poseDetail);
    if(passed&&trainingKind.StartsWith("benchmark")&&File.Exists(candidate))
    {
        var file=candidate;var show=new Button{Content="Show file",HorizontalAlignment=HorizontalAlignment.Left,Margin=new(0,12,0,0)};show.SetResourceReference(StyleProperty,"PrimaryButton");
        show.Click+=(_,_)=>{try{System.Diagnostics.Process.Start("explorer.exe",$"/select,\"{file}\"").Dispose();}catch(Exception){Error("Couldn’t open File Explorer. The file is in "+Path.GetDirectoryName(file)+".");}};
        var actions=new WrapPanel{Margin=new(0,12,0,0)};show.Margin=new(0,0,8,8);actions.Children.Add(show);
        if(DiscordProfile.Length>0){var discord=new Button{Content="Message on Discord",Margin=new(0,0,0,8)};discord.Click+=MessageOnDiscord;actions.Children.Add(discord);}
        calibrationContent.Children.Add(actions);
    }
    if(!passed)
    {
        var lines=message.Split('\n',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
        calibrationContent.Children.Add(Text(lines.FirstOrDefault()??"Try recording your poses again."));
        if(lines.Length>1)
        {
            var details=new StackPanel{Margin=new(0,12,0,0)};
            foreach(var line in lines.Skip(1))details.Children.Add(Text(line));
            var header=Text("What needs attention");header.Margin=new(0);
            calibrationContent.Children.Add(new Expander{Header=header,Content=details,MinHeight=44,HorizontalContentAlignment=HorizontalAlignment.Stretch,Margin=new(0,8,0,0)});
        }
    }
    if(begin is not null){begin.Content=passed?"Calibrate again":"Try again";if(passed)begin.ClearValue(StyleProperty);else begin.SetResourceReference(StyleProperty,"PrimaryButton");}
    if(review is not null)review.Visibility=File.Exists(prefix+".avi")?Visibility.Visible:Visibility.Collapsed;
    RefreshCalibrationControls();
    UIElementAutomationPeer.FromElement(poseTitle)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
 }
 async Task Tick()
 {
    if(polling)return;polling=true;
    try
    {
        if(manualTesting){if(page=="Manual"&&IsVisible)PublishManual();else StopManual();}
        if(!busy)session.Poll();
        try{state=Session.Read(Path.Combine(session.Root,"studio.state.json"));}catch(InvalidDataException){state=new();}
        var id=state["runtimeId"]?.GetValue<string>()??"";
        if(id!=runtimeId){runtimeId=id;command=state["ack"]?.GetValue<long>()??0;}
        if(cancelPending&&FreshState()&&state["ack"]?.GetValue<long>()>=command&&Send("cancel"))
        {cancelPending=false;recordingCommand=command;ClearNotice();}
        if(reloadPending&&FreshState()&&state["ack"]?.GetValue<long>()>=command){reloadPending=false;Send("reload");}
        if(recording&&!cancelPending&&DateTime.UtcNow-heartbeat>TimeSpan.FromMilliseconds(700))Send("heartbeat");
        if(page=="Calibration"&&recording)UpdateGuide();
        if(recording&&!FreshState())Error("Camera feed paused. Make sure the headset is awake and connected, or cancel.");
        if(page=="Cameras"&&IsVisible)await UpdateCameras();
        if(IsVisible){liveLog?.Invoke();adjustmentRefresh?.Invoke();}
        if(state["error"]?.GetValue<string>() is {Length:>0} error && (!recording||state["ack"]?.GetValue<long>()>=recordingCommand) && (recording||page=="Calibration"))Error(error);
        if(page!="Tracking"&&session.Hybrid is not null&&!Session.Alive(session.Hybrid))Error("Hybrid hands stopped. The Hybrid hands log in Settings shows why.");
    }
    catch(Exception error){Error(error.Message);}
    finally{polling=false;}
 }
 void UpdateGuide()
 {
    if(cancelPending||!FreshState()||(state["ack"]?.GetValue<long>()??0)<recordingCommand)return;
    if(state["kind"]?.GetValue<string>()!=kind)return;
    poseTitle!.Text=state["title"]?.GetValue<string>()??poseTitle.Text;
    poseDetail!.Text=state["instruction"]?.GetValue<string>()??"";
    cue!.Text=state["cue"]?.GetValue<string>()??"Look at the circle";
    progress!.Value=(state["progress"]?.GetValue<double>()??(state["index"]?.GetValue<int>()??0)/4.0)*100;
    count!.Text=$"{(state["index"]?.GetValue<int>()??0)+1} of {state["total"]?.GetValue<int>()??4}";
    if(kind=="pupils")
    {
        SetPupilStimulus(state["level"]?.GetValue<double>()??0);cue.Text=$"{state["remaining"]?.GetValue<double>():0} seconds";
    }
    else DrawFace(face!,state["targets"]?.AsObject()??new(),kind);
    if(pause is not null)pause.Content=state["paused"]?.GetValue<bool>()==true?"Resume":"Pause";
    if(state["phase"]?.GetValue<string>() is "complete" or "cancelled")
    {
        recording=false;Theme("System");if(restoreState is {} before){WindowState=before;restoreState=null;}
        if(state["phase"]!.GetValue<string>()!="complete"){CalibrationReset();RefreshCalibrationControls();return;}
        begin!.Content="Calibrate again";begin.ClearValue(StyleProperty);count.Text="";prefix=state["prefix"]!.GetValue<string>();progress.Value=100;
        RefreshCalibrationControls();
        if(kind=="pupils"){candidate=prefix+".pupils.json";ApplyPupils();}
        else if(kind.StartsWith("benchmark")){trainingKind=kind;_=ShareBenchmark();}
        else _=Enroll();
    }
 }
 async Task ShareBenchmark()
 {
    candidate="";
    if(!session.Tester){Finished(true,"Benchmark recorded. Its files start with "+Path.GetFileName(prefix)+" in "+Path.GetDirectoryName(prefix)+".");return;}
    training=true;RefreshCalibrationControls();poseTitle!.Text="Preparing your recording";poseDetail!.Text="This takes about a minute. Tracking stays on.";
    cue!.Text="";progress!.IsIndeterminate=true;progress.Visibility=Visibility.Visible;
    try
    {
        candidate=await session.Package(prefix);
        var size=new FileInfo(candidate).Length/1e6;
        training=false;Finished(true,$"Send this file to the QFT+ developer directly: {Path.GetFileName(candidate)} ({size:0} MB). If it’s too big to attach to a message, send a OneDrive or Google Drive link to it instead.");
    }
    catch(Exception error){training=false;candidate="";Finished(true,"Benchmark recorded, but it couldn’t be prepared to share. "+error.Message);}
    finally{training=false;progress.IsIndeterminate=false;RefreshCalibrationControls();}
 }
 void ApplyPupils()
 {
    try{trainingKind="pupils";Apply("pupils");Finished(true,"Pupil calibration is on.");}
    catch(Exception error){Finished(false,error.Message);}
 }
 void SetPupilStimulus(double level)
 {
    var gray=(byte)Math.Round(18+Math.Clamp(level,0,1)*217);var bg=new SolidColorBrush(Color.FromRgb(gray,gray,gray));var fg=level>.5?Brushes.Black:Brushes.White;
    foreach(var key in new[]{"Canvas","Surface","Sidebar"})Resources[key]=bg;
    Resources["Ink"]=Resources["Muted"]=fg;
    if(face is not null)DrawFace(face,new(),"pupils");
 }
 bool FreshState()=>state["time"]?.GetValue<double>() is {} timestamp && Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()/1000.0-timestamp)<3;
}
