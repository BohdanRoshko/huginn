using System.Text;
using System.Text.Json;

namespace Huginn;

/// <summary>
/// Who Kumospace heard talking during one recording: the remote audio levels per person (from Daily, through
/// <see cref="KumoWatcher"/>), plus the rooms and people the meeting had. Saved next to the WAVs as
/// <c>{stamp}_speakers.json</c>; <see cref="MeetingProcessor"/> reads it to put a name on each "Them" line.
/// Times are milliseconds from the recording start (the start of both WAVs).
/// </summary>
internal sealed class SpeakerTimeline
{
    public long StartUnixMs { get; set; }
    public string? Me { get; set; }
    public List<string> Rooms { get; set; } = new();
    public List<string> People { get; set; } = new();
    /// <summary>[offset ms, name, level], one row per person per 200 ms sample with a level above the floor.</summary>
    public List<object[]> Remote { get; set; } = new();
    /// <summary>[offset ms, level]: my own level as Kumospace sends it.</summary>
    public List<object[]> Local { get; set; } = new();

    private readonly List<(long T, string Name, double Level)> _remote = new();

    public static string PathFor(string stamp) => Path.Combine(Paths.MeetingsDir(), $"{stamp}_speakers.json");

    public static SpeakerTimeline Begin(DateTime startedLocal) =>
        new() { StartUnixMs = new DateTimeOffset(startedLocal).ToUnixTimeMilliseconds() };

    /// <summary>Takes one read's samples and who was there.</summary>
    public void Add(KumoSnapshot snap)
    {
        if (!snap.InCall) return;
        if (!string.IsNullOrEmpty(snap.Me?.Name)) Me = snap.Me!.Name;
        if (snap.Zone.Length > 0 && !Rooms.Contains(snap.Zone)) Rooms.Add(snap.Zone);
        foreach (var p in snap.Company)
            if (p.Name.Length > 0 && !People.Contains(p.Name)) People.Add(p.Name);
        foreach (var (t, levels) in snap.Samples)
        {
            long off = t - StartUnixMs;
            if (off < -2000) continue; // buffered before this recording began
            foreach (var (sid, level) in levels)
            {
                var name = snap.Names.TryGetValue(sid, out var n) && n.Length > 0 ? n : sid[..Math.Min(8, sid.Length)];
                _remote.Add((off, name, level));
                Remote.Add(new object[] { off, name, level });
            }
        }
        foreach (var (t, level) in snap.Local)
        {
            long off = t - StartUnixMs;
            if (off >= -2000) Local.Add(new object[] { off, level });
        }
    }

    public bool HasRemoteSpeech(double talkLevel) => _remote.Any(r => r.Level >= talkLevel);

    public void Save(string path)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(this), new UTF8Encoding(false));
        }
        catch (Exception ex) { HuginnLog.Write("speakers: could not save the timeline", ex); }
    }

    public static SpeakerTimeline? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var r = doc.RootElement;
            var tl = new SpeakerTimeline
            {
                StartUnixMs = r.GetProperty("StartUnixMs").GetInt64(),
                Me = r.TryGetProperty("Me", out var me) && me.ValueKind == JsonValueKind.String ? me.GetString() : null,
                Rooms = r.GetProperty("Rooms").EnumerateArray().Select(x => x.GetString() ?? "").ToList(),
                People = r.GetProperty("People").EnumerateArray().Select(x => x.GetString() ?? "").ToList(),
            };
            foreach (var row in r.GetProperty("Remote").EnumerateArray())
                tl._remote.Add((row[0].GetInt64(), row[1].GetString() ?? "", row[2].GetDouble()));
            return tl;
        }
        catch (Exception ex)
        {
            HuginnLog.Write($"speakers: could not read {Path.GetFileName(path)}", ex);
            return null;
        }
    }

    /// <summary>The person talking during [start, end] of the recording: the one with the most speech energy in that
    /// span (±300 ms for timestamp drift). Two names when the second has at least 60% of the first's. Null when
    /// nobody Kumospace heard was talking then.</summary>
    public string? SpeakerFor(TimeSpan start, TimeSpan end, double talkLevel)
    {
        long a = (long)start.TotalMilliseconds - 300, b = (long)end.TotalMilliseconds + 300;
        var score = new Dictionary<string, double>();
        foreach (var (t, name, level) in _remote)
        {
            if (t < a || t > b || level < talkLevel) continue;
            score[name] = score.GetValueOrDefault(name) + level;
        }
        if (score.Count == 0) return null;
        var ranked = score.OrderByDescending(kv => kv.Value).ToList();
        if (ranked.Count > 1 && ranked[1].Value >= 0.6 * ranked[0].Value) return $"{ranked[0].Key} / {ranked[1].Key}";
        return ranked[0].Key;
    }

    /// <summary>A one-line summary for the log: rows, people, and the level spread (to tune the talk level).</summary>
    public string Describe()
    {
        if (_remote.Count == 0) return $"no remote levels; rooms [{string.Join(", ", Rooms)}], people [{string.Join(", ", People)}]";
        var lv = _remote.Select(r => r.Level).OrderBy(x => x).ToList();
        double P(double q) => lv[(int)Math.Min(lv.Count - 1, Math.Round(q * (lv.Count - 1)))];
        return $"{_remote.Count} level rows, rooms [{string.Join(", ", Rooms)}], people [{string.Join(", ", People)}], level p50 {P(0.5):F3} p90 {P(0.9):F3} max {lv[^1]:F3}";
    }
}
