using Windows.Media.Control;

namespace Huginn;

/// <summary>
/// Pauses whatever media is currently playing (browser, Spotify, etc.) while you dictate, then
/// resumes exactly the sessions it paused. Uses the Windows System Media Transport Controls, so it
/// only touches what's actually playing — it never starts playback. A gate serialises pause/resume
/// so a quick press never leaves media stuck paused.
/// </summary>
internal sealed class MediaController : IMediaControl
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<GlobalSystemMediaTransportControlsSession> _paused = new();

    public async Task PauseAllAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _paused.Clear();
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            foreach (var session in manager.GetSessions())
            {
                try
                {
                    var info = session.GetPlaybackInfo();
                    if (info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                        && await session.TryPauseAsync())
                    {
                        _paused.Add(session);
                    }
                }
                catch { /* this session doesn't allow pause; skip it */ }
            }
        }
        catch { /* media transport controls unavailable; ignore */ }
        finally { _gate.Release(); }
    }

    public async Task ResumeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false); // waits for any in-flight pause to finish
        try
        {
            foreach (var session in _paused)
            {
                try { await session.TryPlayAsync(); } catch { }
            }
            _paused.Clear();
        }
        finally { _gate.Release(); }
    }
}
