namespace Huginn;

/// <summary>
/// Decides which "Me" (microphone) segments are not the user at all. On speakers the mic re-captures the far
/// side, so Whisper attributes their words to "Me"; on silence Whisper invents lines outright. Two acoustic
/// gates run before the text pass (<see cref="TranscriptReconciler"/>), each keeping what it cannot prove:
///
/// 1. Silence: a segment whose mic level (80th percentile of its 10 ms frames) is below −65 dBFS holds no
///    speech; the line is a hallucination.
/// 2. Echo: both tracks are reduced to 10 ms RMS envelopes in dB. The lag between the tracks (capture start skew
///    plus the speaker path — it differs per recording and per device) is measured in 10 s pieces
///    (<see cref="LagCurve"/>), because a track that lost audio runs ahead of the other from there on. A segment
///    whose mic envelope correlates with the aligned loopback envelope above 0.45 (small local search, far side
///    not silent) is the far side heard through the speakers. When few pieces match (headphones, or a one-sided
///    call) this gate is off.
///
/// Between the two, when the meeting app reports the user's own level (Kumospace does): a segment in which it got
/// nothing from the microphone is dropped, and a segment it heard the user speak in is not dropped for its
/// envelope. If the far side is in that segment too, both spoke at once, and the words decide whose line it is.
///
/// Measured on 2026-09-04_182520 (Bohdan on speakers, barely speaking): 326 Me lines → 78 silent + ~230 echo;
/// the text pass took the rest, leaving the one genuine question. Envelopes, not samples: an hour of audio is
/// 360k frames, so the lag search is a few hundred million multiply-adds, done in well under a second.
/// </summary>
internal static class EchoGate
{
    public const int FrameMs = 10;
    private const int SampleRate = 16000;
    private const int FrameSamples = SampleRate * FrameMs / 1000;
    private const int LocalLagFrames = 8;       // ±80 ms around the piece's lag, per segment
    private const int MinSegmentFrames = 30;    // 300 ms: shorter than this, the envelope says nothing
    private const int MinMatchedPieces = 3;     // fewer than this, or under a fifth of the pieces: no echo here
    private const double MinMatchedShare = 0.20;
    private const double EchoCorrelation = 0.45;
    private const float LoopSilenceDb = -55f;
    private const float MicSilenceDb = -65f;

    // 10 s pieces every 5 s; the first lag within ±3 s, then it may move 1 s per piece, at most 10 s after a gap.
    private static readonly LagOptions MicToLoop = new(Window: 1000, Step: 500, FirstSearch: 300, NextSearch: 100, MaxSearch: 1000, SideOffset: 30);

    /// <param name="Drop">Per Me segment, in order: true = not the user.</param>
    /// <param name="Heard">Per Me segment: true = the meeting app heard the user talk then.</param>
    /// <param name="Mixed">Per Me segment: true = the app heard the user AND the mic heard the far side, so both
    /// spoke at once. The envelopes cannot say whose words Whisper wrote down; the caller decides by the words.</param>
    /// <param name="Unheard">Dropped because the meeting app got nothing from the user's microphone then.</param>
    /// <param name="LagMs">The lag at the start of the recording; <paramref name="LagEndMs"/> the lag at its end.</param>
    /// <param name="Matched">How many of the <paramref name="Pieces"/> 10 s pieces showed the far side in the mic.</param>
    public sealed record Verdict(bool[] Drop, bool[] Heard, bool[] Mixed, int Silent, int Unheard, int Echo, int LagMs, int LagEndMs, int Matched, int Pieces)
    {
        /// <summary>A time on the mic track as a time on the system-audio track.</summary>
        public Func<TimeSpan, TimeSpan> ToLoop { get; init; } = t => t;
    }

    /// <param name="heard">What the meeting app says about a span of the system-audio track's clock: true = it
    /// heard the user talk, false = it got nothing from the user's microphone, null = it cannot tell. The app
    /// cancels the echo before it measures, so its word beats the envelopes: on 2026-11-20 a line where the user
    /// and the far side spoke at once correlated with the far side and was dropped.</param>
    public static Verdict Judge(IReadOnlyList<TranscriptSegment> me, float[]? micEnv, float[]? loopEnv,
        Func<TimeSpan, TimeSpan, bool?>? heard = null)
    {
        var drop = new bool[me.Count];
        var sure = new bool[me.Count];
        var mixed = new bool[me.Count];
        if (micEnv == null || micEnv.Length == 0) return new Verdict(drop, sure, mixed, 0, 0, 0, 0, 0, 0, 0);

        int silent = 0, unheard = 0, echo = 0;
        for (int i = 0; i < me.Count; i++)
            if (IsSilent(micEnv, me[i])) { drop[i] = true; silent++; }

        var curve = loopEnv != null && loopEnv.Length > 0 ? LagCurve.Measure(micEnv, loopEnv, MicToLoop) : LagCurve.None;
        bool hearsFarSide = curve.Matched.Count >= MinMatchedPieces && curve.Matched.Count >= MinMatchedShare * curve.Pieces;

        // The mic track can run ahead of the system-audio track too; this puts a mic time on that track's clock.
        TimeSpan ToLoop(TimeSpan t) =>
            hearsFarSide ? t + TimeSpan.FromMilliseconds(curve.At((int)(t.TotalMilliseconds / FrameMs)) * (double)FrameMs) : t;

        if (heard != null)
            for (int i = 0; i < me.Count; i++)
            {
                if (drop[i]) continue;
                switch (heard(ToLoop(me[i].Start), ToLoop(me[i].Start) + (me[i].End - me[i].Start)))
                {
                    case true: sure[i] = true; break;
                    case false: drop[i] = true; unheard++; break;
                }
            }

        if (hearsFarSide)
            for (int i = 0; i < me.Count; i++)
            {
                if (drop[i] || Correlation(micEnv, loopEnv!, me[i], curve) < EchoCorrelation) continue;
                if (sure[i]) mixed[i] = true;
                else { drop[i] = true; echo++; }
            }

        int first = curve.Matched.Count > 0 ? curve.Matched[0].Lag * FrameMs : 0;
        int end = curve.Matched.Count > 0 ? curve.Matched[^1].Lag * FrameMs : 0;
        return new Verdict(drop, sure, mixed, silent, unheard, echo, first, end, curve.Matched.Count, curve.Pieces) { ToLoop = ToLoop };
    }

    /// <summary>10 ms RMS envelope in dBFS of a 16 kHz mono 16-bit WAV (any chunk layout; the stream is read
    /// from its current position to the end of the data chunk).</summary>
    public static float[] Envelope(Stream wav)
    {
        using var br = new BinaryReader(wav, System.Text.Encoding.ASCII, leaveOpen: true);
        if (br.ReadUInt32() != 0x46464952 /* RIFF */) throw new InvalidDataException("not a RIFF file");
        br.ReadUInt32();
        if (br.ReadUInt32() != 0x45564157 /* WAVE */) throw new InvalidDataException("not a WAVE file");

        while (wav.Position + 8 <= wav.Length)
        {
            uint id = br.ReadUInt32();
            int size = (int)br.ReadUInt32();
            if (id != 0x61746164 /* data */) { wav.Position += size + (size & 1); continue; }

            var env = new List<float>(size / (2 * FrameSamples) + 1);
            var buf = new byte[FrameSamples * 2];
            int left = size;
            while (left >= buf.Length)
            {
                int got = br.Read(buf, 0, buf.Length);
                if (got < buf.Length) break;
                left -= got;
                double sum = 0;
                for (int i = 0; i < buf.Length; i += 2)
                {
                    double s = (short)(buf[i] | (buf[i + 1] << 8)) / 32768.0;
                    sum += s * s;
                }
                env.Add((float)(20 * Math.Log10(Math.Sqrt(sum / FrameSamples) + 1e-6)));
            }
            return env.ToArray();
        }
        return Array.Empty<float>();
    }

    private static bool IsSilent(float[] micEnv, TranscriptSegment seg)
    {
        var (f0, f1) = Frames(seg, micEnv.Length);
        if (f1 - f0 < 3) return false;
        var span = new float[f1 - f0];
        Array.Copy(micEnv, f0, span, 0, span.Length);
        Array.Sort(span);
        return span[(int)(span.Length * 0.8)] < MicSilenceDb;
    }

    private static double Correlation(float[] micEnv, float[] loopEnv, TranscriptSegment seg, LagCurve curve)
    {
        var (f0, f1) = Frames(seg, micEnv.Length);
        int len = f1 - f0;
        if (len < MinSegmentFrames) return 0;
        double best = 0;
        foreach (int lagFrames in curve.Around((f0 + f1) / 2))
            for (int k = -LocalLagFrames; k <= LocalLagFrames; k++)
            {
                int l0 = f0 + lagFrames + k;
                if (l0 < 0 || l0 + len > loopEnv.Length) continue;
                float loudest = float.MinValue;
                for (int i = l0; i < l0 + len; i++) if (loopEnv[i] > loudest) loudest = loopEnv[i];
                if (loudest < LoopSilenceDb) continue;
                best = Math.Max(best, LagCurve.Pearson(micEnv, f0, loopEnv, l0, len));
            }
        return best;
    }

    private static (int, int) Frames(TranscriptSegment seg, int total)
    {
        int f0 = Math.Clamp((int)(seg.Start.TotalMilliseconds / FrameMs), 0, total);
        int f1 = Math.Clamp((int)(seg.End.TotalMilliseconds / FrameMs), f0, total);
        return (f0, f1);
    }

}
