using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace QFTPlus;
public partial class StudioWindow
{
 ComboBox? calibrationModes;
 WrapPanel? manualControls;
 Expander? captureOptions;
 Button? review;
 static string Intro(string kind)=>(kind switch
 {
    "tongue"=>"Fits tongue tracking to you, so smiles and open-mouth poses don't trigger it.",
    "puff"=>"Lets each cheek puff on its own.",
    "brows"=>"You'll look worried, surprised, annoyed and sun-squinting, so your frown and raised brows track apart.",
    "pucker"=>"You'll kiss, pout and make a skeptical “hmm”, so each lip and your mouth corners track on their own.",
    "corners"=>"You'll smile big, smile with your mouth closed and react to a bad smell.",
    "mouth"=>"You'll move your mouth, then your jaw, to each side.",
    "nose"=>"You'll breathe in deeply and sniff, which widens and narrows your nostrils by themselves.",
    "jaw"=>"You'll rest your teeth together, bite down and pull your jaw back.",
    _=>"Lets your avatar's pupils follow yours. Dim the room lights (or attach the light blockers) a few minutes before you start."
 })+" Open it in the Steam overlay so you can see this window.";
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
    if(captureOptions is not null){captureOptions.Visibility=recording||training?Visibility.Collapsed:Visibility.Visible;captureOptions.IsEnabled=!recording&&!training;}
    if(manualControls is not null)manualControls.Visibility=recording&&!automatic?Visibility.Visible:Visibility.Collapsed;
    if(begin is not null)begin.Visibility=recording||training?Visibility.Collapsed:Visibility.Visible;
    if(pause is not null)pause.Visibility=recording&&kind!="pupils"?Visibility.Visible:Visibility.Collapsed;
    if(skip is not null)skip.Visibility=recording&&QproFaceTracking.Hub.CalibrationSettings.IsFaceGroup(kind)?Visibility.Visible:Visibility.Collapsed;
    if(cancel is not null)cancel.Visibility=recording?Visibility.Visible:Visibility.Collapsed;
    if(progress is not null)progress.Visibility=recording||training||prefix.Length>0?Visibility.Visible:Visibility.Collapsed;
    StartButton.IsEnabled=!recording;
 }
 void Calibration()
 {
    manualControls=null;captureOptions=null;
    if(training)kind=trainingKind;
    var modes=calibrationModes=new ComboBox{Margin=new(0,0,0,16)};
    foreach(var (id,title) in new[]{("tongue","Tongue")}.Concat(QproFaceTracking.Hub.CalibrationSettings.FaceGroups).Append(("pupils","Pupils")))
    {var item=new ComboBoxItem{Content=title,Tag=id,IsSelected=kind==id};modes.Items.Add(item);}
    modes.SelectionChanged+=(_,_)=>{if(recording||training||modes.SelectedItem is not ComboBoxItem item)return;var focus=modes.IsKeyboardFocusWithin;kind=(string)item.Tag;candidate=prefix="";CalibrationReset();if(focus)Dispatcher.BeginInvoke(()=>calibrationModes?.Focus(),System.Windows.Threading.DispatcherPriority.Input);};
    Field(Page,"Tracking area",modes);
    var content=new StackPanel();
    poseTitle=Text("Calibrate "+GroupTitle(kind).ToLowerInvariant(),23);poseTitle.TextAlignment=TextAlignment.Center;content.Children.Add(poseTitle);
    poseDetail=Text(Intro(kind),14,true);poseDetail.TextAlignment=TextAlignment.Center;content.Children.Add(poseDetail);
    cue=Text(kind=="pupils"?"About 80 seconds":"3 short rounds",25);cue.FontWeight=FontWeights.SemiBold;cue.TextAlignment=TextAlignment.Center;cue.Margin=new(0,4,0,0);content.Children.Add(cue);
    face=new Canvas{Width=360,Height=250};DrawFace(face,new JsonObject(),kind);
    content.Children.Add(new Viewbox{Stretch=Stretch.Uniform,StretchDirection=StretchDirection.DownOnly,Child=face});
    ResizeGuide();
    progress=new ProgressBar{Height=5,Minimum=0,Maximum=100,Margin=new(0,16,0,10),Foreground=(Brush)FindResource("Accent"),Visibility=Visibility.Collapsed};content.Children.Add(progress);
    count=Text(kind=="pupils"?"":"Recordings stay on this PC.",13,true);count.TextAlignment=TextAlignment.Center;count.Visibility=kind=="pupils"?Visibility.Collapsed:Visibility.Visible;content.Children.Add(count);
    Page.Children.Add(Card(content));
    var actions=Actions;actions.Children.Clear();
    begin=AsyncButton("Start calibration",Begin,true);actions.Children.Add(begin);
    pause=Button("Pause",()=>Send("pause"));pause.Visibility=Visibility.Collapsed;actions.Children.Add(pause);
    skip=Button("Skip pose",()=>Send("skip"));skip.Margin=new(8,0,0,0);skip.Visibility=Visibility.Collapsed;actions.Children.Add(skip);
    cancel=Button("Cancel",()=>Send("cancel"));cancel.Margin=new(12,0,0,0);cancel.Visibility=Visibility.Collapsed;actions.Children.Add(cancel);
    review=Button("Review video",()=>Open(prefix+".avi"));review.Visibility=Visibility.Collapsed;review.Margin=new(12,0,0,0);actions.Children.Add(review);
    if(kind!="pupils")
    {
        var manual=manualControls=new WrapPanel{Margin=new(0,14,0,0),Visibility=Visibility.Collapsed};
        foreach(var (title,action) in new[]{("Capture","capture"),("Next pose","next"),("Undo","undo")}){var button=Button(title,()=>Send(action));button.Margin=new(0,0,8,0);manual.Children.Add(button);}
        var options=captureOptions=new Expander{Header="Options",Margin=new(0,0,0,0)};var stack=new StackPanel();
        var auto=new CheckBox{Content="Capture automatically",IsChecked=automatic};auto.Click+=(_,_)=>{if(recording)return;automatic=auto.IsChecked==true;};stack.Children.Add(auto);
        var combo=new ComboBox{ItemsSource=new[]{"Normal","Slow"},SelectedIndex=settle>3?1:0,MinWidth=150,HorizontalAlignment=HorizontalAlignment.Left};combo.SelectionChanged+=(_,_)=>{if(!recording)settle=combo.SelectedIndex==1?4:2.0;};Field(stack,"Pace",combo);options.Content=stack;Page.Children.Add(options);Page.Children.Add(manual);
    }
    if(training){RefreshCalibrationControls();ShowTraining();}
 }
 static string GroupTitle(string kind)=>kind=="tongue"?"Tongue":kind=="pupils"?"Pupils":Array.Find(QproFaceTracking.Hub.CalibrationSettings.FaceGroups,group=>group.Kind==kind).Title??kind;
 void CalibrationReset(){Page.Children.Clear();manualControls=null;captureOptions=null;Calibration();}
 async Task Begin()
 {
    if(recording||training)return;
    if(!session.Running&&!preview){Error("Start tracking, then start calibration.");return;}
    if((string.IsNullOrEmpty(runtimeId)||session.State!="Connected"||!FreshState())&&!preview){Error("Waiting for the headset cameras. Make sure the headset is awake and connected.");return;}
    ClearNotice();candidate="";prefix="";
    if(!Send("begin",new JsonObject{["kind"]=kind,["automatic"]=automatic,["settle"]=settle}))return;
    recording=true;review!.Visibility=Visibility.Collapsed;count!.Visibility=Visibility.Visible;RefreshCalibrationControls();
    if(kind=="pupils")WindowState=WindowState.Maximized;
    await Task.CompletedTask;
 }
 bool Send(string action,JsonObject? fields=null)
 {
    if(preview)return true;
    if(string.IsNullOrEmpty(runtimeId))return false;
    if(state["ack"]?.GetValue<long>()<command){if(action=="reload")reloadPending=true;else if(action!="heartbeat")Error("Waiting for the previous action…");return false;}
    var data=fields??new JsonObject();data["id"]=++command;data["runtimeId"]=runtimeId;data["action"]=action;
    try{Session.Write(Path.Combine(session.Root,"studio.command.json"),data);heartbeat=DateTime.UtcNow;return true;}
    catch{command--;throw;}
 }
 string trainingKind="";
 double trainingFraction;
 DateTime trainingStarted;
 async Task Train()
 {
    if(training||string.IsNullOrEmpty(prefix))return;
    training=true;trainingKind=kind;trainingFraction=0;trainingStarted=DateTime.UtcNow;ClearNotice();RefreshCalibrationControls();ShowTraining();
    var name=trainingKind=="puff"?"Cheek":GroupTitle(trainingKind);
    try
    {
        candidate=await session.Train(trainingKind,prefix,fraction=>{trainingFraction=Math.Max(trainingFraction,Math.Clamp(fraction,0,1));ShowTraining();});
        await Apply(trainingKind);
        training=false;Finished(true,name+" calibration is on.");
    }
    catch(Exception error){training=false;Finished(false,error.Message);}
    finally{training=false;RefreshCalibrationControls();}
 }
 async Task Apply(string which)
 {
    if(candidate.Length==0)return;
    await session.Apply(which,candidate);applyingAt=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()/1000.0;Send("reload");
 }
 void ShowTraining()
 {
    if(page!="Calibration"||!training||poseTitle is null)return;
    poseTitle.Text="Creating your calibration";poseDetail!.Text="You can take off the headset and keep using QFT+. Tracking stays on.";
    var elapsed=(DateTime.UtcNow-trainingStarted).TotalSeconds;
    var left=trainingFraction<.05?-1:elapsed*(1-trainingFraction)/trainingFraction;
    cue!.Text=left<0?"Starting…":left<50?"Less than a minute left":$"About {Math.Max(1,Math.Round(left/60))} min left";
    progress!.IsIndeterminate=false;progress.Value=trainingFraction*100;progress.Visibility=Visibility.Visible;
    count!.Text=$"{trainingFraction:P0}";count.Visibility=Visibility.Visible;
 }
 void Finished(bool passed,string message)
 {
    if(page!="Calibration"||kind!=trainingKind||poseTitle is null){Error(message);return;}
    poseTitle.Text=passed?"Calibration is on":"Calibration didn't pass";
    poseDetail!.Text=passed?"Check your avatar to see the difference.":message;
    cue!.Text="";count!.Text="";progress!.Value=passed?100:0;progress.Visibility=passed?Visibility.Visible:Visibility.Collapsed;
    if(begin is not null){begin.Content="Calibrate again";begin.ClearValue(StyleProperty);}
    if(review is not null)review.Visibility=File.Exists(prefix+".avi")?Visibility.Visible:Visibility.Collapsed;
 }
 async Task Tick()
 {
    if(polling)return;polling=true;
    try
    {
        if(manualTesting){if(page=="Manual"&&IsVisible)PublishManual();else StopManual();}
        if(!busy)session.Poll();
        state=Session.Read(Path.Combine(session.Root,"studio.state.json"));
        var id=state["runtimeId"]?.GetValue<string>()??"";
        if(id!=runtimeId){runtimeId=id;command=state["ack"]?.GetValue<long>()??0;}
        if(reloadPending&&FreshState()&&state["ack"]?.GetValue<long>()>=command){reloadPending=false;Send("reload");}
        if(recording&&DateTime.UtcNow-heartbeat>TimeSpan.FromMilliseconds(700))Send("heartbeat");
        if(page=="Calibration"&&recording)UpdateGuide();
        if(applyingAt>0&&state["reloaded"]?.GetValue<double>()>=applyingAt)applyingAt=0;
        if(training)ShowTraining();
        if(recording&&!FreshState())Error("Camera feed paused. Make sure the headset is awake and connected, or cancel.");
        if(page=="Cameras"&&IsVisible)await UpdateCameras();
        if(IsVisible)liveLog?.Invoke();
        if(state["error"]?.GetValue<string>() is {Length:>0} error && (recording||page=="Calibration"))Error(error);
        if(page!="Tracking"&&session.Hybrid is not null&&!Session.Alive(session.Hybrid))Error("Hybrid hands stopped. The Hybrid hands log in Settings shows why.");
    }
    catch(Exception error){Error(error.Message);}
    finally{polling=false;}
 }
 void UpdateGuide()
 {
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
        recording=false;Theme(session.Config["theme"]?.GetValue<string>()??"System");
        if(state["phase"]!.GetValue<string>()!="complete"){CalibrationReset();RefreshCalibrationControls();return;}
        begin!.Content="Calibrate again";begin.ClearValue(StyleProperty);count.Text="";prefix=state["prefix"]!.GetValue<string>();progress.Value=100;
        RefreshCalibrationControls();
        if(kind=="pupils"){candidate=prefix+".pupils.json";_=ApplyPupils();}
        else _=Train();
    }
 }
 async Task ApplyPupils()
 {
    try{trainingKind="pupils";await Apply("pupils");Finished(true,"Pupil calibration is on.");}
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
