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
    private readonly KumoMeetingDetector _detector;
    private readonly ConcurrentQueue<KumoSnapshot> _kumoReads = new();
    private readonly PresenceLog _presence = new(Paths.PresenceDir());
    private Timer? _kumoTimer;
    private Timer? _previewTimer;
    private FileSystemWatcher? _quitWatcher;
    private SpeakerTimeline? _timeline;
    private KumoSnapshot? _lastKumoRead;
    private string? _lastCompanyLogged;
    private long _lastKumoReadAt;
    private int _presenceRefresh;
    private bool _kumoRestartHinted;
    private MeetingCapture? _current;      // the recording in progress
    private bool _meetingActive;
    private int _processingCount;
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
        SendMeetings = Settings.SendMeetingsToOdin;
        KumoAuto = Settings.KumoAutoRecord;
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
    public bool KumoAuto { get; private set; }
    /// <summary>"always", "record" or "off" (see <see cref="Settings.NotesMode"/>).</summary>
    public string NotesMode { get; private set; }
    public bool IsMeetingRecording => _p.Meeting.IsRecording;
    public string HotkeyName => _p.Hotkey.Name;
    public string Microphone => _p.Dictation.Device;
    public bool KumoInstalled => _p.Kumo.IsInstalled;
    public string KumoStatusText { get; private set; } = "Kumospace: starting…";
    /// <summary>The Kumospace status line is a button: it restarts Kumospace with the port.</summary>
    public bool KumoRestartAvailable { get; private set; }
    public string PresenceTodayText { get; private set; } = "Kumospace today: nothing yet";
    public DateTime? MeetingStartedLocal => _current?.StartedLocal;

    /// <summary>Starts the quit watcher, the Kumospace watcher and the first-run model download.</summary>
    public void Start()
    {
        HuginnLog.Write($"Huginn started ({Environment.OSVersion.Platform}, {(DictationEnabled ? "dictation on" : "dictation off")})");
        StartQuitWatcher();
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
        SendMeetings = on;
        Settings.SendMeetingsToOdin = on;
        _shell.Refresh();
    }

    public void SetKumoAuto(bool on)
    {
        KumoAuto = on;
        Settings.KumoAutoRecord = on;
        if (_kumo.Status == KumoStatus.NoDebugPort) OnKumoStatus(_kumo.Status);
        _shell.Refresh();
    }

    public void SetNotesMode(string mode)
    {
        NotesMode = mode;
        Settings.NotesMode = mode;
        _shell.Refresh();
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
        _meetingActive = true;
        _transcriber.KeepWarm = true; // load now, so Stop finds the model in memory
        _ = _transcriber.EnsureLoadedAsync();
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

    // Shared teardown for every stop: the capture is flushed and the UI is back to ready at once. The next
    // meeting can start while (if kept) this one processes in the background.
    private async Task<MeetingCapture?> EndMeetingAsync()
    {
        SaveNotes(); // the last edits, before the recorder stops accepting them
        _p.Meeting.Stop();
        var cap = await _p.Meeting.FinalizeAsync();
        SaveTimeline(cap);
        _current = null;

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
        if (cap != null) HuginnLog.Write($"meeting {cap.Stamp}: discarded by the user, audio deleted");
        _p.Notes.Discarded();
        SetStatus("Recording discarded"); // no notification: the user just asked for it
    }

    private static void DeleteCapture(MeetingCapture? cap)
    {
        if (cap == null) return;
        foreach (var path in new[] { cap.MicPath, cap.LoopPath, cap.NotesPath, SpeakerTimeline.PathFor(cap.Stamp) })
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
        }
    }

    // Who Kumospace heard during the recording goes next to its WAVs, for the transcript step to name the speakers.
    private void SaveTimeline(MeetingCapture? cap)
    {
        var tl = _timeline;
        _timeline = null;
        if (cap == null || tl == null || (tl.Rooms.Count == 0 && tl.People.Count == 0 && tl.Remote.Count == 0)) return;
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

    /// <summary>The cancel button on an in-flight row: stop that job's transcription and Odin filing. The WAVs
    /// stay on disk; nothing is deleted.</summary>
    public void CancelJob(int id)
    {
        if (_jobCts.TryGetValue(id, out var cts))
        {
            try { cts.Cancel(); } catch { /* already disposed */ }
        }
    }

    // Transcribe + file a finished capture in the BACKGROUND; it never blocks a new recording. Several can run at
    // once (meeting after meeting); transcription serializes on the shared model, recording does not.
    private async Task ProcessCaptureAsync(MeetingCapture cap)
    {
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
            var r = await MeetingProcessor.TranscribeAsync(cap, _transcriber, cts.Token);
            HuginnLog.Write($"meeting {cap.Stamp}: transcribed {r.SegmentCount} segments in {sw.Elapsed:m\\:ss} -> {Path.GetFileName(r.TranscriptPath)}");
            HuginnLog.Write($"meeting {cap.Stamp}: echo gates: {r.Echo}{(r.RawTranscriptPath == null ? "" : " (raw kept)")}");
            Recent.SetTranscript(jobId, r.TranscriptPath);
            Ui(() => _p.Notes.SetTranscriptReady(cap.Stamp));
            if (SendMeetings)
            {
                Recent.SetStatus(jobId, "Filing to Odin…", JobTone.Busy, cancelable: true);
                Ui(() => _p.Notes.SetJobStatus(cap.Stamp, "Filing to Odin…"));
                try
                {
                    var odin = await OdinClient.ProcessMeetingAsync(r.Text, cap.StartedLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), notes, cts.Token);
                    HuginnLog.Write($"meeting {cap.Stamp}: filed to Odin ({odin.Classification}, {odin.TaskCount} task(s)) \"{odin.Title}\"");
                    if (!string.IsNullOrWhiteSpace(odin.Title)) Recent.SetTitle(jobId, odin.Title);
                    var sec = string.IsNullOrEmpty(odin.SecondaryType) ? "" : $" + {odin.SecondaryType}";
                    Recent.SetStatus(jobId, $"✓ {odin.Classification} · {odin.TaskCount} task(s)", JobTone.Good, cancelable: false);
                    Ui(() => _p.Notes.SetJobStatus(cap.Stamp, $"✓ Filed to Odin · {odin.Title}"));
                    _shell.Info("Huginn — meeting filed to Odin", $"{odin.Classification} · {odin.TaskCount} task(s){sec}\n{odin.Title}");
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    HuginnLog.Write($"meeting {cap.Stamp}: Odin filing failed", ex);
                    Recent.SetStatus(jobId, "Saved · Odin offline", JobTone.Warn, cancelable: false);
                    Ui(() => _p.Notes.SetJobStatus(cap.Stamp, "Saved · Odin offline"));
                    _shell.Warn("Huginn — Odin not reachable", $"Saved the transcript to disk; file it later.\n{Path.GetFileName(r.TranscriptPath)}");
                    _shell.Reveal(Recent.TranscriptPathFor(cap.Stamp) ?? r.TranscriptPath);
                }
            }
            else
            {
                Recent.SetStatus(jobId, "Saved", JobTone.Good, cancelable: false);
                Ui(() => _p.Notes.SetJobStatus(cap.Stamp, "✓ Transcript saved"));
                _shell.Info("Huginn — meeting transcript ready", Path.GetFileName(r.TranscriptPath));
                _shell.Reveal(Recent.TranscriptPathFor(cap.Stamp) ?? r.TranscriptPath);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            HuginnLog.Write($"meeting {cap.Stamp}: cancelled by user after {sw.Elapsed:m\\:ss}");
            Recent.SetStatus(jobId, "Cancelled", JobTone.Muted, cancelable: false);
            Ui(() => _p.Notes.SetJobStatus(cap.Stamp, "Cancelled"));
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
        }
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
        if (s == KumoStatus.NoDebugPort && KumoAuto && !_kumoRestartHinted)
        {
            _kumoRestartHinted = true;
            _shell.Info("Huginn — Kumospace needs a restart",
                "It was started without the port Huginn reads. Click \"Kumospace: restart it…\" in Huginn's menu to restart it (you leave your room for a few seconds).");
        }
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
            if (_p.Meeting.IsRecording) _timeline?.Add(snap);
        }
        var (action, reason) = _detector.Tick(now, snap, _p.Meeting.IsRecording);
        if (!KumoAuto) return;
        if (action == KumoAction.Start && !_p.Meeting.IsRecording)
        {
            StartMeeting();
            if (!_p.Meeting.IsRecording) return;
            _detector.OnAutoStarted(now);
            var zone = snap?.Zone is { Length: > 0 } z ? z : "outside a room";
            var people = string.Join(", ", snap?.Company.Select(p => p.Name) ?? Array.Empty<string>());
            HuginnLog.Write($"kumo: auto start ({reason}), {zone}, with {people}");
            _shell.Info("Huginn — recording a Kumospace meeting", $"{zone} · with {people}");
        }
        else if (action == KumoAction.Stop && _p.Meeting.IsRecording)
        {
            _ = StopAutoMeetingAsync(reason);
        }
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
            _p.Notes.Discarded();
            SetStatus("Short chat not kept");
            _shell.Info("Huginn — short chat not kept", $"Only {speech:F0} s of talk, so the recording was deleted.");
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
        _previewTimer?.Dispose();
        _kumo.Dispose();
        _p.Hotkey.Dispose();
        _p.Dictation.Dispose();
        _p.Meeting.Dispose();
        await _transcriber.DisposeAsync().ConfigureAwait(false);
        await _cleaner.DisposeAsync().ConfigureAwait(false);
    }
}
