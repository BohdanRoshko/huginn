using System.Text;

namespace Munin;

/// <summary>The notes you type in the recorder panel while a meeting records: one markdown list line per
/// note, stamped with the offset from the recording start (the transcript carries the same [hh:mm:ss]
/// stamps, so Odin can quote the transcript around each note). Each line is appended the moment it is
/// committed, so a crash loses nothing. File: <c>{stamp}_notes.md</c> next to the WAV tracks.</summary>
internal static class MeetingNotes
{
    public static string Stamp(TimeSpan at) => $"[{(int)at.TotalHours:D2}:{at.Minutes:D2}:{at.Seconds:D2}]";

    public static void Append(string path, DateTime started, TimeSpan at, string text)
    {
        try
        {
            bool fresh = !File.Exists(path);
            using var w = new StreamWriter(path, append: true, new UTF8Encoding(false));
            if (fresh)
            {
                w.WriteLine($"# Notes typed during the meeting of {started:yyyy-MM-dd HH:mm}");
                w.WriteLine();
            }
            w.WriteLine($"- {Stamp(at)} {text}");
        }
        catch (Exception ex)
        {
            MuninLog.Write("meeting note append failed", ex);
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
