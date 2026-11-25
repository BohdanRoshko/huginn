using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace Huginn;

/// <summary>
/// The whole app minus its windows: push-to-talk dictation (hotkey → mic → Whisper → optional cleanup → paste),
/// meeting recording (two tracks → background transcription → echo gates → speaker names → Odin), Kumospace
/// auto-record, the work timer and the quit flag. The Windows and Mac apps build a <see cref="Platform"/> and call
/// this; every decision is made here, so both behave the same. Call it on the UI thread.
/// </summary>
internal sealed class HuginnEngine : IAsyncDisposable
{
    private const int PreviewWindowSeconds = 10; // the live preview transcribes only the last N seconds → constant cost

    private readonly Platform _p;
    private readonly IShell _shell;
    private readonly Transcriber _transcriber;
    private readonly Cleaner _cleaner;
    private readonly string _modelPath;
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _jobCts = new();
    private readonly KumoWatcher _kumo;
    private readonly MeetBridge _meet = new(HuginnSettings.Current.MeetBridgePort);
    private readonly WorkerClient? _worker = WorkerClient.FromSettings(); // another computer transcribes the meetings; null = this one
    private readonly KumoMeetingDetector _detector;
    private readonly ConcurrentQueue<KumoSnapshot> _kumoReads = new();
    private readonly PresenceLog _presence = new(Paths.PresenceDir());
    private Timer? _kumoTimer;
    private Timer? _callTimer;
    private readonly CallDetector _calls = new();
    private string? _callRecording;  // the call app whose start began the current recording (it ends it too)
    private string? _source;         // the app the current recording is from (Zoom, Kumospace, …); null = started by hand
    private string? _prompting;      // the call app the pop-up is asking about
    private int _callPolling;
    private Timer? _previewTimer;
    private FileSystemWatcher? _quitWatcher;
    private SpeakerTimeline? _timeline;
    private KumoSnapshot? _lastKumoRead;
    private string? _lastCompanyLogged;
    private long _lastKumoReadAt;
    private int _presenceRefresh;
    private MeetingCapture? _current;      // the recording in progress
    private bool _meetingActive;
    private int _processingCount;
    private readonly ConcurrentDictionary<string, byte> _busyStamps = new(); // recordings being transcribed or waiting for it
    private Timer? _audioTimer;            // the cleanup of old meeting audio
    private int _audioCleaning;
    private bool _audioCleanedOnce;
    private bool _dictationOn;             // the hotkey is installed (the switch is on and the model is there)
    private bool _ready;                   // the Whisper model is on disk
    private bool _quitting;
    private bool _previewBusy;
    private volatile bool _cancelledDictation; // the cancel key fired: no transcribe / paste for this clip

    public HuginnEngine(Platform platform, RecentMeetings recent)
    {
        _p = platform;
        _shell = platform.Shell;
        Recent = recent;
        _modelPath = Paths.ModelPath();
        var s = HuginnSettings.Current;

        _transcriber = new Transcriber(_modelPath, Paths.VadModelPath(), Paths.PreviewModelPath());
        _cleaner = new Cleaner(Paths.CleanupModelPath());
        // The personal dictionary biases Whisper toward its spellings and is the cleanup LLM's spelling authority.
        _transcriber.InitialPrompt = Dictionary.WhisperPrompt();
        _cleaner.Vocabulary = Dictionary.VocabularyLine();
        _transcriber.Status += SetStatus;
        _cleaner.Status += SetStatus;

        DictationEnabled = Settings.DictationEnabled;
        CleanupEnabled = Settings.CleanupEnabled;
        HasOdin = !string.IsNullOrWhiteSpace(s.OdinBaseUrl);
        SendMeetings = HasOdin && Settings.SendMeetingsToOdin;
        NotesMode = Settings.NotesMode;
        _p.Dictation.Device = Settings.Microphone;
        _p.Meeting.Device = Settings.Microphone;

        _kumo = new KumoWatcher(s.KumoDebugPort, _p.Kumo);
        _detector = new KumoMeetingDetector
        {
            TalkLevel = s.KumoTalkLevel,
            StartSpeechSeconds = s.KumoStartSpeechSeconds,
            AloneStopSeconds = s.KumoAloneStopSeconds,
            QuietStopSeconds = s.KumoQuietStopSeconds,
        };
        _kumo.Snapshot += snap => _kumoReads.Enqueue(snap);
        _meet.Events += (meeting, events) => Ui(() => OnMeetEvents(meeting, events));
        _meet.Shot += (meeting, shot) => Ui(() => OnMeetShot(meeting, shot));
        _kumo.StatusChanged += st => Ui(() => OnKumoStatus(st));

        _p.Meeting.LevelChanged += _p.Recorder.SetLevel;
        _p.Dictation.RecordingComplete += OnRecordingComplete;
        _p.Dictation.SilenceDetected += OnSilence;
        _p.Dictation.RecordingAborted += OnAborted;
        _p.Dictation.LevelChanged += lvl => Ui(() => _p.Overlay?.SetLevel(lvl));
        _p.Dictation.CaptureStarted += OnCaptureStarted;
        _p.Hotkey.KeyDown += () => Ui(OnTalkDown);
        _p.Hotkey.KeyUp += () => Ui(OnTalkUp);
        _p.Hotkey.CancelRequested += () => Ui(OnCancel);
    }

    // ---- state the menus show ----
    public RecentMeetings Recent { get; }
    public bool DictationEnabled { get; private set; }
    public bool CleanupEnabled { get; private set; }
    public bool SendMeetings { get; private set; }
    /// <summary>settings.json names an Odin to file to. Only Bohdan has Odin; for everyone else the menu hides the
    /// Odin item and meetings are only saved.</summary>
    public bool HasOdin { get; }
    /// <summary>"always", "record" or "off" (see <see cref="Settings.NotesMode"/>).</summary>
    public string NotesMode { get; private set; }
    /// <summary>Meeting detection, for every source (Kumospace and the call apps): "ask", "auto" or "off"
    /// (see <see cref="Settings.MeetingDetection"/>).</summary>
    public string DetectionMode { get; private set; } = Settings.MeetingDetection;
    /// <summary>This OS can tell which apps hold the mic, so meeting apps can be followed.</summary>
    public bool CanFollowCalls => _p.MicUsers != null;
    public bool IsMeetingRecording => _p.Meeting.IsRecording;
    public string HotkeyName => _p.Hotkey.Name;
    public string Microphone => _p.Dictation.Device;
    public bool KumoInstalled => _p.Kumo.IsInstalled;
    public string KumoStatusText { get; private set; } = "Kumospace: starting…";
    /// <summary>The Kumospace status line is a button: it restarts Kumospace with the port.</summary>
    public bool KumoRestartAvailable { get; private set; }
    public string PresenceTodayText { get; private set; } = "Kumospace today: nothing yet";
    public DateTime? MeetingStartedLocal => _current?.StartedLocal;

    /// <summary>The menu line for the recordings folder: "Recordings: 3.2 GB" (audio, snapshots, transcripts).</summary>
    public string RecordingsSizeText
    {
        get
        {
            long bytes = 0;
            try
            {
                if (Directory.Exists(Paths.MeetingsDir()))
                    foreach (var f in new DirectoryInfo(Paths.MeetingsDir()).EnumerateFiles("*", SearchOption.AllDirectories)) bytes += f.Length;
            }
            catch { /* a file vanished mid-count; the next open counts again */ }
            return "Recordings: " + SizeText(bytes);
        }
    }

    internal static string SizeText(long bytes) => bytes switch
    {
        < 1024L * 1024 => $"{bytes / 1024.0:0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1048576.0:0} MB",
        _ => $"{bytes / 1073741824.0:0.0} GB",
    };

    /// <summary>Starts the quit watcher, the Kumospace watcher and the first-run model download.</summary>
    public void Start()
    {
        HuginnLog.Write($"Huginn started ({Environment.OSVersion.Platform}, {(DictationEnabled ? "dictation on" : "dictation off")})");
        if (_worker != null) HuginnLog.Write($"worker: meetings are sent to the other computer at {HuginnSettings.Current.WorkerUrl.Trim()}");
        StartQuitWatcher();
        _meet.Start();
        foreach (var stamp in MeetingFiles.ForgetCancelled(Recent))
            HuginnLog.Write($"meeting {stamp}: it was cancelled earlier; its row and its files are removed");
        _ = NameUnnamedAsync();
        if (_p.Kumo.IsInstalled)
        {
            _p.Kumo.EnsureLaunchesWithPort(HuginnSettings.Current.KumoDebugPort);
            _kumo.Start();
            _kumoTimer = new Timer(_ => Ui(OnKumoTick), null, 1000, 1000);
        }
        else
        {
            KumoStatusText = "Kumospace: not installed";
        }
        if (_p.MicUsers != null)
        {
            _callTimer = new Timer(_ => PollCalls(), null, 2000, 1000);
            if (_p.CallPrompt != null)
            {
                _p.CallPrompt.StartRequested += () => Ui(StartFromPrompt);
                _p.CallPrompt.OptionChosen += option => Ui(() => PromptOption(option));
            }
        }
        _audioTimer = new Timer(_ => CleanAudio("on a timer"), null, TimeSpan.FromMinutes(2), AudioCleanEvery);
        _shell.SetTrayState(TrayState.Off);
        _shell.Refresh();
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        if (!File.Exists(_modelPath))
        {
            _shell.SetTrayState(TrayState.Busy);
            SetStatus("Downloading model… (one-time)");
            try
            {
                await ModelDownloader.EnsureAsync(_modelPath, Config.ModelUrl, p => SetStatus($"Downloading model… {p:P0}"));
            }
            catch (Exception ex)
            {
                HuginnLog.Write("model download failed", ex);
                _shell.SetTrayState(TrayState.Off);
                SetStatus($"Model download failed: {ex.Message}");
                _shell.Warn("Huginn — model download failed", ex.Message);
                return;
            }
        }
        _ready = true;
        if (DictationEnabled) TurnDictationOn();
        else { _shell.SetTrayState(TrayState.Off); SetStatus("Dictation off"); }
    }

    // ---- the quit flag: another app writes quit.flag → flush any recording to disk and exit ----

    private void StartQuitWatcher()
    {
        try
        {
            Directory.CreateDirectory(Paths.BaseDir());
            try { File.Delete(Paths.QuitFlagPath()); } catch { /* none / locked — fine */ }
            _quitWatcher = new FileSystemWatcher(Paths.BaseDir(), "quit.flag")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            };
            _quitWatcher.Created += (_, _) => Ui(() => _ = QuitFromFlagAsync());
            _quitWatcher.Changed += (_, _) => Ui(() => _ = QuitFromFlagAsync());
            _quitWatcher.EnableRaisingEvents = true;
        }
        catch { /* no watcher → the menu's Quit still works */ }
    }

    private async Task QuitFromFlagAsync()
    {
        if (_quitting) return; // Created + Changed can both fire for one write
        _quitting = true;
        HuginnLog.Write("quit flag received");
        try { File.Delete(Paths.QuitFlagPath()); } catch { /* best effort */ }
        if (_p.Meeting.IsRecording)
        {
            SaveNotes();
            _p.Meeting.Stop();
            SaveTimeline(await _p.Meeting.FinalizeAsync()); // the WAVs survive on disk, unprocessed
        }
        Shutdown();
        _shell.Exit();
    }

    /// <summary>The app is quitting: close the work timer's day and stop the background jobs.</summary>
    public void Shutdown()
    {
        _presence.Observe(DateTime.Now, "off");
        foreach (var cts in _jobCts.Values) { try { cts.Cancel(); } catch { /* already disposed */ } }
        Recent.FlushNow();
    }

    // ---- switches ----

    /// <summary>The dictation switch. Off: the hotkey is not listened to and the dictation models leave memory.
    /// Meetings, Kumospace auto-record and the work timer keep running. Remembered across restarts.</summary>
    public void SetDictationEnabled(bool on)
    {
        if (DictationEnabled == on) return;
        DictationEnabled = on;
        Settings.DictationEnabled = on;
        HuginnLog.Write(on ? "dictation turned on" : "dictation turned off");
        if (on) { if (_ready) TurnDictationOn(); }
        else TurnDictationOff();
        _shell.Refresh();
    }

    public void SetCleanupEnabled(bool on)
    {
        CleanupEnabled = on;
        Settings.CleanupEnabled = on;
        if (on) _ = _cleaner.PrepareAsync();
        else _ = _cleaner.UnloadAsync();
        _shell.Refresh();
    }

    public void SetSendMeetings(bool on)
    {
        if (!HasOdin) return;
        SendMeetings = on;
        Settings.SendMeetingsToOdin = on;
        _shell.Refresh();
    }

    public void SetDetectionMode(string mode)
    {
        DetectionMode = mode;
        Settings.MeetingDetection = mode;
        HuginnLog.Write($"meeting detection: {mode}");
        if (mode != "ask") HidePrompt();
        _shell.Refresh();
    }

    public void SetNotesMode(string mode)
    {
        NotesMode = mode;
        Settings.NotesMode = mode;
        _shell.Refresh();
    }

    // ---- old meeting audio ----

    /// <summary>How long a meeting's raw audio is kept: a key of <see cref="AudioRetention.Choices"/>.</summary>
    public string KeepAudio { get; private set; } = Settings.KeepAudio;

    private static readonly TimeSpan AudioCleanEvery = TimeSpan.FromHours(6);

    /// <summary>The menu's choice. The cleanup follows a minute later, so a wrong click can be put back first.</summary>
    public void SetKeepAudio(string key)
    {
        if (!AudioRetention.IsChoice(key) || key == KeepAudio) return;
        KeepAudio = key;
        Settings.KeepAudio = key;
        HuginnLog.Write($"meeting audio: keep set to \"{AudioRetention.LabelFor(key)}\"; the cleanup runs in a minute");
        _audioTimer?.Change(TimeSpan.FromMinutes(1), AudioCleanEvery);
        _shell.Refresh();
    }

    // Deletes the audio of meetings past the chosen age that have a transcript. Runs two minutes after the start,
    // every six hours, a minute after the setting changes, and after each transcription. Never the recording in
    // progress, and never one still being transcribed or waiting for it.
    private void CleanAudio(string why)
    {
        if (Interlocked.Exchange(ref _audioCleaning, 1) == 1) return;
        try
        {
            var key = KeepAudio;
            var r = AudioRetention.Clean(Paths.MeetingsDir(), key, DateTime.Now, stamp => _current?.Stamp == stamp || _busyStamps.ContainsKey(stamp));
            if (r.Files > 0 || r.Failed > 0 || !_audioCleanedOnce) HuginnLog.Write($"meeting audio ({why}): {r.Describe(key, dryRun: false)}");
            _audioCleanedOnce = true;
            if (r.Files > 0) _shell.Refresh();
        }
        catch (Exception ex) { HuginnLog.Write("meeting audio: the cleanup failed", ex); }
        finally { Interlocked.Exchange(ref _audioCleaning, 0); }
    }

    /// <summary>Picks the microphone for dictation and meetings ("" = system default).</summary>
    public void SelectMicrophone(string id, string name)
    {
        _p.Dictation.Device = id;
        _p.Meeting.Device = id;
        Settings.Microphone = id;
        SetStatus(id.Length == 0 ? "Mic: System default" : $"Mic: {name}");
        _shell.Refresh();
    }

    private void TurnDictationOn()
    {
        _dictationOn = true;
        if (!_meetingActive) _p.Hotkey.Install();
        if (!_p.Meeting.IsRecording) _shell.SetTrayState(TrayState.Idle);
        SetStatus("Idle");
        // Nothing loads here. The models load on the dictation key or at Record, and leave memory after
        // IdleUnloadMinutes without use.
    }

    private void TurnDictationOff()
    {
        _dictationOn = false;
        _p.Hotkey.Uninstall();
        if (_p.Dictation.IsRecording) _p.Dictation.Cancel();
        _previewTimer?.Dispose();
        _previewTimer = null;
        ResetOverlayToIdle();
        if (!_meetingActive && _processingCount == 0) _ = _transcriber.UnloadAsync(); // free memory (not mid-meeting)
        _ = _cleaner.UnloadAsync();
        if (!_p.Meeting.IsRecording) _shell.SetTrayState(TrayState.Off);
        SetStatus("Dictation off");
    }

    // ---- dictation ----

    private void OnTalkDown()
    {
        if (!_dictationOn || _meetingActive) return;
        _cancelledDictation = false;
        _p.Hotkey.CancelArmed = true; // arm the cancel key for the whole flow (recording + transcription)
        _shell.SetTrayState(TrayState.Recording);
        SetStatus("Starting…");
        _p.Overlay?.ShowStarting();
        _ = _p.Media?.PauseAllAsync();
        try { _p.Dictation.Start(); }
        catch (Exception ex)
        {
            HuginnLog.Write("dictation mic failed to start", ex);
            SetStatus($"Mic failed: {ex.Message}");
            ResetOverlayToIdle();
            _ = _p.Media?.ResumeAsync();
            return;
        }
        _ = PreloadForDictationAsync();
    }

    private void OnTalkUp()
    {
        if (!_dictationOn) return;
        if (_cancelledDictation) { _cancelledDictation = false; return; } // the cancel key already ended this one
        if (!_p.Dictation.IsRecording) return;
        _p.Dictation.Stop();
        StopPreview();
        _shell.SetTrayState(TrayState.Busy);
        SetStatus("Transcribing…");
        _p.Overlay?.ShowTranscribing();
        _ = _p.Media?.ResumeAsync();
    }

    // The cancel key during a dictation: drop the recording, and the paste if transcription already runs.
    private void OnCancel()
    {
        if (!_dictationOn) return;
        _cancelledDictation = true;
        if (_p.Dictation.IsRecording)
        {
            _p.Dictation.Cancel();
            StopPreview();
            _ = _p.Media?.ResumeAsync();
        }
        SetStatus("Cancelled");
        ResetOverlayToIdle();
    }

    // Loads the speech models while the user speaks, then the cleanup LLM (staggered, so the two loads do not
    // pile into one long stall). The lazy loads in TranscribeAsync and CleanAsync stay as a safety net.
    private async Task PreloadForDictationAsync()
    {
        try { await _transcriber.EnsureLoadedAsync(); }
        catch { /* falls back to a cold first transcription */ }
        if (CleanupEnabled)
        {
            try { await _cleaner.PrepareAsync(); }
            catch { /* falls back to a lazy first-use load */ }
        }
    }

    private void OnCaptureStarted() => Ui(() =>
    {
        if (!_p.Dictation.IsRecording) return; // key released before the mic opened
        _p.Overlay?.ShowListening();
        SetStatus("Listening…");
        if (HuginnSettings.Current.LivePreview && _p.Overlay != null)
            _previewTimer ??= new Timer(_ => Ui(() => _ = PreviewTickAsync()), null, 700, 700);
    });

    private void StopPreview()
    {
        _previewTimer?.Dispose();
        _previewTimer = null;
    }

    private async Task PreviewTickAsync()
    {
        if (_previewBusy || !_p.Dictation.IsRecording) return;
        var wav = _p.Dictation.SnapshotWav(PreviewWindowSeconds);
        if (wav == null) return;
        _previewBusy = true;
        try
        {
            string text;
            using (wav) text = await _transcriber.TranscribePreviewAsync(wav);
            if (_p.Dictation.IsRecording && !string.IsNullOrWhiteSpace(text))
                _p.Overlay?.SetPreviewText(text);
        }
        catch { /* a dropped preview pass is harmless */ }
        finally { _previewBusy = false; }
    }

    private async void OnRecordingComplete(MemoryStream wav)
    {
        try
        {
            long wavBytes = wav.Length;
            string text;
            using (wav)
                text = await _transcriber.TranscribeAsync(wav, gateSpeech: true);

            // 16 kHz mono 16-bit = 32000 bytes/sec (minus the 44-byte WAV header).
            double clipSeconds = Math.Max(0, wavBytes - 44) / 32000.0;

            if (!string.IsNullOrWhiteSpace(text) && !GhostPhrases.IsLikelyGhost(text, clipSeconds))
            {
                if (_cancelledDictation) return; // cancelled during transcription → no paste
                if (CleanupEnabled)
                {
                    SetStatus("Cleaning…");
                    Ui(() => _p.Overlay?.ShowCleaning());
                    text = await _cleaner.CleanAsync(text);
                }
                if (_cancelledDictation) return; // cancelled during cleanup → no paste
                _p.Injector.Inject(text);
                SetStatus("Idle");
            }
            else
            {
                SetStatus("Idle (no speech detected)");
            }
        }
        catch (Exception ex)
        {
            HuginnLog.Write("dictation failed", ex);
            SetStatus($"Error: {ex.Message}");
        }
        finally
        {
            ResetOverlayToIdle();
        }
    }

    private void OnSilence(int peak)
    {
        SetStatus($"No mic signal (peak {peak}) — muted or wrong mic?");
        ResetOverlayToIdle();
    }

    // Recording stopped with nothing usable (a fast tap, or no audio). Reset so the overlay never sticks open.
    private void OnAborted()
    {
        SetStatus("Idle");
        ResetOverlayToIdle();
    }

    // Single owner of "dictation is over → back to idle". EVERY terminal path routes here, so a stop can never
    // strand the overlay open. Idempotent.
    private void ResetOverlayToIdle()
    {
        _p.Hotkey.CancelArmed = false; // the cancel key passes through normally again
        Ui(() =>
        {
            StopPreview();
            if (!_p.Meeting.IsRecording) _shell.SetTrayState(_dictationOn ? TrayState.Idle : TrayState.Off);
            _p.Overlay?.HideOverlay();
        });
    }

    // ---- meeting recording (separate from push-to-talk dictation) ----

    public void ToggleMeeting()
    {
        if (_p.Meeting.IsRecording) { _detector.OnUserEnded(); _ = StopMeetingAsync(); }
        else StartMeeting();
    }

    /// <summary>"This meeting is not worth keeping": stop and DELETE the audio. No transcript, no filing.</summary>
    public void Discard()
    {
        if (!_p.Meeting.IsRecording) return;
        _detector.OnUserEnded();
        _ = DiscardMeetingAsync();
    }

    private void StartMeeting()
    {
        if (!File.Exists(_modelPath))
        {
            SetStatus("Meeting: model still downloading…");
            _shell.Warn("Huginn — can't record yet", "The Whisper model is still downloading.");
            return;
        }
        HidePrompt(); // a recording is starting, whoever started it: the pop-up has nothing left to ask
        _meetingActive = true;
        _transcriber.KeepWarm = true; // a model in memory stays there until Stop
        // Load now, so Stop finds the model in memory. With another computer set up nothing loads here at Record,
        // whether that one answers or not: this one loads at Stop, and only if that one cannot be reached then.
        if (_worker == null) _ = _transcriber.EnsureLoadedAsync();
        if (_dictationOn) _p.Hotkey.Uninstall(); // free the mic and keys for the call; dictation pauses while recording
        var plan = MeetingCapture.Plan(DateTime.Now);
        try
        {
            Directory.CreateDirectory(Paths.MeetingsDir());
            _p.Meeting.Start(plan);
        }
        catch (Exception ex)
        {
            HuginnLog.Write("meeting start failed", ex);
            _meetingActive = false;
            _transcriber.KeepWarm = false;
            if (_dictationOn) _p.Hotkey.Install();
            SetStatus($"Meeting start failed: {ex.Message}");
            _shell.Warn("Huginn — meeting recording failed", ex.Message);
            return;
        }
        _current = plan;
        if (_worker != null)
        {
            _workerWord = new CancellationTokenSource();
            _ = TellWorkerAsync(_worker, plan.Stamp, _workerWord.Token, WorkerStillOn, WorkerAskAgain);
        }
        _source = null; // StartForCall / StartForKumo name the app right after this
        _timeline = SpeakerTimeline.Begin(plan.StartedLocal);
        _shell.SetTrayState(TrayState.Recording);
        SetStatus("● Recording meeting…");
        _p.Indicator?.ShowRecording(plan.StartedLocal);
        _p.Recorder.ShowRecording(plan.StartedLocal);
        if (NotesMode != "off")
        {
            _p.Notes.ShowRecording(plan.StartedLocal); // opens the window too, when it was hidden
            SaveNotes(); // an agenda typed before Record joins the file at [00:00:00]
            // Hand the keyboard to the pad only when Record was clicked in one of Huginn's own windows; a start
            // from the menu or from Kumospace must not pull focus away from the call.
            if (_shell.OwnWindowActive) _p.Notes.FocusPad();
        }
        _shell.Refresh();
    }

    private static readonly TimeSpan WorkerStillOn = TimeSpan.FromSeconds(60);  // "the recording still runs", while it answers
    private static readonly TimeSpan WorkerAskAgain = TimeSpan.FromSeconds(10); // the same, while it does not
    private CancellationTokenSource? _workerWord; // ends the word to the other computer when the recording ends

    /// <summary>
    /// Says to the other computer, at Record and again while the recording runs, that a recording runs: it loads
    /// the models at the first word it gets and lets them go some minutes after the last. Nothing loads on this
    /// computer, whatever the answer: a request without an answer is asked again, and the models are needed here
    /// only if the other computer cannot be reached when the recording ends. Ends with <paramref name="ct"/>.
    /// </summary>
    internal static async Task TellWorkerAsync(WorkerClient worker, string stamp, CancellationToken ct, TimeSpan stillOn, TimeSpan askAgain)
    {
        bool? answered = null;
        while (!ct.IsCancellationRequested)
        {
            var problem = await worker.MeetingStartedAsync(stamp, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) return; // the recording ended meanwhile: nothing more to say about it
            if (problem == null && answered != true)
                HuginnLog.Write(answered == null
                    ? $"meeting {stamp}: the other computer loads the models; none load here"
                    : $"meeting {stamp}: the other computer answers now and loads the models");
            if (problem != null && answered != false)
                HuginnLog.Write($"meeting {stamp}: the other computer does not answer ({problem}); it is asked again while the recording runs, and no model loads here");
            answered = problem == null;
            try { await Task.Delay(answered.Value ? stillOn : askAgain, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void EndWorkerWord()
    {
        try { Interlocked.Exchange(ref _workerWord, null)?.Cancel(); } catch { /* already over */ }
    }

    // Shared teardown for every stop: the capture is flushed and the UI is back to ready at once. The next
    // meeting can start while (if kept) this one processes in the background.
    private async Task<MeetingCapture?> EndMeetingAsync()
    {
        EndWorkerWord(); // no more "a recording runs": what follows is a job, or the word that it ended
        SaveNotes(); // the last edits, before the recorder stops accepting them
        _p.Meeting.Stop();
        var cap = await _p.Meeting.FinalizeAsync();
        SaveTimeline(cap);
        if (cap != null) MeetingProcessor.SaveSource(cap.Stamp, _source);
        _current = null;
        _callRecording = null;
        _source = null;

        _meetingActive = false;
        _transcriber.KeepWarm = false;
        if (_dictationOn) { _p.Hotkey.Install(); _shell.SetTrayState(TrayState.Idle); }
        else _shell.SetTrayState(TrayState.Off);
        _p.Indicator?.HideIndicator();
        _p.Recorder.SetIdle();
        if (cap != null) _p.Notes.EndRecording(cap.Stamp); // the pad stays; its status line follows the job
        _shell.Refresh();
        return cap;
    }

    private async Task StopMeetingAsync()
    {
        var cap = await EndMeetingAsync();
        SetStatus("Idle");
        if (cap != null) _ = ProcessCaptureAsync(cap);
    }

    private async Task DiscardMeetingAsync()
    {
        var cap = await EndMeetingAsync();
        DeleteCapture(cap);
        if (cap != null) _ = _worker?.MeetingDroppedAsync(cap.Stamp);
        if (cap != null) HuginnLog.Write($"meeting {cap.Stamp}: discarded by the user, audio deleted");
        _p.Notes.Discarded();
        SetStatus("Recording discarded"); // no notification: the user just asked for it
    }

    // A file that was still open when its recording was cancelled is deleted a moment later.
    private static async Task SweepLaterAsync(string stamp)
    {
        for (int i = 0; i < 3; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            if (MeetingFiles.DeleteAll(stamp).Left == 0) return;
        }
        HuginnLog.Write($"meeting {stamp}: some files of the cancelled recording could not be deleted");
    }

    private static void DeleteCapture(MeetingCapture? cap)
    {
        if (cap == null) return;
        MeetingFiles.DeleteAll(cap.Stamp); // whatever else carries its stamp
        foreach (var path in new[] { cap.MicPath, cap.LoopPath, cap.NotesPath, SpeakerTimeline.PathFor(cap.Stamp), MeetingProcessor.SourcePathFor(cap.Stamp) }.Concat(SpeakerTimeline.ShotFiles(cap.Stamp)))
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
        }
    }

    // Who Kumospace heard during the recording goes next to its WAVs, for the transcript step to name the speakers.
    private void SaveTimeline(MeetingCapture? cap)
    {
        var tl = _timeline;
        _timeline = null;
        if (cap == null || tl == null || !tl.HasContent) return;
        tl.Save(SpeakerTimeline.PathFor(cap.Stamp));
    }

    /// <summary>The pad changed (debounced) or the recording is ending: rewrite the meeting's notes file from the
    /// pad, so a crash keeps everything typed so far and the finished job posts exactly what was on screen.</summary>
    public void SaveNotes()
    {
        var cur = _current;
        if (cur == null || !_p.Meeting.IsRecording || NotesMode == "off") return;
        MeetingNotes.Save(cur.NotesPath, cur.StartedLocal, _p.Notes.Snapshot());
    }

    /// <summary>Where the open button opens a recording: Odin's drawer when chosen (and there is an Odin), else Huginn's
    /// meeting window.</summary>
    public bool OpenMeetingsInOdin => HasOdin && Settings.OpenMeetingsInOdin;

    public void SetOpenMeetingsInOdin(bool odin)
    {
        Settings.OpenMeetingsInOdin = odin;
        _shell.Refresh();
    }

    /// <summary>The open button on a finished row: Huginn's meeting window, a tab per recording; or, when chosen, the
    /// note in Odin. A row Odin has not filed opens in the window either way.</summary>
    public async Task OpenMeetingAsync(int id)
    {
        var row = Recent.Find(id);
        if (row == null) return;
        var slug = HasOdin ? await OdinSlugAsync(row) : null;
        if (OpenMeetingsInOdin && slug != null)
        {
            HuginnLog.Write($"odin: opening note {slug}");
            _shell.OpenOdinNote(slug);
            return;
        }
        var view = await MeetingView.BuildAsync(row, slug, MeetingShotHost);
        if (view == null) { SetStatus("The transcript is not on disk"); return; }
        Ui(() => _shell.ShowMeeting(view));
    }

    /// <summary>The host name the meeting window loads snapshots from (mapped to the recordings folder).</summary>
    public const string MeetingShotHost = "meetings.huginn";

    private async Task<string?> OdinSlugAsync(RecentMeeting row)
    {
        if (row.OdinSlug != null) return row.OdinSlug;
        if (string.IsNullOrWhiteSpace(row.Title)) return null;
        try
        {
            var slug = await OdinClient.FindMeetingSlugAsync(row.Title!);
            if (slug != null) Recent.SetOdinSlug(row.Id, slug);
            return slug;
        }
        catch (Exception ex) { HuginnLog.Write("odin: note lookup failed", ex); return null; }
    }

    /// <summary>The "open in Odin" button on a filed row: opens its note in Odin. A row filed before Huginn kept the
    /// slug finds it by its title first.</summary>
    public async Task OpenInOdinAsync(int id)
    {
        if (!HasOdin) return;
        var row = Recent.Find(id);
        if (row == null) return;
        var slug = row.OdinSlug;
        if (slug == null && !string.IsNullOrWhiteSpace(row.Title))
        {
            try { slug = await OdinClient.FindMeetingSlugAsync(row.Title!); }
            catch (Exception ex) { HuginnLog.Write("odin: note lookup failed", ex); }
            if (slug != null) Recent.SetOdinSlug(id, slug);
        }
        if (slug == null) { SetStatus("Not found in Odin"); return; }
        HuginnLog.Write($"odin: opening note {slug}");
        _shell.OpenOdinNote(slug);
    }

    /// <summary>The cancel button on an in-flight row: stop that job. Before its transcript is written the row and
    /// every file of the recording go (<see cref="MeetingFiles.Cancel"/>); after it, the meeting stays as saved.</summary>
    public void CancelJob(int id)
    {
        if (_jobCts.TryGetValue(id, out var cts))
        {
            try { cts.Cancel(); } catch { /* already disposed */ }
        }
    }

    // The local summary (MeetingSummary), written next to the transcript. A ✕ while it runs skips the summary only:
    // the transcript is already saved.
    private async Task SummarizeAsync(int jobId, string stamp, string transcript, string? notes, CancellationToken ct)
    {
        if (!File.Exists(Paths.CleanupModelPath())) { HuginnLog.Write($"meeting {stamp}: no summary (the local model is not on disk)"); return; }
        Recent.SetStatus(jobId, "Summarizing…", JobTone.Busy, cancelable: true);
        var sw = Stopwatch.StartNew();
        try
        {
            var md = await MeetingSummary.MakeAsync(transcript, notes, Settings.UserName, _cleaner, ct);
            if (md == null) { HuginnLog.Write($"meeting {stamp}: no summary (too little speech, or nothing usable from the model)"); return; }
            await File.WriteAllTextAsync(MeetingSummary.PathFor(stamp), md, CancellationToken.None);
            HuginnLog.Write($"meeting {stamp}: summarized here in {sw.Elapsed:m\\:ss} ({md.Length} chars)");
        }
        catch (OperationCanceledException) { HuginnLog.Write($"meeting {stamp}: summary skipped by the user"); }
        catch (Exception ex) { HuginnLog.Write($"meeting {stamp}: the local summary failed", ex); }
    }

    // Transcribe + file a finished capture in the BACKGROUND; it never blocks a new recording. Several can run at
    // once (meeting after meeting); transcription serializes on the shared model, recording does not.
    private async Task ProcessCaptureAsync(MeetingCapture cap)
    {
        _busyStamps[cap.Stamp] = 0;
        Interlocked.Increment(ref _processingCount);
        UpdatePriority();
        int jobId = Recent.Add(cap.Stamp, cap.StartedLocal);
        var cts = new CancellationTokenSource();
        _jobCts[jobId] = cts;
        var sw = Stopwatch.StartNew();
        var notes = MeetingNotes.Read(cap.NotesPath);
        HuginnLog.Write($"meeting {cap.Stamp}: processing started (me={FileSize(cap.MicPath)}, them={FileSize(cap.LoopPath)}, notes={(notes == null ? "none" : $"{notes.Count(c => c == '\n') + 1} lines")})");
        try
        {
            // The transcript and the name: from the other computer when one is set up and it does the job, else made here.
            var done = (_worker == null ? null : await ProcessOnWorkerAsync(cap, jobId, sw, cts.Token)) ?? await ProcessHereAsync(cap, jobId, sw, cts.Token);
            var r = done.Transcript;
            // The row does not say which computer transcribed the meeting; the log does, in the line below.
            // The meeting's title, made by this app's naming code (here or on the other computer). It is the only one.
            Recent.SetTitle(jobId, done.Title);
            HuginnLog.Write($"meeting {cap.Stamp}: named \"{done.Title}\" {(done.Far ? "on the other computer" : "here")}");
            Ui(() => _p.Notes.SetTranscriptReady(cap.Stamp));
            if (SendMeetings)
            {
                Recent.SetStatus(jobId, "Filing to Odin…", JobTone.Busy, cancelable: true);
                Ui(() => _p.Notes.SetJobStatus(cap.Stamp, "Filing to Odin…"));
                try
                {
                    // The title goes TO Odin and never comes back: this app is the one place that names a meeting.
                    string title = Recent.Find(jobId)?.Title ?? done.Title;
                    var odin = await OdinClient.ProcessMeetingAsync(r.Text, cap.StartedLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), notes, title, cts.Token);
                    HuginnLog.Write($"meeting {cap.Stamp}: filed to Odin ({odin.Classification}, {odin.TaskCount} task(s)) as \"{title}\"" +
                                    (odin.Title.Length > 0 && odin.Title != title ? $"; Odin's own title \"{odin.Title}\" is not used" : ""));
                    Recent.SetOdinSlug(jobId, odin.Slug);
                    await UploadShotsAsync(cap.Stamp, odin.Slug, cts.Token);
                    Recent.SetStatus(jobId, $"✓ {odin.Classification} · {odin.TaskCount} task(s)", JobTone.Good, cancelable: false);
                    Ui(() => _p.Notes.SetJobStatus(cap.Stamp, $"✓ Filed to Odin · {title}"));
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    HuginnLog.Write($"meeting {cap.Stamp}: Odin filing failed", ex);
                    await SummarizeAsync(jobId, cap.Stamp, r.Text, notes, cts.Token); // Odin wrote none: the local one
                    // The row looks like any saved meeting: it has its local name, and Odin is never named on it
                    // (Bohdan, 2026-11-20). The notice below is the one place the failure shows.
                    Recent.SetStatus(jobId, "Saved", JobTone.Good, cancelable: false);
                    Ui(() => _p.Notes.SetJobStatus(cap.Stamp, "✓ Transcript saved"));
                    _shell.Warn("Huginn — Odin not reachable", $"Saved the transcript to disk; file it later.\n{Path.GetFileName(r.TranscriptPath)}");
                    _shell.Reveal(Recent.TranscriptPathFor(cap.Stamp) ?? r.TranscriptPath);
                }
            }
            else
            {
                await SummarizeAsync(jobId, cap.Stamp, r.Text, notes, cts.Token);
                Recent.SetStatus(jobId, "Saved", JobTone.Good, cancelable: false);
                Ui(() => _p.Notes.SetJobStatus(cap.Stamp, "✓ Transcript saved"));
                _shell.Reveal(Recent.TranscriptPathFor(cap.Stamp) ?? r.TranscriptPath);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // A cancelled recording leaves nothing: no row, no file. Once its transcript is on disk it is no longer
            // a cancelled recording: only the naming or the filing was stopped, and the meeting stays as saved.
            if (MeetingFiles.Cancel(Recent, jobId, cap.Stamp))
            {
                HuginnLog.Write($"meeting {cap.Stamp}: cancelled by user after {sw.Elapsed:m\\:ss}; its row and its files are removed");
                Ui(() => _p.Notes.SetJobStatus(cap.Stamp, "Cancelled"));
                _ = SweepLaterAsync(cap.Stamp);
            }
            else
            {
                HuginnLog.Write($"meeting {cap.Stamp}: stopped by user after {sw.Elapsed:m\\:ss}, with the transcript already written; it stays as saved");
                Ui(() => _p.Notes.SetJobStatus(cap.Stamp, "✓ Transcript saved"));
            }
        }
        catch (Exception ex)
        {
            HuginnLog.Write($"meeting {cap.Stamp}: FAILED after {sw.Elapsed:m\\:ss}", ex);
            Recent.SetStatus(jobId, $"Failed · {ShortReason(ex)}", JobTone.Bad, cancelable: false);
            Ui(() => _p.Notes.SetJobStatus(cap.Stamp, $"Failed · {ShortReason(ex)}"));
            _shell.Warn("Huginn — meeting processing failed", $"{ex.Message}\nDetails: {Paths.LogPath()}");
        }
        finally
        {
            _jobCts.TryRemove(jobId, out _);
            cts.Dispose();
            Interlocked.Decrement(ref _processingCount);
            UpdatePriority();
            _busyStamps.TryRemove(cap.Stamp, out _);
            _ = Task.Run(() => CleanAudio("after a transcription")); // "delete after transcription" acts here
        }
    }

    /// <summary>A recording's transcript (on disk) and name, and whether the other computer made them.</summary>
    private sealed record Processed(MeetingResultFiles Transcript, string Title, bool Far);

    /// <summary>The transcript as the steps after it read it: the text and where it was written.</summary>
    private sealed record MeetingResultFiles(string TranscriptPath, string Text);

    // This computer transcribes and names the recording, as it does with no other computer set up.
    private async Task<Processed> ProcessHereAsync(MeetingCapture cap, int jobId, Stopwatch sw, CancellationToken ct)
    {
        var job = await MeetingJob.RunAsync(cap, _transcriber, _cleaner, options: null, transcribed: async r =>
        {
            HuginnLog.Write($"meeting {cap.Stamp}: transcribed {r.SegmentCount} segments on this computer in {sw.Elapsed:m\\:ss} -> {Path.GetFileName(r.TranscriptPath)}");
            HuginnLog.Write($"meeting {cap.Stamp}: echo gates: {r.Echo}{(r.RawTranscriptPath == null ? "" : " (raw kept)")}");
            Recent.SetTranscript(jobId, r.TranscriptPath);
            // Without Odin the local model writes the only summary (and the name), so it is fetched once when it is
            // not on disk yet: the dictation cleanup is off by default, so most people never got it.
            if (!SendMeetings && !File.Exists(Paths.CleanupModelPath()))
            {
                Recent.SetStatus(jobId, "Getting the summary model…", JobTone.Busy, cancelable: true);
                await _cleaner.PrepareAsync();
            }
        }, ct);
        return new Processed(new MeetingResultFiles(job.Transcript.TranscriptPath, job.Transcript.Text), job.Title, Far: false);
    }

    // The other computer transcribes and names the recording; this one sends the files and writes what comes back.
    // Null when it could not do it: the caller then processes the recording here.
    private async Task<Processed?> ProcessOnWorkerAsync(MeetingCapture cap, int jobId, Stopwatch sw, CancellationToken ct)
    {
        void Show(string line) => Ui(() =>
        {
            Recent.SetStatus(jobId, line, JobTone.Busy, cancelable: true);
            _p.Notes.SetJobStatus(cap.Stamp, line);
        });
        try
        {
            var files = new[]
            {
                ("me.wav", cap.MicPath), ("them.wav", cap.LoopPath), ("speakers.json", SpeakerTimeline.PathFor(cap.Stamp)),
                ("source.txt", MeetingProcessor.SourcePathFor(cap.Stamp)), ("notes.md", cap.NotesPath), ("dictionary.txt", Paths.File("dictionary.txt")),
            };
            var far = await _worker!.ProcessAsync(cap.Stamp, cap.StartedLocal, files, MeetingOptions.Here(prompt: null), Show, ct);
            var (path, rawPath) = await MeetingProcessor.SaveAsync(cap.Stamp, far.Text, far.RawText, ct);
            HuginnLog.Write($"meeting {cap.Stamp}: transcribed {far.Lines} segments on the other computer, {TimeSpan.FromSeconds(far.Seconds):m\\:ss} there, {sw.Elapsed:m\\:ss} with the sending -> {Path.GetFileName(path)}");
            HuginnLog.Write($"meeting {cap.Stamp}: echo gates: {far.Echo}{(rawPath == null ? "" : " (raw kept)")}");
            Recent.SetTranscript(jobId, path);
            // The name came from the same naming code on the other computer; the rule is applied here again, so a
            // title from an older build there is held to it too.
            var title = MeetingName.TopicOnly(far.Title);
            return new Processed(new MeetingResultFiles(path, far.Text), title.Length > 0 ? title : MeetingName.Place(far.Text), Far: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            HuginnLog.Write($"meeting {cap.Stamp}: the other computer did not do it ({ShortReason(ex)}); it is processed here");
            Show("Transcribing…");
            return null;
        }
    }

    // Rows from before Huginn named meetings itself, or filed while Odin was off, get a name once, in the background.
    private async Task NameUnnamedAsync()
    {
        foreach (var row in Recent.Entries.Where(e => e.Title == null && !e.Cancelable).ToList())
        {
            try
            {
                var path = RecentMeetings.ResolveTranscript(row);
                if (!File.Exists(path)) continue;
                var name = await MeetingName.MakeAsync(await File.ReadAllTextAsync(path), _cleaner);
                Recent.SetTitle(row.Id, name);
                HuginnLog.Write($"meeting {row.Stamp}: named \"{name}\" here (it had no name)");
            }
            catch (Exception ex) { HuginnLog.Write($"meeting {row.Stamp}: could not be named", ex); }
        }
        _shell.Refresh();
    }

    // The screen snapshots go up after the note is filed, one by one; a failure loses only the pictures.
    private static async Task UploadShotsAsync(string stamp, string slug, CancellationToken ct)
    {
        var files = SpeakerTimeline.ShotFiles(stamp).ToList();
        if (files.Count == 0 || string.IsNullOrWhiteSpace(slug)) return;
        int sent = 0;
        foreach (var f in files)
        {
            try
            {
                await OdinClient.UploadShotAsync(slug, Path.GetFileName(f)[(stamp.Length + 1)..], await File.ReadAllBytesAsync(f, ct), ct);
                sent++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { HuginnLog.Write($"meeting {stamp}: snapshot {Path.GetFileName(f)} not sent", ex); }
        }
        HuginnLog.Write($"meeting {stamp}: sent {sent} of {files.Count} snapshot(s) to Odin");
    }

    private static string FileSize(string path)
    {
        try { return File.Exists(path) ? $"{new FileInfo(path).Length / 1048576.0:F1} MB" : "missing"; }
        catch { return "?"; }
    }

    // One line for the row. No trailing "…": RecentMeetings reads that as "still in flight".
    private static string ShortReason(Exception ex)
    {
        var m = (string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return m.Length <= 90 ? m : m[..90];
    }

    // A meeting job runs below normal priority, so the desktop and a video keep the CPU first.
    private void UpdatePriority()
    {
        try
        {
            Process.GetCurrentProcess().PriorityClass =
                _processingCount > 0 ? ProcessPriorityClass.BelowNormal : ProcessPriorityClass.Normal;
        }
        catch { /* priority is a nicety */ }
    }

    // ---- calls in other apps: Google Meet, Teams, Slack huddles, Zoom ----

    // Once a second, off the UI thread: ask the OS who holds the mic, then let the detector decide on the UI thread.
    private void PollCalls()
    {
        if (Interlocked.Exchange(ref _callPolling, 1) == 1) return;
        try
        {
            IReadOnlyList<MicUser> users;
            try { users = _p.MicUsers!.Current(); }
            catch (Exception ex) { HuginnLog.Write("calls: could not read the mic users", ex); users = Array.Empty<MicUser>(); }
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Ui(() => HandleCalls(now, users));
        }
        finally { Volatile.Write(ref _callPolling, 0); }
    }

    private void HandleCalls(long now, IReadOnlyList<MicUser> users)
    {
        foreach (var e in _calls.Tick(now, users))
        {
            if (e.Kind == CallEventKind.Started) OnCallStarted(e.App);
            else OnCallEnded(e.App);
        }
    }

    private void OnCallStarted(string app)
    {
        HuginnLog.Write($"calls: {app} is using the mic (detection {DetectionMode}{(_p.Meeting.IsRecording ? ", already recording" : "")})");
        if (DetectionMode == "off" || _p.Meeting.IsRecording) return;
        if (DetectionMode == "auto")
        {
            StartForCall(app);
            return;
        }
        Ask(app);
    }

    private void OnCallEnded(string app)
    {
        HuginnLog.Write($"calls: {app} let go of the mic");
        if (_prompting == app) HidePrompt();
        if (_callRecording == app && _p.Meeting.IsRecording)
        {
            HuginnLog.Write($"calls: auto stop, {app} ended");
            _ = StopMeetingAsync();
        }
    }

    private void StartForCall(string app)
    {
        StartMeeting();
        if (!_p.Meeting.IsRecording) return;
        _callRecording = app;
        _source = app;
        HuginnLog.Write($"calls: recording {app}");
        SetStatus($"● Recording · {app}");
    }

    /// <summary>Shows the pop-up for a meeting (one at a time: a second source waits until the first is answered).</summary>
    private void Ask(string app)
    {
        if (_prompting != null) return;
        _prompting = app;
        HuginnLog.Write($"meeting detection: asking about {app}");
        _p.CallPrompt?.ShowPrompt(app);
    }

    private void StartFromPrompt()
    {
        var app = _prompting;
        HidePrompt();
        if (app == null || _p.Meeting.IsRecording) return;
        if (app == KumoSource) StartForKumo(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "started from the pop-up", _lastKumoRead);
        else StartForCall(app);
    }

    private void PromptOption(string option)
    {
        var app = _prompting;
        switch (option)
        {
            case "auto":
                SetDetectionMode("auto");
                if (app != null && !_p.Meeting.IsRecording)  // "always" includes this meeting
                {
                    if (app == KumoSource) StartForKumo(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "started from the pop-up", _lastKumoRead);
                    else StartForCall(app);
                }
                break;
            case "off":
                if (app == KumoSource) _detector.OnUserEnded();
                SetDetectionMode("off");
                break;
            default:
                HuginnLog.Write($"meeting detection: not now for {app}");
                // Kumospace would ask again a second later; "not now" lasts until I am alone, as a hand stop does.
                if (app == KumoSource) _detector.OnUserEnded();
                HidePrompt();
                break;
        }
    }

    private void HidePrompt()
    {
        if (_prompting == null) return;
        _prompting = null;
        _p.CallPrompt?.HidePrompt();
    }

    // ---- Kumospace auto-record ----

    private void OnKumoStatus(KumoStatus s)
    {
        KumoRestartAvailable = s == KumoStatus.NoDebugPort;
        KumoStatusText = s switch
        {
            KumoStatus.Watching => KumoWatchingText(),
            KumoStatus.NotInSpace => "Kumospace: open, not in a space",
            KumoStatus.NotRunning => "Kumospace: not running",
            KumoStatus.NoDebugPort => "Kumospace: restart it so Huginn can follow meetings",
            _ => "Kumospace: off",
        };
        _shell.Refresh();
    }

    private string KumoWatchingText()
    {
        var zone = _kumo.Zone.Length > 0 ? _kumo.Zone : "outside a room";
        var with = _lastKumoRead?.Company.Count ?? 0;
        return with == 0 ? $"Kumospace: watching · {zone}" : $"Kumospace: watching · {zone} · with {with}";
    }

    public async Task RestartKumoAsync()
    {
        if (_p.Meeting.IsRecording) { _shell.Warn("Huginn — not now", "Stop the recording before restarting Kumospace."); return; }
        KumoRestartAvailable = false;
        KumoStatusText = "Kumospace: restarting…";
        _shell.Refresh();
        if (!await _p.Kumo.RestartWithDebugPortAsync(HuginnSettings.Current.KumoDebugPort))
        {
            _shell.Warn("Huginn — Kumospace did not close", "Close Kumospace yourself, then start it again from Huginn's menu.");
            OnKumoStatus(_kumo.Status);
        }
    }

    // Once a second, on the UI thread: take the watcher's reads, add them to the recording's speaker timeline,
    // and let the detector start or stop a recording.
    private void OnKumoTick()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bool any = false;
        while (_kumoReads.TryDequeue(out var snap)) { any = true; HandleKumoRead(now, snap); }
        if (!any) HandleKumoRead(now, null);
        TrackPresence(now);
    }

    // The work timer: my Kumospace status, written on each change (and every 5 min) to Presence\<day>.tsv.
    private void TrackPresence(long now)
    {
        var snap = _lastKumoRead;
        string state = _kumo.Status switch
        {
            KumoStatus.NotRunning => "closed",
            KumoStatus.Off or KumoStatus.NoDebugPort => "unknown",
            _ when snap == null || now - _lastKumoReadAt > 10_000 => "unknown",
            _ when !snap.InSpace => "not in space",
            _ => snap.Availability.Length > 0 ? snap.Availability : "unknown",
        };
        _presence.Observe(DateTime.Now, state);
        if (_presenceRefresh++ % 30 == 0) // the menu line, every 30 s
        {
            PresenceTodayText = "Kumospace today: " + PresenceLog.Describe(PresenceLog.Totals(Paths.PresenceDir(), DateTime.Today, DateTime.Now));
            _shell.Refresh();
        }
    }

    /// <summary>The current week, Monday to Sunday like a calendar: each day by its date, today as "Today",
    /// future days "—", with away time too.</summary>
    public IReadOnlyList<string> PresenceWeek() => PresenceLog.Week(Paths.PresenceDir(), DateTime.Now);

    private void HandleKumoRead(long now, KumoSnapshot? snap)
    {
        if (snap != null)
        {
            var before = _lastKumoRead?.Company.Count ?? 0;
            _lastKumoRead = snap;
            _lastKumoReadAt = now;
            // Who I am with, logged on each change, so a late or missed start can be traced afterwards.
            var with = snap.InCall ? string.Join(", ", snap.Company.Select(p => p.Name).OrderBy(n => n)) : "";
            if (with != _lastCompanyLogged)
            {
                _lastCompanyLogged = with;
                HuginnLog.Write(with.Length == 0 ? "kumo: alone" : $"kumo: with {with} ({(snap.Zone is { Length: > 0 } zn ? zn : "outside a room")})");
            }
            if (_kumo.Status == KumoStatus.Watching && snap.Company.Count != before)
            {
                KumoStatusText = KumoWatchingText();
                _shell.Refresh();
            }
            // Kumospace data belongs to a Kumospace recording or one started by hand, not to a Meet or Zoom call.
            if (_p.Meeting.IsRecording && _current is { } cur && (_source == null || _source == KumoSource)) _timeline?.Add(snap, cur.Stamp);
        }
        var (action, reason) = _detector.Tick(now, snap, _p.Meeting.IsRecording);
        // Asked about a Kumospace meeting and now nobody is around: the question is moot.
        if (_prompting == KumoSource && snap != null && (!snap.InCall || snap.Company.Count == 0)) HidePrompt();
        if (DetectionMode == "off") return;
        if (action == KumoAction.Start && !_p.Meeting.IsRecording)
        {
            if (DetectionMode == "auto") StartForKumo(now, reason, snap);
            else Ask(KumoSource);
        }
        else if (action == KumoAction.Stop && _p.Meeting.IsRecording)
        {
            _ = StopAutoMeetingAsync(reason);
        }
    }

    private const string KumoSource = "Kumospace";

    // Who is talking, hands, reactions and chat from a call in the browser (the extension: Google Meet and the other
    // sites it reads). Kept only while recording.
    private void OnMeetEvents(MeetBridge.BrowserCall call, IReadOnlyList<KumoEvent> events)
    {
        var meeting = call.Tag;
        bool keep = _p.Meeting.IsRecording && _timeline != null;
        foreach (var note in events.Where(e => e.Kind == "note")) HuginnLog.Write($"{meeting}: {note.Text}");

        // Talk samples name the lines. They come five a second, so the log gets one line per person, not one per sample.
        // A Kumospace recording has Kumospace's own levels; a Meet tab open beside it must not name its lines.
        // The system-audio track never holds my own voice, so a sample under my own name names nothing.
        var mine = Settings.UserName.Trim();
        var talk = events.Where(e => e.Kind == "talk" && e.Who.Trim().Length > 0
            && !e.Who.Trim().Equals("Me", StringComparison.OrdinalIgnoreCase)
            && !(mine.Length > 0 && e.Who.Trim().Equals(mine, StringComparison.OrdinalIgnoreCase))).ToList();
        if (talk.Count > 0 && keep && _source != KumoSource)
            foreach (var e in talk)
            {
                if (!_timeline!.People.Contains(e.Who.Trim())) HuginnLog.Write($"{meeting}: {e.Who.Trim()} is talking (the first time in this recording)");
                _timeline.AddTalk(call.App, e.Who, e.T); // the app whose page showed the person talking
            }

        var rest = events.Where(e => e.Kind is not ("talk" or "note")).ToList();
        if (rest.Count == 0) return;
        HuginnLog.Write($"{meeting}: {string.Join(", ", rest.Select(e => e.Kind))}{(keep ? "" : " (not recording, dropped)")}");
        if (keep) foreach (var e in rest) _timeline!.AddEvent(e);
    }

    // A snapshot of a screen shared in a call in the browser (the extension). Kept only while recording, and not in
    // a Kumospace recording, which takes its own.
    private void OnMeetShot(MeetBridge.BrowserCall call, KumoShot shot)
    {
        var whose = shot.Who.Length > 0 ? $"{shot.Who}'s screen" : "the shared screen";
        if (!_p.Meeting.IsRecording || _timeline == null || _current is not { } cur || _source == KumoSource)
        {
            HuginnLog.Write($"{call.Tag}: snapshot of {whose} (not recording, dropped)");
            return;
        }
        var name = _timeline.AddShot(shot, cur.Stamp);
        if (name != null) HuginnLog.Write($"{call.Tag}: snapshot of {whose} saved as {name} ({shot.Jpeg.Length / 1024} KB)");
    }

    // A Kumospace meeting starts recording (automatically or from the pop-up); the detector then owns its stop.
    private void StartForKumo(long now, string reason, KumoSnapshot? snap)
    {
        StartMeeting();
        if (!_p.Meeting.IsRecording) return;
        _detector.OnAutoStarted(now);
        _source = KumoSource;
        var zone = snap?.Zone is { Length: > 0 } z ? z : "outside a room";
        var people = string.Join(", ", snap?.Company.Select(p => p.Name) ?? Array.Empty<string>());
        HuginnLog.Write($"kumo: auto start ({reason}), {zone}, with {people}");
        SetStatus($"● Recording · {zone} · with {people}");
    }

    private async Task StopAutoMeetingAsync(string reason)
    {
        var speech = _detector.SpeechSecondsThisRecording;
        _detector.OnAutoStopped();
        HuginnLog.Write($"kumo: auto stop ({reason}), {speech:F0} s of speech");
        if (speech < HuginnSettings.Current.KumoMinSpeechSeconds)
        {
            var cap = await EndMeetingAsync();
            DeleteCapture(cap);
            if (cap != null) _ = _worker?.MeetingDroppedAsync(cap.Stamp);
            _p.Notes.Discarded();
            SetStatus($"Short chat not kept ({speech:F0} s of talk)");
            return;
        }
        await StopMeetingAsync();
    }

    // ---- helpers ----

    private void SetStatus(string text) => _shell.SetStatus(text);

    private void Ui(Action action) => _shell.RunOnUi(action);

    public async ValueTask DisposeAsync()
    {
        _quitWatcher?.Dispose();
        _kumoTimer?.Dispose();
        _audioTimer?.Dispose();
        _worker?.Dispose();
        _meet.Dispose();
        _callTimer?.Dispose();
        _previewTimer?.Dispose();
        _kumo.Dispose();
        _p.Hotkey.Dispose();
        _p.Dictation.Dispose();
        _p.Meeting.Dispose();
        await _transcriber.DisposeAsync().ConfigureAwait(false);
        await _cleaner.DisposeAsync().ConfigureAwait(false);
    }
}
