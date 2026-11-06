using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using AltDeck.Services;

namespace AltDeck;

/// <summary>Shown once after an update: that version's GitHub release notes, as plain text.</summary>
public partial class WhatsNewWindow : Window
{
    private readonly string _url;

    public WhatsNewWindow(Version version, string releaseNotes)
    {
        InitializeComponent();
        var v = $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
        Heading.Text = "What's new in " + v;
        Notes.Text = ToPlainText(releaseNotes);
        _url = $"https://github.com/{Updater.Repo}/releases/tag/v{v}";
    }

    private void GitHub_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(_url) { UseShellExecute = true }); } catch { }
    }

    /// <summary>Keeps the "What's in" part of the release text and drops the Markdown symbols.</summary>
    public static string ToPlainText(string md)
    {
        md = md.Replace("\r\n", "\n");
        int start = md.IndexOf("What's in", StringComparison.OrdinalIgnoreCase);
        if (start >= 0)
        {
            int lineEnd = md.IndexOf('\n', start);
            md = lineEnd >= 0 ? md[(lineEnd + 1)..] : "";   // the heading itself is already the window's title
        }

        var lines = new List<string>();
        foreach (var raw in md.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("See the [README]", StringComparison.OrdinalIgnoreCase)) continue;
            line = Regex.Replace(line, @"^#+\s*", "");                 // headings
            line = Regex.Replace(line, @"^>\s?", "");                   // quotes
            line = Regex.Replace(line, @"^\s*[-*]\s+", "•  ");          // bullets
            line = Regex.Replace(line, @"\[([^\]]+)\]\([^)]+\)", "$1"); // links: keep the text
            line = line.Replace("**", "").Replace("`", "").Replace("*", "");
            lines.Add(line);
        }
        return Regex.Replace(string.Join("\n", lines), @"\n{3,}", "\n\n").Trim();
    }
}
