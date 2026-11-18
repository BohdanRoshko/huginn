using System.Text;
using System.Text.RegularExpressions;

namespace Huginn;

/// <summary>
/// De-echoes a 2-track meeting transcript ("Me" = microphone, "Them" = system audio / the remote party). When the
/// user is on SPEAKERS, the mic re-captures the remote party's voice, so most "Them" lines reappear a beat later
/// as near-identical "Me" lines — the remote's words wrongly attributed to the user, and every utterance doubled.
///
/// The fix rests on a hard invariant: Windows loopback capture (the "Them" track) takes the RENDER endpoint, so it
/// physically cannot contain the local microphone. Therefore a "Them" line is always genuinely the remote party,
/// and any "Me" line that closely matches a near-simultaneous "Them" line is that line bleeding back through the
/// speakers into the mic — an echo. We drop those "Me" echoes and keep the clean "Them" original plus the user's
/// genuine (unmatched) "Me" lines. Deterministic, text-only (no audio), conservative (when unsure it KEEPS the
/// line). A transcript not in this "[hh:mm:ss] Me|Them: …" shape is returned unchanged.
/// </summary>
internal static class TranscriptReconciler
{
    public sealed record Result(string Text, int RemovedEchoes, int MeLines, int ThemLines);

    private static readonly Regex LineRe = new(
        @"^\s*\[(\d{1,2}):(\d{2}):(\d{2})\]\s*(Me|Them)\s*:\s*(.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <param name="windowSeconds">How far apart a "Me" echo and its "Them" original may be (ASR chunk/timestamp
    /// drift, not acoustic delay). ~10s covers a long remote utterance the mic re-chunks a few seconds later.</param>
    /// <param name="windowSeconds">How far apart a "Me" echo and its "Them" original may be. This is ASR chunk /
    /// timestamp drift, not acoustic delay: the two tracks split the same speech at different points, so an echo
    /// can land ~15s after the line it came from.</param>
    /// <param name="containment">For a substantive (≥4-word) "Me" line: drop it if this fraction of its distinct
    /// words appear anywhere in the nearby "Them" speech (see <see cref="DeEcho"/> remarks on the union).</param>
    /// <param name="shortJaccard">For a short "Me" line (&lt;4 words): require a near-exact (Jaccard) match against
    /// a SINGLE "Them" line, so a genuine one-word question like "AR?" isn't discarded for sharing a word.</param>
    public static Result DeEcho(string transcript, double windowSeconds = 20.0, double containment = 0.70, double shortJaccard = 0.80)
    {
        if (string.IsNullOrWhiteSpace(transcript)) return new Result(transcript ?? "", 0, 0, 0);
        var lines = transcript.Replace("\r\n", "\n").Split('\n');

        // Parse in place; header/blank/unparseable lines are kept verbatim (time=null, so never removed).
        var times = new double?[lines.Length];
        var speakers = new string?[lines.Length];
        var tokens = new string[lines.Length][];
        var them = new List<(double t, string[] tok)>();
        int meCount = 0, themCount = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            var m = LineRe.Match(lines[i]);
            if (!m.Success) { tokens[i] = System.Array.Empty<string>(); continue; }
            var secs = int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 + int.Parse(m.Groups[3].Value);
            var spk = m.Groups[4].Value.Equals("me", System.StringComparison.OrdinalIgnoreCase) ? "Me" : "Them";
            var tok = Tokenize(m.Groups[5].Value);
            times[i] = secs; speakers[i] = spk; tokens[i] = tok;
            if (spk == "Me") meCount++;
            else { themCount++; them.Add((secs, tok)); }
        }

        var remove = new bool[lines.Length];
        int removed = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            if (speakers[i] != "Me" || times[i] is not double t || tokens[i].Length == 0) continue;
            var me = new HashSet<string>(tokens[i]);

            // SHORT line (<4 distinct words): compare against ONE "Them" line at a time. A union would swallow a
            // genuine "AR?" / "Okay?" the moment any nearby line happened to contain that word.
            if (me.Count < ShortLineWords)
            {
                foreach (var (tt, tok) in them)
                {
                    var lag = t - tt;                              // > 0 when they spoke first
                    if (System.Math.Abs(lag) > windowSeconds) continue;
                    if (ShortIsEcho(me, tok, lag, shortJaccard)) { remove[i] = true; removed++; break; }
                }
                continue;
            }

            // SUBSTANTIVE line: compare against the UNION of every "Them" line in the window. The two tracks chunk
            // the same speech differently — one "Me" line can fuse two "Them" lines, or be half of one — so
            // matching a single line misses those. The union asks the real question: "was all of this said by them,
            // right around now?"
            var nearby = new HashSet<string>();
            foreach (var (tt, tok) in them)
                if (System.Math.Abs(tt - t) <= windowSeconds)
                    foreach (var w in tok) nearby.Add(w);
            if (nearby.Count > 0 && LongIsEcho(me, nearby, containment)) { remove[i] = true; removed++; }
        }

        if (removed == 0) return new Result(transcript, 0, meCount, themCount);

        var sb = new StringBuilder(transcript.Length);
        for (int i = 0; i < lines.Length; i++)
            if (!remove[i]) sb.Append(lines[i]).Append('\n');
        return new Result(sb.ToString().TrimEnd() + "\n", removed, meCount, themCount);
    }

    private const int ShortLineWords = 4;             // fewer distinct words → the conservative single-line rule
    private const int ContentWordLen = 5;             // a word this long is "content", not filler ("the","that","just")
    private const double VerbatimContainment = 0.90;  // almost every word was theirs → echo, no further questions
    private const double ContentContainment = 0.60;   // else: this share of its distinctive words must be theirs
    private const double NoContentContainment = 0.85; // a line with no distinctive words needs a near-total match
    private const double FragmentContainment = 0.60;  // short line whose words are theirs, and they spoke first

    // Short line vs ONE "Them" line. <paramref name="lag"/> is (me − them) in seconds: positive when they spoke first.
    private static bool ShortIsEcho(HashSet<string> me, string[] themTok, double lag, double shortJaccard)
    {
        var them = new HashSet<string>(themTok);
        if (them.Count == 0) return false;
        int inter = 0;
        foreach (var w in me) if (them.Contains(w)) inter++;

        // (a) Echoed backchannel — the two lines are near-identical ("Okay." ↔ "Okay."). Direction-agnostic,
        //     since both tracks chunk a one-word utterance at essentially the same instant.
        int union = me.Count + them.Count - inter;
        if (union > 0 && (double)inter / union >= shortJaccard) return true;

        // (b) FRAGMENT echo — the mic re-chunks their sentence into pieces, so a few of their words stand alone as
        //     a "Me" line ("The first thing." / "Bonus." / "We know."). A fragment is a SUBSET of their line, never
        //     a near-equal, so (a) can't see it. Gate it on THEY-SPOKE-FIRST: an echo cannot precede its source.
        //     That single condition is what keeps a genuine short question alive — "AR?" was asked one second
        //     BEFORE they answered "Consoscible AR", so it is never a fragment of that answer.
        return lag >= 0 && (double)inter / me.Count >= FragmentContainment;
    }

    // Substantive line vs the UNION of nearby "Them" speech.
    //   Gate 1 (containment) — most of the line's words were said by them nearby. Below this it's never an echo.
    //   Near-verbatim bypass — if almost EVERY word came from them, it's their sentence, full stop. This matters
    //     because the mic transcribes the echo imperfectly, and the words it mangles are precisely the distinctive
    //     ones ("Redis"→"Reddits", "SQL"→"sequel", "attention"→"intention"), which would fail gate 2 below.
    //   Gate 2 (CONTENT containment) — for the middle band, require its distinctive (long) words to be theirs too.
    //     Without this a genuine interjection built from common words ("Right, but the impact of moving that to
    //     code is not the same") gets swallowed, since a large union covers "but/the/of/that/to/not" by itself.
    //     With it the line survives: none of {right, impact, moving, distinct, between} appear in their speech.
    //   No content words at all (e.g. "We just had two.") → gate 2 can't run, so gate 1 must be near-total.
    private static bool LongIsEcho(HashSet<string> me, HashSet<string> nearbyThem, double containment)
    {
        int inter = 0;
        foreach (var w in me) if (nearbyThem.Contains(w)) inter++;
        var overall = (double)inter / me.Count;
        if (overall < containment) return false;
        if (overall >= VerbatimContainment) return true;

        int contentTotal = 0, contentHit = 0;
        foreach (var w in me)
            if (w.Length >= ContentWordLen) { contentTotal++; if (nearbyThem.Contains(w)) contentHit++; }
        if (contentTotal == 0) return overall >= NoContentContainment;
        return (double)contentHit / contentTotal >= ContentContainment;
    }

    // Lowercase word list, punctuation stripped — "Consciable AR." → ["consciable","ar"]. Single characters are
    // dropped: "it's" splits to "it"+"s", and those stray fragments only add noise to the overlap counts.
    private static string[] Tokenize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        return sb.ToString().Split(' ', System.StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 1).ToArray();
    }
}
