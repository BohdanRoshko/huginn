namespace Huginn;

/// <summary>An app that holds the microphone right now. <see cref="Id"/> is the exe name (Windows) or bundle id (Mac),
/// lower case; <see cref="Titles"/> are its window titles where the OS gives them (to tell a Meet tab in a browser).</summary>
internal sealed record MicUser(string Id, IReadOnlyList<string> Titles);

internal enum CallEventKind { Started, Ended }

internal sealed record CallEvent(CallEventKind Kind, string App);

/// <summary>
/// Turns "which apps hold the mic" into call starts and ends for the meeting apps Huginn knows: Google Meet (and other
/// calls in a browser), Microsoft Teams, Slack huddles and Zoom. Kumospace has its own detector and is ignored here.
/// A call starts once its app has held the mic for <see cref="StartSeconds"/> and ends once it has let go for
/// <see cref="EndSeconds"/>. Pure logic: times are Unix ms, so tests drive it.
/// </summary>
internal sealed class CallDetector
{
    public double StartSeconds { get; init; } = 3;
    public double EndSeconds { get; init; } = 5;

    private static readonly string[] Browsers =
    {
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "arc",
        "com.google.chrome", "com.microsoft.edgemac", "org.mozilla.firefox", "com.brave.browser", "com.apple.safari",
        "company.thebrowser.browser", "com.operasoftware.opera", "com.vivaldi.vivaldi",
    };

    private readonly Dictionary<string, long> _seen = new();     // app → first ms seen holding the mic (this run)
    private readonly Dictionary<string, long> _lastSeen = new(); // app → last ms seen
    private readonly HashSet<string> _active = new();             // apps whose call has started

    /// <summary>The meeting app a mic user is, or null when it is not one (games, Discord, Huginn, Kumospace…).</summary>
    public static string? Classify(MicUser user)
    {
        var id = user.Id.ToLowerInvariant();
        if (id.EndsWith(".exe")) id = id[..^4]; // only Windows names: a Mac bundle id has dots of its own
        if (id.Contains("teams")) return "Microsoft Teams";
        if (id.Contains("slack") || id == "com.tinyspeck.slackmacgap") return "Slack huddle";
        if (id is "zoom" or "us.zoom.xos" || id.StartsWith("zoom")) return "Zoom";
        if (Browsers.Any(b => id == b || id.StartsWith(b + "_") || id.StartsWith(b + ".")))
        {
            // Any open window can name the call, so look for each app across all of them, most specific first.
            bool Any(Func<string, bool> match) => user.Titles.Any(match);
            if (Any(t => t.Contains("Google Meet", StringComparison.OrdinalIgnoreCase) || t.StartsWith("Meet - ", StringComparison.OrdinalIgnoreCase)
                    || t.Contains(" Meet - ", StringComparison.OrdinalIgnoreCase))) return "Google Meet";
            if (Any(t => t.Contains("Microsoft Teams", StringComparison.OrdinalIgnoreCase))) return "Microsoft Teams";
            if (Any(t => t.Contains("Zoom", StringComparison.OrdinalIgnoreCase))) return "Zoom";
            if (Any(t => t.Contains("Slack", StringComparison.OrdinalIgnoreCase))) return "Slack huddle";
            return "Browser call";
        }
        return null;
    }

    /// <summary>The calls going on now (started and not yet ended).</summary>
    public IReadOnlyCollection<string> Active => _active;

    /// <summary>Feeds one look at the mic users; returns the calls that started or ended.</summary>
    public IReadOnlyList<CallEvent> Tick(long nowMs, IEnumerable<MicUser> users)
    {
        var events = new List<CallEvent>();
        var present = new HashSet<string>();
        foreach (var u in users)
        {
            var app = Classify(u);
            if (app == null) continue;
            present.Add(app);
            _seen.TryAdd(app, nowMs);
            _lastSeen[app] = nowMs;
        }
        foreach (var app in _seen.Keys.ToList())
        {
            if (present.Contains(app))
            {
                if (!_active.Contains(app) && nowMs - _seen[app] >= StartSeconds * 1000)
                {
                    _active.Add(app);
                    events.Add(new CallEvent(CallEventKind.Started, app));
                }
            }
            else if (nowMs - _lastSeen[app] >= EndSeconds * 1000)
            {
                _seen.Remove(app);
                _lastSeen.Remove(app);
                if (_active.Remove(app)) events.Add(new CallEvent(CallEventKind.Ended, app));
            }
        }
        return events;
    }
}
