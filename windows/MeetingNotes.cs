using System.Text;

namespace Munin;

/// <summary>The notes typed in <see cref="NotesPanel"/> while a meeting records, as a file: one line per
/// non-empty pad line, stamped with the offset from the recording start at which the line was begun (the
/// transcript carries the same [hh:mm:ss] stamps, so Odin can quote the transcript around each note). The
/// file is rewritten from the pad on every change, so a crash loses nothing. Lives at <c>{stamp}_notes.md</c>
/// next to the WAV tracks.</summary>
internal static class MeetingNotes
{
    public static string Stamp(TimeSpan at) => $"[{(int)at.TotalHours:D2}:{at.Minutes:D2}:{at.Seconds:D2}]";

    /// <summary>Overwrites the file with the pad's current lines; no lines = no file.</summary>
    public static void Save(string path, DateTime started, IReadOnlyList<(TimeSpan at, string text)> lines)
    {
        try
        {
            if (lines.Count == 0)
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            var sb = new StringBuilder();
            sb.AppendLine($"# Notes typed during the meeting of {started:yyyy-MM-dd HH:mm}");
            sb.AppendLine();
            foreach (var (at, text) in lines) sb.AppendLine($"{Stamp(at)} {text}");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            MuninLog.Write("meeting notes save failed", ex);
        }
    }

    /// <summary>The notes text to send along with the transcript; null when nothing was typed.</summary>
    public static string? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path).Trim();
            return text.Length == 0 ? null : text;
        }
        catch { return null; }
    }
}
