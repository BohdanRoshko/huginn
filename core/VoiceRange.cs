namespace Huginn;

/// <summary>
/// Who came into hearing and who went out of it during a recording, from the once-a-second reads of the meeting
/// app. "In range" is the set of people whose audio reaches this computer (in Kumospace: the people near me or in
/// my room), not the people who are merely on the same floor.
/// The rule, so the transcript is not filled with noise:
/// - whoever is in range at the first read was there when the recording began, and did not "enter";
/// - a change counts once it has held for <see cref="Hold"/>: someone who drops out and is back within that time
///   writes nothing, and neither does someone who only walks past;
/// - the line carries the time the change began, not the time it was believed;
/// - a change still waiting when the recording stops is never written, so the people who are there at the end
///   did not "leave".
/// </summary>
internal sealed class VoiceRange
{
    public static readonly TimeSpan Hold = TimeSpan.FromSeconds(10);

    public const string Entered = "enter";
    public const string Left = "leave";

    private Dictionary<string, string>? _in;                                      // id -> name: believed in range
    private readonly Dictionary<string, (long Since, string Name)> _coming = new(); // seen, not yet believed
    private readonly Dictionary<string, long> _going = new();                       // missing, not yet believed gone

    /// <summary>One read: the people in range at <paramref name="atMs"/> (milliseconds from the recording start).
    /// Returns the changes this read settles, each with the time it began.</summary>
    public IReadOnlyList<(long At, string Kind, string Who)> Read(long atMs, IEnumerable<KumoPerson> inRange)
    {
        var now = new Dictionary<string, string>();
        foreach (var p in inRange)
            if (p.Id.Length > 0 && p.Name.Trim().Length > 0) now[p.Id] = p.Name.Trim();
        if (_in == null) { _in = now; return Array.Empty<(long, string, string)>(); }

        var settled = new List<(long At, string Kind, string Who)>();
        long hold = (long)Hold.TotalMilliseconds;

        foreach (var (id, name) in now)
        {
            _going.Remove(id); // back before the leaving was believed: it never left
            if (_in.ContainsKey(id)) { _in[id] = name; continue; }
            if (!_coming.TryGetValue(id, out var c)) _coming[id] = (atMs, name);
            else if (atMs - c.Since >= hold)
            {
                _coming.Remove(id);
                _in[id] = name;
                settled.Add((c.Since, Entered, name));
            }
        }
        foreach (var id in _coming.Keys.Where(id => !now.ContainsKey(id)).ToList()) _coming.Remove(id); // walked past

        foreach (var (id, name) in _in.ToList())
        {
            if (now.ContainsKey(id)) continue;
            if (!_going.TryGetValue(id, out var since)) _going[id] = atMs;
            else if (atMs - since >= hold)
            {
                _going.Remove(id);
                _in.Remove(id);
                settled.Add((since, Left, name));
            }
        }
        return settled.OrderBy(s => s.At).ToList();
    }
}
