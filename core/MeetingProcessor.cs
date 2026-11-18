using System.Text;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Huginn;

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
        var themEnds = new Dictionary<(TimeSpan, string), TimeSpan>();
        foreach (var s in them)
        {
            all.Add((s.Start, "Them", s.Text));
            kept.Add((s.Start, "Them", s.Text));
            themEnds[(s.Start, s.Text)] = s.End;
        }
        all.Sort((a, b) => a.start.CompareTo(b.start));
        kept.Sort((a, b) => a.start.CompareTo(b.start));

        var rawText = Format(all, cap.StartedLocal);
        var reconciled = TranscriptReconciler.DeEcho(Format(kept, cap.StartedLocal));
        // De-echo needs the Me/Them labels, so the names from Kumospace go on after it.
        var timeline = SpeakerTimeline.Load(SpeakerTimeline.PathFor(cap.Stamp));
        var text = timeline == null ? reconciled.Text
            : NameSpeakers(reconciled.Text, kept.Where(k => k.speaker == "Them").Select(k => (k.start, themEnds.GetValueOrDefault((k.start, k.text), k.start))).ToList(), timeline, cap.Stamp);
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
            HuginnLog.Write($"meeting {stamp}: transcript renamed -> {Path.GetFileName(target)}");
            return target;
        }
        catch (Exception ex)
        {
            HuginnLog.Write($"meeting {stamp}: transcript rename failed", ex);
            return currentPath;
        }
    }

    private static string SafeFileName(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        // Windows' set on every OS: macOS allows ':' and '?', so its own list would name the same meeting differently.
        const string invalid = "<>:\"/\\|?*";
        var sb = new StringBuilder();
        foreach (var c in title.Trim())
            sb.Append(invalid.Contains(c) || char.IsControl(c) ? ' ' : c);
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

    private static readonly System.Text.RegularExpressions.Regex ThemLine =
        new(@"^(\s*\[\d{1,2}:\d{2}:\d{2}\]\s*)Them(\s*:)", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Replaces each "Them" line's label with the person Kumospace heard talking at that time. The text pass
    /// removes only "Me" lines, so the n-th "Them" line of <paramref name="text"/> is the n-th of <paramref name="themSpans"/>.
    /// A line with no clear speaker stays "Them". The header names the rooms and the people.</summary>
    internal static string NameSpeakers(string text, List<(TimeSpan start, TimeSpan end)> themSpans, SpeakerTimeline tl, string stamp)
    {
        var talk = HuginnSettings.Current.KumoTalkLevel;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        int themCount = lines.Count(l => ThemLine.IsMatch(l));
        if (themCount != themSpans.Count)
        {
            HuginnLog.Write($"meeting {stamp}: speaker names skipped ({themCount} Them lines vs {themSpans.Count} segments)");
            return text;
        }
        int k = 0, named = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            var m = ThemLine.Match(lines[i]);
            if (!m.Success) continue;
            var (s, e) = themSpans[k++];
            var who = tl.SpeakerFor(s, e, talk);
            if (who == null) continue;
            lines[i] = m.Groups[1].Value + who + lines[i][m.Groups[2].Index..];
            named++;
        }
        HuginnLog.Write($"meeting {stamp}: speakers: named {named} of {themCount} Them lines; {tl.Describe()}");

        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("# Tracks:")) continue;
            var where = tl.Rooms.Count > 0 ? string.Join(", ", tl.Rooms) : "outside a room";
            var people = tl.People.Count > 0 ? string.Join(", ", tl.People) : "nobody recorded";
            lines[i] = $"# Kumospace: {where} · with {people}\n"
                     + $"# Speakers: Me = {tl.Me ?? "the microphone"}. A named line is the person Kumospace heard talking then. \"Them\" = no clear speaker.";
            break;
        }
        return string.Join("\n", lines);
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
        // WaveFileReader, not AudioFileReader: the tracks are always WAV, and AudioFileReader is Windows-only.
        using var reader = new WaveFileReader(path);
        var samples = reader.ToSampleProvider();
        ISampleProvider mono = reader.WaveFormat.Channels > 1
            ? new StereoToMonoSampleProvider(samples) { LeftVolume = 0.5f, RightVolume = 0.5f }
            : samples;
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
