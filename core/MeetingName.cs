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

    internal static string SpeechOf(string transcript)
    {
        var sb = new StringBuilder();
        foreach (Match m in Speech.Matches(transcript.Replace("\r\n", "\n")))
        {
            sb.Append(m.Groups[1].Value.Trim()).Append(": ").Append(m.Groups[2].Value.Trim()).Append('\n');
            if (sb.Length >= MaxSpeechChars) break;
        }
        return sb.Length > MaxSpeechChars ? sb.ToString(0, MaxSpeechChars) : sb.ToString();
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
