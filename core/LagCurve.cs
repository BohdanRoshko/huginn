namespace Huginn;

/// <summary>How to match two level curves piece by piece. All lengths are in frames of the curves.</summary>
/// <param name="Window">Length of one piece.</param>
/// <param name="Step">Distance between the starts of two pieces.</param>
/// <param name="FirstSearch">How far either way to look for the lag until one piece has matched.</param>
/// <param name="NextSearch">How far the lag may move from one piece to the next.</param>
/// <param name="MaxSearch">The widest search, after many pieces in a row did not match.</param>
/// <param name="SideOffset">A real match stands out: the fit this far to either side must be clearly worse.</param>
internal sealed record LagOptions(int Window, int Step, int FirstSearch, int NextSearch, int MaxSearch, int SideOffset)
{
    public double MinCorrelation { get; init; } = 0.5;
    public double MinSharpness { get; init; } = 0.15;
    /// <summary>A piece whose level moves less than this (dB) holds no speech, so there is nothing to match.</summary>
    public float MinSwingDb { get; init; } = 10f;
}

/// <summary>
/// The lag between two level curves of the same sound, measured in pieces. One lag for a whole recording is not
/// enough: when the PC drops audio, a track loses that time, and everything after it sits earlier than it
/// happened. On 2026-11-20 the system-audio track ended 9 s short and the microphone track 4.8 s short, in steps.
/// So each piece of the first curve is slid along the second, close to where the piece before it matched. A piece
/// counts only when the fit is good and sharp; a piece with nothing to match takes the lag of the nearest one
/// that matched.
/// </summary>
internal sealed class LagCurve
{
    public readonly record struct Piece(int Centre, int Lag, double Correlation);

    private readonly List<Piece> _matched;

    /// <summary>Pieces that had sound on both curves, so a match was possible.</summary>
    public int Pieces { get; }
    public IReadOnlyList<Piece> Matched => _matched;
    public int MinLag => _matched.Count == 0 ? 0 : _matched.Min(p => p.Lag);
    public int MaxLag => _matched.Count == 0 ? 0 : _matched.Max(p => p.Lag);

    private LagCurve(List<Piece> matched, int pieces) { _matched = matched; Pieces = pieces; }

    public static readonly LagCurve None = new(new List<Piece>(), 0);

    /// <summary>The lag for a position on the first curve: second = first + lag. 0 when nothing matched.</summary>
    public int At(int frame) => Nearest(frame, onSecond: false) is { } p ? p.Lag : 0;

    /// <summary>The lag for a position on the second curve.</summary>
    public int AtSecond(int frame) => Nearest(frame, onSecond: true) is { } p ? p.Lag : 0;

    /// <summary>The lags to try for a span of the first curve: the nearest matched piece's, and its neighbours'
    /// (a step in the lag can fall inside the span).</summary>
    public IReadOnlyList<int> Around(int frame)
    {
        if (_matched.Count == 0) return Array.Empty<int>();
        int i = NearestIndex(frame, onSecond: false);
        var lags = new List<int> { _matched[i].Lag };
        if (i > 0 && !lags.Contains(_matched[i - 1].Lag)) lags.Add(_matched[i - 1].Lag);
        if (i + 1 < _matched.Count && !lags.Contains(_matched[i + 1].Lag)) lags.Add(_matched[i + 1].Lag);
        return lags;
    }

    private Piece? Nearest(int frame, bool onSecond) => _matched.Count == 0 ? null : _matched[NearestIndex(frame, onSecond)];

    private int NearestIndex(int frame, bool onSecond)
    {
        int best = 0;
        long bestDist = long.MaxValue;
        for (int i = 0; i < _matched.Count; i++)
        {
            long d = Math.Abs((long)_matched[i].Centre + (onSecond ? _matched[i].Lag : 0) - frame);
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    public static LagCurve Measure(float[] first, float[] second, LagOptions o)
    {
        var matched = new List<Piece>();
        int pieces = 0, last = 0, missed = 0;
        bool have = false;
        for (int start = 0; start + o.Window <= first.Length; start += o.Step)
        {
            if (Swing(first, start, o.Window) < o.MinSwingDb) { missed++; continue; }
            int radius = !have ? o.FirstSearch : Math.Min(o.MaxSearch, o.NextSearch * (1 + missed));
            int bestLag = 0;
            double best = -2;
            for (int k = last - radius; k <= last + radius; k++)
            {
                int s = start + k;
                if (s < 0 || s + o.Window > second.Length) continue;
                double c = Pearson(first, start, second, s, o.Window);
                if (c > best) { best = c; bestLag = k; }
            }
            if (best < -1 || Swing(second, start + bestLag, o.Window) < o.MinSwingDb) { missed++; continue; }
            pieces++;
            if (best >= o.MinCorrelation && best - Side(first, second, start, bestLag, o) >= o.MinSharpness)
            {
                matched.Add(new Piece(start + o.Window / 2, bestLag, best));
                last = bestLag; missed = 0; have = true;
            }
            else missed++;
        }
        return new LagCurve(matched, pieces);
    }

    // The better of the two fits a little to each side of the best lag.
    private static double Side(float[] first, float[] second, int start, int lag, LagOptions o)
    {
        double side = -1;
        foreach (int k in new[] { lag - o.SideOffset, lag + o.SideOffset })
        {
            int s = start + k;
            if (s < 0 || s + o.Window > second.Length) continue;
            side = Math.Max(side, Pearson(first, start, second, s, o.Window));
        }
        return side;
    }

    private static float Swing(float[] a, int start, int len)
    {
        float lo = float.MaxValue, hi = float.MinValue;
        for (int i = start; i < start + len; i++) { if (a[i] < lo) lo = a[i]; if (a[i] > hi) hi = a[i]; }
        return hi - lo;
    }

    internal static double Pearson(float[] a, int ao, float[] b, int bo, int len)
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
