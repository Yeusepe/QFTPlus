using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Qpro.GazeBridge;

namespace QFTPlus;
public partial class StudioWindow
{
 bool manualTesting;
 JsonObject manualValues=new();
 string outputParameter="*", manualGroup="Tongue";
 string[] Parameters()=>JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(session.Root,"tracking-parameters.json")))??[];
 static string Label(string key)=>Regex.Replace(key,"([a-z])([A-Z])","$1 $2");
 static double Neutral(string name)=>name.StartsWith("Pupil")?.5:name.StartsWith("Openness")?1:0;
 void Field(Panel parent,string title,Control control)
 {
    var label=Text(title);AutomationProperties.SetLabeledBy(control,label);AutomationProperties.SetName(control,title);
    parent.Children.Add(label);parent.Children.Add(control);
 }

 Slider SliderRow(Panel parent,string title,double min,double max,double value,Action<double> changed,string format="0.##'%'",double displayScale=1)
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
        var typed=System.Text.RegularExpressions.Regex.Match(number.Text.Trim(),@"^[+\-−]?[\d.,]+").Value.Replace('−','-');
        if(!double.TryParse(typed,NumberStyles.Float,CultureInfo.CurrentCulture,out var input)||!double.IsFinite(input)||input/displayScale<min||input/displayScale>max){problem.Visibility=Visibility.Visible;return;}
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
    var gain=session.VergenceGain;void SaveGain()=>session.SetVergenceGain(gain);
    var strength=SliderRow(controls,"Convergence strength",0,3,gain,v=>{gain=v;SaveLater(SaveGain);},displayScale:100);
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
    enabled.Click+=(_,_)=>{session.Save("independentGaze",enabled.IsChecked==true);Refresh();};
    trackingRefresh=Refresh;Refresh();Page.Children.Add(Card(eyes));
 }
 Action? adjustmentRefresh;
 void Adjustments()
 {
    var parameters=Parameters();
    string[] Members(string area)=>parameters.Where(p=>OutputAdjustments.Area(p)==area).ToArray();
    var areas=OutputAdjustments.Areas.Where(a=>Members(a).Length>0).ToArray();
    var area=outputParameter=="*"?null:OutputAdjustments.Areas.Contains(outputParameter)?outputParameter:OutputAdjustments.Area(outputParameter);
    var single=area is not null&&area!=outputParameter;
    var members=area is null?parameters:Members(area);string[] names=single?[outputParameter]:members;
    void Picker(string title,string[] items,int index,Func<int,string> choose,int position)
    {
        var picker=new ComboBox{ItemsSource=items,SelectedIndex=index,Margin=new(0,0,0,area is not null&&position==0?16:24)};
        picker.SelectionChanged+=(_,_)=>{if(picker.SelectedIndex<0)return;var focus=picker.IsKeyboardFocusWithin;outputParameter=choose(picker.SelectedIndex);Navigate("Adjustments");if(focus)Page.Children.OfType<ComboBox>().ElementAt(position).Focus();};
        Field(Page,title,picker);
    }
    Picker("Area",["All areas",..areas],area is null?0:Array.IndexOf(areas,area)+1,i=>i==0?"*":areas[i-1],0);
    if(area is not null)Picker("Parameter",["All parameters",..members.Select(Label)],single?Array.IndexOf(members,outputParameter)+1:0,i=>i==0?area:members[i-1],1);
    var path=Path.Combine(session.Root,"output-settings.json");
    JsonObject? Load(string file){try{return Session.Read(file);}catch(InvalidDataException error){Error(error.Message);return null;}}
    if(Load(path) is not {} config)return;
    var inherited=config["*"] as JsonObject??new();var group=single?config[area!] as JsonObject??new():new JsonObject();
    var values=(config[outputParameter] as JsonObject??new()).DeepClone().AsObject();
    double Value(string key,double fallback,bool inherit=true)=>OutputAdjustments.Number(values,key,OutputAdjustments.Number(group,key,inherit?OutputAdjustments.Number(inherited,key,fallback):fallback));
    bool Defined(string key)=>values.ContainsKey(key)||group.ContainsKey(key)||inherited.ContainsKey(key);
    var gaze=area=="Gaze";var minimum=gaze?-1.2:0;var maximum=gaze?1.2:1;var scale=gaze?1:100;var format=gaze?"0.###":"0.##'%'";
    var live=new StackPanel();var status=Text("",13,true);AutomationProperties.SetLiveSetting(status,AutomationLiveSetting.Polite);live.Children.Add(status);
    var readout=Text("",16);AutomationProperties.SetName(readout,"Live adjustment values");
    if(area is null||single)live.Children.Add(readout);Page.Children.Add(Card(live));
    if(area is not null&&!single)Page.Children.Add(new Expander{Header="Live values",Content=readout,Margin=new(0,-8,0,16)});
    var problem=Text("",13);problem.Visibility=Visibility.Collapsed;AutomationProperties.SetLiveSetting(problem,AutomationLiveSetting.Polite);Page.Children.Add(problem);
    var selected=outputParameter;JsonNode? edited=null;
    void Write(){if(Load(path) is {} saved){saved[selected]=edited;CalibrationSettings.WriteJson(path,saved);Refresh();}}
    void Save(string key,double value)
    {
        values[key]=value;
        var valid=Value("inputMax",maximum,false)-Value("inputMin",minimum,false)>=.0001&&Value("outputMin",minimum,false)<=Value("outputMax",maximum,false);
        problem.Text=valid?"":"Input minimum must be below input maximum. Output minimum cannot exceed output maximum. Changes are not saved until the ranges are valid.";
        problem.Visibility=valid?Visibility.Collapsed:Visibility.Visible;
        if(!valid)return;
        edited=values.DeepClone();SaveLater(Write);
    }
    var modeled=names.Any(OutputAdjustments.Modeled);
    var scope=area is null?"These settings apply to every parameter unless an area or parameter changes them.":single?$"Only settings you change here override {area} and All areas.":$"These settings apply to every parameter in {area}. Only settings you change here override All areas.";
    Page.Children.Add(Text(modeled?scope:scope+(single?" QFT+’s camera models don’t track this parameter, so it always comes from the headset.":" QFT+’s camera models don’t track these parameters, so they always come from the headset."),13,true));
    var paired=!single&&names.Any(n=>OutputAdjustments.Partner(n) is not null);
    if(modeled||paired)
    {
        var input=new StackPanel();
        if(modeled)
        {
            var passthrough=new CheckBox{Content=new TextBlock{Text="Headset passthrough"},IsChecked=Value("passthrough",0)>=.5};AutomationProperties.SetName(passthrough,"Headset passthrough");
            var about="Sends the headset’s own tracking instead of QFT+’s camera models. The adjustments below still apply."+(area is null or "Pupils"?" The headset doesn’t measure pupils, so they stay at 50%.":"");
            AutomationProperties.SetHelpText(passthrough,about);passthrough.Click+=(_,_)=>Save("passthrough",passthrough.IsChecked==true?1:0);
            var note=Text(about,13,true);note.Margin=new(0,0,0,paired?20:0);input.Children.Add(passthrough);input.Children.Add(note);
        }
        if(paired)
        {
            var match=new ComboBox{ItemsSource=new[]{"Off","Average both sides","Follow the stronger side"},SelectedIndex=(int)Math.Clamp(Math.Round(Value("match",0)),0,2),Margin=new(0,0,0,8)};
            match.SelectionChanged+=(_,_)=>Save("match",match.SelectedIndex);Field(input,"Match left and right",match);
            input.Children.Add(Text("Moves the two sides of each pair together. Averaging evens out one-sided jitter; following the stronger side keeps blinks together.",13,true));
        }
        Page.Children.Add(Card(input));
    }
    var panel=new StackPanel();Slider? release=null;var syncing=false;
    SliderRow(panel,"Strength",0,10,Value("strength",1),v=>Save("strength",v),displayScale:100);
    SliderRow(panel,"Offset",minimum-maximum,maximum-minimum,Value("offset",0),v=>Save("offset",v),format,scale);
    SliderRow(panel,"Dead zone",0,maximum-minimum,Value("deadzone",0),v=>Save("deadzone",v),format,scale);
    panel.Children.Add(Text("Dead zone ignores movement around neutral, then scales the remaining movement to reach full output. Offset shifts the result after this step.",13,true));
    SliderRow(panel,"Smoothing",0,100,Value("smoothing",0),v=>{Save("smoothing",v);if(release is not null&&!Defined("release")){syncing=true;release.Value=v;syncing=false;}});
    Page.Children.Add(Card(panel));
    var advanced=new StackPanel{Margin=new(0,16,0,0)};
    SliderRow(advanced,"Response curve",.1,5,Value("curve",1),v=>Save("curve",v),"0.##");
    advanced.Children.Add(Text("1 is linear. Below 1 boosts small movements; above 1 makes them gentler.",13,true));
    release=SliderRow(advanced,"Smoothing when relaxing",0,100,Value("release",Value("smoothing",0)),v=>{if(!syncing)Save("release",v);});
    advanced.Children.Add(Text("Used while an expression returns to rest. Higher values let it fade out slowly. Matches Smoothing until you change it.",13,true));
    var invert=new CheckBox{Content="Invert output",IsChecked=Value("invert",0)>=.5};AutomationProperties.SetName(invert,"Invert output");
    invert.Click+=(_,_)=>Save("invert",invert.IsChecked==true?1:0);advanced.Children.Add(invert);
    Page.Children.Add(new Expander{Header="Response",Content=advanced});
    if(area is not null)
    {
        var limits=new StackPanel{Margin=new(0,16,0,0)};
        limits.Children.Add(Text(gaze?"Gaze values use radians. Set the movement you can comfortably reach, then limit the output sent to VRCFaceTracking.":"Set the input range you can comfortably reach. Output limits clamp the adjusted result; equal limits hold a fixed value.",13,true));
        SliderRow(limits,"Input minimum",minimum,maximum,Value("inputMin",minimum,false),v=>Save("inputMin",v),format,scale);
        SliderRow(limits,"Input maximum",minimum,maximum,Value("inputMax",maximum,false),v=>Save("inputMax",v),format,scale);
        var rests=names.Select(Neutral).Distinct().ToArray();
        if(rests.Length==1)
        {
            SliderRow(limits,"Input neutral",minimum,maximum,Value("neutral",rests[0],false),v=>Save("neutral",v),format,scale);
            limits.Children.Add(Text("Neutral is your resting input, clamped to the input range. Pupils default to 50%, open eyelids to 100%, and other parameters to 0%.",13,true));
        }
        SliderRow(limits,"Output minimum",minimum,maximum,Value("outputMin",minimum,false),v=>Save("outputMin",v),format,scale);
        SliderRow(limits,"Output maximum",minimum,maximum,Value("outputMax",maximum,false),v=>Save("outputMax",v),format,scale);
        Page.Children.Add(new Expander{Header="Input and output limits",Content=limits,IsExpanded=single});
    }
    Page.Children.Add(Text("Live values show the QFT+ module’s output. Your avatar must support the selected expression; VRCFaceTracking’s own adjustments can change it further.",13,true));
    string[] own=single?[]:members.Where(m=>config[m] is JsonObject {Count:>0}).ToArray();
    if(own.Length>0)Page.Children.Add(Text((own.Length==1?$"{Label(own[0])} has its own settings, which take precedence here.":$"{own.Length} parameters have their own settings, which take precedence here.")+" Select one and choose Use inherited settings to remove them.",13,true));
    void Refresh()
    {
        try{File.WriteAllBytes(Path.Combine(session.Root,"output-status.lease"),[]);}catch(IOException){}
        JsonObject data;try{data=Session.Read(Path.Combine(session.Root,"output-status.json"));}catch(InvalidDataException){data=new();}
        var age=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()/1000.0-OutputAdjustments.Number(data,"updated",0);
        if(age<0||age>2){status.Text="No live response from the QFT+ module. Open VRCFaceTracking; if it is already running, update the module in Setup and restart VRCFaceTracking.";readout.Text="Waiting for live values";return;}
        status.Text=JsonNode.DeepEquals(data["settings"],Load(path))?"Changes applied by the QFT+ module.":"Changes saved. Waiting for the module to apply them…";
        if(area is null){readout.Text="Select an area to see its live values.";return;}
        var inputs=data["inputs"] as JsonObject??new();var outputs=data["outputs"] as JsonObject??new();
        var missing=names.Where(name=>!inputs.ContainsKey(name)||!outputs.ContainsKey(name)).ToArray();
        readout.Text=string.Join("\n",names.Except(missing).Select(name=>$"{Label(name)}: {(OutputAdjustments.Number(inputs,name,0)*scale).ToString(format)} → {(OutputAdjustments.Number(outputs,name,0)*scale).ToString(format)}")
            .Concat(missing.Length==0?[]:[$"Not provided by the QFT+ module: {string.Join(", ",missing.Select(Label))}. Check its eye and face modules in VRCFaceTracking."]));
        if(area=="Pupils")status.Text+=data["pupilTracking"]?.GetValue<bool>()==true?" Pupil tracking is live. Dilation combines both eyes.":" Pupil tracking is off or has no fresh data: input stays at 50%. Offset and output limits still apply. Dilation combines both eyes.";
    }
    adjustmentRefresh=Refresh;Refresh();
    var reset=Button(outputParameter=="*"?"Reset defaults":"Use inherited settings",()=>{SaveNow();if(Load(path) is {} data){data.Remove(outputParameter);CalibrationSettings.WriteJson(path,data);}Navigate("Adjustments");});Actions.Children.Add(reset);
 }
 void Manual()
 {
    var choices=OutputAdjustments.Areas.Where(area=>Parameters().Any(name=>OutputAdjustments.Area(name)==area)).ToArray();
    var select=new ComboBox{ItemsSource=choices,SelectedItem=manualGroup,Margin=new(0,0,0,12)};
    select.SelectionChanged+=(_,_)=>{var focus=select.IsKeyboardFocusWithin;StopManual();manualGroup=(string)select.SelectedItem;Navigate("Manual");if(focus)Page.Children.OfType<ComboBox>().First().Focus();};Field(Page,"Area",select);
    manualValues=new();var stack=new StackPanel{IsEnabled=false};
    var enabled=new CheckBox{Content="Override live tracking",IsChecked=false};AutomationProperties.SetName(enabled,"Override live tracking");
    enabled.Click+=(_,_)=>{manualTesting=enabled.IsChecked==true;if(manualTesting&&!Processes.Running("VRCFaceTracking")){enabled.IsChecked=manualTesting=false;Error("Open VRCFaceTracking to test movements.");}stack.IsEnabled=manualTesting;PublishManual();};Page.Children.Add(enabled);
    var about=Text("While it’s on, VRCFaceTracking gets these values instead of your face.",13,true);about.Margin=new(0,0,0,16);Page.Children.Add(about);
    var names=Parameters().Where(name=>OutputAdjustments.Area(name)==manualGroup).ToArray();
    foreach(var name in names)
    {
        manualValues[name]=Neutral(name);var gaze=name.StartsWith("Gaze");
        SliderRow(stack,Label(name),gaze?-1.2:0,gaze?1.2:1,Neutral(name),v=>{manualValues[name]=v;PublishManual();},gaze?"0.###":"0.##'%'",gaze?1:100);
    }
    Page.Children.Add(Card(stack));
    Actions.Children.Add(Button("Reset",()=>{StopManual();Navigate("Manual");}));
 }
 void PublishManual()
 {
    var data=new JsonObject{["expires"]=manualTesting?DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()/1000.0+1.5:0,["values"]=manualTesting?manualValues.DeepClone():new JsonObject()};
    CalibrationSettings.WriteJson(Path.Combine(session.Root,"manual-output.json"),data);
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
 RadioButton Choice(string group,string title,string about,bool selected,Action picked)
 {
    var body=new StackPanel();body.Children.Add(new TextBlock{Text=title});body.Children.Add(Text(about,13,true));
    var radio=new RadioButton{GroupName=group,Content=body,IsChecked=selected,Margin=new(0,4,0,4),VerticalContentAlignment=VerticalAlignment.Top};
    AutomationProperties.SetName(radio,title);AutomationProperties.SetHelpText(radio,about);radio.Checked+=(_,_)=>picked();return radio;
 }
 static readonly (string Id,string Title,string About)[] Uses={
    ("face","Face and eye tracking","Sends your expressions and eye movement to VRCFaceTracking."),
    ("hands","Hybrid hands and controllers","Hand tracking and controllers together in Virtual Desktop. Doesn’t use VRCFaceTracking."),
    ("both","Both","Face and eye tracking, plus hybrid hands whenever you connect with Virtual Desktop.")};
 (Border Card,Action Refresh,Action<string> Select) UseOptions(Action<string> changed)
 {
    var panel=new StackPanel();var heading=Text("What do you want to use?",16);heading.FontWeight=FontWeights.SemiBold;AutomationProperties.SetHeadingLevel(heading,AutomationHeadingLevel.Level2);panel.Children.Add(heading);
    var steamLink=SteamVr.IsSteamLink;var current=session.Use;var radios=new List<(string Id,RadioButton Radio)>();
    var note=Text("Hybrid hands pause Virtual Desktop body tracking while they’re on.",13,true);
    var status=Text("",13,true);AutomationProperties.SetLiveSetting(status,AutomationLiveSetting.Polite);
    foreach(var (id,title,about) in Uses)
    {
        var radio=Choice("use",title,steamLink&&id=="hands"?"Requires Virtual Desktop. Steam Link uses its own hand tracking.":about,current==id,()=>
        {
            session.Save("use",id);
            note.Visibility=id=="face"?Visibility.Collapsed:Visibility.Visible;changed(id);
        });
        radios.Add((id,radio));panel.Children.Add(radio);
    }
    note.Visibility=current=="face"?Visibility.Collapsed:Visibility.Visible;panel.Children.Add(note);panel.Children.Add(status);
    void Refresh()
    {
        foreach(var (id,radio) in radios)radio.IsEnabled=!busy&&!session.Running&&!(steamLink&&id=="hands");
        status.Text=busy?"Wait for the current step to finish to change this.":session.Running?"Stop tracking to change this.":"";
        status.Visibility=status.Text.Length>0?Visibility.Visible:Visibility.Collapsed;
    }
    Refresh();
    return (Card(panel),Refresh,id=>radios.Single(r=>r.Id==id).Radio.IsChecked=true);
 }
 Action? setupRefresh;
 int setupStep=-1;
 bool setupDone, searching, moduleMissing;
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
    TextBlock Heading(string text){var heading=Text(text,16);heading.FontWeight=FontWeights.SemiBold;AutomationProperties.SetHeadingLevel(heading,AutomationHeadingLevel.Level2);return heading;}
    TextBlock Symbol(string glyph,string brush){var symbol=new TextBlock{Text=glyph,FontFamily=new("Segoe UI Symbol"),Width=28,Margin=new(0,1,0,0)};symbol.SetResourceReference(TextBlock.ForegroundProperty,brush);return symbol;}
    UIElement Row(TextBlock symbol,params TextBlock[] lines){var row=new DockPanel{Margin=new(0,6,0,6)};DockPanel.SetDock(symbol,Dock.Left);row.Children.Add(symbol);var text=new StackPanel();foreach(var line in lines){line.Margin=new(0,0,0,2);text.Children.Add(line);}row.Children.Add(text);return row;}
    var use=session.Use;
    var (useCard,useRefresh,selectUse)=UseOptions(id=>{use=id;UseChanged(id);setupRefresh?.Invoke();});Page.Children.Add(useCard);

    var found=new StackPanel();var caption=Text("",13,true);var activity=new ProgressBar{IsIndeterminate=true,Height=3,Margin=new(0,0,0,8)};
    var search=new Button{Content="Search again"};search.Click+=(_,_)=>_=Search();AutomationProperties.SetHelpText(search,"Looks for headsets over USB and Wi-Fi.");
    var top=new DockPanel{Margin=new(0,0,0,4)};DockPanel.SetDock(search,Dock.Right);top.Children.Add(search);var title=Heading("Headset");title.VerticalAlignment=VerticalAlignment.Center;top.Children.Add(title);
    var headsetCard=new StackPanel();headsetCard.Children.Add(top);headsetCard.Children.Add(caption);headsetCard.Children.Add(activity);headsetCard.Children.Add(found);Page.Children.Add(Card(headsetCard));
    void ShowHeadsets()
    {
        found.Children.Clear();activity.Visibility=searching?Visibility.Visible:Visibility.Collapsed;search.IsEnabled=!searching;search.Content=searching?"Searching…":"Search again";
        caption.Text=searching?"Searching USB and Wi-Fi…":headsetsChecked is {} at?$"Last checked at {at:t}.":"Not checked yet.";
        var preferred=session.Config["headsetSerial"]?.GetValue<string>()??"";
        found.Children.Add(Choice("headset","Any Quest Pro","Connects to the first Quest Pro it finds.",preferred.Length==0,()=>session.Save("headsetSerial","")));
        foreach(var group in headsets.Where(q=>q.State=="device").GroupBy(q=>q.Serial))
        {
            var ways=string.Join(" and ",group.OrderBy(q=>q.Wireless?0:1).Select(q=>q.Wireless?$"Wi-Fi ({q.Target})":"USB"));var serial=group.Key;
            found.Children.Add(Choice("headset","Quest Pro · Ready",$"Connected by {ways}. Serial {serial}.",preferred==serial,()=>session.Save("headsetSerial",serial)));
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
        connection.Children.Add(Choice("connection",name,about,current==id,()=>session.Save("connectionMode",id)));
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
            var (from,name,about)=SetupSteps[i];var now=i==setupStep&&!setupDone;var failed=now&&setupProblem.Length>0;
            var skipped=use=="hands"&&from is 55 or 70||from==55&&!session.IndependentGaze;
            if(use=="hands"&&from is 55 or 70)about="Skipped. Face tracking isn’t selected.";
            else if(from==55&&!session.IndependentGaze)about="Skipped while independent eye gaze is off. Uses standard eye tracking.";
            if(from==60&&use=="hands")about="Makes sure Steam, SteamVR, and Virtual Desktop are installed.";
            var (glyph,brush,word)=failed?("⚠","Ink","Couldn’t finish"):skipped?("–","Muted","Skipped"):setupDone||i<setupStep?("✓","Accent","Done"):now&&busy?("●","Accent","In progress"):now?("○","Ink","Paused"):("○","Muted","Not started");
            var heading=new TextBlock{Text=name,FontWeight=now&&!skipped?FontWeights.SemiBold:FontWeights.Normal,Margin=new(0)};
            var detail=Text(failed?setupProblem:now&&busy&&!skipped&&session.Detail.Length>0?session.Detail:about,13,!(now&&busy)&&!failed);
            var row=Row(Symbol(glyph,brush),heading,detail);AutomationProperties.SetName(row,$"{name}, {word}");list.Children.Add(row);
            if(failed&&moduleMissing)
            {
                var fixes=new WrapPanel{Margin=new(28,0,0,6)};
                fixes.Children.Add(Button("Install module",()=>{session.Save("installModule",true);_=RunSetup();},true));
                if(!SteamVr.IsSteamLink){var hands=Button("Use hand tracking only",()=>{selectUse("hands");_=RunSetup();});hands.Margin=new(8,0,0,0);fixes.Children.Add(hands);}
                list.Children.Add(fixes);
            }
            else if(failed&&session.HelpTarget is {} help){var link=Button(session.HelpCaption,()=>Open(help));link.Margin=new(28,0,0,6);list.Children.Add(link);}
        }
        useRefresh();
        bar.Visibility=busy?Visibility.Visible:Visibility.Collapsed;bar.Value=session.Progress;
        status.Text=busy&&setupStep>=0&&setupStep<SetupSteps.Length?$"Step {setupStep+1} of {SetupSteps.Length}: {SetupSteps[setupStep].Title}":setupDone?"Setup complete. Choose Start to begin tracking.":setupProblem.Length>0?"Setup couldn’t finish. The step below shows what to do.":setupStep>=0?"Setup paused. Your progress is saved.":"Run setup once per headset, or again after an update.";
        go.Content=busy?"Cancel":setupDone?"Set up again":setupProblem.Length>0?"Try again":setupStep>=0?"Continue":"Set up";
        if(moduleMissing&&!busy)go.ClearValue(StyleProperty);else go.SetResourceReference(StyleProperty,"PrimaryButton");
    }
    async Task RunSetup()
    {
        if(busy)return;
        busy=true;setupDone=false;setupProblem="";moduleMissing=false;setupStep=0;StartButton.Content="Cancel";ClearNotice();ShowSteps();
        try{await session.Prepare();}
        catch(OperationCanceledException){}
        catch(Exception error){setupProblem=error.Message;moduleMissing=error is ModuleNotInstalledException;Error(error.Message);}
        finally{busy=false;StartButton.Content=session.Running?"Stop":"Start";if(page=="Setup"){ShowSteps();_=Search();}}
    }
    go.Click+=async(_,_)=>{if(busy)session.CancelSetup();else await RunSetup();};
    setupRefresh=()=>{SetupProgress();ShowSteps();};

    var tips=new StackPanel();
    foreach(var tip in new[]{"Use a USB cable that carries data. Charge-only cables don’t work.","Turn on Developer Mode for your headset in the Meta Horizon app.","In the headset, allow USB debugging and choose “Always allow from this computer.”","In Magisk → Superuser, allow Shell.","For Wi-Fi, keep this PC and the headset on the same network.","If a headset shows “Not responding,” restart it and keep USB connected."})
        tips.Children.Add(Row(Symbol("•","Muted"),Text(tip,14)));
    var log=Path.Combine(session.Root,"setup.log");var openLog=Button("Open setup log",()=>Open(log));openLog.Margin=new(0,8,0,0);openLog.IsEnabled=File.Exists(log);tips.Children.Add(openLog);
    Page.Children.Add(new Expander{Header="Can’t find your headset?",Content=tips,Margin=new(0,0,0,8)});

    ShowSteps();ShowHeadsets();if(!busy)_=Search();
 }
 void StartupOptions(List<UIElement> faceOnly)
 {
    var stack=new StackPanel();var heading=Text("Startup",16);heading.FontWeight=FontWeights.SemiBold;AutomationProperties.SetHeadingLevel(heading,AutomationHeadingLevel.Level2);stack.Children.Add(heading);
    foreach(var (key,label) in new[]{("setupOnStart","Set up the headset on Start"),("installModule","Install the VRCFaceTracking module"),("startWithVrcft","Start with VRCFaceTracking"),("openVrApps","Open VR apps on Start")})
    {
        var check=new CheckBox{Content=new TextBlock{Text=label},IsChecked=session.Config[key]?.GetValue<bool>()??true};AutomationProperties.SetName(check,label);check.Click+=(_,_)=>session.Save(key,check.IsChecked==true);
        if(key is "setupOnStart" or "openVrApps"){stack.Children.Add(check);continue;}
        var option=new StackPanel();option.Children.Add(check);
        if(key=="installModule"){const string about="Turn off only if you install the module yourself.";option.Children.Add(Text(about,13,true));AutomationProperties.SetHelpText(check,about);}
        faceOnly.Add(option);stack.Children.Add(option);
    }
    Page.Children.Add(Card(stack));
 }
 void ProcessingOptions()
 {
    var hardware=new StackPanel{Margin=new(0,16,0,0)};
    var devices=new[]{"auto","directml","cpu"};var compute=new ComboBox{ItemsSource=new[]{"Automatic","Graphics card (DirectML)","Processor (CPU)"},SelectedIndex=Math.Max(0,Array.IndexOf(devices,session.Config["inferenceDevice"]?.GetValue<string>()??"auto")),Margin=new(0,0,0,20)};
    var graphics=new StackPanel{Visibility=compute.SelectedIndex==2?Visibility.Collapsed:Visibility.Visible};
    compute.SelectionChanged+=(_,_)=>{graphics.Visibility=compute.SelectedIndex==2?Visibility.Collapsed:Visibility.Visible;session.Save("inferenceDevice",devices[compute.SelectedIndex]);};Field(hardware,"Tracking processor",compute);
    var adapters=GraphicsAdapters.List();
    if(adapters.Count>1)
    {
        var gpu=new ComboBox{ItemsSource=adapters.Select(a=>a.Name).ToArray(),SelectedIndex=Math.Max(0,adapters.FindIndex(a=>a.Index==(session.Config["gpuIndex"]?.GetValue<int>()??0))),Margin=new(0,0,0,8)};
        gpu.SelectionChanged+=(_,_)=>session.Save("gpuIndex",adapters[gpu.SelectedIndex].Index);Field(graphics,"Graphics card",gpu);
    }
    hardware.Children.Add(graphics);hardware.Children.Add(Text("Takes effect the next time tracking starts. To leave more performance for VR, choose Graphics card, then a different card from the one running your game.",13,true));Page.Children.Add(new Expander{Header="Processing",Content=hardware});
 }
}
