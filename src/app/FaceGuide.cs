using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QFTPlus;

public sealed class FaceGuide : Control
{
    Session? session;
    CancellationTokenSource? rendering;
    BitmapSource? image;
    float[] pose = new float[76];
    bool pupils, loading;
    public FaceGuide()
    {
        AutomationProperties.SetName(this, Loc.Get("AvatarPreview"));
        Loaded += (_, _) => Start();
        Unloaded += (_, _) => Stop();
        IsVisibleChanged += (_, _) => { if (IsVisible) Start(); else Stop(); };
    }

    internal void Configure(Session? value) { if (!ReferenceEquals(session, value)) Stop(); session = value; Start(); }

    public void Show(JsonObject values, string condition, string kind, bool animate, int poseIndex = -1, int captured = 0)
    {
        pose = Pose(values, condition, animate);
        pupils = kind == "pupils";
        if (pupils) Stop(); else Start();
        InvalidateVisual();
    }

    void Stop() { rendering?.Cancel(); image = null; }

    async void Start()
    {
        if (!IsLoaded || !IsVisible || pupils || session is not { } active || rendering is not null) return;
        using var cancel = new CancellationTokenSource();
        rendering = cancel;
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                loading = true; InvalidateVisual();
                try { await AvatarPreview.Run(active, () => pose, frame => { image = frame; InvalidateVisual(); }, cancel.Token); }
                catch (Exception error) when (error is IOException or OperationCanceledException or System.ComponentModel.Win32Exception or System.Net.Sockets.SocketException)
                {
                    if (!cancel.IsCancellationRequested) Session.Log(Path.Combine(active.Root, "studio.log"), "Avatar preview: " + error.Message);
                }
                image = null; loading = false; InvalidateVisual();
                await Task.Delay(5000, cancel.Token);
            }
        }
        catch (OperationCanceledException) { }
        finally { rendering = null; if (IsLoaded && IsVisible && !pupils && session is not null && cancel.IsCancellationRequested) Start(); }
    }

    internal static float[] Pose(JsonObject values, string condition, bool animate)
    {
        float V(string name) => float.TryParse(values[name]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && float.IsFinite(v) ? Math.Clamp(v, -1, 1) : 0;
        var weights = new float[76];
        void Set(int index, params string[] keys) => weights[index] = Math.Clamp(keys.Max(V), 0, 1);
        for (var side = 0; side < 2; side++)
        {
            var suffix = side == 0 ? "Right" : "Left";
            foreach (var (index, name) in new (int, string)[] { (0,"BrowLowerer"), (2,"CheekPuff"), (4,"CheekSquint"), (6,"CheekSuck"),
                (10,"MouthDimple"), (22,"BrowInnerUp"), (30,"MouthFrown"), (32,"MouthCornerPull"), (38,"MouthPress"),
                (40,"LipPuckerUpper"), (42,"MouthStretch"), (48,"MouthTightener"), (51,"MouthLowerDown"), (55,"NoseSneer"), (57,"BrowOuterUp"), (59,"EyeWide"), (61,"MouthUpperUp") })
                Set(index + side, name + suffix);
            Set(side, "BrowLowerer" + suffix, "BrowPinch" + suffix);
            Set(12 + side, "EyeClosed", "EyeClosed" + suffix);
            Set(28 + side, "EyeSquint", "EyeSquint" + suffix);
            Set(32 + side, "MouthSmile", "MouthCornerPull" + suffix, "MouthCornerSlant" + suffix);
            Set(38 + side, "MouthPress" + suffix, "JawClench");
            Set(48 + side, "MouthTightener" + suffix, "JawClench");
            Set(55 + side, "NoseSneer" + suffix, "NasalDilation" + suffix, "NasalConstrict" + suffix);
            Set(61 + side, "MouthUpperUp" + suffix, "MouthUpperDeepen" + suffix);
            weights[2 + side] = Math.Max(weights[2 + side], Math.Max(0, .35f * V("TongueBulge" + suffix)));
            Set(40 + side, "LipPuckerUpper" + suffix, "LipPuckerLower" + suffix);
            Set(44 + side * 2, "LipSuck", "LipSuckLower" + suffix, "LipSuckCorner" + suffix);
            Set(45 + side * 2, "LipSuck", "LipSuckUpper" + suffix, "LipSuckCorner" + suffix);
            Set(34 + side * 2, "LipFunnelLower" + suffix);
            Set(35 + side * 2, "LipFunnelUpper" + suffix);
            weights[(V("LookUp") < 0 ? 14 : 20) + side] = Math.Abs(V("LookUp"));
        }
        Set(24, "JawOpen", "MouthOpen");
        if (condition is "anchor:jaw_open" or "target:JawOpen") weights[24] = 1;
        weights[25] = weights[53] = Math.Max(V("MouthUpperRight"), V("MouthLowerRight"));
        weights[26] = weights[54] = Math.Max(V("MouthUpperLeft"), V("MouthLowerLeft"));
        weights[27] = Math.Max(0, -V("JawBackward"));
        Set(8, "JawMandibleRaise");
        weights[72] = Math.Max(0, V("visibility") * V("extension"));
        weights[73] = V("horizontal"); weights[74] = V("vertical");
        if (weights[72] > 0) weights[24] = Math.Max(weights[24], .7f);
        weights[75] = animate && SystemParameters.ClientAreaAnimation && V("LookUp") == 0 ? 1 : 0;
        return weights;
    }

    protected override void OnRender(DrawingContext drawing)
    {
        if (pupils)
        {
            drawing.DrawEllipse(null, new Pen(Foreground, 5), new Point(ActualWidth / 2, ActualHeight / 2), 20, 20);
            drawing.DrawEllipse(Foreground, null, new Point(ActualWidth / 2, ActualHeight / 2), 3, 3);
        }
        else if (image is not null)
        {
            var scale = Math.Min(ActualWidth / AvatarPreview.Width, ActualHeight / AvatarPreview.Height);
            var width = AvatarPreview.Width * scale; var height = AvatarPreview.Height * scale;
            drawing.DrawImage(image, new Rect((ActualWidth - width) / 2, (ActualHeight - height) / 2, width, height));
        }
        else
        {
            var text = new FormattedText(Loc.Get(loading ? "AvatarPreviewLoading" : "AvatarPreviewUnavailable"), CultureInfo.CurrentUICulture,
                FlowDirection, new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), 14, Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip)
                { MaxTextWidth = Math.Max(1, ActualWidth - 32), TextAlignment = TextAlignment.Center };
            drawing.DrawText(text, new Point(16, Math.Max(0, (ActualHeight - text.Height) / 2)));
        }
    }
}
