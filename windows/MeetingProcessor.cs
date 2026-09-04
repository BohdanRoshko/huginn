using System.Text;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Munin;

/// <summary>The artifacts a finished meeting recording produces.</summary>
internal sealed record MeetingResult(string TranscriptPath, string Text, int SegmentCount, string MicWavPath, string LoopWavPath);

/// <summary>
/// Batch-transcribes a finished <see cref="MeetingCapture"/> (off the recording hot path, so it can run
/// in the background while a new meeting records): transcribes the mic ("Me") and the loopback ("Them",
/// downmixed + resampled to 16 kHz mono) via the shared <see cref="Transcriber"/>, interleaves the
/// segments by timestamp, and writes a `[hh:mm:ss] Me/Them:` transcript file. The <see cref="Transcriber"/>
/// is gated, so concurrent jobs queue on the GPU — recording is never blocked, only transcription serializes.
/// </summary>
internal static class MeetingProcessor
{
    public static async Task<MeetingResult> TranscribeAsync(MeetingCapture cap, Transcriber transcriber, CancellationToken ct = default)
    {
        var labeled = new List<(TimeSpan start, string speaker, string text)>();
        foreach (var (path, speaker) in new[] { (cap.MicPath, "Me"), (cap.LoopPath, "Them") })
        {
            if (!HasAudio(path)) continue;
            using var wav = OpenAsWhisperWav(path);
            foreach (var seg in await transcriber.TranscribeSegmentsAsync(wav, ct).ConfigureAwait(false))
                labeled.Add((seg.Start, speaker, seg.Text));
        }
        labeled.Sort((a, b) => a.start.CompareTo(b.start));

        var transcriptPath = TranscriptPathFor(cap.Stamp, null);
        var text = Format(labeled, cap.StartedLocal);
        await File.WriteAllTextAsync(transcriptPath, text, new UTF8Encoding(false), ct).ConfigureAwait(false);
        return new MeetingResult(transcriptPath, text, labeled.Count, cap.MicPath, cap.LoopPath);
    }

    /// <summary>Where a transcript lives: <c>{stamp}_{name}.txt</c> once the user has named the meeting, else the
    /// default <c>{stamp}_transcript.txt</c>. The stamp prefix stays so the file sorts next to its WAV tracks.</summary>
    public static string TranscriptPathFor(string stamp, string? title)
    {
        var safe = SafeFileName(title);
        return Path.Combine(Paths.MeetingsDir(), safe.Length == 0 ? $"{stamp}_transcript.txt" : $"{stamp}_{safe}.txt");
    }

    /// <summary>Moves an existing transcript to the name <see cref="TranscriptPathFor"/> gives it and returns the
    /// path the file has afterwards (unchanged when the file is missing or the move fails).</summary>
    public static string RenameTranscript(string currentPath, string stamp, string? title)
    {
        try
        {
            if (!File.Exists(currentPath)) return currentPath;
            var target = TranscriptPathFor(stamp, title);
            if (string.Equals(target, currentPath, StringComparison.OrdinalIgnoreCase)) return currentPath;
            if (File.Exists(target))
                target = Path.Combine(Path.GetDirectoryName(target)!, $"{Path.GetFileNameWithoutExtension(target)} (2).txt");
            File.Move(currentPath, target);
            MuninLog.Write($"meeting {stamp}: transcript renamed -> {Path.GetFileName(target)}");
            return target;
        }
        catch (Exception ex)
        {
            MuninLog.Write($"meeting {stamp}: transcript rename failed", ex);
            return currentPath;
        }
    }

    private static string SafeFileName(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (var c in title.Trim())
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? ' ' : c);
        var s = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ").Trim(' ', '.');
        return s.Length <= 80 ? s : s[..80].TrimEnd(' ', '.');
    }

    private static string Format(List<(TimeSpan start, string speaker, string text)> segs, DateTime started)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Meeting transcript — {started:yyyy-MM-dd HH:mm}");
        sb.AppendLine("# Tracks: Me (microphone) · Them (system audio — all remote participants, mixed)");
        sb.AppendLine();
        if (segs.Count == 0)
        {
            sb.AppendLine("(no speech detected on either track)");
            return sb.ToString();
        }
        foreach (var (start, speaker, line) in segs)
            sb.AppendLine($"[{(int)start.TotalHours:D2}:{start.Minutes:D2}:{start.Seconds:D2}] {speaker}: {line}");
        return sb.ToString();
    }

    private static bool HasAudio(string path)
    {
        try { return File.Exists(path) && new FileInfo(path).Length > 1024; }
        catch { return false; }
    }

    /// <summary>Opens any WAV and returns a 16 kHz mono 16-bit WAV stream (downmix + resample as needed).
    /// The mic track is already 16 kHz mono so this is a near no-op for it.</summary>
    private static MemoryStream OpenAsWhisperWav(string path)
    {
        using var reader = new AudioFileReader(path);
        ISampleProvider mono = reader.WaveFormat.Channels > 1
            ? new StereoToMonoSampleProvider(reader) { LeftVolume = 0.5f, RightVolume = 0.5f }
            : reader;
        ISampleProvider at16k = reader.WaveFormat.SampleRate == 16000
            ? mono
            : new WdlResamplingSampleProvider(mono, 16000);
        var pcm16 = new SampleToWaveProvider16(at16k);

        var ms = new MemoryStream();
        WaveFileWriter.WriteWavFileToStream(ms, pcm16);
        ms.Position = 0;
        return ms;
    }
}
