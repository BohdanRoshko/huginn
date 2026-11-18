using System.Globalization;

namespace Munin;

/// <summary>
/// A work timer from my Kumospace status. One file per day, <c>Presence\yyyy-MM-dd.tsv</c>, with a line
/// <c>HH:mm:ss&lt;TAB&gt;state</c> on each change and again every <see cref="Heartbeat"/>. States: available,
/// focusing, away (Kumospace's own values), "not in space", "closed" (Kumospace not running), "unknown", "off"
/// (Munin quit). A state lasts until the next line, at most <see cref="MaxGap"/> (after that Munin was not
/// running, so the time is not counted), and never past midnight.
/// </summary>
internal sealed class PresenceLog
{
    public static readonly TimeSpan Heartbeat = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaxGap = TimeSpan.FromMinutes(6);

    private readonly string _dir;
    private string? _state;
    private DateTime _lastWrite;

    public PresenceLog(string dir) => _dir = dir;

    public void Observe(DateTime now, string state)
    {
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
        catch (Exception ex) { MuninLog.Write("presence: could not write", ex); }
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
}
