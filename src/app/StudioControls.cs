using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using QproFaceTracking.Hub;

namespace QFTPlus;
public partial class StudioWindow
{
 bool manualTesting;
 JsonObject manualValues=new();
 string outputParameter="*", manualGroup="Tongue";
 string[] Parameters()=>JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(session.Root,"tracking-parameters.json")))??[];
 static string Label(string key)=>key=="*"?"Global defaults":Regex.Replace(key,"([a-z])([A-Z])","$1 $2");
 static double Number(JsonObject data,string key,double fallback)=>data[key] is JsonValue v&&v.TryGetValue<double>(out var n)&&double.IsFinite(n)?n:fallback;
 static double Neutral(string name)=>name.StartsWith("Pupil")?.5:name.StartsWith("Openness")?1:0;
 void Field(Panel parent,string title,Control control)
 {
    var label=Text(title);AutomationProperties.SetLabeledBy(control,label);AutomationProperties.SetName(control,title);
    parent.Children.Add(label);parent.Children.Add(control);
 }

 Slider SliderRow(Panel parent,string title,double min,double max,double value,Action<double> changed,string format="0'%'",double displayScale=1)
 {
    var stack=new StackPanel{Margin=new(0,0,0,16)};
    var slider=new Slider{Minimum=min,Maximum=max,Value=Math.Clamp(value,min,max),SmallChange=(max-min)/100,LargeChange=(max-min)/10,MinHeight=40};AutomationProperties.SetName(slider,title);
    string Display()=>(slider.Value*displayScale).ToString(format);
    var number=new TextBox{Text=Display(),MinWidth=88,HorizontalContentAlignment=HorizontalAlignment.Right,Margin=new(16,0,0,0)};
    AutomationProperties.SetName(number,title+" value");
    var range=$"Enter a value from {(min*displayScale).ToString(format)} to {(max*displayScale).ToString(format)}.";
    AutomationProperties.SetHelpText(number,range);AutomationProperties.SetHelpText(slider,range);
    var problem=Text(range,13);problem.Visibility=Visibility.Collapsed;AutomationProperties.SetLiveSetting(problem,AutomationLiveSetting.Polite);
    var line=new DockPanel();DockPanel.SetDock(number,Dock.Right);line.Children.Add(number);var label=Text(title);label.VerticalAlignment=VerticalAlignment.Center;label.Margin=new(0);line.Children.Add(label);stack.Children.Add(line);
    void Commit()
    {
        if(!double.TryParse(number.Text.Trim().TrimEnd('%'),NumberStyles.Float,CultureInfo.CurrentCulture,out var input)||!double.IsFinite(input)||input/displayScale<min||input/displayScale>max){problem.Visibility=Visibility.Visible;return;}
        slider.Value=input/displayScale;number.Text=Display();problem.Visibility=Visibility.Collapsed;
    }
    number.LostKeyboardFocus+=(_,_)=>Commit();
    number.PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Enter){Commit();e.Handled=true;}else if(e.Key==Key.Escape){number.Text=Display();problem.Visibility=Visibility.Collapsed;e.Handled=true;}};
    slider.ValueChanged+=(_,_)=>{number.Text=Display();problem.Visibility=Visibility.Collapsed;changed(slider.Value);};
    stack.Children.Add(slider);stack.Children.Add(problem);parent.Children.Add(stack);
    return slider;
 }
 Action? trackingRefresh;
 void EyeOptions()
 {
    var eyes=new StackPanel();
    var enabled=new CheckBox{Content=new TextBlock{Text="Independent eye gaze"},IsChecked=session.IndependentGaze};
    AutomationProperties.SetName(enabled,"Independent eye gaze");
    eyes.Children.Add(enabled);
    eyes.Children.Add(Text("Tracks the direction of each eye separately.",13,true));
    var status=Text("",13,true);AutomationProperties.SetLiveSetting(status,AutomationLiveSetting.Polite);eyes.Children.Add(status);
    var setup=Button("Set up eye tracking",()=>Navigate("Setup"));eyes.Children.Add(setup);
    var options=new StackPanel{Margin=new(0,12,0,0)};
    options.Children.Add(Text("Adjust how much your eyes turn toward each other when looking at something nearby.",13,true));
    var controls=new StackPanel();
    var strength=SliderRow(controls,"Convergence strength",0,3,session.VergenceGain,v=>{if(!preview)session.SetVergenceGain(v);},displayScale:100);
    strength.SmallChange=.05;strength.LargeChange=.25;strength.TickFrequency=.05;strength.IsSnapToTickEnabled=true;
    controls.Children.Add(Button("Reset to 100%",()=>strength.Value=1));options.Children.Add(controls);
    options.Children.Add(Text("100% uses the calibrated movement. Higher values increase it. At 0%, both eyes look in the same direction. Depth accuracy is experimental.",13,true));
    var adjustmentStatus=Text("",13,true);AutomationProperties.SetLiveSetting(adjustmentStatus,AutomationLiveSetting.Polite);options.Children.Add(adjustmentStatus);
    eyes.Children.Add(new Expander{Header="Eye adjustments",Content=options,Margin=new(0,8,0,0)});
    void Refresh()
    {
        var on=enabled.IsChecked==true;
        enabled.IsEnabled=!busy&&!session.Running;
        status.Text=busy?"Wait for the current step to finish to change eye gaze.":session.Running?"Stop tracking to change eye gaze.":on?"Starts with tracking.":"Uses standard eye tracking.";
        AutomationProperties.SetHelpText(enabled,"Tracks the direction of each eye separately. "+status.Text);
        setup.Visibility=on&&!File.Exists(Path.Combine(session.Root,"models/eye/bolt-independent-axes.ptl"))?Visibility.Visible:Visibility.Collapsed;
        setup.IsEnabled=!busy&&!session.Running;
        controls.IsEnabled=on&&!busy&&(!session.Running||session.State=="Connected");
        adjustmentStatus.Text=!on?"Turn on independent eye gaze to adjust convergence.":busy||session.Running&&session.State!="Connected"?"Available when tracking is ready.":session.Running?"Changes apply immediately and are saved automatically.":"Saved automatically. Applies when tracking starts.";
    }
    enabled.Click+=(_,_)=>{if(!preview)session.Save("independentGaze",enabled.IsChecked==true);Refresh();};
    trackingRefresh=Refresh;Refresh();Page.Children.Add(Card(eyes));
 }
 void Adjustments()
 {
    var choices=new[]{"*"}.Concat(Parameters()).ToArray();
    var select=new ComboBox{ItemsSource=choices.Select(Label).ToArray(),SelectedIndex=Array.IndexOf(choices,outputParameter),Margin=new(0,0,0,24)};
    select.SelectionChanged+=(_,_)=>{if(select.SelectedIndex<0)return;var focus=select.IsKeyboardFocusWithin;outputParameter=choices[select.SelectedIndex];Navigate("Adjustments");if(focus)Page.Children.OfType<ComboBox>().First().Focus();};Field(Page,"Parameter",select);
    var path=Path.Combine(session.Root,"output-settings.json");var config=Session.Read(path);
    var inherited=config["*"] as JsonObject??new();var values=(config[outputParameter] as JsonObject??inherited).DeepClone().AsObject();
    void Save(string key,double value){values[key]=value;if(preview)return;var saved=Session.Read(path);saved[outputParameter]=values.DeepClone();Session.Write(path,saved);}
    var panel=new StackPanel();
    SliderRow(panel,"Strength",0,3,Number(values,"strength",1),v=>Save("strength",v),displayScale:100);
    SliderRow(panel,"Smoothing",0,100,Number(values,"smoothing",0),v=>Save("smoothing",v));
    Page.Children.Add(Card(panel));
    var advanced=new StackPanel{Margin=new(0,16,0,0)};
    SliderRow(advanced,"Dead zone",0,.5,Number(values,"deadzone",0),v=>Save("deadzone",v),displayScale:100);
    SliderRow(advanced,"Offset",-.5,.5,Number(values,"offset",0),v=>Save("offset",v),displayScale:100);
    Page.Children.Add(new Expander{Header="Fine tuning",Content=advanced});
    var reset=Button(outputParameter=="*"?"Reset defaults":"Use global settings",()=>{if(!preview){var data=Session.Read(path);data.Remove(outputParameter);Session.Write(path,data);}Navigate("Adjustments");});Actions.Children.Add(reset);
 }
 void Manual()
 {
    var choices=new[]{"Tongue","Cheeks","Eyes","Brows","Mouth","Other"};
    var select=new ComboBox{ItemsSource=choices,SelectedItem=manualGroup,Margin=new(0,0,0,12)};
    select.SelectionChanged+=(_,_)=>{var focus=select.IsKeyboardFocusWithin;StopManual();manualGroup=(string)select.SelectedItem;Navigate("Manual");if(focus)Page.Children.OfType<ComboBox>().First().Focus();};Field(Page,"Expressions",select);
    var enabled=new CheckBox{Content="Override live tracking",IsChecked=false,Margin=new(0,0,0,16)};
    enabled.Click+=(_,_)=>{manualTesting=enabled.IsChecked==true;if(manualTesting&&!preview){var processes=System.Diagnostics.Process.GetProcessesByName("VRCFaceTracking");foreach(var process in processes)process.Dispose();if(processes.Length==0){enabled.IsChecked=manualTesting=false;Error("Open VRCFaceTracking to test movements.");return;}}PublishManual();};Page.Children.Add(enabled);
    var names=Parameters().Where(name=>manualGroup switch{
        "Tongue"=>name.StartsWith("Tongue"),"Cheeks"=>name.StartsWith("Cheek"),
        "Eyes"=>name.StartsWith("Eye")||name.StartsWith("Gaze")||name.StartsWith("Pupil")||name.StartsWith("Openness"),
        "Brows"=>name.StartsWith("Brow"),"Mouth"=>name.StartsWith("Mouth")||name.StartsWith("Lip")||name.StartsWith("Jaw"),
        _=>name.StartsWith("Nose")||name.StartsWith("Nasal")||name.StartsWith("Soft")||name.StartsWith("Throat")||name.StartsWith("Neck")}).ToArray();
    manualValues=new();var stack=new StackPanel();
    foreach(var name in names)
    {
        manualValues[name]=Neutral(name);
        SliderRow(stack,Label(name),name.StartsWith("Gaze")?-1.2:0,name.StartsWith("Gaze")?1.2:1,Neutral(name),v=>{manualValues[name]=v;PublishManual();},"0.00");
    }
    Page.Children.Add(Card(stack));
    Actions.Children.Add(Button("Reset",()=>{StopManual();Navigate("Manual");}));
 }
 void PublishManual()
 {
    if(preview)return;
    var data=new JsonObject{["expires"]=manualTesting?DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()/1000.0+1.5:0,["values"]=manualTesting?manualValues.DeepClone():new JsonObject()};
    Session.Write(Path.Combine(session.Root,"manual-output.json"),data);
 }
 void StopManual(){if(!manualTesting)return;manualTesting=false;PublishManual();}

 static readonly (int From,string Title,string About)[] SetupSteps={
    (0,"Find your Quest Pro","Looks for your headset over USB and Wi-Fi."),
    (10,"Allow access","Approve USB debugging in the headset, then allow Shell in Magisk → Superuser."),
    (25,"Turn on Wi-Fi","Switches the headset to Wi-Fi so you can unplug the cable. Skipped when Wi-Fi is already on."),
    (35,"Install components","One-time install of the tracking runtime on this PC. This can take a few minutes."),
    (55,"Prepare eye tracking","Builds the eye model from your headset. Keep the headset awake."),
    (60,"Check VR apps","Makes sure Steam, SteamVR, and VRCFaceTracking are installed."),
    (70,"Update VRCFaceTracking","Installs the QFT+ module. VRCFaceTracking closes briefly if it’s open.")};
 Action? setupRefresh;
 int setupStep=-1;
 bool setupDone, searching;
 string setupProblem="";
 List<SetupService.Headset> headsets=new();
 DateTime? headsetsChecked;
 void SetupProgress()
 {
    if(!session.SettingUp)return;
    if(session.Progress>=100){setupDone=true;setupStep=SetupSteps.Length;return;}
    if(session.State is "Setup paused" or "Couldn’t connect")return;
    setupStep=Array.FindLastIndex(SetupSteps,s=>s.From<=session.Progress);
 }
 void SetupOptions()
 {
    Subtitle.Text="Connect your Quest Pro with USB the first time. After that, QFT+ connects over Wi-Fi.";Subtitle.Visibility=Visibility.Visible;
    if(preview&&headsets.Count==0)headsets=[new("192.0.2.10:5555","PREVIEW0001",true,"device"),new("PREVIEW0001","PREVIEW0001",false,"device")];
    TextBlock Heading(string text){var heading=Text(text,16);heading.FontWeight=FontWeights.SemiBold;AutomationProperties.SetHeadingLevel(heading,AutomationHeadingLevel.Level2);return heading;}
    TextBlock Symbol(string glyph,string brush){var symbol=new TextBlock{Text=glyph,FontFamily=new("Segoe UI Symbol"),Width=28,Margin=new(0,1,0,0)};symbol.SetResourceReference(TextBlock.ForegroundProperty,brush);return symbol;}
    UIElement Row(TextBlock symbol,params TextBlock[] lines){var row=new DockPanel{Margin=new(0,6,0,6)};DockPanel.SetDock(symbol,Dock.Left);row.Children.Add(symbol);var text=new StackPanel();foreach(var line in lines){line.Margin=new(0,0,0,2);text.Children.Add(line);}row.Children.Add(text);return row;}
    UIElement Choice(string group,string title,string about,bool selected,Action picked)
    {
        var body=new StackPanel();body.Children.Add(new TextBlock{Text=title});body.Children.Add(Text(about,13,true));
        var radio=new RadioButton{GroupName=group,Content=body,IsChecked=selected,Margin=new(0,4,0,4),VerticalContentAlignment=VerticalAlignment.Top};
        AutomationProperties.SetName(radio,title);AutomationProperties.SetHelpText(radio,about);radio.Checked+=(_,_)=>picked();return radio;
    }

    var found=new StackPanel();var caption=Text("",13,true);var activity=new ProgressBar{IsIndeterminate=true,Height=3,Margin=new(0,0,0,8)};
    var search=new Button{Content="Search again"};search.Click+=(_,_)=>_=Search();AutomationProperties.SetHelpText(search,"Looks for headsets over USB and Wi-Fi.");
    var top=new DockPanel{Margin=new(0,0,0,4)};DockPanel.SetDock(search,Dock.Right);top.Children.Add(search);var title=Heading("Headset");title.VerticalAlignment=VerticalAlignment.Center;top.Children.Add(title);
    var headsetCard=new StackPanel();headsetCard.Children.Add(top);headsetCard.Children.Add(caption);headsetCard.Children.Add(activity);headsetCard.Children.Add(found);Page.Children.Add(Card(headsetCard));
    void ShowHeadsets()
    {
        found.Children.Clear();activity.Visibility=searching?Visibility.Visible:Visibility.Collapsed;search.IsEnabled=!searching;search.Content=searching?"Searching…":"Search again";
        caption.Text=searching?"Searching USB and Wi-Fi…":headsetsChecked is {} at?$"Last checked at {at:t}.":"Not checked yet.";
        var preferred=session.Config["headsetSerial"]?.GetValue<string>()??"";
        found.Children.Add(Choice("headset","Any Quest Pro","Connects to the first Quest Pro it finds.",preferred.Length==0,()=>{if(!preview)session.Save("headsetSerial","");}));
        foreach(var group in headsets.Where(q=>q.State=="device").GroupBy(q=>q.Serial))
        {
            var ways=string.Join(" and ",group.OrderBy(q=>q.Wireless?0:1).Select(q=>q.Wireless?$"Wi-Fi ({q.Target})":"USB"));var serial=group.Key;
            found.Children.Add(Choice("headset","Quest Pro · Ready",$"Connected by {ways}. Serial {serial}.",preferred==serial,()=>{if(!preview)session.Save("headsetSerial",serial);}));
        }
        if(preferred.Length>0&&!headsets.Any(q=>q.State=="device"&&q.Serial==preferred))
            found.Children.Add(Choice("headset","Quest Pro · Not found right now",$"Serial {preferred}. Wake the headset, then search again.",true,()=>{}));
        foreach(var q in headsets.Where(q=>q.State!="device"))
        {
            var where=q.Wireless?$"Wi-Fi ({q.Target})":"USB";
            var (status,fix)=q.State=="unauthorized"?("Waiting for permission","In the headset, choose “Always allow from this computer,” then Allow."):("Not responding","Restart the headset and keep USB connected, then search again.");
            var line=new TextBlock{Text=$"Quest Pro · {status}",Margin=new(0)};found.Children.Add(Row(Symbol("⚠","Ink"),line,Text($"{where}. {fix}",13,true)));
        }
        if(!searching&&headsetsChecked is not null&&headsets.Count==0)found.Children.Add(Text("No headsets found. Plug in a USB data cable and put on the headset to wake it.",13,true));
    }
    async Task Search()
    {
        if(searching||preview){ShowHeadsets();return;}
        searching=true;ShowHeadsets();
        try{headsets=await session.Discover();headsetsChecked=DateTime.Now;}
        catch(Exception error){Error("Couldn’t search for headsets. "+error.Message);}
        finally{searching=false;if(page=="Setup")ShowHeadsets();}
    }

    var modes=new[]{"auto","wifi","usb"};var current=session.Config["connectionMode"]?.GetValue<string>()??"auto";
    var connection=new StackPanel();connection.Children.Add(Heading("Connection"));
    foreach(var (id,name,about) in new[]{("auto","Automatic","Uses Wi-Fi when it’s available and USB otherwise."),("wifi","Wi-Fi","Unplug after setup. If the headset restarts, connect USB once to turn Wi-Fi back on."),("usb","USB","Keeps the cable connected. Most reliable.")})
        connection.Children.Add(Choice("connection",name,about,current==id,()=>{if(!preview)session.Save("connectionMode",id);}));
    Page.Children.Add(Card(connection));

    var steps=new StackPanel();var status=Text("",14);AutomationProperties.SetLiveSetting(status,AutomationLiveSetting.Polite);
    var bar=new ProgressBar{Minimum=0,Maximum=100,Height=5,Margin=new(0,4,0,12)};bar.SetResourceReference(ProgressBar.ForegroundProperty,"Accent");
    var list=new StackPanel();steps.Children.Add(Heading("Setup steps"));steps.Children.Add(status);steps.Children.Add(bar);steps.Children.Add(list);Page.Children.Add(Card(steps));
    var go=Button("",()=>{},true);Actions.Children.Add(go);
    void ShowSteps()
    {
        list.Children.Clear();
        for(var i=0;i<SetupSteps.Length;i++)
        {
            var (_,name,about)=SetupSteps[i];var now=i==setupStep&&!setupDone;var failed=now&&setupProblem.Length>0;
            if(SetupSteps[i].From==55&&!session.IndependentGaze)about="Skipped while independent eye gaze is off. Uses standard eye tracking.";
            var (glyph,brush,word)=setupDone||i<setupStep?("✓","Accent","Done"):failed?("⚠","Ink","Couldn’t finish"):now&&busy?("●","Accent","In progress"):now?("○","Ink","Paused"):("○","Muted","Not started");
            var heading=new TextBlock{Text=name,FontWeight=now?FontWeights.SemiBold:FontWeights.Normal,Margin=new(0)};
            var detail=Text(failed?setupProblem:now&&busy&&session.Detail.Length>0?session.Detail:about,13,!(now&&busy)&&!failed);
            var row=Row(Symbol(glyph,brush),heading,detail);AutomationProperties.SetName(row,$"{name}, {word}");list.Children.Add(row);
            if(failed&&session.HelpTarget is {} help){var link=Button(session.HelpCaption,()=>Open(help));link.Margin=new(28,0,0,6);list.Children.Add(link);}
        }
        bar.Visibility=busy?Visibility.Visible:Visibility.Collapsed;bar.Value=session.Progress;
        status.Text=busy&&setupStep>=0&&setupStep<SetupSteps.Length?$"Step {setupStep+1} of {SetupSteps.Length}: {SetupSteps[setupStep].Title}":setupDone?"Setup complete. Choose Start to begin tracking.":setupProblem.Length>0?"Setup couldn’t finish. The step below shows what to do.":setupStep>=0?"Setup paused. Your progress is saved.":"Run setup once per headset, or again after an update.";
        go.Content=busy?"Cancel":setupDone?"Set up again":setupProblem.Length>0?"Try again":setupStep>=0?"Continue":"Set up";
    }
    go.Click+=async(_,_)=>
    {
        if(preview)return;
        if(busy){session.CancelSetup();return;}
        busy=true;setupDone=false;setupProblem="";setupStep=0;StartButton.Content="Cancel";ClearNotice();ShowSteps();
        try{await session.Prepare();}
        catch(OperationCanceledException){}
        catch(Exception error){setupProblem=error.Message;Error(error.Message);}
        finally{busy=false;StartButton.Content=session.Running?"Stop":"Start";if(page=="Setup"){ShowSteps();_=Search();}}
    };
    setupRefresh=()=>{SetupProgress();ShowSteps();};

    var tips=new StackPanel();
    foreach(var tip in new[]{"Use a USB cable that carries data. Charge-only cables don’t work.","Turn on Developer Mode for your headset in the Meta Horizon app.","In the headset, allow USB debugging and choose “Always allow from this computer.”","In Magisk → Superuser, allow Shell.","For Wi-Fi, keep this PC and the headset on the same network.","If a headset shows “Not responding,” restart it and keep USB connected."})
        tips.Children.Add(Row(Symbol("•","Muted"),Text(tip,14)));
    var log=Path.Combine(session.Root,"setup.log");var openLog=Button("Open setup log",()=>Open(log));openLog.Margin=new(0,8,0,0);openLog.IsEnabled=File.Exists(log);tips.Children.Add(openLog);
    Page.Children.Add(new Expander{Header="Can’t find your headset?",Content=tips,Margin=new(0,0,0,8)});

    ShowSteps();ShowHeadsets();if(!busy)_=Search();
 }
 void StartupOptions()
 {
    var stack=new StackPanel();var heading=Text("Startup",18);heading.FontWeight=FontWeights.SemiBold;stack.Children.Add(heading);
    foreach(var (key,label) in new[]{("setupOnStart","Set up the headset on Start"),("installModule","Install the VRCFaceTracking module"),("startWithVrcft","Start with VRCFaceTracking"),("openVrApps","Open VR apps on Start")})
    {
        var check=new CheckBox{Content=new TextBlock{Text=label},IsChecked=session.Config[key]?.GetValue<bool>()??true};AutomationProperties.SetName(check,label);check.Click+=(_,_)=>{if(!preview)session.Save(key,check.IsChecked==true);};stack.Children.Add(check);
    }
    Page.Children.Add(Card(stack));
 }
 void ProcessingOptions()
 {
    var hardware=new StackPanel{Margin=new(0,16,0,0)};
    var devices=new[]{"auto","directml","cpu"};var compute=new ComboBox{ItemsSource=new[]{"Automatic","Graphics card (DirectML)","CPU"},SelectedIndex=Math.Max(0,Array.IndexOf(devices,session.Config["inferenceDevice"]?.GetValue<string>()??"auto")),Margin=new(0,0,0,20)};
    var graphics=new StackPanel{Visibility=compute.SelectedIndex==2?Visibility.Collapsed:Visibility.Visible};
    compute.SelectionChanged+=(_,_)=>{graphics.Visibility=compute.SelectedIndex==2?Visibility.Collapsed:Visibility.Visible;if(!preview)session.Save("inferenceDevice",devices[compute.SelectedIndex]);};Field(hardware,"Tracking processor",compute);
    var adapters=GraphicsAdapters.List();
    if(adapters.Count>1)
    {
        var gpu=new ComboBox{ItemsSource=adapters.Select(a=>a.Name).ToArray(),SelectedIndex=Math.Max(0,adapters.FindIndex(a=>a.Index==(session.Config["gpuIndex"]?.GetValue<int>()??0))),Margin=new(0,0,0,8)};
        gpu.SelectionChanged+=(_,_)=>{if(!preview)session.Save("gpuIndex",adapters[gpu.SelectedIndex].Index);};Field(graphics,"Graphics card",gpu);
    }
    var training=new CheckBox{Content=new TextBlock{Text="Train calibrations on the graphics card"},IsChecked=session.Config["gpuTraining"]?.GetValue<bool>()!=false,Margin=new(0,0,0,4)};
    AutomationProperties.SetHelpText(training,"Much faster calibration. Falls back to the processor automatically if the graphics card can't train correctly or quickly.");
    training.Click+=(_,_)=>{if(!preview)session.Save("gpuTraining",JsonValue.Create(training.IsChecked==true));};
    hardware.Children.Add(training);hardware.Children.Add(Text("Falls back to the processor automatically when the graphics card can't.",13,true));
    hardware.Children.Add(graphics);hardware.Children.Add(Text("Takes effect the next time tracking starts. To leave more performance for VR, choose Graphics card, then a different card from the one running your game.",13,true));Page.Children.Add(new Expander{Header="Processing",Content=hardware});
 }
}
