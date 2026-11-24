using System.Globalization;
using System.Text.RegularExpressions;

namespace Huginn;

/// <summary>
/// How long a meeting's raw audio is kept, and the cleanup that deletes it. Only the two WAV tracks of a meeting
/// (<c>{stamp}_me.wav</c>, <c>{stamp}_them.wav</c>) are ever deleted: they are nearly all of the store. The
/// transcript, the raw transcript, speakers.json, the source, notes, summary and snapshots stay.
/// A meeting's audio is deleted only when its transcript is on disk and not empty, because the transcript is then
/// what remains of the meeting; audio with no transcript stays, whatever its age.
/// </summary>
internal static class AudioRetention
{
    /// <summary>The choices, as the menus list them. A key is what keep_audio.txt holds.</summary>
    public static readonly (string Key, string Label)[] Choices =
    {
        ("1m", "1 month"),
        ("3w", "3 weeks"),
        ("2w", "2 weeks"),
        ("1w", "1 week"),
        ("0", "Delete after transcription"),
    };

    public const string Default = "2w";

    public static bool IsChoice(string? key) => Choices.Any(c => c.Key == key);

    public static string LabelFor(string key) => Choices.FirstOrDefault(c => c.Key == key).Label ?? key;

    /// <summary>Audio of a meeting that started before this moment may go.</summary>
    public static DateTime CutoffFor(string key, DateTime now) => key switch
    {
        "1m" => now.AddMonths(-1),
        "3w" => now.AddDays(-21),
        "1w" => now.AddDays(-7),
        "0" => now,
        _ => now.AddDays(-14),
    };

    /// <summary>What one cleanup did (or, for a dry run, would do).</summary>
    public sealed record Result(int Meetings, int Files, long Bytes, int NoTranscript, long NoTranscriptBytes, int Busy, int Failed)
    {
        public string Describe(string key, bool dryRun)
        {
            var what = key == "0" ? "already transcribed" : $"older than {LabelFor(key)}";
            var did = dryRun ? "would delete" : "deleted";
            var s = $"{did} {Files} file(s), {HuginnEngine.SizeText(Bytes)}, of {Meetings} meeting(s) {what}";
            if (NoTranscript > 0) s += $"; kept {NoTranscript} meeting(s) of that age with no transcript ({HuginnEngine.SizeText(NoTranscriptBytes)})";
            if (Busy > 0) s += $"; left {Busy} that are recording or being transcribed";
            if (Failed > 0) s += $"; {Failed} file(s) could not be deleted";
            return s;
        }
    }

    private static readonly Regex Track = new(@"^(\d{4}-\d{2}-\d{2}_\d{6})_(me|them)\.wav$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Deletes the audio of every meeting in <paramref name="dir"/> that started before the cutoff and has a
    /// transcript. <paramref name="busy"/> names the recordings to leave alone: the one in progress and those
    /// still being transcribed or waiting for it. Files are deleted for good, not moved to a bin.
    /// </summary>
    public static Result Clean(string dir, string key, DateTime now, Func<string, bool> busy, bool dryRun = false)
    {
        int meetings = 0, files = 0, noTranscript = 0, busyCount = 0, failed = 0;
        long bytes = 0, noTranscriptBytes = 0;
        if (!Directory.Exists(dir)) return new Result(0, 0, 0, 0, 0, 0, 0);
        var cutoff = CutoffFor(key, now);

        var byStamp = new Dictionary<string, List<FileInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*.wav", SearchOption.TopDirectoryOnly))
        {
            var m = Track.Match(f.Name);
            if (!m.Success) continue;
            if (!byStamp.TryGetValue(m.Groups[1].Value, out var list)) byStamp[m.Groups[1].Value] = list = new List<FileInfo>();
            list.Add(f);
        }

        foreach (var (stamp, tracks) in byStamp)
        {
            if (!DateTime.TryParseExact(stamp, "yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var started)) continue;
            if (started >= cutoff) continue;
            if (busy(stamp)) { busyCount++; continue; }
            long size = tracks.Sum(t => t.Length);
            if (!HasTranscript(dir, stamp)) { noTranscript++; noTranscriptBytes += size; continue; }
            bool any = false;
            foreach (var t in tracks)
            {
                try
                {
                    long len = t.Length;
                    if (!dryRun) t.Delete();
                    files++; bytes += len; any = true;
                }
                catch (Exception ex)
                {
                    failed++;
                    HuginnLog.Write($"meeting audio: could not delete {t.Name}", ex);
                }
            }
            if (any) meetings++;
        }
        return new Result(meetings, files, bytes, noTranscript, noTranscriptBytes, busyCount, failed);
    }

    private static readonly Regex AnyFile = new(@"^(\d{4}-\d{2}-\d{2}_\d{6})_", RegexOptions.CultureInvariant);

    /// <summary>
    /// For a computer that holds only a COPY of each meeting (it transcribes for another computer, which keeps the
    /// originals): deletes every file of a meeting that started before the cutoff, the audio and the text alike,
    /// with or without a transcript. Same setting and same cutoffs as <see cref="Clean"/>.
    /// </summary>
    public static (int Meetings, int Files, long Bytes, int Failed) CleanCopies(string dir, string key, DateTime now, Func<string, bool> busy)
    {
        int files = 0, failed = 0;
        long bytes = 0;
        var gone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(dir)) return (0, 0, 0, 0);
        var cutoff = CutoffFor(key, now);
        foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.TopDirectoryOnly))
        {
            var m = AnyFile.Match(f.Name);
            if (!m.Success) continue;
            var stamp = m.Groups[1].Value;
            if (!DateTime.TryParseExact(stamp, "yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var started)) continue;
            if (started >= cutoff || busy(stamp)) continue;
            try
            {
                long len = f.Length;
                f.Delete();
                files++; bytes += len; gone.Add(stamp);
            }
            catch (Exception ex)
            {
                failed++;
                HuginnLog.Write($"meeting copies: could not delete {f.Name}", ex);
            }
        }
        return (gone.Count, files, bytes, failed);
    }

    /// <summary>The meeting's transcript is on disk and not empty: <c>{stamp}_transcript.txt</c>, or the file it was
    /// renamed to (<c>{stamp}_{name}.txt</c>). The source note and the raw transcript do not count.</summary>
    public static bool HasTranscript(string dir, string stamp)
    {
        try
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles(stamp + "_*.txt", SearchOption.TopDirectoryOnly))
            {
                if (f.Name.EndsWith("_source.txt", StringComparison.OrdinalIgnoreCase) || f.Name.EndsWith(".raw.txt", StringComparison.OrdinalIgnoreCase)) continue;
                if (f.Length > 0) return true;
            }
        }
        catch { /* unreadable folder: say no, and the audio stays */ }
        return false;
    }
}
