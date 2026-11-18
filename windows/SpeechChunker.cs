namespace Huginn;

/// <summary>
/// Keeps Whisper's decoder from running away on a long track. Fed a whole meeting, whisper.cpp sometimes locks
/// into a repetition loop — the same sentence emitted once a second for minutes (2026-09-07: seven minutes of
/// "The runner could be a machine here."). Two defences, both pure so a harness can prove them:
///
/// <see cref="Group"/> turns the VAD's speech regions into chunks of at most <see cref="MaxChunk"/>, split at
/// silences: each chunk is transcribed on its own, so a loop cannot run past a chunk boundary and costs at
/// most one chunk. <see cref="Tidy"/> then collapses any run of identical consecutive segments to its first
/// line and drops a segment whose words are already the tail of the previous one (Whisper overlaps its own
/// segments now and then).
/// </summary>
internal static class SpeechChunker
{
    public static readonly TimeSpan MaxChunk = TimeSpan.FromSeconds(28);   // under Whisper's 30 s window
    public static readonly TimeSpan JoinGap = TimeSpan.FromSeconds(1.5);   // silences shorter than this stay inside a chunk
    private const int LoopRun = 3;                                          // identical consecutive segments that count as a loop

    public static List<(TimeSpan Start, TimeSpan End)> Group(IEnumerable<(TimeSpan Start, TimeSpan End)> speech)
    {
        var chunks = new List<(TimeSpan Start, TimeSpan End)>();
        foreach (var region in speech)
        {
            if (region.End <= region.Start) continue;
            if (chunks.Count > 0)
            {
                var last = chunks[^1];
                if (region.End - last.Start <= MaxChunk && region.Start - last.End < JoinGap)
                {
                    chunks[^1] = (last.Start, region.End);
                    continue;
                }
            }
            chunks.Add(region);
        }
        return chunks;
    }

    public sealed record TidyResult(List<TranscriptSegment> Segments, int LoopsCollapsed, int OverlapsDropped);

    public static TidyResult Tidy(IReadOnlyList<TranscriptSegment> segments)
    {
        var kept = new List<TranscriptSegment>(segments.Count);
        int loops = 0, overlaps = 0;
        int i = 0;
        while (i < segments.Count)
        {
            var seg = segments[i];
            int j = i;
            while (j + 1 < segments.Count && SameText(segments[j + 1].Text, seg.Text)) j++;
            if (j - i + 1 >= LoopRun) loops += j - i;
            i = j + 1;

            if (kept.Count > 0 && IsTailOf(seg.Text, kept[^1].Text)) { overlaps++; continue; }
            kept.Add(seg);
        }
        return new TidyResult(kept, loops, overlaps);
    }

    private static bool SameText(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.Ordinal);

    // "And this is probably the one that takes us." after "The next tool, and this is probably the one that takes
    // us." — the second is the tail of the first, re-emitted.
    private static bool IsTailOf(string candidate, string previous)
    {
        var c = Normalize(candidate);
        var p = Normalize(previous);
        if (c.Length < 12 || c.Length > p.Length) return false;
        return p.EndsWith(c, StringComparison.Ordinal) || p.Contains(" " + c, StringComparison.Ordinal);
    }

    private static string Normalize(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        bool space = false;
        foreach (var ch in s.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) { sb.Append(ch); space = false; }
            else if (!space && sb.Length > 0) { sb.Append(' '); space = true; }
        }
        return sb.ToString().Trim();
    }
}
