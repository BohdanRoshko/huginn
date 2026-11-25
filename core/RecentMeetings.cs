using System.Text.Json;

namespace Huginn;

internal enum JobTone { Busy, Good, Warn, Bad, Muted }

/// <summary>What a chip on a meeting's row says: how many to-dos Odin made, the type Odin gave the meeting, or anything else.</summary>
internal enum ChipKind { Other, ToDo, Type }

/// <summary>One chip of a meeting's row, as both recorder panels draw it.</summary>
internal readonly record struct StatusChip(string Text, ChipKind Kind);

/// <summary>One row of the recorder's recent list.</summary>
internal sealed class RecentMeeting
{
    public int Id { get; init; }
    public string Stamp { get; init; } = "";
    public DateTime Started { get; init; }
    /// <summary>The meeting's title: made by <see cref="MeetingName"/>, or the user's own. Never Odin's.</summary>
    public string? Title { get; set; }
    public string Status { get; set; } = "Transcribing…";
    public JobTone Tone { get; set; } = JobTone.Busy;
    /// <summary>True only while transcribing / filing: the row shows a cancel button.</summary>
    public bool Cancelable { get; set; } = true;
    /// <summary>The transcript file once written (null while transcribing). It moves on rename.</summary>
    public string? Transcript { get; set; }
    /// <summary>The user named it; a title made later must not overwrite that.</summary>
    public bool Renamed { get; set; }
    /// <summary>The Odin note this meeting was filed as (its slug), once filed. Only Bohdan has Odin.</summary>
    public string? OdinSlug { get; set; }

    /// <summary>A finished row with a transcript gets the copy-path button in place of cancel.</summary>
    public bool CanCopy => !Cancelable && Transcript != null;
}

/// <summary>
/// The recorder's list of the most recent recordings, newest first, kept in Meetings\recent.json so it survives a
/// restart. Both apps render it; only the engine and the rename box change it. Thread-safe: changes take a lock,
/// <see cref="Entries"/> returns a snapshot, and <see cref="Changed"/> fires on the calling thread.
/// </summary>
internal sealed class RecentMeetings
{
    public const int DisplayMax = 8;

    private readonly object _lock = new();
    private readonly List<RecentMeeting> _entries = new();
    private readonly string _file;
    private int _nextId;
    // recent.json is written off the calling thread. A monotonic seq lets the newest snapshot win, so
    // out-of-order background writes cannot persist stale state.
    private long _saveSeq;
    private string? _pendingJson;
    private readonly object _saveLock = new();

    public RecentMeetings(string? file = null)
    {
        _file = file ?? Path.Combine(Paths.MeetingsDir(), "recent.json");
        Load();
    }

    public event Action? Changed;

    public IReadOnlyList<RecentMeeting> Entries { get { lock (_lock) return _entries.ToList(); } }

    public RecentMeeting? Find(int id) { lock (_lock) return _entries.Find(x => x.Id == id); }

    /// <summary>Adds a row on top for a finished capture and returns its id. It starts as a cancelable
    /// "Transcribing…". Older rows beyond <see cref="DisplayMax"/> drop off.</summary>
    public int Add(string stamp, DateTime started)
    {
        int id;
        lock (_lock)
        {
            id = ++_nextId;
            _entries.Insert(0, new RecentMeeting { Id = id, Stamp = stamp, Started = started });
            Trim();
        }
        SaveAndNotify();
        return id;
    }

    /// <summary>Takes a row off the list: a recording that was cancelled leaves none.</summary>
    public void Remove(int id)
    {
        lock (_lock)
        {
            if (_entries.RemoveAll(x => x.Id == id) == 0) return;
        }
        SaveAndNotify();
    }

    public void SetStatus(int id, string status, JobTone tone, bool cancelable) =>
        Update(id, j => { j.Status = status; j.Tone = tone; j.Cancelable = cancelable; });

    /// <summary>Names the row. A name the user typed wins.</summary>
    public void SetTitle(int id, string? title) => Update(id, j =>
    {
        if (j.Renamed || string.IsNullOrWhiteSpace(title)) return;
        j.Title = title.Trim();
    });

    /// <summary>Records where the transcript landed. A name the user gave while it was in flight applies now.</summary>
    public void SetOdinSlug(int id, string? slug) => Update(id, j => { if (!string.IsNullOrWhiteSpace(slug)) j.OdinSlug = slug; });

    public void SetTranscript(int id, string path) => Update(id, j =>
        j.Transcript = j.Renamed ? MeetingProcessor.RenameTranscript(path, j.Stamp, j.Title) : path);

    /// <summary>The user named the row: the transcript file takes the name too.</summary>
    public void Rename(int id, string name) => Update(id, j =>
    {
        var text = name.Trim();
        if (text.Length == 0 || text == j.Title) return;
        j.Title = text;
        j.Renamed = true;
        var current = ResolveTranscript(j);
        if (File.Exists(current)) j.Transcript = MeetingProcessor.RenameTranscript(current, j.Stamp, text);
    });

    /// <summary>The row's transcript path, or where it will be written.</summary>
    public static string ResolveTranscript(RecentMeeting e) => e.Transcript ?? MeetingProcessor.TranscriptPathFor(e.Stamp, null);

    /// <summary>The meeting types Odin gives, and the one short word each shows as on a chip. This is the one place
    /// the wording lives: Odin's stored values are not renamed, and a row saved long ago shows the new word too.
    /// A type that is not listed shows as Odin wrote it.</summary>
    internal static readonly (string Odin, string Shown)[] TypeWords =
    {
        ("client", "client"),
        ("internal-dev", "internal"),
        ("partnership", "partner"),
        ("other", "other"),
    };

    /// <summary>A row's status as the recorder panel's chips: "✓ internal-dev · 1 task(s)" → [1 to-do] [internal], the
    /// to-do count first and only when there is an Odin to make them (Bohdan, 2026-11-20; "to-do" and the short type
    /// words 2026-11-25). "Odin offline", from rows saved before 2026-11-20, never shows: "Saved · Odin offline" →
    /// [Saved]. Neither does "this computer", which rows carried for two days in 2026-11: only a meeting another
    /// computer transcribed says where ("other computer").</summary>
    public static IReadOnlyList<StatusChip> Chips(string status, bool withTasks)
    {
        var chips = new List<StatusChip>();
        StatusChip? todo = null;
        bool filed = status.TrimStart().StartsWith('✓'); // a row Odin filed: its first word is the meeting's type
        int at = 0;
        foreach (var raw in status.Split('·', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            bool first = at++ == 0;
            var part = raw.TrimStart('✓', ' ');
            var tasks = System.Text.RegularExpressions.Regex.Match(part, @"^(\d+) task\(s\)$");
            if (tasks.Success) { todo = new StatusChip($"{tasks.Groups[1].Value} to-do", ChipKind.ToDo); continue; }
            if (part.Length == 0 || part.Equals("Odin offline", StringComparison.OrdinalIgnoreCase)
                || part.Equals("this computer", StringComparison.OrdinalIgnoreCase)) continue;
            if (filed && first)
            {
                var word = TypeWords.FirstOrDefault(t => t.Odin.Equals(part, StringComparison.OrdinalIgnoreCase)).Shown ?? part;
                chips.Add(new StatusChip(word, ChipKind.Type));
            }
            else chips.Add(new StatusChip(part, ChipKind.Other));
        }
        if (todo != null && withTasks) chips.Insert(0, todo.Value);
        return chips;
    }

    /// <summary>The chips as plain words, for a place that writes them in a line (the Mac menu's Recent list).</summary>
    public static IEnumerable<string> StatusChips(string status, bool withTasks) => Chips(status, withTasks).Select(c => c.Text);

    /// <summary>The texts a chip must be wide enough for. The to-do count and the meeting type each keep ONE width in
    /// every row, so the chips line up whatever a row holds: the to-do chip fits a two-digit count, the type chip
    /// fits the longest type word. Any other chip is as wide as its own text.</summary>
    public static IReadOnlyList<string> WidthSamples(StatusChip chip) => chip.Kind switch
    {
        ChipKind.ToDo => new[] { "00 to-do", "88 to-do", chip.Text },
        ChipKind.Type => TypeWords.Select(t => t.Shown).Append(chip.Text).ToArray(),
        _ => new[] { chip.Text },
    };

    /// <summary>The CURRENT transcript of a recording (it moves on rename), or null when none exists yet.</summary>
    public string? TranscriptPathFor(string stamp)
    {
        RecentMeeting? e;
        lock (_lock) e = _entries.Find(x => x.Stamp == stamp);
        if (e == null) return null;
        var path = ResolveTranscript(e);
        return File.Exists(path) ? path : null;
    }

    private void Update(int id, Action<RecentMeeting> change)
    {
        lock (_lock)
        {
            var j = _entries.Find(x => x.Id == id);
            if (j == null) return;
            change(j);
        }
        SaveAndNotify();
    }

    // Drop the OLDEST finished row; never strand an in-flight row (and its cancel button) when a completed row
    // could go instead. Only drop a still-cancelable row if every row is in flight.
    private void Trim()
    {
        while (_entries.Count > DisplayMax)
        {
            int drop = _entries.Count - 1;
            for (int i = _entries.Count - 1; i >= 0; i--)
                if (!_entries[i].Cancelable) { drop = i; break; }
            _entries.RemoveAt(drop);
        }
    }

    private void SaveAndNotify()
    {
        Save();
        Changed?.Invoke();
    }

    // ---- recent.json ----
    private sealed record EntryDto(string Stamp, string Started, string? Title, string Status, int Tone,
        string? Transcript = null, bool Renamed = false, string? OdinSlug = null);

    private void Save()
    {
        string json;
        try
        {
            lock (_lock)
            {
                var dto = _entries.Take(DisplayMax).Select(j =>
                    new EntryDto(j.Stamp, j.Started.ToString("o"), j.Title, j.Status, (int)j.Tone, j.Transcript, j.Renamed, j.OdinSlug)).ToList();
                json = JsonSerializer.Serialize(dto);
            }
        }
        catch { return; }

        long mySeq;
        lock (_saveLock) { mySeq = ++_saveSeq; _pendingJson = json; }
        _ = Task.Run(() => Flush(mySeq));
    }

    private void Flush(long seq)
    {
        // The whole flush is under _saveLock, so writes serialize; a superseded snapshot bails.
        lock (_saveLock)
        {
            if (seq != _saveSeq || _pendingJson is null) return;
            var json = _pendingJson;
            _pendingJson = null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                File.WriteAllText(_file, json);
            }
            catch { /* non-fatal */ }
        }
    }

    /// <summary>Waits for the last background write (tests, and the app's exit).</summary>
    public void FlushNow()
    {
        long seq;
        lock (_saveLock) seq = _saveSeq;
        Flush(seq);
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_file)) return;
            var dto = JsonSerializer.Deserialize<List<EntryDto>>(File.ReadAllText(_file));
            if (dto == null) return;
            foreach (var d in dto.Take(DisplayMax))
            {
                try
                {
                    if (!DateTime.TryParse(d.Started, null, System.Globalization.DateTimeStyles.RoundtripKind, out var started))
                        started = DateTime.Now;
                    // A row left mid-processing at the last shutdown has no live job behind it: show it as
                    // interrupted rather than a fake in-flight one (no cancel handle exists for it).
                    var status = d.Status ?? "";
                    bool inFlight = status.EndsWith("…");
                    var tone = inFlight ? JobTone.Muted
                        : (Enum.IsDefined(typeof(JobTone), d.Tone) ? (JobTone)d.Tone : JobTone.Muted);
                    _entries.Add(new RecentMeeting
                    {
                        Id = ++_nextId,
                        Stamp = d.Stamp ?? "",
                        Started = started,
                        Title = d.Title,
                        Status = inFlight ? "Interrupted" : (status.Length == 0 ? "Saved" : status),
                        Tone = tone,
                        Cancelable = false,
                        Transcript = d.Transcript,
                        Renamed = d.Renamed,
                        OdinSlug = d.OdinSlug,
                    });
                }
                catch { /* skip one bad row, keep the rest */ }
            }
        }
        catch { _entries.Clear(); }
    }
}
