using System.Globalization;

namespace Huginn;

// What each OS supplies to the shared engine. The Windows app implements these with NAudio, a low-level keyboard
// hook and WinForms; the Mac app with AVAudioEngine, ScreenCaptureKit, a CGEvent tap and AppKit. Everything else
// (when to record, what to transcribe, where files go, what to tell the user) lives in HuginnEngine.

/// <summary>A meeting recording's files, decided when it starts. Snapshotted at stop so the recorder is free to
/// record again while this one is transcribed and filed in the background.</summary>
internal sealed record MeetingCapture(string Stamp, string MicPath, string LoopPath, string NotesPath, DateTime StartedLocal)
{
    /// <summary>The files for a recording that starts now: <c>{stamp}_me.wav</c>, <c>{stamp}_them.wav</c> and
    /// <c>{stamp}_notes.md</c> in the meetings folder.</summary>
    public static MeetingCapture Plan(DateTime startedLocal)
    {
        var dir = Paths.MeetingsDir();
        var stamp = startedLocal.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);
        return new MeetingCapture(stamp, Path.Combine(dir, $"{stamp}_me.wav"), Path.Combine(dir, $"{stamp}_them.wav"),
            Path.Combine(dir, $"{stamp}_notes.md"), startedLocal);
    }
}

/// <summary>Push-to-talk capture: the mic at 16 kHz mono 16-bit into memory while the key is held.</summary>
internal interface IDictationRecorder : IDisposable
{
    /// <summary>The platform's microphone id; "" = the system default.</summary>
    string Device { get; set; }
    bool IsRecording { get; }
    void Start();
    void Stop();
    /// <summary>Stops and drops the audio; raises no event.</summary>
    void Cancel();
    /// <summary>A WAV of the last <paramref name="maxSeconds"/> of audio for the live preview; null if too short.</summary>
    MemoryStream? SnapshotWav(int maxSeconds);
    /// <summary>A 16 kHz mono WAV of the whole clip, on a background thread.</summary>
    event Action<MemoryStream>? RecordingComplete;
    /// <summary>The clip was digital silence (peak 16-bit amplitude given): muted or wrong mic.</summary>
    event Action<int>? SilenceDetected;
    /// <summary>Nothing usable was captured (a sub-200 ms tap). The UI must still reset.</summary>
    event Action? RecordingAborted;
    event Action<float>? LevelChanged;
    /// <summary>The first buffer arrived: the mic is listening.</summary>
    event Action? CaptureStarted;
}

/// <summary>Two-track meeting capture: the mic ("Me", 16 kHz mono) and the system audio ("Them", any WAV format)
/// written to the planned files while the meeting runs.</summary>
internal interface IMeetingRecorder : IDisposable
{
    string Device { get; set; }
    bool IsRecording { get; }
    /// <summary>Opens both tracks; throws when capture cannot start (permissions, no device).</summary>
    void Start(MeetingCapture plan);
    void Stop();
    /// <summary>Waits briefly for both files to flush, frees the devices and returns the capture.</summary>
    Task<MeetingCapture?> FinalizeAsync(CancellationToken ct = default);
    /// <summary>Live combined level 0..1, from the capture threads.</summary>
    event Action<float>? LevelChanged;
}

/// <summary>The push-to-talk key chord and the cancel key.</summary>
internal interface IHotkey : IDisposable
{
    /// <summary>How the menu names the chord ("Left Ctrl + Space", "Right Option").</summary>
    string Name { get; }
    /// <summary>While true the cancel key is swallowed and raises <see cref="CancelRequested"/>.</summary>
    bool CancelArmed { get; set; }
    void Install();
    void Uninstall();
    event Action? KeyDown;
    event Action? KeyUp;
    event Action? CancelRequested;
}

/// <summary>Types text at the cursor of whatever app has focus (clipboard + paste).</summary>
internal interface ITextInjector
{
    void Inject(string text);
}

/// <summary>Pauses what is playing while the user dictates, and resumes it after.</summary>
internal interface IMediaControl
{
    Task PauseAllAsync();
    Task ResumeAsync();
}

/// <summary>The Kumospace desktop app on this OS: is it there, is it running, and starting it with the DevTools port.</summary>
internal interface IKumoApp
{
    bool IsInstalled { get; }
    bool IsRunning();
    /// <summary>Asks Kumospace to close, then starts it with the port. False when it would not close.</summary>
    Task<bool> RestartWithDebugPortAsync(int port);
    /// <summary>Makes the usual ways of starting Kumospace pass the port (Windows: its shortcuts). May do nothing.</summary>
    void EnsureLaunchesWithPort(int port);
}

internal enum TrayState { Off, Idle, Recording, Busy }

/// <summary>The app's own UI as the engine sees it: the tray / menu bar, notifications, the file browser.</summary>
internal interface IShell
{
    /// <summary>Runs on the UI thread (now if already there).</summary>
    void RunOnUi(Action action);
    void SetTrayState(TrayState state);
    void SetStatus(string text);
    /// <summary>A notification, for failures only: Bohdan does not want notices for things that went as expected.</summary>
    void Warn(string title, string text);
    /// <summary>Shows the file in Explorer / Finder.</summary>
    void Reveal(string path);
    /// <summary>One of Huginn's own windows has the keyboard (so the notes pad may take it).</summary>
    bool OwnWindowActive { get; }
    /// <summary>The engine's state changed: refresh menu texts and checks.</summary>
    void Refresh();
    /// <summary>The engine asks the app to exit (quit flag). The engine has already saved what it must.</summary>
    void Exit();
    /// <summary>Opens this Odin note in Odin (Windows: the Odin desktop window; elsewhere the web page).</summary>
    void OpenOdinNote(string slug);
}

/// <summary>The floating dictation pill.</summary>
internal interface IDictationOverlay
{
    void ShowStarting();
    void ShowListening();
    void ShowTranscribing();
    void ShowCleaning();
    void SetLevel(float level);
    void SetPreviewText(string text);
    void HideOverlay();
}

/// <summary>The recorder panel's header and meter (its rows render <see cref="RecentMeetings"/>).</summary>
internal interface IRecorderView
{
    void ShowRecording(DateTime startedLocal);
    void SetIdle();
    void SetLevel(float level);
}

/// <summary>The Notes window: a notepad stamped against the recording clock.</summary>
internal interface INotesView
{
    /// <summary>A recording began: clear the last meeting's untouched notes, stamp an agenda [00:00:00], open.</summary>
    void ShowRecording(DateTime startedLocal);
    /// <summary>The recording ended and is kept: the pad stays and its status line follows that job.</summary>
    void EndRecording(string stamp);
    /// <summary>The recording was discarded: the pad clears.</summary>
    void Discarded();
    void SetJobStatus(string stamp, string status);
    void SetTranscriptReady(string stamp);
    /// <summary>The pad's non-empty lines with the stamp each was begun at. UI thread.</summary>
    IReadOnlyList<(TimeSpan at, string text)> Snapshot();
    void FocusPad();
}

/// <summary>An optional always-on-top "● REC" consent badge while a meeting records.</summary>
internal interface IMeetingIndicator
{
    void ShowRecording(DateTime startedLocal);
    void HideIndicator();
}

/// <summary>Which apps hold the microphone right now (Windows: the privacy log of mic use; Mac: Core Audio's process
/// list). Called once a second off the UI thread.</summary>
internal interface IMicUsers
{
    IReadOnlyList<MicUser> Current();
}

/// <summary>The "Start meeting recording" pop-up shown when a meeting app starts using the mic (Notion's pattern).</summary>
internal interface ICallPrompt
{
    /// <summary>Shows the pop-up for this call app ("Google Meet", "Microsoft Teams", …).</summary>
    void ShowPrompt(string app);
    void HidePrompt();
    /// <summary>The user clicked Start recording.</summary>
    event Action? StartRequested;
    /// <summary>The user clicked Not now ("not now"), or picked from the arrow menu ("auto" or "off"). The time running out is "not now" too.</summary>
    event Action<string>? OptionChosen;
}

/// <summary>Everything one OS hands the engine.</summary>
internal sealed record Platform(
    IShell Shell,
    IDictationRecorder Dictation,
    IMeetingRecorder Meeting,
    IHotkey Hotkey,
    ITextInjector Injector,
    IKumoApp Kumo,
    IRecorderView Recorder,
    INotesView Notes,
    IDictationOverlay? Overlay,
    IMediaControl? Media,
    IMeetingIndicator? Indicator,
    IMicUsers? MicUsers = null,
    ICallPrompt? CallPrompt = null);
