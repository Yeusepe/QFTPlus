using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace QFTPlus;

public partial class StudioWindow
{
    UIElement GameVideoOptions()
    {
        var games = new ComboBox { DisplayMemberPath = "Name", Margin = new(0, 0, 0, 12) };
        var editor = new StackPanel { Margin = new(0, 16, 0, 0), Visibility = Visibility.Collapsed };
        var status = Muted("", live: true);
        var panel = new StackPanel();
        panel.Children.Add(Muted(Loc.Get("GameProfilesAbout")));
        panel.Children.Add(Muted(Loc.Get("GameProfilePrecisionAbout")));
        Field(panel, Loc.Get("GameProfileGame"), games);
        var reload = Button(Loc.Get("GameProfileRefresh"), async () =>
        {
            try
            {
                var installed = SteamVr.Running() ? await Task.Run(SteamVr.Games) : new List<SteamVr.Game>();
                foreach (var (key, value) in session.GameVideo.Load())
                    if (value is JsonObject p && installed.All(g => g.Key != key))
                        installed.Add(new(key, p["name"]?.GetValue<string>() ?? key, ""));
                games.ItemsSource = installed;
                if (installed.Count > 0) games.SelectedIndex = 0;
                status.Text = installed.Count == 0 ? Loc.Get("GameProfileNoGames") : "";
            }
            catch (Exception error) { status.Text = error.Message; }
        });
        panel.Children.Add(reload);
        panel.Children.Add(status);
        panel.Children.Add(editor);
        games.SelectionChanged += (_, _) =>
        {
            editor.Children.Clear();
            editor.Visibility = Visible(games.SelectedItem is SteamVr.Game);
            status.Text = "";
            if (games.SelectedItem is not SteamVr.Game game) return;
            var saved = session.GameVideo.Load()[game.Key] as JsonObject;
            var values = saved?["settings"] as JsonObject;
            var getters = new Dictionary<string, Func<JsonNode?>>();
            var ready = false;
            void Changed()
            {
                if (!ready) return;
                try
                {
                    var changes = new JsonObject();
                    foreach (var (key, get) in getters) if (get() is { } value) changes[key] = value;
                    var profile = session.GameVideo.Load()[game.Key] as JsonObject ?? new();
                    profile["name"] = game.Name;
                    profile["settings"] = changes;
                    session.GameVideo.Save(game.Key, profile);
                    status.Text = Loc.Get("GameProfileSaved");
                }
                catch (Exception error) { status.Text = error.Message; }
            }
            foreach (var setting in GameProfiles.Settings)
            {
                var resourceKey = "GameVideo_" + setting.Key;
                var title = Loc.Get(resourceKey);
                if (setting.Kind == "bool")
                {
                    var choice = new ComboBox { ItemsSource = new[] { Loc.Get("GameProfileInherit"), Loc.Get("On"), Loc.Get("Off") },
                        SelectedIndex = values?[setting.Key] is { } v ? v.GetValue<bool>() ? 1 : 2 : 0, Margin = new(0, 0, 0, 16) };
                    Field(editor, title, choice);
                    choice.SelectionChanged += (_, _) => Changed();
                    getters[setting.Key] = () => choice.SelectedIndex == 0 ? null : JsonValue.Create(choice.SelectedIndex == 1);
                }
                else
                {
                    var enabled = new CheckBox { Content = Loc.Format("GameProfileOverride", title), IsChecked = values?.ContainsKey(setting.Key) == true, Margin = new(0, 8, 0, 4) };
                    editor.Children.Add(enabled);
                    var defaultValue = setting.Key switch { "targetBandwidth" => 200, "streamFormatWidth" => 1024, "supersampleScale" => 1, "sharpeningStrength" => 0.2, _ => 0 };
                    var row = new StackPanel { IsEnabled = enabled.IsChecked == true };
                    var slider = NumericSetting.AddTo(row, title, setting.Min, setting.Max, values?[setting.Key]?.GetValue<double>() ?? defaultValue,
                        _ => Changed(), unit: "", decimals: setting.Kind == "int" ? 0 : 2,
                        step: setting.Kind == "int" ? 1 : setting.Key == "preferredFoveatedAngle" ? 0.1 : 0.01);
                    enabled.Checked += (_, _) => { row.IsEnabled = true; Changed(); };
                    enabled.Unchecked += (_, _) => { row.IsEnabled = false; Changed(); };
                    editor.Children.Add(row);
                    getters[setting.Key] = () => enabled.IsChecked != true ? null : setting.Kind == "int" ? JsonValue.Create((int)slider.Value) : JsonValue.Create(slider.Value);
                }
            }
            ready = true;
            editor.Children.Add(Buttons(Button(Loc.Get("GameProfileRemove"), () =>
            {
                try { session.GameVideo.Save(game.Key, null); games.SelectedItem = null; status.Text = Loc.Get("GameProfileRemoved"); }
                catch (Exception error) { status.Text = error.Message; }
            })));
            editor.Children.Add(Muted(Loc.Get("GameProfileRenderingAbout")));
            editor.Children.Add(VrssTunerOptions(game, status));
        };
        var live = Muted("", live: true);
        panel.Children.Add(live);
        trackingRefresh += () => live.Text = session.GameVideo.Status;
        return new Wpf.Ui.Controls.CardExpander { Header = Loc.Get("GameProfilesTitle"), Content = panel, IsExpanded = false };
    }

    UIElement VrssTunerOptions(SteamVr.Game game, TextBlock status)
    {
        var panel = new StackPanel { Margin = new(0, 8, 0, 0) };
        panel.Children.Add(Heading(Loc.Get("VrssTitle")));
        panel.Children.Add(Muted(Loc.Get("VrssAbout")));
        panel.Children.Add(Buttons(Button(Loc.Get("VrssDownload"), () => Open("https://github.com/challenger0303/VRSS-Tuner/releases")),
            Button(Loc.Get("VrssChoose"), () =>
            {
                var dialog = new Microsoft.Win32.OpenFileDialog { Filter = Loc.Get("VrssExecutableFilter"), CheckFileExists = true };
                if (dialog.ShowDialog(this) == true) session.Save("vrssTunerPath", dialog.FileName);
            }), Button(Loc.Get("VrssOpen"), () =>
            {
                try { Process.Start(new ProcessStartInfo(TunerPath()) { UseShellExecute = true })?.Dispose(); }
                catch (Exception error) { status.Text = error.Message; }
            })));
        if (game.Key != "steam.app.438100") return panel;
        var saved = session.GameVideo.Load()[game.Key]?["vrss"];
        var mode = new ComboBox { ItemsSource = new[] { Loc.Get("Off"), Loc.Get("VrssAdaptive"), Loc.Get("VrssAlways") }, SelectedIndex = Math.Clamp(saved?["mode"]?.GetValue<int>() ?? 1, 0, 2) };
        Field(panel, Loc.Get("VrssMode"), mode);
        var area = NumericSetting.AddTo(panel, Loc.Get("VrssArea"), 1, 100, saved?["area"]?.GetValue<double>() ?? 50, _ => { }, step: 0.1);
        panel.Children.Add(Muted(Loc.Get("VrssApplyAbout")));
        var apply = Button(Loc.Get("VrssApply"), async () =>
        {
            try
            {
                panel.IsEnabled = false;
                await VrssTuner.Apply(TunerPath(), session.Root, mode.SelectedIndex, area.Value);
                var profile = session.GameVideo.Load()[game.Key] as JsonObject ?? new JsonObject { ["name"] = game.Name, ["settings"] = new JsonObject() };
                profile["vrss"] = new JsonObject { ["mode"] = mode.SelectedIndex, ["area"] = area.Value };
                session.GameVideo.Save(game.Key, profile);
                status.Text = Loc.Get("VrssApplied");
            }
            catch (Exception error) { status.Text = error.Message; }
            finally { panel.IsEnabled = true; }
        });
        AutomationProperties.SetHelpText(apply, Loc.Get("VrssApplyAbout"));
        panel.Children.Add(apply);
        return panel;

        string TunerPath()
        {
            var path = session.Config["vrssTunerPath"]?.GetValue<string>();
            if (path is null || !File.Exists(path) || !Path.IsPathFullyQualified(path) || !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                throw new IOException(Loc.Get("VrssChooseFirst"));
            return path;
        }
    }
}
