using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace QFTPlus;
public partial class StudioWindow
{
 ListBox? calibrationModes;
 StackPanel? manualControls;
 Expander? captureOptions;
 Button? review;
 void ResizeGuide()
 {
    if(page!="Calibration"||face is null)return;
    face.Height=Math.Clamp(ActualHeight-530,100,230);
    DrawFace(face,recording?state["targets"]?.AsObject()??new():new(),kind);
 }
 void RefreshCalibrationControls()
 {
    foreach(var button in nav.Values)button.IsEnabled=!recording&&!training;
    if(calibrationModes is not null)calibrationModes.IsEnabled=!recording&&!training;
    if(captureOptions is not null){captureOptions.Visibility=recording||training?Visibility.Collapsed:Visibility.Visible;captureOptions.IsEnabled=!recording&&!training;}
    if(manualControls is not null)manualControls.Visibility=recording&&!automatic?Visibility.Visible:Visibility.Collapsed;
    if(begin is not null)begin.Visibility=recording||training?Visibility.Collapsed:Visibility.Visible;
    if(pause is not null)pause.Visibility=recording&&kind!="pupils"?Visibility.Visible:Visibility.Collapsed;
    if(cancel is not null)cancel.Visibility=recording?Visibility.Visible:Visibility.Collapsed;
    if(progress is not null)progress.Visibility=recording||training||prefix.Length>0?Visibility.Visible:Visibility.Collapsed;
    StartButton.IsEnabled=!recording&&!training;
 }
 void Calibration()
 {
    manualControls=null;captureOptions=null;
    var layout=new FrameworkElementFactory(typeof(WrapPanel));layout.SetValue(WrapPanel.OrientationProperty,Orientation.Horizontal);
    var modes=calibrationModes=new ListBox{ItemsPanel=new ItemsPanelTemplate(layout),Background=Brushes.Transparent,BorderThickness=new(0),Padding=new(0),Margin=new(0,0,0,16),ItemContainerStyle=(Style)FindResource("NavigationItem")};
    System.Windows.Automation.AutomationProperties.SetName(modes,"Calibration type");
    foreach(var (id,title) in new[]{("tongue","Tongue"),("puff","Cheeks"),("pupils","Pupils")})
    {var item=new ListBoxItem{Content=title,Tag=id,IsSelected=kind==id,MinWidth=100,FontWeight=kind==id?FontWeights.SemiBold:FontWeights.Normal};modes.Items.Add(item);}
    modes.SelectionChanged+=(_,_)=>{if(recording||training||modes.SelectedItem is not ListBoxItem item)return;var focus=modes.IsKeyboardFocusWithin;kind=(string)item.Tag;candidate=prefix="";CalibrationReset();if(focus)Dispatcher.BeginInvoke(()=>{if(calibrationModes?.SelectedItem is ListBoxItem selected)selected.Focus();},System.Windows.Threading.DispatcherPriority.Input);};
    Page.Children.Add(modes);
    var content=new StackPanel();
    poseTitle=Text("Follow the guide",23);content.Children.Add(poseTitle);
    poseDetail=Text("Open this window in Virtual Desktop.",14,true);content.Children.Add(poseDetail);
    face=new Canvas{Width=360,Height=250,HorizontalAlignment=HorizontalAlignment.Center};DrawFace(face,new JsonObject(),kind);content.Children.Add(face);
    ResizeGuide();
    cue=Text(kind=="pupils"?"56 seconds · Screen alternates light and dark":"3 rounds",14);cue.TextAlignment=TextAlignment.Center;content.Children.Add(cue);
    progress=new ProgressBar{Height=5,Minimum=0,Maximum=100,Margin=new(0,16,0,10),Foreground=(Brush)FindResource("Accent"),Visibility=Visibility.Collapsed};content.Children.Add(progress);
    count=Text(kind=="pupils"?"":"Saves camera video and samples on this PC.",13,true);count.TextAlignment=TextAlignment.Center;count.Visibility=kind=="pupils"?Visibility.Collapsed:Visibility.Visible;content.Children.Add(count);
    Page.Children.Add(Card(content));
    var actions=Actions;actions.Children.Clear();
    begin=AsyncButton("Start calibration",Begin,true);actions.Children.Add(begin);
    pause=Button("Pause",()=>Send("pause"));pause.Visibility=Visibility.Collapsed;actions.Children.Add(pause);
    cancel=Button("Cancel",()=>Send("cancel"));cancel.Margin=new(8,0,0,0);cancel.Visibility=Visibility.Collapsed;actions.Children.Add(cancel);
    train=AsyncButton("Create calibration",Train,true);train.Visibility=Visibility.Collapsed;train.Margin=new(8,0,0,0);actions.Children.Add(train);
    apply=AsyncButton("Apply",Apply,true);apply.Visibility=Visibility.Collapsed;apply.Margin=new(8,0,0,0);actions.Children.Add(apply);
    review=Button("Review video",()=>Open(prefix+".avi"));review.Visibility=Visibility.Collapsed;review.Margin=new(8,0,0,0);actions.Children.Add(review);
    if(kind!="pupils")
    {
        var manual=manualControls=new StackPanel{Orientation=Orientation.Horizontal,Margin=new(0,14,0,0),Visibility=Visibility.Collapsed};
        foreach(var (title,action) in new[]{("Capture","capture"),("Next pose","next"),("Undo","undo")}){var button=Button(title,()=>Send(action));button.Margin=new(0,0,8,0);manual.Children.Add(button);}
        var options=captureOptions=new Expander{Header="Options",Margin=new(0,0,0,0)};var stack=new StackPanel();
        var auto=new CheckBox{Content="Capture automatically",IsChecked=automatic};auto.Click+=(_,_)=>{if(recording)return;automatic=auto.IsChecked==true;};stack.Children.Add(auto);
        var combo=new ComboBox{ItemsSource=new[]{"Normal","Slow"},SelectedIndex=settle>3?1:0,MinWidth=150,HorizontalAlignment=HorizontalAlignment.Left};combo.SelectionChanged+=(_,_)=>{if(!recording)settle=combo.SelectedIndex==1?4:2.5;};Field(stack,"Pace",combo);options.Content=stack;Page.Children.Add(options);Page.Children.Add(manual);
    }
 }
 void CalibrationReset(){Page.Children.Clear();manualControls=null;captureOptions=null;Calibration();}
 async Task Begin()
 {
    if(recording||training)return;
    if(!session.Running&&!preview){Error("Start tracking, then begin calibration.");return;}
    if((string.IsNullOrEmpty(runtimeId)||session.State!="Connected"||!FreshState())&&!preview){Error("Waiting for the camera stream…");return;}
    ClearNotice();candidate="";prefix="";
    if(!Send("begin",new JsonObject{["kind"]=kind,["automatic"]=automatic,["settle"]=settle}))return;
    recording=true;train!.Visibility=apply!.Visibility=review!.Visibility=Visibility.Collapsed;count!.Visibility=Visibility.Visible;RefreshCalibrationControls();
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
 async Task Train()
 {
    if(training||string.IsNullOrEmpty(prefix))return;training=true;ClearNotice();train!.IsEnabled=false;RefreshCalibrationControls();
    try{cue!.Text="Creating calibration…";progress!.IsIndeterminate=true;candidate=await session.Train(kind,prefix);apply!.Visibility=Visibility.Visible;train.Visibility=Visibility.Collapsed;cue.Text="Ready to apply";count!.Text="";}
    finally{training=false;progress!.IsIndeterminate=false;train.IsEnabled=true;RefreshCalibrationControls();}
 }
 async Task Apply()
 {
    if(candidate.Length==0)return;
    await session.Apply(kind,candidate);applyingAt=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()/1000.0;Send("reload");apply!.Visibility=Visibility.Collapsed;cue!.Text="Applying…";
 }
 async Task Tick()
 {
    if(polling)return;polling=true;
    try
    {
        if(manualTesting){if(page=="Manual"&&IsVisible)PublishManual();else StopManual();}
        if(!busy&&!training)session.Poll();
        state=Session.Read(Path.Combine(session.Root,"studio.state.json"));
        var id=state["runtimeId"]?.GetValue<string>()??"";
        if(id!=runtimeId){runtimeId=id;command=state["ack"]?.GetValue<long>()??0;}
        if(reloadPending&&FreshState()&&state["ack"]?.GetValue<long>()>=command){reloadPending=false;Send("reload");}
        if(recording&&DateTime.UtcNow-heartbeat>TimeSpan.FromMilliseconds(700))Send("heartbeat");
        if(page=="Calibration"&&recording)UpdateGuide();
        if(applyingAt>0&&state["reloaded"]?.GetValue<double>()>=applyingAt){applyingAt=0;if(cue is not null)cue.Text="Calibration applied";}
        if(recording&&!FreshState())Error("Camera feed paused. Wait or cancel calibration.");
        if(page=="Cameras"&&IsVisible)await UpdateCameras();
        if(state["error"]?.GetValue<string>() is {Length:>0} error && (recording||page=="Calibration"))Error(error);
        if(session.Hybrid is not null&&!Session.Alive(session.Hybrid))Error("Hybrid hands stopped. Check the hybrid log in Settings.");
    }
    catch(Exception error){Error(error.Message);}
    finally{polling=false;}
 }
 void UpdateGuide()
 {
    if(state["kind"]?.GetValue<string>()!=kind)return;
    poseTitle!.Text=state["title"]?.GetValue<string>()??poseTitle.Text;
    poseDetail!.Text=state["instruction"]?.GetValue<string>()??"";
    cue!.Text=state["cue"]?.GetValue<string>()??"Look at the center cross";
    progress!.Value=(state["progress"]?.GetValue<double>()??(state["index"]?.GetValue<int>()??0)/4.0)*100;
    count!.Text=$"{(state["index"]?.GetValue<int>()??0)+1} of {state["total"]?.GetValue<int>()??4}";
    if(kind=="pupils")
    {
        SetPupilStimulus(state["bright"]?.GetValue<bool>()==true);cue.Text=$"{state["remaining"]?.GetValue<double>():0} seconds";
    }
    else DrawFace(face!,state["targets"]?.AsObject()??new(),kind);
    if(pause is not null)pause.Content=state["paused"]?.GetValue<bool>()==true?"Resume":"Pause";
    if(state["phase"]?.GetValue<string>() is "complete" or "cancelled")
    {
        recording=false;begin!.Content="Start over";Theme(session.Config["theme"]?.GetValue<string>()??"System");poseTitle.Text="";poseDetail.Text="";count.Text="";
        if(state["phase"]!.GetValue<string>()=="complete")
        {
            prefix=state["prefix"]!.GetValue<string>();cue.Text="Recording complete";progress.Value=100;
            if(kind=="pupils"){candidate=prefix+".pupils.json";apply!.Visibility=Visibility.Visible;}
            else {train!.Visibility=review!.Visibility=Visibility.Visible;}
        }
        else cue.Text="Calibration cancelled";
        RefreshCalibrationControls();
    }
 }
 void SetPupilStimulus(bool bright)
 {
    var bg=new SolidColorBrush(bright?Color.FromRgb(235,235,235):Color.FromRgb(18,18,18));var fg=bright?Brushes.Black:Brushes.White;
    foreach(var key in new[]{"Canvas","Surface","Sidebar"})Resources[key]=bg;
    Resources["Ink"]=Resources["Muted"]=fg;
    if(face is not null)DrawFace(face,new(),"pupils");
 }
 bool FreshState()=>state["time"]?.GetValue<double>() is {} timestamp && Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()/1000.0-timestamp)<3;
}
