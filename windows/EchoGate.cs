namespace Munin;

/// <summary>
/// Decides which "Me" (microphone) segments are not the user at all. On speakers the mic re-captures the far
/// side, so Whisper attributes their words to "Me"; on silence Whisper invents lines outright. Two acoustic
/// gates run before the text pass (<see cref="TranscriptReconciler"/>), each keeping what it cannot prove:
///
/// 1. Silence: a segment whose mic level (80th percentile of its 10 ms frames) is below −65 dBFS holds no
///    speech; the line is a hallucination.
/// 2. Echo: both tracks are reduced to 10 ms RMS envelopes in dB. One global lag between the tracks comes from
///    the envelope cross-correlation over ±3 s (capture start skew plus the speaker path — it differs per
///    recording and per device). A segment whose mic envelope correlates with the aligned loopback envelope
///    above 0.45 (small local search, far side not silent) is the far side heard through the speakers.
///    No confident lag (headphones, or a one-sided call) turns this gate off.
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
    private const int MaxLagFrames = 300;       // ±3 s
    private const int LocalLagFrames = 8;       // ±80 ms around the global lag, per segment
    private const int MinSegmentFrames = 30;    // 300 ms: shorter than this, the envelope says nothing
    private const double MinLagConfidence = 0.30;
    private const double EchoCorrelation = 0.45;
    private const float LoopSilenceDb = -55f;
    private const float MicSilenceDb = -65f;

    /// <param name="Drop">Per Me segment, in order: true = not the user.</param>
    public sealed record Verdict(bool[] Drop, int Silent, int Echo, int LagMs, double LagConfidence);

    public static Verdict Judge(IReadOnlyList<TranscriptSegment> me, float[]? micEnv, float[]? loopEnv)
    {
        var drop = new bool[me.Count];
        if (micEnv == null || micEnv.Length == 0) return new Verdict(drop, 0, 0, 0, 0);

        int silent = 0, echo = 0, lagFrames = 0;
        double confidence = 0;
        for (int i = 0; i < me.Count; i++)
            if (IsSilent(micEnv, me[i])) { drop[i] = true; silent++; }

        if (loopEnv != null && loopEnv.Length > 0)
        {
            (lagFrames, confidence) = GlobalLag(micEnv, loopEnv);
            if (confidence >= MinLagConfidence)
                for (int i = 0; i < me.Count; i++)
                    if (!drop[i] && Correlation(micEnv, loopEnv, me[i], lagFrames) >= EchoCorrelation) { drop[i] = true; echo++; }
        }
        return new Verdict(drop, silent, echo, lagFrames * FrameMs, confidence);
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

    /// <summary>Lag (in frames) at which the loopback envelope best matches the mic envelope, and how well.
    /// Positive: the loopback's content is later than the mic's (loop time = mic time + lag).</summary>
    public static (int lagFrames, double correlation) GlobalLag(float[] mic, float[] loop)
    {
        int n = Math.Min(mic.Length, loop.Length);
        if (n < MinSegmentFrames * 10) return (0, 0);
        int bestLag = 0;
        double best = -1;
        for (int k = -MaxLagFrames; k <= MaxLagFrames; k++)
        {
            int len = n - Math.Abs(k);
            if (len < MinSegmentFrames * 10) continue;
            int mo = k >= 0 ? 0 : -k, lo = k >= 0 ? k : 0;
            double c = Pearson(mic, mo, loop, lo, len);
            if (c > best) { best = c; bestLag = k; }
        }
        return (bestLag, best);
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

    private static double Correlation(float[] micEnv, float[] loopEnv, TranscriptSegment seg, int lagFrames)
    {
        var (f0, f1) = Frames(seg, micEnv.Length);
        int len = f1 - f0;
        if (len < MinSegmentFrames) return 0;
        double best = 0;
        for (int k = -LocalLagFrames; k <= LocalLagFrames; k++)
        {
            int l0 = f0 + lagFrames + k;
            if (l0 < 0 || l0 + len > loopEnv.Length) continue;
            float loudest = float.MinValue;
            for (int i = l0; i < l0 + len; i++) if (loopEnv[i] > loudest) loudest = loopEnv[i];
            if (loudest < LoopSilenceDb) continue;
            best = Math.Max(best, Pearson(micEnv, f0, loopEnv, l0, len));
        }
        return best;
    }

    private static (int, int) Frames(TranscriptSegment seg, int total)
    {
        int f0 = Math.Clamp((int)(seg.Start.TotalMilliseconds / FrameMs), 0, total);
        int f1 = Math.Clamp((int)(seg.End.TotalMilliseconds / FrameMs), f0, total);
        return (f0, f1);
    }

    private static double Pearson(float[] a, int ao, float[] b, int bo, int len)
    {
        double ma = 0, mb = 0;
        for (int i = 0; i < len; i++) { ma += a[ao + i]; mb += b[bo + i]; }
        ma /= len; mb /= len;
        double sab = 0, saa = 0, sbb = 0;
        for (int i = 0; i < len; i++)
        {
            double da = a[ao + i] - ma, db = b[bo + i] - mb;
            sab += da * db; saa += da * da; sbb += db * db;
        }
        double d = Math.Sqrt(saa * sbb);
        return d > 0 ? sab / d : 0;
    }
}
