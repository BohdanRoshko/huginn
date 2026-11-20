using System.Text.Json;
using System.Text.RegularExpressions;

namespace Huginn;

/// <summary>
/// What Huginn's meeting window shows for one recording: the transcript as it is on disk, and, when Odin filed the
/// meeting, Odin's summary, attendees and event. <see cref="ShotPrefix"/> + "shot-001.jpg" is where a snapshot loads.
/// </summary>
internal sealed record MeetingView(
    string Id, string Title, string? Date, string? Source, IReadOnlyList<string> Attendees, string? Event,
    string? Summary, string Transcript, string ShotPrefix, string TranscriptPath)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>The view of a recent row; null when its transcript is not on disk.</summary>
    public static async Task<MeetingView?> BuildAsync(RecentMeeting row, string? odinSlug, string shotHost)
    {
        var path = RecentMeetings.ResolveTranscript(row);
        if (!File.Exists(path)) return null;
        var transcript = await File.ReadAllTextAsync(path).ConfigureAwait(false);
        string? summary = null, ev = null, title = row.Title;
        var attendees = new List<string>();
        if (odinSlug != null)
        {
            try
            {
                var note = await OdinClient.NoteContentAsync(odinSlug).ConfigureAwait(false);
                var (front, body) = SplitFrontmatter(note);
                summary = body.Trim().Length > 0 ? body : null;
                ev = Field(front, "event");
                title = Field(front, "description") ?? title;
                attendees = List(front, "attendees");
            }
            catch (Exception ex) { HuginnLog.Write($"meeting window: Odin's note {odinSlug} not read", ex); }
        }
        // Without Odin's summary: the one Huginn wrote here (MeetingSummary), then the lines typed in the notes pad
        // while it recorded.
        if (summary == null)
        {
            var parts = new List<string>();
            var local = MeetingSummary.PathFor(row.Stamp);
            if (File.Exists(local))
            {
                var md = (await File.ReadAllTextAsync(local).ConfigureAwait(false)).Trim();
                if (md.Length > 0) parts.Add(md);
            }
            var notesPath = Path.Combine(Paths.MeetingsDir(), $"{row.Stamp}_notes.md");
            if (File.Exists(notesPath))
            {
                var notes = MeetingNotes.Read(notesPath);
                if (!string.IsNullOrWhiteSpace(notes))
                    parts.Add("## My notes\n\n" + string.Join("\n", notes.Split('\n').Where(l => l.Trim().Length > 0).Select(l => "- " + l.Trim())));
            }
            if (parts.Count > 0) summary = string.Join("\n\n", parts);
        }
        var source = Regex.Match(transcript, @"^# Source:\s*(.+?)\s*$", RegexOptions.Multiline);
        return new MeetingView(
            row.Stamp,
            string.IsNullOrWhiteSpace(title) ? "Meeting" : title!,
            row.Started.ToString("yyyy-MM-dd"),
            source.Success ? source.Groups[1].Value : null,
            attendees, ev, summary, transcript,
            $"https://{shotHost}/{Uri.EscapeDataString(row.Stamp)}_",
            path);
    }

    private static (string Front, string Body) SplitFrontmatter(string md)
    {
        var s = md.Replace("\r\n", "\n");
        if (!s.StartsWith("---\n")) return ("", s);
        int end = s.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0) return ("", s);
        int bodyStart = s.IndexOf('\n', end + 4);
        return (s[4..end], bodyStart < 0 ? "" : s[(bodyStart + 1)..]);
    }

    private static string? Field(string front, string key)
    {
        var m = Regex.Match(front, $@"^{key}:[ \t]*(.+?)\s*$", RegexOptions.Multiline);
        if (!m.Success) return null;
        var v = m.Groups[1].Value.Trim().Trim('"', '\'');
        return v.Length == 0 ? null : v;
    }

    private static List<string> List(string front, string key)
    {
        var lines = front.Split('\n');
        int at = Array.FindIndex(lines, l => l.StartsWith(key + ":"));
        if (at < 0) return new();
        var inline = lines[at][(key.Length + 1)..].Trim();
        if (inline.StartsWith('[')) return inline.Trim('[', ']').Split(',').Select(x => x.Trim().Trim('"', '\'')).Where(x => x.Length > 0).ToList();
        if (inline.Length > 0) return new() { inline.Trim('"', '\'') };
        var list = new List<string>();
        for (int i = at + 1; i < lines.Length && Regex.IsMatch(lines[i], @"^\s+-\s*"); i++)
            list.Add(Regex.Replace(lines[i], @"^\s+-\s*", "").Trim().Trim('"', '\''));
        return list.Where(x => x.Length > 0).ToList();
    }
}
