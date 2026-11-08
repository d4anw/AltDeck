using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;

namespace AltDeck.Services;

public sealed class LogEntry
{
    public DateTime Time { get; init; }
    public string Kind { get; init; } = "Info";
    public string Message { get; init; } = "";

    public string TimeText => Time.Date == DateTime.Today ? Time.ToString("HH:mm:ss") : Time.ToString("d MMM HH:mm:ss");
    public bool IsProblem => ActivityLog.ProblemKinds.Contains(Kind);
    public override string ToString() => $"{Time:yyyy-MM-dd HH:mm:ss} [{Kind}] {Message}";
}

/// <summary>
/// Timestamped record of what AltDeck did: launches, drops, rejoins, give-ups, frozen restarts, sign-in problems.
/// Shown on the Activity page and written to %AppData%\AltDeck\logs\altdeck-YYYY-MM-DD.log (kept for 14 days).
/// Safe to call from any thread.
/// </summary>
public sealed class ActivityLog
{
    public static ActivityLog Shared { get; } = new();

    public const string Launch = "Launch", Rejoin = "Rejoin", Drop = "Drop", GiveUp = "Give up", Frozen = "Frozen",
                        Stop = "Stop", SignIn = "Sign-in", Error = "Error", Info = "Info", Backup = "Backup",
                        Preset = "Preset", Layout = "Layout", Purchase = "Purchase", Trade = "Trade";
    public static readonly HashSet<string> ProblemKinds = new() { Drop, GiveUp, Frozen, SignIn, Error };

    const int MaxEntries = 1000;
    const int KeepDays = 14;
    static readonly Regex LineRx = new(@"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}) \[([^\]]+)\] (.*)$");

    readonly object _fileLock = new();

    /// <summary>Newest first. Only touched on the UI thread.</summary>
    public ObservableCollection<LogEntry> Entries { get; } = new();

    public string LogDir { get; } = Path.Combine(SettingsStore.DataDir, "logs");
    string FileFor(DateTime d) => Path.Combine(LogDir, $"altdeck-{d:yyyy-MM-dd}.log");

    /// <summary>Loads today's earlier lines (so a restart doesn't empty the page) and deletes old files.</summary>
    public void LoadToday()
    {
        try
        {
            var path = FileFor(DateTime.Today);
            if (File.Exists(path))
            {
                var lines = File.ReadAllLines(path);
                foreach (var line in lines.Skip(Math.Max(0, lines.Length - 300)))
                {
                    var m = LineRx.Match(line);
                    if (!m.Success) continue;
                    if (!DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                                                DateTimeStyles.None, out var t)) continue;
                    Entries.Insert(0, new LogEntry { Time = t, Kind = m.Groups[2].Value, Message = m.Groups[3].Value });
                }
            }
        }
        catch { }

        try
        {
            if (!Directory.Exists(LogDir)) return;
            var cutoff = DateTime.Today.AddDays(-KeepDays);
            foreach (var f in Directory.GetFiles(LogDir, "altdeck-*.log"))
                if (File.GetLastWriteTime(f) < cutoff) File.Delete(f);
        }
        catch { }
    }

    public void Add(string kind, string message)
    {
        var e = new LogEntry { Time = DateTime.Now, Kind = kind, Message = message.Replace("\r", " ").Replace("\n", " ") };
        WriteFile(e);

        var app = Application.Current;
        if (app == null) return;
        if (app.Dispatcher.CheckAccess()) AddOnUi(e);
        else app.Dispatcher.BeginInvoke(new Action(() => AddOnUi(e)));
    }

    void AddOnUi(LogEntry e)
    {
        Entries.Insert(0, e);
        while (Entries.Count > MaxEntries) Entries.RemoveAt(Entries.Count - 1);
    }

    void WriteFile(LogEntry e)
    {
        try
        {
            lock (_fileLock)
            {
                Directory.CreateDirectory(LogDir);
                File.AppendAllText(FileFor(e.Time), e + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch { }
    }

    /// <summary>Clears the page only; the log files stay.</summary>
    public void ClearView() => Entries.Clear();
}
