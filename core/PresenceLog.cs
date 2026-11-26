using System.Globalization;

namespace Huginn;

/// <summary>
/// A work timer from my Kumospace status. One file per day, <c>Presence\yyyy-MM-dd.tsv</c>, with a line
/// <c>HH:mm:ss&lt;TAB&gt;state</c> on each change and again every <see cref="Heartbeat"/>. States: available,
/// focusing, away (Kumospace's own values), "not in space", "closed" (Kumospace not running), "unknown", "off"
/// (Huginn quit). A state lasts until the next line, at most <see cref="MaxGap"/> (after that Huginn was not
/// running, so the time is not counted), and never past midnight.
/// The timer is kept only where an Odin is set up (<see cref="HuginnEngine.HasOdin"/>): for anyone else nothing
/// is written and no menu shows hours, so nobody gets a log of their hours they did not ask for.
/// </summary>
internal sealed class PresenceLog
{
    public static readonly TimeSpan Heartbeat = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaxGap = TimeSpan.FromMinutes(6);

    private readonly string _dir;
    private readonly bool _enabled;
    private string? _state;
    private DateTime _lastWrite;

    /// <param name="enabled">False: <see cref="Observe"/> writes nothing, not even the folder.</param>
    public PresenceLog(string dir, bool enabled = true) { _dir = dir; _enabled = enabled; }

    public void Observe(DateTime now, string state)
    {
        if (!_enabled) return;
        bool newDay = _state != null && now.Date != _lastWrite.Date;
        if (newDay && now - _lastWrite <= MaxGap) Append(now.Date, _state!); // carry the state over midnight
        if (state == _state && !newDay && now - _lastWrite < Heartbeat) return;
        Append(now, state);
        _state = state;
        _lastWrite = now;
    }

    private void Append(DateTime t, string state)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            File.AppendAllText(Path.Combine(_dir, $"{t:yyyy-MM-dd}.tsv"), $"{t:HH:mm:ss}\t{state}\r\n");
        }
        catch (Exception ex) { HuginnLog.Write("presence: could not write", ex); }
    }

    /// <summary>Time per state on one day. <paramref name="now"/> ends the last open state of today.</summary>
    public static Dictionary<string, TimeSpan> Totals(string dir, DateTime day, DateTime now)
    {
        var totals = new Dictionary<string, TimeSpan>();
        var path = Path.Combine(dir, $"{day:yyyy-MM-dd}.tsv");
        if (!File.Exists(path)) return totals;
        var rows = new List<(DateTime T, string State)>();
        string[] lines;
        try { lines = File.ReadAllLines(path); } catch { return totals; }
        foreach (var line in lines)
        {
            var parts = line.Split('\t');
            if (parts.Length < 2 || !TimeSpan.TryParseExact(parts[0], @"hh\:mm\:ss", CultureInfo.InvariantCulture, out var tod)) continue;
            rows.Add((day.Date + tod, parts[1].Trim()));
        }
        rows.Sort((a, b) => a.T.CompareTo(b.T));
        var dayEnd = day.Date.AddDays(1);
        for (int i = 0; i < rows.Count; i++)
        {
            var start = rows[i].T;
            var end = i + 1 < rows.Count ? rows[i + 1].T : (now < dayEnd ? now : dayEnd);
            if (end > start + MaxGap) end = start + MaxGap;
            if (end > dayEnd) end = dayEnd;
            if (end <= start) continue;
            totals[rows[i].State] = totals.GetValueOrDefault(rows[i].State) + (end - start);
        }
        return totals;
    }

    public static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : $"{t.Minutes}m";

    /// <summary>"5h 12m available · 40m focusing", or "nothing yet".</summary>
    public static string Describe(Dictionary<string, TimeSpan> totals, bool withAway = false)
    {
        var parts = new List<string>();
        foreach (var s in withAway ? new[] { "available", "focusing", "away" } : new[] { "available", "focusing" })
            if (totals.TryGetValue(s, out var t) && t >= TimeSpan.FromMinutes(1)) parts.Add($"{Format(t)} {s}");
        return parts.Count == 0 ? "nothing yet" : string.Join(" · ", parts);
    }

    /// <summary>The Monday of the week <paramref name="now"/> is in.</summary>
    private static DateTime MondayOf(DateTime now) => now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7));

    /// <summary>Time per state over the week of <paramref name="now"/>, Monday up to now: the days of
    /// <see cref="Week"/> added up. Today's open state counts up to <paramref name="now"/>, as in the day's line.</summary>
    public static Dictionary<string, TimeSpan> WeekTotals(string dir, DateTime now)
    {
        var sum = new Dictionary<string, TimeSpan>();
        for (var day = MondayOf(now); day <= now.Date; day = day.AddDays(1))
            foreach (var (state, t) in Totals(dir, day, now))
                sum[state] = sum.GetValueOrDefault(state) + t;
        return sum;
    }

    /// <summary>The menu line under the days: "This week:  23h 40m available · 3h 10m focusing · 4h 05m away".</summary>
    public static string WeekTotalLine(string dir, DateTime now) =>
        "This week:  " + Describe(WeekTotals(dir, now), withAway: true);

    /// <summary>The week of <paramref name="now"/>, Monday to Sunday like a calendar, one menu line per day: each day
    /// by its date, today as "Today", future and empty past days "—", with away time too.</summary>
    public static IReadOnlyList<string> Week(string dir, DateTime now)
    {
        var today = now.Date;
        var monday = MondayOf(now);
        var list = new List<string>();
        for (int i = 0; i < 7; i++)
        {
            var day = monday.AddDays(i);
            var label = day == today ? "Today" : day.ToString("ddd d MMM", CultureInfo.InvariantCulture);
            var text = day > today ? "—" : Describe(Totals(dir, day, now), withAway: true);
            if (day < today && text == "nothing yet") text = "—";
            list.Add($"{label}:  {text}");
        }
        return list;
    }
}
