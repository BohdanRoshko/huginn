using System.Text;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Munin;

/// <summary>The artifacts a finished meeting recording produces. <paramref name="RawTranscriptPath"/> is set only
/// when the echo gates removed something: the untouched interleave, kept for a second look.</summary>
internal sealed record MeetingResult(string TranscriptPath, string Text, int SegmentCount, string MicWavPath, string LoopWavPath,
    string? RawTranscriptPath, EchoReport Echo);

/// <summary>What the three echo gates did to the "Me" lines (see <see cref="EchoGate"/>).</summary>
internal sealed record EchoReport(int MeLines, int RemovedSilent, int RemovedAcoustic, int RemovedText, int LagMs, double LagConfidence)
{
    public int Removed => RemovedSilent + RemovedAcoustic + RemovedText;
    public override string ToString() =>
        $"lag {LagMs:+#;-#;0} ms (confidence {LagConfidence:F2}); of {MeLines} Me lines removed {RemovedSilent} silent, {RemovedAcoustic} echo, {RemovedText} by text";
}

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
        var me = new List<TranscriptSegment>();
        var them = new List<TranscriptSegment>();
        float[]? micEnv = null, loopEnv = null;
        if (HasAudio(cap.MicPath))
        {
            using var wav = OpenAsWhisperWav(cap.MicPath);
            micEnv = EchoGate.Envelope(wav);
            wav.Position = 0;
            me.AddRange(await transcriber.TranscribeSegmentsAsync(wav, ct).ConfigureAwait(false));
        }
        if (HasAudio(cap.LoopPath))
        {
            using var wav = OpenAsWhisperWav(cap.LoopPath);
            loopEnv = EchoGate.Envelope(wav);
            wav.Position = 0;
            them.AddRange(await transcriber.TranscribeSegmentsAsync(wav, ct).ConfigureAwait(false));
        }

        // On speakers most "Me" lines are the far side again, and Whisper invents lines on silence. Two acoustic
        // gates, then the text pass; each keeps what it cannot prove. The untouched interleave is kept as .raw.
        var verdict = EchoGate.Judge(me, micEnv, loopEnv);
        var all = new List<(TimeSpan start, string speaker, string text)>();
        var kept = new List<(TimeSpan start, string speaker, string text)>();
        for (int i = 0; i < me.Count; i++)
        {
            all.Add((me[i].Start, "Me", me[i].Text));
            if (!verdict.Drop[i]) kept.Add((me[i].Start, "Me", me[i].Text));
        }
        foreach (var s in them)
        {
            all.Add((s.Start, "Them", s.Text));
            kept.Add((s.Start, "Them", s.Text));
        }
        all.Sort((a, b) => a.start.CompareTo(b.start));
        kept.Sort((a, b) => a.start.CompareTo(b.start));

        var rawText = Format(all, cap.StartedLocal);
        var reconciled = TranscriptReconciler.DeEcho(Format(kept, cap.StartedLocal));
        var text = reconciled.Text;
        var echo = new EchoReport(me.Count, verdict.Silent, verdict.Echo, reconciled.RemovedEchoes, verdict.LagMs, verdict.LagConfidence);

        var transcriptPath = TranscriptPathFor(cap.Stamp, null);
        await File.WriteAllTextAsync(transcriptPath, text, new UTF8Encoding(false), ct).ConfigureAwait(false);
        string? rawPath = null;
        if (echo.Removed > 0)
        {
            rawPath = Path.Combine(Paths.MeetingsDir(), $"{cap.Stamp}_transcript.raw.txt");
            await File.WriteAllTextAsync(rawPath, rawText, new UTF8Encoding(false), ct).ConfigureAwait(false);
        }
        return new MeetingResult(transcriptPath, text, kept.Count - reconciled.RemovedEchoes, cap.MicPath, cap.LoopPath, rawPath, echo);
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
