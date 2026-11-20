using System.Text;
using System.Text.RegularExpressions;

namespace Huginn;

/// <summary>
/// Names a finished meeting on this computer, so every row in the recorder has a name even when Odin is off or
/// files nothing. With enough speech and the local text model on disk, the model writes a short title from the
/// transcript; otherwise the name says where the meeting was ("Google Meet call", "Kumospace · Aquamarine Office").
/// Odin's title replaces it when Odin files the meeting, and a name the user typed always wins.
/// </summary>
internal static class MeetingName
{
    private const int MinSpeechChars = 300; // less than this is a test or a hello: the place names it better
    private const int MaxSpeechChars = 3000;

    private static readonly Regex Speech = new(@"^\[\d{1,2}:\d{2}:\d{2}\][ \t]*([^:*\n][^:\n]*?):[ \t]?(.+)$", RegexOptions.Multiline);

    public static async Task<string> MakeAsync(string transcript, Cleaner? model)
    {
        var place = Place(transcript);
        var speech = SpeechOf(transcript);
        if (model != null && speech.Length >= MinSpeechChars && File.Exists(Paths.CleanupModelPath()))
        {
            try
            {
                var title = Tidy(await model.TitleAsync(speech).ConfigureAwait(false));
                if (title.Length > 0) return title;
            }
            catch (Exception ex) { HuginnLog.Write("meeting name: the local model failed", ex); }
        }
        return place;
    }

    /// <summary>Where the meeting was, from the transcript header.</summary>
    internal static string Place(string transcript)
    {
        var source = Regex.Match(transcript, @"^# Source:\s*(.+?)\s*$", RegexOptions.Multiline);
        if (source.Success && source.Groups[1].Value != "Kumospace") return $"{source.Groups[1].Value} call";
        var room = Regex.Match(transcript, @"^# Kumospace:\s*(.+?)\s*·", RegexOptions.Multiline);
        if (room.Success && room.Groups[1].Value != "outside a room") return $"Kumospace · {room.Groups[1].Value}";
        return source.Success ? "Kumospace meeting" : "Meeting";
    }

    // A chat line from the meeting: "[00:12:34] * Andy typed: "…"".
    private static readonly Regex Typed = new(@"^\[\d{1,2}:\d{2}:\d{2}\][ \t]*\*[ \t]*(.+? typed: .+)$", RegexOptions.Multiline);

    /// <summary>The spoken lines as "Name: words", one per line, at most <paramref name="max"/> characters. With
    /// <paramref name="withChat"/>, what people typed in the meeting chat too (a summary wants it; a title does not).</summary>
    internal static string SpeechOf(string transcript, int max = MaxSpeechChars, bool withChat = false)
    {
        var sb = new StringBuilder();
        var text = transcript.Replace("\r\n", "\n");
        var lines = Speech.Matches(text).Select(m => (m.Index, Line: $"{m.Groups[1].Value.Trim()}: {m.Groups[2].Value.Trim()}"));
        if (withChat) lines = lines.Concat(Typed.Matches(text).Select(m => (m.Index, Line: m.Groups[1].Value.Trim()))).OrderBy(x => x.Index);
        foreach (var (_, line) in lines)
        {
            sb.Append(line).Append('\n');
            if (sb.Length >= max) break;
        }
        return sb.Length > max ? sb.ToString(0, max) : sb.ToString();
    }

    // One line, no quotes or "Title:", no full stop, at most 60 characters on a word boundary.
    internal static string Tidy(string raw)
    {
        var s = (raw ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        s = Regex.Replace(s, @"^(meeting\s+)?title\s*:\s*", "", RegexOptions.IgnoreCase).Trim().Trim('"', '\'', '“', '”', '*', '#', ' ').TrimEnd('.');
        if (!s.Contains(' ') && s.Contains('-')) s = s.Replace('-', ' '); // "Outbox-Table-Sync" from a slug-minded model
        if (s.Length > 60) s = s[..s.LastIndexOf(' ', 60)].TrimEnd(',', ' ', '-', '–');
        return s;
    }
}
