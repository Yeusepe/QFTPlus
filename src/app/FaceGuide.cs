using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace QFTPlus;

public sealed class FaceGuide : Control
{
    static readonly double[] NeutralMouth = [32, 170, 1 / 3.0, 14.67, 14.67],
        SmileMouth = [38, 168, 1 / 3.0, 26.67, 26.67], OpenMouth = [22, 176, 1, -20, 20],
        FlatMouth = [34, 176, 1 / 3.0, 0, 0], PursedMouth = [12, 176, 1 / 3.0, 2, 2];
    static readonly double[] Neutral = Pose(new(), "");
    static readonly DependencyProperty PhaseProperty = DependencyProperty.Register("Phase", typeof(double), typeof(FaceGuide), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));
    static readonly DependencyProperty PulseProperty = DependencyProperty.Register("Pulse", typeof(double), typeof(FaceGuide), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(nameof(Accent), typeof(Brush), typeof(FaceGuide), new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    double[] from = Neutral, target = Neutral;
    bool throughNeutral, pupils, shown;
    int index = -1, count;

    static FaceGuide() => ForegroundProperty.OverrideMetadata(typeof(FaceGuide), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public FaceGuide() => Unloaded += (_, _) =>
    {
        BeginAnimation(PhaseProperty, null);
        BeginAnimation(PulseProperty, null);
        shown = false;
    };

    public void Show(JsonObject values, string prompt, string kind, bool animate, int poseIndex = -1, int captured = 0)
    {
        var next = Pose(values, prompt.ToLowerInvariant());
        animate &= SystemParameters.ClientAreaAnimation && kind != "pupils";
        if (animate && poseIndex == index && captured > count)
            BeginAnimation(PulseProperty, new DoubleAnimation(1, 0, TimeSpan.FromSeconds(.6)));
        var changed = !target.SequenceEqual(next) || poseIndex != index || pupils != (kind == "pupils");
        pupils = kind == "pupils";
        count = captured;
        if (changed || !animate)
        {
            from = CurrentPose();
            throughNeutral = index >= 0 && poseIndex != index || from[5] > .05 && next[5] > .05 && Math.Abs(from[6] - next[6]) > 90;
            if (from[5] < .05) from[6] = next[6];
            if (next[5] < .05) next[6] = from[6];
            target = next;
            BeginAnimation(PhaseProperty, animate && shown
                ? new DoubleAnimation(0, 1, TimeSpan.FromSeconds(throughNeutral ? .7 : .4)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } }
                : null);
            InvalidateVisual();
        }
        index = poseIndex;
        shown = true;
    }

    double[] CurrentPose()
    {
        var phase = (double)GetValue(PhaseProperty);
        var a = throughNeutral && phase >= .5 ? Neutral : from;
        var b = throughNeutral && phase < .5 ? Neutral : target;
        var t = throughNeutral ? phase < .5 ? phase * 2 : (phase - .5) * 2 : phase;
        var pose = a.Zip(b, (x, y) => x + (y - x) * t).ToArray();
        if (throughNeutral) pose[6] = phase < .5 ? from[6] : target[6];
        return pose;
    }

    static double[] Pose(JsonObject targets, string prompt)
    {
        double Value(string key) => double.TryParse(targets[key]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
        var faceGoal = new double[29];
        double right = Value("CheekPuffRight"), left = Value("CheekPuffLeft"), tongue = 0, angle = 0, length = 0;
        var mouth = NeutralMouth;
        if (right > 0 || left > 0 || Value("CheekSuckRight") > 0 || Value("CheekSuckLeft") > 0)
            mouth = PursedMouth;
        else if (Value("visibility") > 0)
        {
            double h = Value("horizontal"), v = Value("vertical");
            mouth = FlatMouth;
            tongue = 1;
            angle = v > 0 ? 180 + h * 35 : -h * 35;
            length = (12 + Value("extension") * 16) * (v > 0 ? .7 : 1 - Math.Min(v, 0) * .1);
        }
        else if (prompt.Contains("open"))
            mouth = OpenMouth;
        else if (Value("MouthSmile") > 0 || prompt.Contains("smil"))
            mouth = SmileMouth;
        else if (Value("LipSuck") > 0)
            mouth = FlatMouth;
        var open = Math.Min(1, Value("MouthOpen"));
        if (open > 0)
            mouth = mouth.Zip(OpenMouth, (a, b) => a + open * (b - a)).ToArray();
        right += .35 * Value("TongueBulgeRight") - .5 * Value("CheekSuckRight");
        left += .35 * Value("TongueBulgeLeft") - .5 * Value("CheekSuckLeft");
        double Both(string name) => (Value(name + "Left") + Value(name + "Right")) / 2;
        var kiss = Math.Min(Both("LipPuckerUpper"), Both("LipPuckerLower"));
        var pout = Both("LipPuckerLower") - kiss;
        var m = (double[])mouth.Clone();
        void Shape(double amount, double width, double upper, double lower)
        {
            m[0] += amount * width;
            m[3] += amount * upper;
            m[4] += amount * lower;
        }

        Shape(Both("MouthCornerPull"), 6, -4, 12);
        Shape(Both("MouthCornerSlant"), 2, 4, 4);
        Shape(Both("MouthUpperDeepen"), -2, -10, 0);
        Shape(kiss, -18, -20.67, -6.67);
        Shape(pout, -4, -22, -16);
        Shape(Both("LipSuckCorner"), -6, -11, -11);
        Shape(Math.Max(Value("JawClench"), Value("JawMandibleRaise")), 2, -8, -8);
        Shape(Value("JawBackward"), -4, -3, -3);
        m.CopyTo(faceGoal, 0);
        faceGoal[5] = tongue;
        faceGoal[6] = angle;
        faceGoal[7] = length;
        faceGoal[8] = right;
        faceGoal[9] = left;
        var mouthMoves = new[]
        {
            "MouthCornerPull",
            "MouthCornerSlant",
            "MouthUpperDeepen",
            "LipPuckerUpper",
            "LipPuckerLower",
            "LipSuckCorner"
        }.Sum(Both) + new[]
        {
            "JawClench",
            "JawMandibleRaise",
            "JawBackward",
            "MouthUpperLeft",
            "MouthUpperRight",
            "MouthLowerLeft",
            "MouthLowerRight"
        }.Sum(Value);
        var lift = 3 * Both("MouthCornerSlant") - 2 * pout - 2 * Value("JawBackward");
        new[]
        {
            lift,
            6 * (Value("MouthUpperRight") - Value("MouthUpperLeft")),
            6 * (Value("MouthLowerRight") - Value("MouthLowerLeft")),
            Math.Min(1, mouthMoves),
            Both("MouthUpperDeepen"),
            8 * Value("BrowLowererLeft"),
            8 * Value("BrowLowererRight"),
            8 * Value("BrowPinchLeft"),
            8 * Value("BrowPinchRight"),
            10 * Value("BrowInnerUpLeft"),
            10 * Value("BrowInnerUpRight"),
            10 * Value("BrowOuterUpLeft"),
            10 * Value("BrowOuterUpRight"),
            Value("NasalDilationLeft") - Value("NasalConstrictLeft"),
            Value("NasalDilationRight") - Value("NasalConstrictRight"),
            Value("JawClench"),
            Value("JawBackward"),
            Math.Min(1, Math.Max(Value("EyeClosed"), .6 * Value("EyeSquint"))),
            Value("LookUp")
        }.CopyTo(faceGoal, 10);
        return faceGoal;
    }

    protected override void OnRender(DrawingContext drawing)
    {
        var scale = Math.Min(ActualWidth / 360, ActualHeight / 250);
        var pulse = (double)GetValue(PulseProperty);
        var pop = pulse > .5 ? 1 + .04 * Math.Sin(2 * Math.PI * (1 - pulse)) : 1;
        var matrix = Matrix.Identity;
        matrix.ScaleAt(pop, pop, 180, 130);
        matrix.Scale(scale, scale);
        matrix.Translate((ActualWidth - 360 * scale) / 2, 0);
        drawing.PushTransform(new MatrixTransform(matrix));
        var ink = Foreground;
        var accent = Accent;
        void Draw(string data, Brush? fill, Pen? pen, Transform? at, double opacity)
        {
            drawing.PushOpacity(opacity);
            drawing.PushTransform(at ?? Transform.Identity);
            drawing.DrawGeometry(fill, pen, Geometry.Parse(data));
            drawing.Pop();
            drawing.Pop();
        }
        void Line(string data, Brush brush, Transform? at = null, double opacity = 1, double width = 8) =>
            Draw(data, null, new Pen(brush, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, at, opacity);
        void Fill(string data, double opacity, Transform? at = null) => Draw(data, accent, null, at, opacity);
        if (pupils)
        {
            Line("M 160,125 A 20,20 0 1 1 200,125 A 20,20 0 1 1 160,125 M 180,125 L 180,125", ink);
            drawing.Pop();
            return;
        }
        var p = CurrentPose();
        string F(FormattableString value) => FormattableString.Invariant(value);
        var chin = 230 - 4 * p[26];
        var head = F($"M 180,22 C 234,22 258,62 258,112 C {258 + p[8] * 30},190 220,{chin} 180,{chin} C 140,{chin} {102 - p[9] * 30},190 102,112 C 102,62 126,22 180,22 Z");
        Line(head, ink);
        if (pulse > 0)
            Line(head, accent, null, pulse);
        var closed = Math.Clamp(p[27], 0, 1);
        var look = 6 * p[28];
        foreach (var x in new[]
{ 142.0, 218.0 })
        {
            if (closed > .85)
                Line(F($"M {x - 9},107 Q {x},112 {x + 9},107"), accent);
            else
                Line(F($"M {x},{107 - 9 * (1 - closed) - look} V {107 + 9 * (1 - closed) - look}"), closed > .1 || Math.Abs(look) > .5 ? accent : ink);
        }

        var flare = (p[23] + p[24]) / 2;
        if (Math.Abs(flare) > .02)
        {
            Line("M 180,98 V 124", ink);
            Line(F($"M {171 - 4 * flare},135 Q {173 - 3 * flare},127 180,129 Q {187 + 3 * flare},127 {189 + 4 * flare},135"), Math.Abs(flare) > .05 ? accent : ink, null, 1, 6);
        }
        else
            Line("M 180,98 V 124 Q 180,134 170,134", ink);
        foreach (var (side, down, pinch, inner, outer) in new[]
{ (-1.0, p[15], p[17], p[19], p[21]), (1.0, p[16], p[18], p[20], p[22]) })
        {
            double ix = 180 + side * (20 - pinch), ox = 180 + side * (56 - pinch * .3), iy = 86 + down - inner, oy = 84 + down - outer;
            Line(F($"M {ox},{oy} Q {(ix + ox) / 2},{Math.Min(iy, oy) - 5} {ix},{iy}"), down + pinch + inner + outer > .1 ? accent : ink);
        }

        if (p[14] > .01)
            Line("M 173,109 L 187,109 M 174,116 L 186,116", accent, null, Math.Min(1, p[14]), 3);
        foreach (var (puff, x, outward) in new[]
{ (p[9], 136.0, -1.0), (p[8], 224.0, 1.0) })
            if (puff < -.02)
                Line(F($"M {x + outward * 8},148 Q {x - outward * (6 + 24 * -puff)},168 {x + outward * 8},188"), accent, null, Math.Min(1, -puff * 3), 5);
            else if (puff > .01)
            {
                var rx = 8 + 22 * puff;
                var ry = 6 + 16 * puff;
                var cx = x + outward * 6 * puff;
                Fill(F($"M {cx - rx},168 A {rx},{ry} 0 1 0 {cx + rx},168 A {rx},{ry} 0 1 0 {cx - rx},168 Z"), .15 + .3 * Math.Min(puff, 1));
            }

        if (p[5] > .01)
        {
            var at = new TransformGroup();
            at.Children.Add(new ScaleTransform(p[5], p[5], 180, 176));
            at.Children.Add(new RotateTransform(p[6], 180, 176));
            var lean = Math.Clamp(p[6] - 180 * Math.Round(p[6] / 180), -60, 60);
            var trim = 15 * Math.Tan(lean * Math.PI / 180);
            var tongue = F($"M 165,{176 + trim} V {176 + p[7]} A 15,15 0 0 0 195,{176 + p[7]} V {176 - trim}");
            Fill(tongue + " Z", .2, at);
            Line(tongue, accent, at);
        }

        var shift = (p[11] + p[12]) / 2;
        double w = p[0], y = p[1], k = p[2] * p[0], leftX = 180 - w + shift, rightX = 180 + w + shift, corner = y - p[10];
        var bite = p[25];
        if (bite > .05)
        {
            double h = 7 * bite, third = (rightX - leftX) / 3;
            Line(F($"M {leftX},{y - h} H {rightX} V {y + h} H {leftX} Z M {leftX},{y} H {rightX} M {leftX + third},{y - h} V {y + h} M {rightX - third},{y - h} V {y + h}"), accent, null, 1, 5);
        }
        else
            Line(F($"M {leftX},{corner} C {180 - k + p[11]},{y + p[3]} {180 + k + p[11]},{y + p[3]} {rightX},{corner} C {180 + k + p[12]},{y + p[4]} {180 - k + p[12]},{y + p[4]} {leftX},{corner} Z"), p[13] > .1 ? accent : ink);
        drawing.Pop();
    }
}
