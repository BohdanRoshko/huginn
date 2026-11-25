using System.Text;
using System.Text.RegularExpressions;

namespace Huginn;

/// <summary>
/// Names a finished meeting. This is the ONE place a meeting gets its title (Bohdan, 2026-11-25): the row, the
/// meeting window and the note Odin files all carry the title made here, and nothing takes a title back from
/// Odin. With enough speech and the local text model on disk, the model writes a short title from the transcript;
/// otherwise the name says where the meeting was ("Google Meet call", "Kumospace · Aquamarine Office"). A name the
/// user typed always wins.
/// The rule for a title, kept in code and not only in the prompt (<see cref="TopicOnly"/>): it names the topic
/// in about four or five words and never the kind of meeting ("standup", "weekly sync", "call").
/// </summary>
internal static class MeetingName
{
    private const int MinSpeechChars = 300; // less than this is a test or a hello: the place names it better
    private const int MaxSpeechChars = 3000;

    private static readonly Regex Speech = new(@"^\[\d{1,2}:\d{2}:\d{2}\][ \t]*([^:*\n][^:\n]*?):[ \t]?(.+)$", RegexOptions.Multiline);

    public static async Task<string> MakeAsync(string transcript, Cleaner? model)
    {
        var place = Place(transcript);
        var speech = SpeechOf(transcript);
        if (model != null && speech.Length >= MinSpeechChars && File.Exists(Paths.CleanupModelPath()))
        {
            try
            {
                var title = TopicOnly(await model.TitleAsync(speech).ConfigureAwait(false));
                if (title.Length > 0) return title; // a title that was nothing but the kind of meeting falls to the place
            }
            catch (Exception ex) { HuginnLog.Write("meeting name: the local model failed", ex); }
        }
        return place;
    }

    /// <summary>Where the meeting was, from the transcript header.</summary>
    internal static string Place(string transcript)
    {
        var source = Regex.Match(transcript, @"^# Source:\s*(.+?)\s*$", RegexOptions.Multiline);
        if (source.Success && source.Groups[1].Value != "Kumospace") return $"{source.Groups[1].Value} call";
        var room = Regex.Match(transcript, @"^# Kumospace:\s*(.+?)\s*·", RegexOptions.Multiline);
        if (room.Success && room.Groups[1].Value != "outside a room") return $"Kumospace · {room.Groups[1].Value}";
        return source.Success ? "Kumospace meeting" : "Meeting";
    }

    // A chat line from the meeting: "[00:12:34] * Andy typed: "…"".
    private static readonly Regex Typed = new(@"^\[\d{1,2}:\d{2}:\d{2}\][ \t]*\*[ \t]*(.+? typed: .+)$", RegexOptions.Multiline);

    /// <summary>The spoken lines as "Name: words", one per line, at most <paramref name="max"/> characters. With
    /// <paramref name="withChat"/>, what people typed in the meeting chat too (a summary wants it; a title does not).</summary>
    internal static string SpeechOf(string transcript, int max = MaxSpeechChars, bool withChat = false)
    {
        var sb = new StringBuilder();
        var text = transcript.Replace("\r\n", "\n");
        var lines = Speech.Matches(text).Select(m => (m.Index, Line: $"{m.Groups[1].Value.Trim()}: {m.Groups[2].Value.Trim()}"));
        if (withChat) lines = lines.Concat(Typed.Matches(text).Select(m => (m.Index, Line: m.Groups[1].Value.Trim()))).OrderBy(x => x.Index);
        foreach (var (_, line) in lines)
        {
            sb.Append(line).Append('\n');
            if (sb.Length >= max) break;
        }
        return sb.Length > max ? sb.ToString(0, max) : sb.ToString();
    }

    // The kind of a meeting, said in words that are never a topic.
    private static readonly HashSet<string> SureKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "standup", "stand-up", "daily", "weekly", "biweekly", "bi-weekly", "monthly", "check-in", "checkin", "catch-up",
        "catchup", "1:1", "1-1", "1-on-1", "one-on-one", "huddle", "retro", "retrospective", "scrum",
    };
    // Words that name a kind in "Weekly sync: …" and a topic in "Sync engine retry queue": they go only where the
    // place in the title shows they are the kind.
    private static readonly HashSet<string> MaybeKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "sync", "sync-up", "call", "meeting", "session", "review", "discussion", "update", "updates", "chat", "talk", "planning", "briefing",
    };
    private static readonly HashSet<string> Fillers = new(StringComparer.OrdinalIgnoreCase)
    {
        "team", "our", "the", "a", "an", "quick", "short", "brief", "regular", "internal", "morning", "afternoon", "status", "group",
    };
    private static readonly HashSet<string> Joiners = new(StringComparer.OrdinalIgnoreCase)
    {
        "on", "about", "for", "re", "regarding", "of", "over", "to", "discuss", "discussing", "with", "-", "–", "—",
    };
    private static readonly HashSet<string> TrailingKinds = new(StringComparer.OrdinalIgnoreCase) { "meeting", "call", "session" };
    private static readonly HashSet<string> LooseEnds = new(StringComparer.OrdinalIgnoreCase)
    {
        "and", "or", "for", "of", "the", "to", "in", "on", "with", "a", "an", "at", "by", "from", "into", "onto", "via", "that", "than", "before", "after", "about", "&", "-", "–", "—",
    };

    internal const int MaxWords = 6;  // "ideally 4-5 words": one more is let through, a seventh is cut
    internal const int MaxChars = 50;

    private static string Word(string token) => token.Trim(',', ';', '.', '(', ')', '"', '\'', ':');

    /// <summary>
    /// A title by the rule: the topic only, short. Takes any title (the model's answer, or one made by an older
    /// build on another computer) and returns it without the kind of meeting: no "Team daily standup: " in front,
    /// no "Weekly sync on …", no "… call" at the end; at most <see cref="MaxWords"/> words and
    /// <see cref="MaxChars"/> characters, cut on a word and not left hanging on "and" or "for". Empty when nothing
    /// but the kind was there.
    /// </summary>
    internal static string TopicOnly(string raw)
    {
        var s = Tidy(raw);

        // "Kind: topic" and "Kind - topic". The part in front goes when it names a kind for certain, or is made of
        // nothing but kind words; "Invoice sync: retry queue" keeps its front, which is a topic.
        for (int pass = 0; pass < 2; pass++)
        {
            var cut = Regex.Match(s, @":\s+|\s+[-–—]\s+");
            if (!cut.Success) break;
            var head = s[..cut.Index].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Word).Where(w => w.Length > 0).ToList();
            var tail = s[(cut.Index + cut.Length)..].Trim();
            bool kind = head.Any(SureKinds.Contains)
                        || (head.Any(MaybeKinds.Contains) && head.All(w => MaybeKinds.Contains(w) || Fillers.Contains(w)));
            if (!kind || !tail.Any(char.IsLetter)) break;
            s = tail;
        }

        var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

        // A kind in front: "Team daily standup release checklist", "Weekly sync on invoices", "Meeting about prices".
        int run = 0;
        while (run < words.Count && (SureKinds.Contains(Word(words[run])) || MaybeKinds.Contains(Word(words[run])) || Fillers.Contains(Word(words[run])))) run++;
        int lastSure = -1;
        for (int i = 0; i < run; i++) if (SureKinds.Contains(Word(words[i]))) lastSure = i;
        int drop = 0;
        if (lastSure >= 0)
        {
            drop = lastSure + 1;
            // "sync" right after "weekly" is the kind too, unless a topic follows it at once ("weekly sync engine fix").
            while (drop < words.Count && MaybeKinds.Contains(Word(words[drop])) && (drop + 1 == words.Count || Joiners.Contains(Word(words[drop + 1])))) drop++;
        }
        else if (run > 0 && run < words.Count && Joiners.Contains(Word(words[run])) && words.Take(run).Any(w => MaybeKinds.Contains(Word(w))))
            drop = run; // only "maybe" words, but a joiner after them settles it: "Meeting about …", "Quick call on …"
        if (drop > 0)
        {
            words.RemoveRange(0, drop);
            while (words.Count > 0 && (Joiners.Contains(Word(words[0])) || Word(words[0]).Length == 0)) words.RemoveAt(0);
            while (words.Count > 1 && Word(words[0]).ToLowerInvariant() is "the" or "a" or "an") words.RemoveAt(0);
        }

        // A kind at the end: "Pricing questions call", "Invoices weekly sync".
        while (words.Count > 0)
        {
            var last = Word(words[^1]);
            bool afterSure = words.Count > 1 && SureKinds.Contains(Word(words[^2]));
            if (SureKinds.Contains(last) || TrailingKinds.Contains(last) || (afterSure && MaybeKinds.Contains(last))) words.RemoveAt(words.Count - 1);
            else break;
        }

        if (words.Count > MaxWords) words.RemoveRange(MaxWords, words.Count - MaxWords);
        while (words.Count > 1 && string.Join(' ', words).Length > MaxChars) words.RemoveAt(words.Count - 1);
        while (words.Count > 0 && LooseEnds.Contains(Word(words[^1]))) words.RemoveAt(words.Count - 1);

        // What is left may still be nothing but the kind: "Team meeting" lost "meeting" and kept "Team".
        if (words.All(w => SureKinds.Contains(Word(w)) || MaybeKinds.Contains(Word(w)) || Fillers.Contains(Word(w)))) return "";
        s = string.Join(' ', words).Trim(' ', ',', ';', ':', '-', '–', '—');
        if (!s.Any(char.IsLetterOrDigit)) return "";
        return char.ToUpperInvariant(s[0]) + s[1..];
    }

    // One line, no quotes or "Title:", no full stop, at most 60 characters on a word boundary.
    internal static string Tidy(string raw)
    {
        var s = (raw ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        s = Regex.Replace(s, @"^(meeting\s+)?title\s*:\s*", "", RegexOptions.IgnoreCase).Trim().Trim('"', '\'', '“', '”', '*', '#', ' ').TrimEnd('.');
        if (!s.Contains(' ') && s.Contains('-')) s = s.Replace('-', ' '); // "Outbox-Table-Sync" from a slug-minded model
        if (s.Length > 60) s = s[..s.LastIndexOf(' ', 60)].TrimEnd(',', ' ', '-', '–');
        return s;
    }
}
