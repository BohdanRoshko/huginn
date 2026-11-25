using System.Text;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Huginn;

/// <summary>The artifacts a finished meeting recording produces. <paramref name="RawTranscriptPath"/> is set only
/// when the echo gates removed something: the untouched interleave, kept for a second look.</summary>
internal sealed record MeetingResult(string TranscriptPath, string Text, int SegmentCount, string MicWavPath, string LoopWavPath,
    string? RawTranscriptPath, EchoReport Echo);

/// <summary>What the three echo gates did to the "Me" lines (see <see cref="EchoGate"/>).</summary>
internal sealed record EchoReport(int MeLines, int RemovedSilent, int RemovedUnheard, int RemovedAcoustic, int RemovedText, int LagMs, int LagEndMs, int Matched, int Pieces)
{
    public int Removed => RemovedSilent + RemovedUnheard + RemovedAcoustic + RemovedText;
    public override string ToString() =>
        $"lag {LagMs:+#;-#;0} ms at the start, {LagEndMs:+#;-#;0} ms at the end ({Matched} of {Pieces} pieces matched); of {MeLines} Me lines removed {RemovedSilent} silent, {RemovedUnheard} not heard by the meeting app, {RemovedAcoustic} echo, {RemovedText} by text";
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
    /// <param name="o">The settings of this job; null = this computer's, as a meeting recorded here is processed.</param>
    public static async Task<MeetingResult> TranscribeAsync(MeetingCapture cap, Transcriber transcriber, CancellationToken ct = default, MeetingOptions? o = null)
    {
        var me = new List<TranscriptSegment>();
        var them = new List<TranscriptSegment>();
        float[]? micEnv = null, loopEnv = null;
        if (HasAudio(cap.MicPath))
        {
            using var wav = OpenAsWhisperWav(cap.MicPath);
            micEnv = EchoGate.Envelope(wav);
            wav.Position = 0;
            me.AddRange(await transcriber.TranscribeSegmentsAsync(wav, ct, o).ConfigureAwait(false));
        }
        if (HasAudio(cap.LoopPath))
        {
            using var wav = OpenAsWhisperWav(cap.LoopPath);
            loopEnv = EchoGate.Envelope(wav);
            wav.Position = 0;
            them.AddRange(await transcriber.TranscribeSegmentsAsync(wav, ct, o).ConfigureAwait(false));
        }

        var timeline = SpeakerTimeline.Load(SpeakerTimeline.PathFor(cap.Stamp));
        var (text, rawText, echo, lines) = Compose(me, them, micEnv, loopEnv, timeline, cap.StartedLocal, ReadSource(cap.Stamp), cap.Stamp, o);

        var (transcriptPath, rawPath) = await SaveAsync(cap.Stamp, text, echo.Removed > 0 ? rawText : null, ct).ConfigureAwait(false);
        return new MeetingResult(transcriptPath, text, lines, cap.MicPath, cap.LoopPath, rawPath, echo);
    }

    /// <summary>Writes a meeting's transcript, and the untouched interleave when there is one, into the meetings
    /// folder. A transcript made on another computer is saved through here too, so both land the same way.</summary>
    public static async Task<(string TranscriptPath, string? RawPath)> SaveAsync(string stamp, string text, string? rawText, CancellationToken ct = default)
    {
        var transcriptPath = TranscriptPathFor(stamp, null);
        await File.WriteAllTextAsync(transcriptPath, text, new UTF8Encoding(false), ct).ConfigureAwait(false);
        string? rawPath = null;
        if (rawText != null)
        {
            rawPath = Path.Combine(Paths.MeetingsDir(), $"{stamp}_transcript.raw.txt");
            await File.WriteAllTextAsync(rawPath, rawText, new UTF8Encoding(false), ct).ConfigureAwait(false);
        }
        return (transcriptPath, rawPath);
    }

    /// <summary>Turns the two tracks' segments into the transcript: drops the "Me" lines that are not the user,
    /// names the "Them" lines, and adds what people did. No files, no model: the same segments always give the
    /// same text. Returns the transcript, the untouched interleave, what the echo gates did, and the line count.</summary>
    internal static (string Text, string RawText, EchoReport Echo, int Lines) Compose(
        IReadOnlyList<TranscriptSegment> me, IReadOnlyList<TranscriptSegment> them, float[]? micEnv, float[]? loopEnv,
        SpeakerTimeline? timeline, DateTime startedLocal, string? source, string stamp, MeetingOptions? o = null)
    {
        string userName = o?.UserName ?? Settings.UserName;
        // On speakers most "Me" lines are the far side again, and Whisper invents lines on silence. Two acoustic
        // gates, then the text pass; each keeps what it cannot prove. The untouched interleave is kept as .raw.
        bool kumo = timeline != null && timeline.HasKumospace;
        var toMeeting = kumo ? MeetingClock(timeline!, loopEnv, stamp) : t => t;
        double talk = o?.TalkLevel ?? HuginnSettings.Current.KumoTalkLevel;
        var verdict = EchoGate.Judge(me, micEnv, loopEnv, kumo ? (s, e) => timeline!.HeardMe(toMeeting(s), toMeeting(e), talk) : null);
        var all = new List<(TimeSpan start, string speaker, string text)>();
        var kept = new List<(TimeSpan start, string speaker, string text)>();
        int theirWords = 0;
        for (int i = 0; i < me.Count; i++)
        {
            all.Add((me[i].Start, "Me", me[i].Text));
            if (verdict.Drop[i]) continue;
            // Both spoke at once: the line is theirs when its words, in their order, are what they had just said.
            if (verdict.Mixed[i])
            {
                TimeSpan from = verdict.ToLoop(me[i].Start) - TimeSpan.FromSeconds(10), to = verdict.ToLoop(me[i].End) + TimeSpan.FromSeconds(0.5);
                if (TranscriptReconciler.SaidByThem(me[i].Text, them.Where(t => t.Start >= from && t.Start <= to).Select(t => t.Text))) { theirWords++; continue; }
            }
            // A line Kumospace heard me say goes past the text pass under a label that pass does not read.
            kept.Add((me[i].Start, verdict.Heard[i] ? SureMe : "Me", me[i].Text));
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

        var rawText = Format(all, startedLocal, source);
        var reconciled = TranscriptReconciler.DeEcho(Format(kept, startedLocal, source));
        // De-echo needs the Me/Them labels, so the names from Kumospace go on after it.
        var text = SureMeLine.Replace(reconciled.Text, "$1Me$2");
        if (kumo)
        {
            var spans = kept.Where(k => k.speaker == "Them")
                .Select(k => (toMeeting(k.start), toMeeting(themEnds.GetValueOrDefault((k.start, k.text), k.start)))).ToList();
            text = NameSpeakers(text, spans, timeline!, stamp, talk, userName);
        }
        else text = NameMe(text, userName);
        if (timeline != null && timeline.Events.Count > 0) text = AddEvents(text, timeline.Events);
        var echo = new EchoReport(me.Count, verdict.Silent, verdict.Unheard, verdict.Echo + theirWords, reconciled.RemovedEchoes, verdict.LagMs, verdict.LagEndMs, verdict.Matched, verdict.Pieces);
        return (text, rawText, echo, kept.Count - reconciled.RemovedEchoes);
    }

    /// <summary>Says whose microphone "Me" is, on a transcript with no names from Kumospace (Meet, Zoom, a recording
    /// started by hand): the name set under "Your name…". The header line has the shape the Kumospace one has, so a
    /// reader that shows a name on the "Me" lines needs one rule. No name set: the text is left as it is.</summary>
    internal static string NameMe(string text, string name)
    {
        name = name.Replace(".", " ").Trim();
        if (name.Length == 0) return text;
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        int at = lines.FindIndex(l => l.StartsWith("# Tracks:"));
        if (at < 0) return text;
        lines.Insert(at + 1, $"# Speakers: Me = {name}. \"Them\" = everyone else in the call; this app does not tell them apart.");
        return string.Join("\n", lines);
    }

    private const string SureMe = "Me+";
    private static readonly System.Text.RegularExpressions.Regex SureMeLine =
        new(@"^(\s*\[\d{1,2}:\d{2}:\d{2}\]\s*)Me\+(\s*:)", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.Multiline);

    private const int ClockBinMs = 200; // Kumospace's level samples come every 200 ms
    // 10 s pieces every 5 s; the first lag within ±3 s, then it may move 1 s per piece, at most 10 s after a gap.
    private static readonly LagOptions MeetingToLoop = new(Window: 50, Step: 25, FirstSearch: 15, NextSearch: 5, MaxSearch: 50, SideOffset: 3)
    { MinSharpness = 0.10 };

    /// <summary>Turns a time on the system-audio track into the time on Kumospace's clock, which the speaker levels
    /// use. The two start together, but a track that lost audio runs ahead from there on, and then the level
    /// rows of one person sit under the words of the next. The levels and the track are the same sound, so the
    /// lag is measured by matching them, piece by piece (<see cref="LagCurve"/>).</summary>
    internal static Func<TimeSpan, TimeSpan> MeetingClock(SpeakerTimeline tl, float[]? loopEnv, string stamp)
    {
        if (loopEnv == null || loopEnv.Length == 0) return t => t;
        int per = ClockBinMs / EchoGate.FrameMs;
        var loop = new float[loopEnv.Length / per];
        for (int i = 0; i < loop.Length; i++)
        {
            float loudest = float.MinValue;
            for (int j = i * per; j < (i + 1) * per; j++) if (loopEnv[j] > loudest) loudest = loopEnv[j];
            loop[i] = loudest;
        }
        // The level curve runs past the track's end: a track that lost audio is shorter than the meeting.
        var curve = LagCurve.Measure(tl.RemoteCurve(ClockBinMs, loop.Length + MeetingToLoop.MaxSearch), loop, MeetingToLoop);
        // One sample either way is the measure's own grain, and the name lookup already allows for it.
        if (curve.Matched.Count == 0 || (Math.Abs(curve.MinLag) <= 1 && Math.Abs(curve.MaxLag) <= 1)) return t => t;
        HuginnLog.Write($"meeting {stamp}: the system-audio track runs {-curve.Matched[0].Lag * ClockBinMs} ms ahead of Kumospace at the start, " +
                        $"{-curve.Matched[^1].Lag * ClockBinMs} ms at the end ({curve.Matched.Count} of {curve.Pieces} pieces matched)");
        return t =>
        {
            int lag = curve.AtSecond((int)(t.TotalMilliseconds / ClockBinMs));
            return Math.Abs(lag) <= 1 ? t : t - TimeSpan.FromMilliseconds(lag * (double)ClockBinMs);
        };
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

    /// <summary>The app a recording came from (Zoom, Kumospace, …), kept next to its WAVs until it is transcribed.</summary>
    public static string SourcePathFor(string stamp) => Path.Combine(Paths.MeetingsDir(), $"{stamp}_source.txt");

    public static void SaveSource(string stamp, string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return;
        try { File.WriteAllText(SourcePathFor(stamp), source.Trim(), new UTF8Encoding(false)); }
        catch (Exception ex) { HuginnLog.Write($"meeting {stamp}: source not saved", ex); }
    }

    private static string? ReadSource(string stamp)
    {
        try
        {
            var path = SourcePathFor(stamp);
            var s = File.Exists(path) ? File.ReadAllText(path).Trim() : "";
            return s.Length > 0 ? s : null;
        }
        catch { return null; }
    }

    internal static string Format(List<(TimeSpan start, string speaker, string text)> segs, DateTime started, string? source = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Meeting transcript — {started:yyyy-MM-dd HH:mm}");
        if (source != null) sb.AppendLine($"# Source: {source}"); // Odin shows it as the meeting's source
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
    internal static string NameSpeakers(string text, List<(TimeSpan start, TimeSpan end)> themSpans, SpeakerTimeline tl, string stamp,
        double? talkLevel = null, string? userName = null)
    {
        var talk = talkLevel ?? HuginnSettings.Current.KumoTalkLevel;
        userName ??= Settings.UserName;
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
            var me = tl.Me ?? (userName.Length > 0 ? userName.Replace(".", " ").Trim() : "the microphone");
            // Another app's talk samples (Meet) have no rooms, and the app shows who talks; it does not hear them.
            lines[i] = tl.App == null
                ? $"# Kumospace: {where} · with {people}\n"
                + $"# Speakers: Me = {me}. A named line is the person Kumospace heard talking then. \"Them\" = no clear speaker."
                : $"# {tl.App}: with {people}\n"
                + $"# Speakers: Me = {me}. A named line is the person {tl.App} showed talking then. \"Them\" = no clear speaker.";
            break;
        }
        return string.Join("\n", lines);
    }

    /// <summary>Puts what people did into the transcript, each at its time, as a line that starts with "*":
    /// <c>[00:12:34] * Andy reacted 👍</c>, <c>* Andy raised a hand</c>, <c>* Andy typed: "…"</c>,
    /// <c>* Andy started sharing their screen</c>, <c>* Snapshot of Andy's screen: shot-001.jpg</c>,
    /// <c>* Andy entered the voice range</c>, <c>* Andy and Sam left the voice range</c>.
    /// The de-echo and the speaker names only read "Me"/"Them" lines, so these pass through both.</summary>
    internal static string AddEvents(string text, IReadOnlyList<object[]> events)
    {
        var lines = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').ToList();
        var stamped = new System.Text.RegularExpressions.Regex(@"^\s*\[(\d{1,2}):(\d{2}):(\d{2})\]");
        long Sec(string l)
        {
            var m = stamped.Match(l);
            return m.Success ? int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 + int.Parse(m.Groups[3].Value) : -1;
        }
        var ordered = events.Select(e => (off: Convert.ToInt64(e[0]), kind: e[1]?.ToString() ?? "", who: e[2]?.ToString() ?? "", what: e[3]?.ToString() ?? ""))
            .OrderBy(e => e.off).ToList();
        // People who come or go together are one line: a room that empties is "A, B and C left", not three lines.
        for (int i = 0; i < ordered.Count; i++)
        {
            var first = ordered[i];
            if (first.kind is not (VoiceRange.Entered or VoiceRange.Left) || first.who.Length == 0) continue;
            var names = new List<string> { first.who };
            for (int j = i + 1; j < ordered.Count && ordered[j].off - first.off <= RangeTogetherMs; )
            {
                if (ordered[j].kind == first.kind && ordered[j].who.Length > 0)
                {
                    if (!names.Contains(ordered[j].who)) names.Add(ordered[j].who);
                    ordered.RemoveAt(j);
                }
                else j++;
            }
            ordered[i] = (first.off, first.kind, NameList(names), first.what);
        }
        int at = lines.FindIndex(l => Sec(l) >= 0);
        if (at < 0)
        {
            if (lines.Count > 0 && lines[^1].StartsWith("(no speech")) lines.RemoveAt(lines.Count - 1);
            at = lines.Count;
        }
        foreach (var e in ordered)
        {
            var line = EventLine(e.kind, e.who.Length > 0 ? e.who : "Someone", e.what);
            if (line == null) continue;
            long sec = e.off / 1000;
            // After every line stamped at or before the event's second.
            while (at < lines.Count && (Sec(lines[at]) < 0 ? true : Sec(lines[at]) <= sec)) at++;
            var t = TimeSpan.FromSeconds(sec);
            lines.Insert(at++, $"[{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}] * {line}");
        }
        return string.Join("\n", lines) + "\n";
    }

    private const int RangeTogetherMs = 3000;

    /// <summary>The lines that say who entered and who left the voice range (<see cref="VoiceRange"/>), added to a
    /// transcript that lacks them: one made by a build that did not know them yet, on another computer. A transcript
    /// that already has such a line, or a recording with no such change, comes back as it was.</summary>
    internal static string AddRangeLines(string text, SpeakerTimeline? timeline)
    {
        var range = timeline?.Events.Where(e => e[1]?.ToString() is VoiceRange.Entered or VoiceRange.Left).ToList();
        if (range == null || range.Count == 0) return text;
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"^\[\d{1,2}:\d{2}:\d{2}\] \* .+ (entered|left) the voice range\r?$", System.Text.RegularExpressions.RegexOptions.Multiline)) return text;
        return AddEvents(text, range);
    }

    // "Ana", "Ana and Tom", "Ana, Tom and Marta".
    private static string NameList(IReadOnlyList<string> names) => names.Count switch
    {
        1 => names[0],
        2 => $"{names[0]} and {names[1]}",
        _ => $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}",
    };

    private static string? EventLine(string kind, string who, string what) => kind switch
    {
        VoiceRange.Entered => $"{who} entered the voice range",
        VoiceRange.Left => $"{who} left the voice range",
        "react" when what.Length > 0 => $"{who} reacted {what}",
        "hand-up" => $"{who} raised a hand",
        "hand-down" => $"{who} lowered a hand",
        "share-start" => $"{who} started sharing their screen",
        "share-stop" => $"{who} stopped sharing their screen",
        "chat" when what.Trim().Length > 0 => $"{who} typed: \"{System.Text.RegularExpressions.Regex.Replace(what.Trim(), @"\s*\r?\n\s*", " / ")}\"",
        "shot" when what.Length > 0 => $"Snapshot of {who}'s screen: {what}",
        _ => null,
    };

    private static bool HasAudio(string path)
    {
        try { return File.Exists(path) && new FileInfo(path).Length > 1024; }
        catch { return false; }
    }

    /// <summary>Opens any WAV and returns a 16 kHz mono 16-bit WAV stream (downmix + resample as needed).
    /// The mic track is already 16 kHz mono so this is a near no-op for it.</summary>
    internal static MemoryStream OpenAsWhisperWav(string path)
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
