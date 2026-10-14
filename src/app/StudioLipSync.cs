using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Ui = Wpf.Ui.Controls;

namespace QFTPlus;

public partial class StudioWindow
{
    static readonly (string Code, string Sounds, string Words)[] Consonants =
    [
        ("PP", Loc.Get("VisemeSoundsPP"), Loc.Get("VisemeWordsPP")), ("FF", Loc.Get("VisemeSoundsFF"), Loc.Get("VisemeWordsFF")),
        ("TH", Loc.Get("VisemeSoundsTH"), Loc.Get("VisemeWordsTH")), ("DD", Loc.Get("VisemeSoundsDD"), Loc.Get("VisemeWordsDD")),
        ("kk", Loc.Get("VisemeSoundskk"), Loc.Get("VisemeWordskk")), ("CH", Loc.Get("VisemeSoundsCH"), Loc.Get("VisemeWordsCH")),
        ("SS", Loc.Get("VisemeSoundsSS"), Loc.Get("VisemeWordsSS")), ("nn", Loc.Get("VisemeSoundsnn"), Loc.Get("VisemeWordsnn")),
        ("RR", Loc.Get("VisemeSoundsRR"), Loc.Get("VisemeWordsRR")),
    ], Vowels =
    [
        ("aa", Loc.Get("VisemeSoundsaa"), Loc.Get("VisemeWordsaa")), ("E", Loc.Get("VisemeSoundsE"), Loc.Get("VisemeWordsE")),
        ("ih", Loc.Get("VisemeSoundsih"), Loc.Get("VisemeWordsih")), ("oh", Loc.Get("VisemeSoundsoh"), Loc.Get("VisemeWordsoh")),
        ("ou", Loc.Get("VisemeSoundsou"), Loc.Get("VisemeWordsou")),
    ];
    DispatcherTimer? lipSyncMeters;

    string LipSyncPath => Path.Combine(session.Root, "lipsync-settings.json");

    void LipSyncPage()
    {
        var s = LipSync.Load(LipSyncPath);
        void Changed()
        {
            lipSync.Apply(s);
            SaveLater(() => LipSync.Save(LipSyncPath, s));
        }
        var live = new List<Action>();
        var groups = new List<Panel>();
        StackPanel Group(string? title = null)
        {
            if (title is not null) Page.Children.Add(Section(title));
            var list = new StackPanel();
            Page.Children.Add(list);
            groups.Add(list);
            return list;
        }
        void Rounded() => groups.ForEach(RoundEnds);
        TextBlock Footer(string text = "")
        {
            var footer = Muted(text, live: true);
            footer.Margin = new(24, 8, 24, 0);
            Page.Children.Add(footer);
            return footer;
        }
        Border Wrap(UIElement content, Panel list)
        {
            var row = new Border { Child = content, Margin = new(0, 0, 0, 2), Padding = new(24, 12, 20, 12) };
            row.SetResourceReference(Border.BackgroundProperty, "Surface");
            list.Children.Add(row);
            return row;
        }
        Border Row(string title, string? about, FrameworkElement control, Panel list)
        {
            var label = Text(title);
            label.Margin = new(0);
            var text = Stack(label);
            text.VerticalAlignment = VerticalAlignment.Center;
            if (about is not null)
            {
                var help = Muted(about);
                help.Margin = new(0, 2, 0, 0);
                text.Children.Add(help);
            }
            (control.VerticalAlignment, control.Margin) = (VerticalAlignment.Center, new(16, 0, 0, 0));
            DockPanel.SetDock(control, Dock.Right);
            AutomationProperties.SetName(control, title);
            var dock = new DockPanel();
            dock.Children.Add(control);
            dock.Children.Add(text);
            return Wrap(dock, list);
        }
        ComboBox Menu(string[] items, int selected, Action<int> picked)
        {
            var box = new ComboBox { ItemsSource = items, SelectedIndex = selected, Width = 260 };
            box.SelectionChanged += (_, _) =>
            {
                if (box.SelectedIndex < 0) return;
                picked(box.SelectedIndex);
                Changed();
            };
            return box;
        }
        TextBox NameBox(string value, Action<string> set)
        {
            var box = new TextBox { Text = value, Width = 260 };
            box.TextChanged += (_, _) =>
            {
                set(box.Text.Trim());
                Changed();
            };
            return box;
        }
        string[] engines = [Loc.Get("EngineOriginal"), Loc.Get("EngineEnhanced"), Loc.Get("EngineLaughter")];
        string AvatarStatus() => lipSync.Matched switch
        {
            null => Loc.Get("AvatarParametersUnknown"),
            0 => Loc.Get("AvatarParametersNone"),
            var count => Loc.Format("AvatarParametersFound", count),
        };

        switch (page)
        {
            case "LipSync/MouthShapes": MouthShapes(); break;
            case "LipSync/Sound": Sound(); break;
            case "LipSync/VRChat": VRChatOutput(); break;
            default: Overview(); break;
        }
        Rounded();

        void Overview()
        {
            var main = Group();
            var on = new CheckBox { Content = Loc.Get("LipSync"), IsChecked = s.Enabled, Margin = new(0) };
            AutomationProperties.SetHelpText(on, Loc.Get("LipSyncAbout"));
            Wrap(on, main);
            var devices = LipSync.Devices().Prepend((Id: LipSync.Headset, Name: Loc.Get("HeadsetMicrophone"))).ToArray();
            var selected = Array.FindIndex(devices, d => d.Id == s.Device);
            var microphone = new ComboBox
            {
                ItemsSource = new[] { Loc.Format("WindowsDefault", LipSync.DefaultDevice()) }.Concat(devices.Select(d => d.Name)).ToList(),
                SelectedIndex = s.Device.Length == 0 ? 0 : selected < 0 ? -1 : selected + 1, Width = 300,
            };
            Row(Loc.Get("Microphone"), null, microphone, main);
            var level = new ProgressBar { Minimum = -60, Maximum = 0, Height = 6, Width = 300 };
            var levelRow = Row(Loc.Get("InputLevel"), null, level, main);
            levelRow.Visibility = Visible(s.Enabled);
            var status = Footer();
            var headsetAbout = Footer(Loc.Get("HeadsetMicrophoneAbout"));
            headsetAbout.Visibility = Visible(s.Device == LipSync.Headset);
            microphone.SelectionChanged += (_, _) =>
            {
                if (microphone.SelectedIndex < 0) return;
                s.Device = microphone.SelectedIndex == 0 ? "" : devices[microphone.SelectedIndex - 1].Id;
                headsetAbout.Visibility = Visible(s.Device == LipSync.Headset);
                Changed();
            };
            on.Click += (_, _) =>
            {
                s.Enabled = on.IsChecked == true;
                levelRow.Visibility = Visible(s.Enabled);
                Rounded();
                Changed();
            };

            var details = Group(Loc.Get("Advanced"));
            Ui.CardAction Detail(string name)
            {
                var row = ListRow(Loc.Get(name), null, () => Navigate("LipSync/" + name));
                details.Children.Add(row);
                return row;
            }
            var always = Consonants.Concat(Vowels).Count(v => s.Of(v.Code) is { On: true, WithTracking: true });
            var off = Consonants.Concat(Vowels).Count(v => s.Of(v.Code) is { On: true, WithTracking: false });
            var never = Consonants.Length + Vowels.Length - always - off;
            ShowRow(Detail("MouthShapes"), never > 0 ? Loc.Format("MouthShapesSummaryNever", always, off, never) : Loc.Format("MouthShapesSummary", always, off));
            ShowRow(Detail("Sound"), engines[Math.Max(0, Array.IndexOf(LipSync.Engines, s.Engine))]);
            var output = Detail("VRChat");
            string Avatar() => lipSync.Matched switch
            {
                null => Loc.Get("AvatarShortUnknown"),
                0 => Loc.Get("AvatarShortNone"),
                var count => Loc.Format("AvatarShortFound", count),
            };
            var shown = Avatar();
            ShowRow(output, shown, lipSync.Matched == 0);
            status.Text = lipSync.Status;
            live.Add(() =>
            {
                level.Value = Math.Clamp(lipSync.Level, -60, 0);
                if (status.Text != lipSync.Status) status.Text = lipSync.Status;
                if (Avatar() is var now && now != shown) ShowRow(output, shown = now, lipSync.Matched == 0);
            });
        }

        void MouthShapes()
        {
            (Subtitle.Text, Subtitle.Visibility) = (Loc.Format("VisemesAbout", Loc.Get("VisemeWithoutTracking")), Visibility.Visible);
            string[] modes = [Loc.Get("VisemeAlways"), Loc.Get("VisemeWithoutTracking"), Loc.Get("VisemeOff")];
            foreach (var (group, members) in new[] { ("Consonants", Consonants), ("Vowels", Vowels) })
            {
                var list = Group(Loc.Get(group));
                foreach (var (name, sounds, words) in members)
                {
                    var viseme = s.Of(name);
                    var example = Muted(Loc.Format("VisemeExample", words));
                    example.Margin = new(0);
                    var bar = new ProgressBar { Maximum = 1, Height = 4, Margin = new(0, 4, 0, 0) };
                    AutomationProperties.SetName(bar, Loc.Format("VisemeOutput", sounds));
                    var label = Stack(new TextBlock { Text = sounds, FontWeight = FontWeights.SemiBold }, example, bar);
                    (label.MinWidth, label.Margin, label.VerticalAlignment, label.Background, label.ToolTip) = (170, new(0, 0, 16, 0), VerticalAlignment.Center, Brushes.Transparent, Loc.Format("VisemeCode", name));
                    var when = new ComboBox { ItemsSource = modes, Width = 230, Margin = new(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center, SelectedIndex = !viseme.On ? 2 : viseme.WithTracking ? 0 : 1 };
                    AutomationProperties.SetName(when, Loc.Format("VisemeWhen", sounds));
                    var strength = new Ui.NumberBox
                    {
                        Minimum = 0, Maximum = 200, SmallChange = 5, LargeChange = 25, MaxDecimalPlaces = 0, Value = Math.Round(viseme.Strength * 100),
                        Width = 130, ClearButtonEnabled = false, IsEnabled = viseme.On, VerticalAlignment = VerticalAlignment.Center,
                    };
                    AutomationProperties.SetName(strength, Loc.Format("VisemeStrength", sounds));
                    strength.LostKeyboardFocus += (_, _) =>
                    {
                        if (PresentationSource.FromVisual(strength) is { } source)
                            strength.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyUpEvent });
                    };
                    strength.ValueChanged += (_, _) =>
                    {
                        if (strength.Value is not { } percent || !double.IsFinite(percent)) return;
                        viseme.Strength = Math.Clamp(percent, 0, 200) / 100;
                        Changed();
                    };
                    when.SelectionChanged += (_, _) =>
                    {
                        (viseme.On, viseme.WithTracking) = (when.SelectedIndex != 2, when.SelectedIndex == 0);
                        strength.IsEnabled = viseme.On;
                        Changed();
                    };
                    var row = new WrapPanel();
                    foreach (var element in new UIElement[] { label, when, strength, new TextBlock { Text = "%", Margin = new(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center } })
                        row.Children.Add(element);
                    Wrap(row, list);
                    live.Add(() => bar.Value = lipSync.Output[Array.IndexOf(LipSync.Visemes, name)]);
                }
            }
        }

        void Sound()
        {
            var list = Group();
            Row(Loc.Get("Engine"), null, Menu(engines, Math.Max(0, Array.IndexOf(LipSync.Engines, s.Engine)), i => s.Engine = LipSync.Engines[i]), list);
            void Number(string title, string? about, double min, double max, double value, Action<double> set, string unit, double scale = 1)
            {
                var cell = new StackPanel();
                NumericSetting.AddTo(cell, title, min, max, value, number =>
                {
                    set(number);
                    Changed();
                }, unit: unit, scale: scale, decimals: 0);
                if (about is not null) cell.Children.Add(Muted(about));
                Wrap(cell, list);
            }
            Number(Loc.Get("Smoothing"), Loc.Get("LipSyncSmoothingAbout"), 1, 100, s.Smoothing, v => s.Smoothing = (int)Math.Round(v), "");
            Number(Loc.Get("VisemeFadeIn"), Loc.Get("VisemeFadeInAbout"), 1, 200, s.FadeInMs, v => s.FadeInMs = (int)Math.Round(v), " ms");
            Number(Loc.Get("VisemeFadeOut"), Loc.Get("VisemeFadeOutAbout"), 1, 500, s.FadeOutMs, v => s.FadeOutMs = (int)Math.Round(v), " ms");
            Number(Loc.Get("InputGain"), null, 0, 4, s.Gain, v => s.Gain = v, "%", 100);
            Number(Loc.Get("NoiseGate"), Loc.Get("NoiseGateAbout"), 6, 40, s.GateMargin, v => s.GateMargin = v, " dB");
            Row(Loc.Get("FrameLength"), null, Menu([Loc.Get("FrameLength10"), Loc.Get("FrameLength20")], s.FrameMs >= 20 ? 1 : 0, i => s.FrameMs = i == 1 ? 20 : 10), list);
        }

        void VRChatOutput()
        {
            var list = Group();
            var sends = Footer();
            var found = Footer();
            Border? index = null, weight = null;
            void Describe()
            {
                sends.Text = s.Index ? Loc.Format("SendsIndexAndWeight", s.IndexName, s.WeightName)
                    : Loc.Format("SendsPerViseme", LipSync.Parameter("sil"), LipSync.Parameter("ou"));
                if (index is null || weight is null) return;
                index.Visibility = weight.Visibility = Visible(s.Index);
                Rounded();
            }
            Row(Loc.Get("Parameters"), null, Menu([Loc.Get("OneParameterPerViseme"), Loc.Get("VisemeIndexAndWeight")], s.Index ? 1 : 0, i =>
            {
                s.Index = i == 1;
                Describe();
            }), list);
            index = Row(Loc.Get("IndexParameter"), null, NameBox(s.IndexName, v => s.IndexName = v), list);
            weight = Row(Loc.Get("WeightParameter"), null, NameBox(s.WeightName, v => s.WeightName = v), list);
            var laughter = Row(Loc.Get("LaughterParameter"), Loc.Get("LaughterAbout"), NameBox(s.Laughter, v => s.Laughter = v), list);
            laughter.Visibility = Visible(s.Engine == "Laughter");
            foreach (var box in new[] { index, weight }.Select(r => ((DockPanel)r.Child).Children.OfType<TextBox>().Single())) box.TextChanged += (_, _) => Describe();
            Describe();
            live.Add(() =>
            {
                if (AvatarStatus() is var now && found.Text != now) found.Text = now;
            });
        }

        lipSyncMeters?.Stop();
        var meters = lipSyncMeters = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        meters.Tick += (_, _) =>
        {
            if (ParentPage(page) != "LipSync")
            {
                meters.Stop();
                return;
            }
            live.ForEach(update => update());
        };
        meters.Start();
    }
}
