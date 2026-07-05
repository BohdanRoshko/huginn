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

        var transcriptPath = Path.Combine(Paths.MeetingsDir(), $"{cap.Stamp}_transcript.txt");
        var text = Format(labeled, cap.StartedLocal);
        await File.WriteAllTextAsync(transcriptPath, text, new UTF8Encoding(false), ct).ConfigureAwait(false);
        return new MeetingResult(transcriptPath, text, labeled.Count, cap.MicPath, cap.LoopPath);
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
