using System.Text;
using System.Text.RegularExpressions;

namespace Huginn;

/// <summary>
/// Writes a finished meeting's summary on this computer, with the same local model that names it, so everyone gets
/// one, Odin or not (Bohdan, 2026-11-20): an overview, the key points, the action items for the person who recorded it
/// (by the name set in the menu, else "you"), and the action items for the others. It goes next to the transcript as
/// <c>{stamp}_summary.md</c>; the meeting window shows it on the Summary tab, above the lines typed in the notes pad.
/// Where Odin filed the meeting, Odin's summary is shown instead.
/// </summary>
internal static class MeetingSummary
{
    private const int MinSpeechChars = 300;  // less is a test or a hello: nothing to summarise
    private const int ChunkChars = 24000;    // about 7k tokens of speech per pass, in the model's 12k window
    private const int MaxSpeechChars = 200_000;
    private const int AnswerTokens = 900;

    public static string PathFor(string stamp) => Path.Combine(Paths.MeetingsDir(), $"{stamp}_summary.md");

    /// <summary>The summary as markdown, or null when there is too little speech or the model gave nothing usable.
    /// A long meeting is read in parts, and the parts' notes are merged into one summary.</summary>
    public static async Task<string?> MakeAsync(string transcript, string? notes, string? userName, Cleaner model, CancellationToken ct = default)
    {
        var speech = MeetingName.SpeechOf(transcript, MaxSpeechChars, withChat: true);
        if (speech.Length < MinSpeechChars) return null;
        var name = string.IsNullOrWhiteSpace(userName) ? null : userName.Trim();
        var chunks = Split(speech, ChunkChars);
        Notes notesOf;
        if (chunks.Count == 1)
            notesOf = Parse(await model.AskLongAsync(ReadPrompt(name), Input(chunks[0], notes), AnswerTokens, ct).ConfigureAwait(false));
        else
        {
            // A long meeting: each part is read on its own, the typed notes with the first. A small model asked to merge
            // the parts' notes repeats them all, so the merge is done here: the model writes only the overview of the
            // whole meeting from the parts' overviews.
            var parts = new List<Notes>();
            for (int i = 0; i < chunks.Count; i++)
                parts.Add(Parse(await model.AskLongAsync(ReadPrompt(name), Input(chunks[i], i == 0 ? notes : null), AnswerTokens, ct).ConfigureAwait(false)));
            var overviews = string.Join("\n", parts.Select((p, i) => (p, i)).Where(x => x.p.Overview != null).Select(x => $"Part {x.i + 1}: {x.p.Overview}"));
            var whole = Parse(await model.AskLongAsync(OverviewPrompt, overviews, 200, ct).ConfigureAwait(false)).Overview;
            notesOf = Merge(parts, whole);
        }
        notesOf = Grounded(notesOf, speech);
        return notesOf.Overview == null && notesOf.Points.Count == 0 ? null : Render(notesOf, name);
    }

    private const string OverviewPrompt =
        "You are given short overviews of the parts of one long meeting, in order. Write one overview of the whole " +
        "meeting: two or three sentences on what it was about and what was decided. Use only what the overviews say. " +
        "Write ONLY one line, in this form:\nOVERVIEW: the overview.";

    private const int MaxPoints = 8, MaxTasks = 8;

    // The parts' points taken in turn, so every part of the meeting is in the key points; the tasks once each.
    internal static Notes Merge(IReadOnlyList<Notes> parts, string? overview)
    {
        var points = new List<string>();
        for (int round = 0; points.Count < MaxPoints && parts.Any(p => p.Points.Count > round); round++)
            foreach (var p in parts)
                if (p.Points.Count > round && points.Count < MaxPoints) Add(points, p.Points[round]);
        var mine = new List<string>();
        var others = new List<(string, string)>();
        foreach (var p in parts)
        {
            foreach (var m in p.Mine) Add(mine, m);
            foreach (var (who, what) in p.Others) AddOther(others, who, what);
        }
        return new Notes(overview ?? parts.Select(p => p.Overview).FirstOrDefault(o => o != null), points, mine, others);
    }

    // A small model invents: a name nobody in the transcript has is dropped from its task, and a "task" about the
    // meeting's own notes ("review the action items", "send a summary") is not one. At most 8 of each.
    internal static Notes Grounded(Notes n, string speech)
    {
        bool Said(string who) => who.Length > 0 && !who.Equals("Them", StringComparison.OrdinalIgnoreCase) &&
            !who.Equals("Someone", StringComparison.OrdinalIgnoreCase) &&
            Regex.IsMatch(speech, $@"\b{Regex.Escape(who.Split(' ')[0])}\b", RegexOptions.IgnoreCase);
        bool Real(string what) => !what.TrimEnd().EndsWith('?') && // a question someone asked is not a task
            !Regex.IsMatch(what, @"\b(action items?|meeting notes|meeting transcript|summary)\b", RegexOptions.IgnoreCase);
        return n with
        {
            Points = n.Points.Take(MaxPoints).ToList(),
            Mine = n.Mine.Where(Real).Take(MaxTasks).ToList(),
            Others = n.Others.Where(o => Real(o.What)).Take(MaxTasks)
                .Select(o => (Said(o.Who) || o.Who.Equals("Me", StringComparison.OrdinalIgnoreCase) ? o.Who : "", o.What)).ToList(),
        };
    }

    // ---- the prompts: one tagged line per fact, which a small model keeps to better than JSON or free markdown ----

    private const string Form =
        "Write ONLY lines in this form, one per line, and nothing else:\n" +
        "OVERVIEW: two or three sentences on what the meeting was about and what was decided.\n" +
        "POINT: one key point, decision or fact. Write 3 to 8 of these.\n" +
        "MINE: one thing the person who recorded the meeting has to do after it.\n" +
        "OTHER: Name: one thing someone else has to do after it.\n" +
        "Write a MINE or OTHER line only for a task someone clearly took on or was asked to do. If there is none, " +
        "write no MINE or OTHER lines. If someone says there are no action items, write none. Use the words the " +
        "people used. Never invent a name, a date, a decision or a task.";

    private static string Who(string? name) =>
        "Lines labelled \"Me\" are the person who recorded the meeting" +
        (name == null ? "." : $", whose name is {name}; a task given to {name} by name is theirs too.") +
        " \"Them\" is someone else whose name is not known: never give them a name. Any other label is that person's " +
        "name. A line with \"typed:\" after a name is what that person wrote in the meeting chat. For an OTHER line, use " +
        "only a name that appears in the transcript, else write \"Someone\".";

    private static string ReadPrompt(string? name) =>
        "You write meeting notes from a meeting transcript. " + Who(name) + "\n" + Form;

    private static string Input(string speech, string? notes)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(notes))
        {
            sb.AppendLine("NOTES THE PERSON WHO RECORDED TYPED DURING THE MEETING (a task named here is theirs or as named):");
            sb.AppendLine(notes.Trim()).AppendLine();
        }
        sb.AppendLine("TRANSCRIPT:");
        sb.Append(speech.Trim());
        return sb.ToString();
    }

    // On line boundaries, so no line is cut in two.
    internal static List<string> Split(string speech, int size)
    {
        var chunks = new List<string>();
        var sb = new StringBuilder();
        foreach (var line in speech.Split('\n'))
        {
            if (sb.Length > 0 && sb.Length + line.Length + 1 > size) { chunks.Add(sb.ToString()); sb.Clear(); }
            sb.Append(line).Append('\n');
        }
        if (sb.ToString().Trim().Length > 0) chunks.Add(sb.ToString());
        return chunks;
    }

    // ---- the answer back into a summary ----

    internal sealed record Notes(string? Overview, List<string> Points, List<string> Mine, List<(string Who, string What)> Others);

    private static readonly Regex Tag = new(@"^[\s\-*•]*\**(OVERVIEW|POINT|MINE|OTHER)\**\s*:\s*(.+?)\s*$", RegexOptions.IgnoreCase);

    internal static Notes Parse(string answer)
    {
        string? overview = null;
        var points = new List<string>();
        var mine = new List<string>();
        var others = new List<(string, string)>();
        foreach (var raw in (answer ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var m = Tag.Match(raw);
            if (!m.Success) continue;
            var text = m.Groups[2].Value.Trim().Trim('*').Trim();
            if (text.Length == 0 || Regex.IsMatch(text, @"^(none|n/?a|no action items?)\.?$", RegexOptions.IgnoreCase)) continue;
            switch (m.Groups[1].Value.ToUpperInvariant())
            {
                case "OVERVIEW": overview ??= text; break; // the first one: a second is the model repeating itself
                case "POINT": if (text.Split(' ').Length >= 3) Add(points, text); break; // shorter is a line cut off at the token limit
                case "MINE": Add(mine, text); break;
                case "OTHER":
                    var who = Regex.Match(text, @"^([^:]{1,40}):\s*(.+)$");
                    var (owner, task) = who.Success ? (who.Groups[1].Value.Trim(), who.Groups[2].Value.Trim()) : ("", text);
                    if (task.Split(' ').Length >= 3) AddOther(others, owner, task); // "OTHER: Someone" alone is no task
                    break;
            }
        }
        return new Notes(overview, points, mine, others);
    }

    private static void Add(List<string> list, string text)
    {
        if (!list.Any(x => x.Equals(text, StringComparison.OrdinalIgnoreCase))) list.Add(text);
    }

    private static void AddOther(List<(string, string)> list, string who, string what)
    {
        if (!list.Any(x => x.Item2.Equals(what, StringComparison.OrdinalIgnoreCase))) list.Add((who, what));
    }

    /// <summary>The markdown the Summary tab shows. The user's own section is always there, so "No action items for
    /// you" (or for the name set in the menu) says it was looked for.</summary>
    internal static string Render(Notes n, string? name)
    {
        // A task the model listed for "Me" (or the user's name) under the others is the user's own.
        bool IsUser(string who) => who.Equals("Me", StringComparison.OrdinalIgnoreCase) ||
            (name != null && (who.Equals(name, StringComparison.OrdinalIgnoreCase) || who.Equals(name.Split(' ')[0], StringComparison.OrdinalIgnoreCase)));
        var mine = n.Mine.Concat(n.Others.Where(o => IsUser(o.Who)).Select(o => o.What)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // A task listed both as the user's and as someone else's stays with the user only.
        n = n with { Mine = mine, Others = n.Others.Where(o => !IsUser(o.Who) && !mine.Contains(o.What, StringComparer.OrdinalIgnoreCase)).ToList() };
        var sb = new StringBuilder();
        if (n.Overview != null) sb.AppendLine("## Overview").AppendLine().AppendLine(n.Overview).AppendLine();
        if (n.Points.Count > 0)
        {
            sb.AppendLine("## Key points").AppendLine();
            foreach (var p in n.Points) sb.AppendLine($"- {p}");
            sb.AppendLine();
        }
        sb.AppendLine(name == null ? "## Your action items" : $"## Action items for {name}").AppendLine();
        if (n.Mine.Count == 0) sb.AppendLine(name == null ? "No action items for you." : $"No action items for {name}.");
        foreach (var m in n.Mine) sb.AppendLine($"- {m}");
        sb.AppendLine();
        if (n.Others.Count > 0)
        {
            sb.AppendLine("## Action items for others").AppendLine();
            foreach (var (who, what) in n.Others) sb.AppendLine(who.Length == 0 ? $"- {what}" : $"- **{who}**: {what}");
            sb.AppendLine();
        }
        return sb.ToString().Replace("\r\n", "\n").TrimEnd() + "\n"; // the same file on Windows and the Mac
    }
}
