namespace Huginn;

internal enum KumoAction { None, Start, Stop }

/// <summary>
/// Decides when a Kumospace conversation starts and ends, from the reads <see cref="KumoWatcher"/> takes.
/// A conversation = I am with someone (someone I hear, near me or in my room — a room is not required)
/// AND there is speech (theirs, or mine as Kumospace sends it). Start: <see cref="StartSpeechSeconds"/> of
/// speech inside the last <see cref="StartWindowSeconds"/>. Stop (only a recording this detector started):
/// alone for <see cref="AloneStopSeconds"/>, or no speech for <see cref="QuietStopSeconds"/>. When the user ends
/// a recording by hand while people are still around, it waits until they are alone before starting again.
/// Pure logic: times are Unix milliseconds, so it can be driven by replayed reads.
/// </summary>
internal sealed class KumoMeetingDetector
{
    private const double SampleSeconds = 0.2; // KumoProbe.js runs Daily's level observers at 200 ms

    public double TalkLevel { get; init; } = 0.03;
    public double StartSpeechSeconds { get; init; } = 0.2; // one 200 ms sample: start as soon as anyone talks
    public double StartWindowSeconds { get; init; } = 15;
    public double AloneStopSeconds { get; init; } = 5;
    public double QuietStopSeconds { get; init; } = 180;

    private readonly Queue<long> _speechTimes = new(); // one entry per 200 ms of speech, within the start window
    private long _lastCompany = long.MinValue / 2;
    private long _lastSpeech = long.MinValue / 2;
    private long? _aloneSince;
    private bool _suppressed;

    /// <summary>The current recording was started by this detector (it may also stop it).</summary>
    public bool AutoRecording { get; private set; }
    /// <summary>Seconds of speech heard during the current auto recording.</summary>
    public double SpeechSecondsThisRecording { get; private set; }

    /// <summary>Feeds one read (or null when no read arrived this second). Returns what the app should do.</summary>
    public (KumoAction Action, string Reason) Tick(long nowMs, KumoSnapshot? snap, bool recording)
    {
        int company = snap is { InCall: true } ? snap.Company.Count : 0;
        if (company > 0) { _lastCompany = nowMs; _aloneSince = null; }
        else _aloneSince ??= nowMs;
        if (_suppressed && _aloneSince is long since && nowMs - since >= AloneStopSeconds * 1000) _suppressed = false;

        if (snap != null && company > 0)
        {
            foreach (var (t, levels) in snap.Samples)
                if (levels.Values.Any(l => l >= TalkLevel)) Speech(t);
            foreach (var (t, level) in snap.Local)
                if (level >= TalkLevel) Speech(t);
        }
        while (_speechTimes.Count > 0 && nowMs - _speechTimes.Peek() > StartWindowSeconds * 1000) _speechTimes.Dequeue();

        if (!recording)
        {
            AutoRecording = false;
            if (!_suppressed && company > 0 && _speechTimes.Count * SampleSeconds >= StartSpeechSeconds)
                return (KumoAction.Start, $"{company} {(company == 1 ? "person" : "people")}, talk started");
            return (KumoAction.None, "");
        }

        if (!AutoRecording) return (KumoAction.None, "");
        if (nowMs - _lastCompany >= AloneStopSeconds * 1000) return (KumoAction.Stop, "alone");
        if (nowMs - _lastSpeech >= QuietStopSeconds * 1000) return (KumoAction.Stop, "quiet");
        return (KumoAction.None, "");

        void Speech(long t)
        {
            _speechTimes.Enqueue(t);
            if (t > _lastSpeech) _lastSpeech = t;
            if (AutoRecording && recording) SpeechSecondsThisRecording += SampleSeconds;
        }
    }

    /// <summary>The app started a recording because <see cref="Tick"/> said so.</summary>
    public void OnAutoStarted(long nowMs)
    {
        AutoRecording = true;
        SpeechSecondsThisRecording = _speechTimes.Count * SampleSeconds; // the speech that triggered it is in the meeting
        _lastSpeech = Math.Max(_lastSpeech, nowMs);
        _lastCompany = Math.Max(_lastCompany, nowMs);
    }

    /// <summary>The detector's own stop went through.</summary>
    public void OnAutoStopped()
    {
        AutoRecording = false;
        _speechTimes.Clear();
    }

    /// <summary>The user ended a recording (stop or discard). If people are still around, wait until I am alone.</summary>
    public void OnUserEnded()
    {
        AutoRecording = false;
        _speechTimes.Clear();
        if (_aloneSince == null) _suppressed = true;
    }
}
