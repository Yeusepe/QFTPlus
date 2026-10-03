using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace QFTPlus;

public partial class LogViewer : UserControl
{
    static readonly (string Title, string File, string Empty)[] Logs =
    {
        ("Tracking (tracking.log)", "tracking.log", "Start tracking to create it."),
        ("Eye tracking (eyes.log)", "eyes.log", "Start tracking with independent eye gaze on to create it."),
        ("Face and tongue (face.log)", "face.log", "Start tracking to create it."),
        ("Headset relay (questpro-live-relay.txt)", "questpro-live-relay.txt", "Start tracking to create it."),
        ("Setup (setup.log)", "setup.log", "Run setup to create it."),
        ("Components (studio.log)", "studio.log", "It’s created when calibration or hybrid hands run a step."),
        ("Hybrid hands (hybrid.log)", "hybrid.log", "Turn on hybrid hands, then start tracking to create it.")
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
            LogCaption.Text = "Nothing logged yet. " + Logs[LogPicker.SelectedIndex].Empty;
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
        catch (IOException error) { LogCaption.Text = "Couldn’t read the log. " + error.Message; return; }
        if (following) LogText.ScrollToEnd();
        else LogText.ScrollToVerticalOffset(offset);
        LogCaption.Text = $"Updates live. Last changed at {info.LastWriteTime:T}" + (first > 0 ? ". Showing the most recent part." : ".");
    }

    void CopyLog(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(LogText.Text); LogCaption.Text = "Copied to the clipboard."; }
        catch (Exception error) { LogCaption.Text = "Couldn’t copy the log. " + error.Message; }
    }

    static void Launch(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
    void OpenLog(object sender, RoutedEventArgs e) => Launch(Target);
    void OpenRecordings(object sender, RoutedEventArgs e) => Launch(Path.Combine(root, "captures"));
}
