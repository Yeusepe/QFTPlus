using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace QFTPlus;

public partial class LogViewer : UserControl
{
    static readonly (string Title, string File, string Empty)[] Logs =
    {
        (Loc.Get("TrackingTrackingLog"), "tracking.log", Loc.Get("StartTrackingToCreateIt")),
        (Loc.Get("EyeTrackingEyesLog"), "eyes.log", Loc.Get("StartTrackingWithIndependentEyeGazeOnToCreate")),
        (Loc.Get("FaceAndTongueFaceLog"), "face.log", Loc.Get("StartTrackingToCreateIt")),
        (Loc.Get("HeadsetRelayQuestproLiveRelayTxt"), "questpro-live-relay.txt", Loc.Get("StartTrackingToCreateIt")),
        (Loc.Get("SetupSetupLog"), "setup.log", Loc.Get("RunSetupToCreateIt")),
        (Loc.Get("ComponentsStudioLog"), "studio.log", Loc.Get("ItSCreatedWhenCalibrationOrHybridHandsRun")),
        (Loc.Get("HybridHandsHybridLog"), "hybrid.log", Loc.Get("TurnOnHybridHandsThenStartTrackingToCreate"))
    };
    const int TailSize = 128 * 1024;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    string root = "";
    long first, read;
    string Target => Path.Combine(root, Logs[Math.Max(0, LogPicker.SelectedIndex)].File);

    public LogViewer()
    {
        InitializeComponent();
        LogPicker.ItemsSource = Logs.Select(log => log.Title);
        LogPicker.SelectedIndex = 0;
        timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); timer.Start(); };
        Unloaded += (_, _) => timer.Stop();
    }

    public void Open(string directory, string? reveal)
    {
        root = directory;
        RecordingsButton.IsEnabled = Directory.Exists(Path.Combine(root, "captures"));
        if (reveal is null) return;
        LogPicker.SelectedIndex = Math.Max(0, Array.FindIndex(Logs, log => log.File == reveal));
        LogExpander.IsExpanded = true;
        Refresh(true);
        LogText.ScrollToEnd();
    }

    void SelectLog(object sender, SelectionChangedEventArgs e)
    {
        if (root.Length == 0) return;
        LogText.Clear();
        Refresh(true);
        LogText.ScrollToEnd();
    }

    void RefreshLog(object sender, RoutedEventArgs e) { Refresh(true); LogText.ScrollToEnd(); }
    internal void Refresh(bool force = false)
    {
        if (root.Length == 0 || !LogExpander.IsExpanded) return;
        var info = new FileInfo(Target);
        CopyLogButton.IsEnabled = OpenLogButton.IsEnabled = info.Exists;
        LogText.Visibility = info.Exists ? Visibility.Visible : Visibility.Collapsed;
        if (!info.Exists)
        {
            first = read = 0;
            LogText.Clear();
            LogCaption.Text = Loc.Format("LogEmpty", Logs[LogPicker.SelectedIndex].Empty);
            return;
        }
        if (!force && info.Length == read) return;
        var following = LogText.VerticalOffset + LogText.ViewportHeight >= LogText.ExtentHeight - 4;
        var offset = LogText.VerticalOffset;
        var reload = force || info.Length < read || info.Length - first > 2 * TailSize;
        try
        {
            using var stream = new FileStream(Target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = reload ? Math.Max(0, stream.Length - TailSize) : read;
            stream.Seek(start, SeekOrigin.Begin);
            var text = new StreamReader(stream).ReadToEnd();
            if (reload)
            {
                LogText.Text = start > 0 ? text[(text.IndexOf('\n') + 1)..] : text;
                first = start;
            }
            else LogText.AppendText(text);
            read = stream.Position;
        }
        catch (IOException error) { LogCaption.Text = Loc.Format("LogReadFailure", error.Message); return; }
        if (following) LogText.ScrollToEnd();
        else LogText.ScrollToVerticalOffset(offset);
        LogCaption.Text = Loc.Format(first > 0 ? "LogUpdatedTail" : "LogUpdated", info.LastWriteTime);
    }

    void CopyLog(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(LogText.Text); LogCaption.Text = Loc.Get("CopiedToTheClipboard"); }
        catch (Exception error) { LogCaption.Text = Loc.Format("LogCopyFailure", error.Message); }
    }

    static void Launch(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
    void OpenLog(object sender, RoutedEventArgs e) => Launch(Target);
    void OpenRecordings(object sender, RoutedEventArgs e) => Launch(Path.Combine(root, "captures"));
}
