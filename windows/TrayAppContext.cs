using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using NAudio.Wave;

namespace Huginn;

/// <summary>
/// The whole app: a tray icon that wires the keyboard hook → recorder → transcriber → paste,
/// with an Enabled/Disabled toggle that frees the GPU and CPU when off.
/// </summary>
internal sealed class TrayAppContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly Form _marshal;            // hidden window: gives us an STA thread to Invoke onto
    private readonly KeyboardHook _hook;
    private readonly AudioRecorder _recorder;
    private readonly Transcriber _transcriber;
    private readonly TextInjector _injector;
    private readonly RecordingOverlay? _overlay;
    private readonly MediaController? _media;
    private readonly Cleaner _cleaner;
    private readonly MeetingRecorder _meeting;
    private readonly MeetingOverlay? _meetingOverlay;
    private readonly MeetingPanel _panel;
    private readonly NotesPanel _notes;
    private string _notesMode = "always";
    private ToolStripMenuItem _notesOffItem = null!, _notesAlwaysItem = null!, _notesRecordItem = null!;
    private ToolStripMenuItem _meetingItem = null!;
    private ToolStripMenuItem _discardItem = null!;
    private ToolStripMenuItem _sendMeetingsItem = null!;
    private ToolStripMenuItem _panelItem = null!;
    private FileSystemWatcher? _quitWatcher;
    private bool _quitting;
    private bool _meetingActive;
    private int _processingCount;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, CancellationTokenSource> _jobCts = new();
    private bool _sendMeetings;
    private bool _panelVisible;
    private readonly System.Windows.Forms.Timer? _previewTimer;
    private bool _previewBusy;
    private volatile bool _cancelledDictation; // set when the cancel key fires; suppresses transcribe/paste (read on a bg thread)
    private const int PreviewWindowSeconds = 10; // live preview transcribes only the last N seconds → constant cost
    private bool _cleanupEnabled;
    private ToolStripMenuItem _cleanupItem = null!;
    // Kumospace: the watcher reads the app once a second; the detector turns that into start/stop; the timeline
    // keeps who was talking during the current recording (names the "Them" lines later).
    private readonly KumoWatcher _kumo;
    private readonly KumoMeetingDetector _detector;
    private readonly System.Collections.Concurrent.ConcurrentQueue<KumoSnapshot> _kumoReads = new();
    private readonly System.Windows.Forms.Timer _kumoTimer = new() { Interval = 1000 };
    private SpeakerTimeline? _timeline;
    private KumoSnapshot? _lastKumoRead;
    private string? _lastCompanyLogged;
    private long _lastKumoReadAt;
    private readonly PresenceLog _presence = new(Paths.PresenceDir());
    private ToolStripMenuItem _presenceItem = null!;
    private int _presenceRefresh;
    private bool _kumoAuto;
    private bool _kumoRestartHinted;
    private ToolStripMenuItem _kumoAutoItem = null!;
    private ToolStripMenuItem _kumoStatusItem = null!;
    private readonly string _modelPath;

    private readonly Icon _iconIdle;
    private readonly Icon _iconRec;
    private readonly Icon _iconBusy;
    private readonly Icon _iconOff;

    private bool _enabled;
    private ToolStripMenuItem _toggleItem = null!;
    private ToolStripMenuItem _statusItem = null!;

    public TrayAppContext()
    {
        _modelPath = Paths.ModelPath();
        HuginnLog.Write("Huginn started");

        _marshal = new Form { ShowInTaskbar = false };
        _ = _marshal.Handle; // force handle creation on the UI thread so Invoke works

        _iconIdle = MakeIcon(Color.FromArgb(60, 160, 255));   // blue
        _iconRec = MakeIcon(Color.FromArgb(230, 60, 60));     // red
        _iconBusy = MakeIcon(Color.FromArgb(240, 190, 40));   // amber
        _iconOff = MakeIcon(Color.FromArgb(120, 120, 120));   // grey

        _recorder = new AudioRecorder { DeviceNumber = MicSettings.Load() };
        _transcriber = new Transcriber(_modelPath, Paths.VadModelPath(), Paths.PreviewModelPath());
        _injector = new TextInjector(_marshal);
        _overlay = Config.ShowOverlay ? new RecordingOverlay() : null;
        _media = Config.PauseMediaWhileRecording ? new MediaController() : null;
        _cleaner = new Cleaner(Paths.CleanupModelPath());
        // Personal dictionary → bias Whisper's decoder toward domain-term spellings + give the cleanup LLM a
        // spelling authority for them. Edit %LOCALAPPDATA%\Huginn\dictionary.txt to add terms.
        _transcriber.InitialPrompt = Dictionary.WhisperPrompt();
        _cleaner.Vocabulary = Dictionary.VocabularyLine();
        _meeting = new MeetingRecorder { MicDeviceNumber = MicSettings.Load() };
        _meetingOverlay = Config.ShowMeetingOverlay ? new MeetingOverlay() : null;
        _panel = new MeetingPanel();
        _panel.RecordToggleRequested += ToggleMeeting;
        _panel.CancelRequested += CancelJob;
        _panel.DiscardRequested += () => UserDiscard();
        _meeting.LevelChanged += _panel.SetLevel;
        // The notepad: the recorder's controls, beside it. Settings.NotesMode says when it shows — "always"
        // (paired with the recorder's show/hide), "record" (opens when a recording starts), or "off".
        _notesMode = Settings.NotesMode;
        _notes = new NotesPanel();
        _notes.RecordToggleRequested += ToggleMeeting;
        _notes.DiscardRequested += () => UserDiscard();
        _notes.NotesChanged += SaveNotes;
        _notes.TranscriptResolver = _panel.TranscriptPathFor;
        _notes.HideRequested += () => { if (_notesMode == "always") _panel.HidePanel(); else _notes.HidePanel(); };
        _panel.LocationChanged += (_, _) => _notes.RecorderChanged(_panel.Bounds, _panel.Visible);
        _panel.VisibleChanged += (_, _) =>
        {
            _notes.RecorderChanged(_panel.Bounds, _panel.Visible);
            if (_notesMode != "always") return;
            if (_panel.Visible) _notes.ShowPanel(); else _notes.HidePanel();
        };
        var ms = HuginnSettings.Current;
        _kumo = new KumoWatcher(ms.KumoDebugPort);
        _detector = new KumoMeetingDetector
        {
            TalkLevel = ms.KumoTalkLevel,
            StartSpeechSeconds = ms.KumoStartSpeechSeconds,
            AloneStopSeconds = ms.KumoAloneStopSeconds,
            QuietStopSeconds = ms.KumoQuietStopSeconds,
        };
        _kumo.Snapshot += snap => _kumoReads.Enqueue(snap);
        _kumo.StatusChanged += s => OnUi(() => OnKumoStatus(s));
        _kumoTimer.Tick += OnKumoTick;
        _kumoAuto = Settings.KumoAutoRecord;
        _sendMeetings = Settings.SendMeetingsToOdin;
        _panelVisible = Settings.MeetingPanelVisible;
        _cleanupEnabled = Settings.CleanupEnabled;
        _hook = new KeyboardHook(Config.PushToTalkVk, Config.PushToTalkModifierVk, Config.SwallowHotkey, Config.CancelVk);

        _recorder.RecordingComplete += OnRecordingComplete;
        _recorder.SilenceDetected += OnSilence;
        _recorder.RecordingAborted += OnAborted;
        _recorder.LevelChanged += lvl => OnUi(() => _overlay?.SetLevel(lvl));
        _recorder.CaptureStarted += OnCaptureStarted;
        _transcriber.Status += SetStatus;
        _cleaner.Status += SetStatus;
        _hook.KeyDown += OnTalkDown;
        _hook.KeyUp += OnTalkUp;
        _hook.CancelRequested += OnCancel;

        if (Config.LivePreview && _overlay != null)
        {
            _previewTimer = new System.Windows.Forms.Timer { Interval = 700 };
            _previewTimer.Tick += OnPreviewTick;
        }

        _tray = new NotifyIcon
        {
            Icon = _iconOff,
            Visible = true,
            Text = "Huginn",
            ContextMenuStrip = BuildMenu(),
        };

        if (_panelVisible) _panel.ShowPanel();

        StartQuitWatcher();

        if (KumoLauncher.IsInstalled)
        {
            KumoLauncher.EnsureShortcuts(ms.KumoDebugPort);
            _kumo.Start();
            _kumoTimer.Start();
        }
        else _kumoStatusItem.Text = "Kumospace: not installed";

        _ = InitializeAsync();
    }

    /// <summary>The programmatic quit channel (SKILL lifecycle contract): a host app writes
    /// %LOCALAPPDATA%\Huginn\quit.flag → Huginn flushes any active recording to disk (skipping
    /// transcription — the WAVs survive) and exits. A stale flag from a crashed host is cleared
    /// at startup WITHOUT quitting.</summary>
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
            _quitWatcher.Created += (_, _) => OnUi(() => _ = QuitFromFlagAsync());
            _quitWatcher.Changed += (_, _) => OnUi(() => _ = QuitFromFlagAsync());
            _quitWatcher.EnableRaisingEvents = true;
        }
        catch { /* no watcher → the tray Quit still works; the host falls back to kill */ }
    }

    private async Task QuitFromFlagAsync()
    {
        if (_quitting) return; // Created + Changed can both fire for one write
        _quitting = true;
        HuginnLog.Write("quit flag received");
        try { File.Delete(Paths.QuitFlagPath()); } catch { /* best effort */ }
        if (_meeting.IsRecording)
        {
            SaveNotes();
            _meeting.Stop();
            SaveTimeline(await _meeting.FinalizeAsync()); // flush the WAV writers — the recording survives on disk unprocessed
        }
        Quit();
    }

    private async Task InitializeAsync()
    {
        if (!File.Exists(_modelPath))
        {
            SetIcon(_iconBusy);
            SetStatus("Downloading model… (one-time)");
            try
            {
                await ModelDownloader.EnsureAsync(_modelPath, Config.ModelUrl, p => SetStatus($"Downloading model… {p:P0}"));
            }
            catch (Exception ex)
            {
                HuginnLog.Write("model download failed", ex);
                SetIcon(_iconOff);
                SetStatus($"Model download failed: {ex.Message}");
                ShowBalloon("Huginn — model download failed", ex.Message);
                return;
            }
        }
        Enable();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        _statusItem = new ToolStripMenuItem("Starting…") { Enabled = false };
        _toggleItem = new ToolStripMenuItem("Enabled", null, (_, _) => Toggle()) { Checked = true };
        var keyInfo = new ToolStripMenuItem($"Hold {HotkeyName()} to talk") { Enabled = false };

        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_toggleItem);
        menu.Items.Add(keyInfo);
        menu.Items.Add(BuildMicMenu());
        _cleanupItem = new ToolStripMenuItem("Clean up text (AI)", null, (_, _) => ToggleCleanup()) { Checked = _cleanupEnabled };
        menu.Items.Add(_cleanupItem);
        menu.Items.Add(new ToolStripSeparator());
        _meetingItem = new ToolStripMenuItem("Start meeting recording", null, (_, _) => ToggleMeeting());
        menu.Items.Add(_meetingItem);
        _discardItem = new ToolStripMenuItem("Stop && discard recording", null, (_, _) => UserDiscard()) { Enabled = false };
        menu.Items.Add(_discardItem);
        _sendMeetingsItem = new ToolStripMenuItem("Auto-file meetings to Odin", null, (_, _) => ToggleSendMeetings()) { Checked = _sendMeetings };
        menu.Items.Add(_sendMeetingsItem);
        _kumoAutoItem = new ToolStripMenuItem("Auto-record Kumospace meetings", null, (_, _) => ToggleKumoAuto()) { Checked = _kumoAuto };
        menu.Items.Add(_kumoAutoItem);
        _kumoStatusItem = new ToolStripMenuItem("Kumospace: starting…", null, (_, _) => _ = RestartKumoAsync()) { Enabled = false };
        menu.Items.Add(_kumoStatusItem);
        _presenceItem = new ToolStripMenuItem("Kumospace today: nothing yet");
        _presenceItem.DropDownItems.Add(new ToolStripMenuItem("…") { Enabled = false });
        _presenceItem.DropDownOpening += (_, _) => FillPresenceDays();
        menu.Items.Add(_presenceItem);
        _panelItem = new ToolStripMenuItem("Show recorder panel", null, (_, _) => TogglePanel()) { Checked = _panelVisible };
        menu.Items.Add(_panelItem);
        var notesMenu = new ToolStripMenuItem("Notes window");
        _notesOffItem = new ToolStripMenuItem("Off", null, (_, _) => SetNotesMode("off"));
        _notesAlwaysItem = new ToolStripMenuItem("Always on, beside the recorder", null, (_, _) => SetNotesMode("always"));
        _notesRecordItem = new ToolStripMenuItem("Open when a recording starts", null, (_, _) => SetNotesMode("record"));
        notesMenu.DropDownItems.AddRange(new ToolStripItem[] { _notesOffItem, _notesAlwaysItem, _notesRecordItem });
        menu.Items.Add(notesMenu);
        RefreshNotesModeChecks();
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Quit", null, (_, _) => Quit()));
        return menu;
    }

    private ToolStripMenuItem BuildMicMenu()
    {
        var mic = new ToolStripMenuItem("Microphone");

        void rebuild()
        {
            mic.DropDownItems.Clear();
            mic.DropDownItems.Add(new ToolStripMenuItem("System default", null, (_, _) => SelectMic(-1))
            {
                Checked = _recorder.DeviceNumber == -1,
            });
            mic.DropDownItems.Add(new ToolStripSeparator());
            for (int i = 0; i < WaveInEvent.DeviceCount; i++)
            {
                int idx = i;
                string name = WaveInEvent.GetCapabilities(i).ProductName;
                mic.DropDownItems.Add(new ToolStripMenuItem(name, null, (_, _) => SelectMic(idx))
                {
                    Checked = _recorder.DeviceNumber == idx,
                });
            }
        }

        mic.DropDownOpening += (_, _) => rebuild(); // refresh if devices were plugged/unplugged
        rebuild();
        return mic;
    }

    private void SelectMic(int deviceNumber)
    {
        _recorder.DeviceNumber = deviceNumber;
        MicSettings.Save(deviceNumber);
        SetStatus(deviceNumber == -1
            ? "Mic: System default"
            : $"Mic: {WaveInEvent.GetCapabilities(deviceNumber).ProductName}");
    }

    private void Toggle()
    {
        if (_enabled) Disable();
        else Enable();
    }

    private void ToggleCleanup()
    {
        _cleanupEnabled = !_cleanupEnabled;
        _cleanupItem.Checked = _cleanupEnabled;
        Settings.CleanupEnabled = _cleanupEnabled;
        if (_cleanupEnabled) _ = _cleaner.PrepareAsync();
        else _ = _cleaner.UnloadAsync();
    }

    // ---- Meeting recording (separate flow from push-to-talk dictation) ----

    private void ToggleMeeting()
    {
        if (_meeting.IsRecording) { _detector.OnUserEnded(); _ = StopMeetingAsync(); }
        else StartMeeting();
    }

    private void UserDiscard()
    {
        if (!_meeting.IsRecording) return;
        _detector.OnUserEnded();
        _ = DiscardMeetingAsync();
    }

    // ---- Kumospace auto-record ----

    private void ToggleKumoAuto()
    {
        _kumoAuto = !_kumoAuto;
        _kumoAutoItem.Checked = _kumoAuto;
        Settings.KumoAutoRecord = _kumoAuto;
        if (_kumo.Status == KumoStatus.NoDebugPort) OnKumoStatus(_kumo.Status);
    }

    private void OnKumoStatus(KumoStatus s)
    {
        _kumoStatusItem.Enabled = s == KumoStatus.NoDebugPort;
        _kumoStatusItem.Text = s switch
        {
            KumoStatus.Watching => KumoWatchingText(),
            KumoStatus.NotInSpace => "Kumospace: open, not in a space",
            KumoStatus.NotRunning => "Kumospace: not running",
            KumoStatus.NoDebugPort => "Kumospace: restart it so Huginn can follow meetings",
            _ => "Kumospace: off",
        };
        if (s == KumoStatus.NoDebugPort && _kumoAuto && !_kumoRestartHinted)
        {
            _kumoRestartHinted = true;
            ShowInfo("Huginn — Kumospace needs a restart",
                "It was started without the port Huginn reads. Tray → \"Kumospace: restart it…\" restarts it (you leave your room for a few seconds).");
        }
    }

    private string KumoWatchingText()
    {
        var zone = _kumo.Zone.Length > 0 ? _kumo.Zone : "outside a room";
        var with = _lastKumoRead?.Company.Count ?? 0;
        return with == 0 ? $"Kumospace: watching · {zone}" : $"Kumospace: watching · {zone} · with {with}";
    }

    private async Task RestartKumoAsync()
    {
        if (_meeting.IsRecording) { ShowBalloon("Huginn — not now", "Stop the recording before restarting Kumospace."); return; }
        _kumoStatusItem.Enabled = false;
        _kumoStatusItem.Text = "Kumospace: restarting…";
        if (!await KumoLauncher.RestartWithDebugPortAsync(HuginnSettings.Current.KumoDebugPort))
        {
            ShowBalloon("Huginn — Kumospace did not close", "Close Kumospace yourself; its shortcuts now start it the right way.");
            OnKumoStatus(_kumo.Status);
        }
    }

    // Once a second, on the UI thread: take the watcher's reads, add them to the current recording's speaker
    // timeline, and let the detector start or stop a recording.
    private void OnKumoTick(object? sender, EventArgs e)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bool any = false;
        while (_kumoReads.TryDequeue(out var snap)) { any = true; HandleKumoRead(now, snap); }
        if (!any) HandleKumoRead(now, null);
        TrackPresence(now);
    }

    // The work timer: my Kumospace status, written once a change (and every 5 min) to Presence\<day>.tsv.
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
        if (_presenceRefresh++ % 30 == 0) // the tray line, every 30 s
            _presenceItem.Text = "Kumospace today: " + PresenceLog.Describe(PresenceLog.Totals(Paths.PresenceDir(), DateTime.Today, DateTime.Now));
    }

    private void FillPresenceDays()
    {
        _presenceItem.DropDownItems.Clear();
        // The current week, Monday to Sunday, like a calendar: each day by its date, today as "Today".
        var now = DateTime.Now;
        var today = DateTime.Today;
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        for (int i = 0; i < 7; i++)
        {
            var day = monday.AddDays(i);
            var label = day == today ? "Today" : day.ToString("ddd d MMM", CultureInfo.InvariantCulture);
            var text = day > today ? "—" : PresenceLog.Describe(PresenceLog.Totals(Paths.PresenceDir(), day, now), withAway: true);
            if (day < today && text == "nothing yet") text = "—";
            _presenceItem.DropDownItems.Add(new ToolStripMenuItem($"{label}:  {text}") { Enabled = false });
        }
        _presenceItem.DropDownItems.Add(new ToolStripSeparator());
        _presenceItem.DropDownItems.Add(new ToolStripMenuItem("Open the presence folder", null, (_, _) =>
        {
            Directory.CreateDirectory(Paths.PresenceDir());
            Process.Start(new ProcessStartInfo("explorer.exe", Paths.PresenceDir()) { UseShellExecute = true });
        }));
    }

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
            if (_kumo.Status == KumoStatus.Watching && snap.Company.Count != before) _kumoStatusItem.Text = KumoWatchingText();
            if (_meeting.IsRecording) _timeline?.Add(snap);
        }
        var (action, reason) = _detector.Tick(now, snap, _meeting.IsRecording);
        if (!_kumoAuto) return;
        if (action == KumoAction.Start && !_meeting.IsRecording)
        {
            StartMeeting();
            if (!_meeting.IsRecording) return;
            _detector.OnAutoStarted(now);
            var zone = snap?.Zone is { Length: > 0 } z ? z : "outside a room";
            var people = string.Join(", ", snap?.Company.Select(p => p.Name) ?? Array.Empty<string>());
            HuginnLog.Write($"kumo: auto start ({reason}), {zone}, with {people}");
            ShowInfo("Huginn — recording a Kumospace meeting", $"{zone} · with {people}");
        }
        else if (action == KumoAction.Stop && _meeting.IsRecording)
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
            _notes.Discarded();
            SetStatus("Short chat not kept");
            ShowInfo("Huginn — short chat not kept", $"Only {speech:F0} s of talk, so the recording was deleted.");
            return;
        }
        await StopMeetingAsync();
    }

    private void ToggleSendMeetings()
    {
        _sendMeetings = !_sendMeetings;
        _sendMeetingsItem.Checked = _sendMeetings;
        Settings.SendMeetingsToOdin = _sendMeetings;
    }

    private void SetNotesMode(string mode)
    {
        _notesMode = mode;
        Settings.NotesMode = mode;
        RefreshNotesModeChecks();
        if (mode == "always") { if (_panel.Visible) _notes.ShowPanel(); }
        else if (mode == "off") _notes.HidePanel();
        // "record": leave the window as it is; it opens with the next recording.
    }

    private void RefreshNotesModeChecks()
    {
        _notesOffItem.Checked = _notesMode == "off";
        _notesAlwaysItem.Checked = _notesMode == "always";
        _notesRecordItem.Checked = _notesMode == "record";
    }

    private void TogglePanel()
    {
        _panelVisible = !_panelVisible;
        _panelItem.Checked = _panelVisible;
        Settings.MeetingPanelVisible = _panelVisible;
        if (_panelVisible) _panel.ShowPanel();
        else _panel.HidePanel();
    }

    private void StartMeeting()
    {
        if (!File.Exists(_modelPath))
        {
            SetStatus("Meeting: model still downloading…");
            ShowBalloon("Huginn — can't record yet", "The Whisper model is still downloading.");
            return;
        }
        _meetingActive = true;
        _transcriber.KeepWarm = true; // load now, so Stop finds the model in VRAM
        _ = _transcriber.EnsureLoadedAsync();
        if (_enabled) _hook.Uninstall(); // free the mic + keys for the call; dictation pauses while recording
        _meeting.MicDeviceNumber = _recorder.DeviceNumber;
        try
        {
            _meeting.Start();
        }
        catch (Exception ex)
        {
            HuginnLog.Write("meeting start failed", ex);
            _meetingActive = false;
            _transcriber.KeepWarm = false;
            if (_enabled) _hook.Install();
            SetStatus($"Meeting start failed: {ex.Message}");
            ShowBalloon("Huginn — meeting recording failed", ex.Message);
            return;
        }
        _timeline = SpeakerTimeline.Begin(_meeting.StartedLocal);
        _meetingItem.Text = "Stop meeting recording";
        _discardItem.Enabled = true;
        SetIcon(_iconRec);
        SetStatus("● Recording meeting…");
        OnUi(() => _meetingOverlay?.ShowRecording(_meeting.StartedLocal));
        _panel.ShowRecording(_meeting.StartedLocal);
        if (_notesMode != "off")
        {
            _notes.ShowRecording(_meeting.StartedLocal); // opens the window too, when it was hidden
            SaveNotes(); // an agenda typed before Record joins the file at [00:00:00]
            // Hand the keyboard to the pad only when Record was clicked in one of Huginn's own windows; a start
            // from the tray menu must not pull focus away from the call.
            if (Form.ActiveForm == _panel || Form.ActiveForm == _notes) _notes.FocusPad();
        }
    }

    // Shared teardown for BOTH stop paths: capture flushed + UI back to ready IMMEDIATELY — the next
    // meeting can start while (if kept) this one processes in the background.
    private async Task<MeetingCapture?> EndMeetingAsync()
    {
        SaveNotes(); // the last edits, before the recorder stops accepting them
        _meeting.Stop();
        var cap = await _meeting.FinalizeAsync();   // fast: flush the WAV writers, free the recorder
        SaveTimeline(cap);

        _meetingActive = false;
        _transcriber.KeepWarm = false;
        if (_enabled) { _hook.Install(); SetIcon(_iconIdle); }
        else SetIcon(_iconOff);
        _meetingItem.Enabled = true;
        _meetingItem.Text = "Start meeting recording";
        _discardItem.Enabled = false;
        OnUi(() => _meetingOverlay?.HideOverlay());
        _panel.SetIdle();
        if (cap != null) _notes.EndRecording(cap.Stamp); // the pad stays; the status line follows the job
        return cap;
    }

    private async Task StopMeetingAsync()
    {
        var cap = await EndMeetingAsync();
        SetStatus("Idle");
        if (cap != null) _ = ProcessCaptureAsync(cap);
    }

    // "This meeting isn't worth keeping": stop capture and DELETE the audio — no transcription, no Odin
    // filing, no panel row. The one deliberately destructive meeting action (the panel arms it two-click).
    private async Task DiscardMeetingAsync()
    {
        var cap = await EndMeetingAsync();
        DeleteCapture(cap);
        _notes.Discarded();
        SetStatus("Recording discarded");
        ShowInfo("Huginn — recording discarded", "The meeting audio was deleted; nothing was transcribed or filed.");
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

    // The pad changed (debounced) or the recording is ending: rewrite the meeting's notes file from the pad, so a
    // crash keeps everything typed so far and the finished job posts exactly what was on screen.
    private void SaveNotes()
    {
        if (!_meeting.IsRecording) return;
        MeetingNotes.Save(_meeting.NotesPath, _meeting.StartedLocal, _notes.Snapshot());
    }

    // User clicked ✕ on an in-flight row: cancel that job's transcription / Odin filing. The transcript and
    // raw WAVs stay on disk (cancel only means "don't file it to Odin"); nothing is auto-deleted.
    private void CancelJob(int id)
    {
        if (_jobCts.TryGetValue(id, out var cts))
        {
            try { cts.Cancel(); } catch { /* already disposed */ }
        }
    }

    // Transcribe + file a finished capture in the BACKGROUND — never blocks a new recording. Surfaces as a
    // PERSISTENT row in the panel's last-5 list (named with the Odin note title once filed). Several can run
    // at once (meeting after meeting); transcription serializes on the shared GPU, recording does not. The
    // row stays after filing (you see the name); a ✕ on it cancels processing via the per-job token.
    private async Task ProcessCaptureAsync(MeetingCapture cap)
    {
        System.Threading.Interlocked.Increment(ref _processingCount);
        UpdatePriority();
        int jobId = _panel.AddJob(cap.Stamp, cap.StartedLocal);
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
            _panel.SetJobTranscript(jobId, r.TranscriptPath);
            _notes.SetTranscriptReady(cap.Stamp);
            if (_sendMeetings)
            {
                _panel.SetJobStatus(jobId, "Filing to Odin…", JobTone.Busy, cancelable: true);
                _notes.SetJobStatus(cap.Stamp, "Filing to Odin…");
                try
                {
                    var odin = await OdinClient.ProcessMeetingAsync(r.Text, cap.StartedLocal.ToString("yyyy-MM-dd"), notes, cts.Token);
                    HuginnLog.Write($"meeting {cap.Stamp}: filed to Odin ({odin.Classification}, {odin.TaskCount} task(s)) \"{odin.Title}\"");
                    if (!string.IsNullOrWhiteSpace(odin.Title)) _panel.SetJobTitle(jobId, odin.Title);
                    var sec = string.IsNullOrEmpty(odin.SecondaryType) ? "" : $" + {odin.SecondaryType}";
                    _panel.SetJobStatus(jobId, $"✓ {odin.Classification} · {odin.TaskCount} task(s)", JobTone.Good, cancelable: false);
                    _notes.SetJobStatus(cap.Stamp, $"✓ Filed to Odin · {odin.Title}");
                    ShowInfo("Huginn — meeting filed to Odin", $"{odin.Classification} · {odin.TaskCount} task(s){sec}\n{odin.Title}");
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    HuginnLog.Write($"meeting {cap.Stamp}: Odin filing failed", ex);
                    _panel.SetJobStatus(jobId, "Saved · Odin offline", JobTone.Warn, cancelable: false);
                    _notes.SetJobStatus(cap.Stamp, "Saved · Odin offline");
                    ShowBalloon("Huginn — Odin not reachable", $"Saved the transcript to disk; file it later.\n{Path.GetFileName(r.TranscriptPath)}");
                    OpenInExplorer(r.TranscriptPath);
                }
            }
            else
            {
                _panel.SetJobStatus(jobId, "Saved", JobTone.Good, cancelable: false);
                _notes.SetJobStatus(cap.Stamp, "✓ Transcript saved");
                ShowInfo("Huginn — meeting transcript ready", Path.GetFileName(r.TranscriptPath));
                OpenInExplorer(r.TranscriptPath);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            HuginnLog.Write($"meeting {cap.Stamp}: cancelled by user after {sw.Elapsed:m\\:ss}");
            _panel.SetJobStatus(jobId, "Cancelled", JobTone.Muted, cancelable: false);
            _notes.SetJobStatus(cap.Stamp, "Cancelled");
        }
        catch (Exception ex)
        {
            HuginnLog.Write($"meeting {cap.Stamp}: FAILED after {sw.Elapsed:m\\:ss}", ex);
            _panel.SetJobStatus(jobId, $"Failed · {ShortReason(ex)}", JobTone.Bad, cancelable: false);
            _notes.SetJobStatus(cap.Stamp, $"Failed · {ShortReason(ex)}");
            ShowBalloon("Huginn — meeting processing failed", $"{ex.Message}\nDetails: {Paths.LogPath()}");
        }
        finally
        {
            _jobCts.TryRemove(jobId, out _);
            cts.Dispose();
            System.Threading.Interlocked.Decrement(ref _processingCount);
            UpdatePriority();
        }
    }

    private static string FileSize(string path)
    {
        try { return File.Exists(path) ? $"{new FileInfo(path).Length / 1048576.0:F1} MB" : "missing"; }
        catch { return "?"; }
    }

    // One line for the panel row. No trailing "…": MeetingPanel.LoadRecent reads that as "still in flight".
    private static string ShortReason(Exception ex)
    {
        var m = (string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return m.Length <= 90 ? m : m[..90];
    }

    private void Enable()
    {
        _enabled = true;
        _hook.Install();
        _toggleItem.Text = "Enabled";
        _toggleItem.Checked = true;
        SetIcon(_iconIdle);
        SetStatus("Idle");
        // Nothing loads here. The models load on the dictation key or at Record, and leave VRAM after
        // Config.IdleUnloadMinutes without use.
    }

    // Loads the speech models while the user speaks, then the cleanup LLM (staggered, so the two GPU loads do
    // not pile into one long stall). The lazy loads in TranscribeAsync and Cleaner.CleanAsync stay as a safety net.
    private async Task PreloadForDictationAsync()
    {
        try { await _transcriber.EnsureLoadedAsync(); }
        catch { /* falls back to a cold first transcription */ }
        if (_cleanupEnabled)
        {
            try { await _cleaner.PrepareAsync(); }
            catch { /* falls back to a lazy first-use load */ }
        }
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

    private void Disable()
    {
        _enabled = false;
        _hook.Uninstall();
        if (_recorder.IsRecording) _recorder.Stop();
        if (!_meetingActive && _processingCount == 0) _ = _transcriber.UnloadAsync(); // free VRAM (not mid-meeting/processing)
        _ = _cleaner.UnloadAsync();
        _toggleItem.Text = "Disabled";
        _toggleItem.Checked = false;
        SetIcon(_iconOff);
        SetStatus("Disabled (0 VRAM, 0 CPU)");
    }

    private void OnTalkDown()
    {
        if (!_enabled || _meetingActive) return;
        _cancelledDictation = false;
        _hook.CancelArmed = true; // arm the cancel key for the whole dictation flow (recording + transcription)
        SetIcon(_iconRec);
        SetStatus("Starting…");
        _overlay?.ShowStarting();
        _ = _media?.PauseAllAsync();
        _recorder.Start();
        _ = PreloadForDictationAsync();
    }

    private void OnTalkUp()
    {
        if (!_enabled) return;
        if (_cancelledDictation) { _cancelledDictation = false; return; } // Escape already cancelled this one
        _recorder.Stop();
        _previewTimer?.Stop();
        SetIcon(_iconBusy);
        SetStatus("Transcribing…");
        _overlay?.ShowTranscribing();
        _ = _media?.ResumeAsync();
    }

    // Cancel key pressed during a dictation flow → panic button. Discards an in-progress recording AND suppresses
    // the paste if transcription/cleanup is already running (the _cancelledDictation guard, checked before paste).
    // Also serves to dismiss a stuck pill from any state.
    private void OnCancel()
    {
        if (!_enabled) return;
        _cancelledDictation = true;
        if (_recorder.IsRecording)
        {
            _recorder.Cancel();
            _previewTimer?.Stop();
            _ = _media?.ResumeAsync();
        }
        SetStatus("Cancelled");
        ResetOverlayToIdle();
    }

    private void OnCaptureStarted() => OnUi(() =>
    {
        if (!_recorder.IsRecording) return; // key released before the mic opened
        _overlay?.ShowListening();
        SetStatus("Listening…");
        _previewTimer?.Start();
    });

    private async void OnPreviewTick(object? sender, EventArgs e)
    {
        if (_previewBusy || !_recorder.IsRecording) return;
        var wav = _recorder.SnapshotWav(PreviewWindowSeconds);
        if (wav == null) return;
        _previewBusy = true;
        try
        {
            string text;
            using (wav) text = await _transcriber.TranscribePreviewAsync(wav);
            if (_recorder.IsRecording && !string.IsNullOrWhiteSpace(text))
                _overlay?.SetPreviewText(text);
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
                if (_cancelledDictation) return; // panic-cancelled during transcription → don't paste
                if (_cleanupEnabled)
                {
                    SetStatus("Cleaning…");
                    OnUi(() => _overlay?.ShowCleaning());
                    text = await _cleaner.CleanAsync(text);
                }
                if (_cancelledDictation) return; // cancelled during cleanup → don't paste
                _injector.Inject(text);
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

    // Recording stopped with nothing usable (a fast tap-and-release, or no audio captured). Reset the UI so
    // the overlay never sticks open. Runs on NAudio's stop thread → ResetOverlayToIdle marshals to the UI.
    private void OnAborted()
    {
        SetStatus("Idle");
        ResetOverlayToIdle();
    }

    // Single owner of "recording is over → back to idle": icon + overlay. EVERY terminal path (complete,
    // silence, abort) routes through here so a stop can never strand the overlay open (BUG-1). Hiding the
    // overlay also stops its animation timer (BUG-2). Idempotent — safe to call when already hidden.
    private void ResetOverlayToIdle()
    {
        _hook.CancelArmed = false; // dictation flow over → the cancel key passes through normally again
        if (_enabled) SetIcon(_iconIdle);
        OnUi(() => _overlay?.HideOverlay());
    }

    // ---- UI thread helpers ----

    private void SetIcon(Icon icon) => OnUi(() => _tray.Icon = icon);

    private void SetStatus(string text) => OnUi(() => _statusItem.Text = text);

    private void ShowBalloon(string title, string text) =>
        OnUi(() => _tray.ShowBalloonTip(5000, title, text, ToolTipIcon.Warning));

    private void ShowInfo(string title, string text) =>
        OnUi(() => _tray.ShowBalloonTip(4000, title, text, ToolTipIcon.Info));

    private static void OpenInExplorer(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch { /* non-fatal — the file is still on disk */ }
    }

    private void OnUi(Action action)
    {
        if (_marshal.IsHandleCreated && _marshal.InvokeRequired)
            _marshal.BeginInvoke(action);
        else
            action();
    }

    private static Icon MakeIcon(Color color)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 4, 4, 24, 24);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    private static string KeyName(int vk) => vk switch
    {
        0xA3 => "Right Ctrl",
        0xA2 => "Left Ctrl",
        0x20 => "Space",
        0x77 => "F8",
        _ => $"key 0x{vk:X2}",
    };

    private static string HotkeyName() => Config.PushToTalkModifierVk == 0
        ? KeyName(Config.PushToTalkVk)
        : $"{KeyName(Config.PushToTalkModifierVk)} + {KeyName(Config.PushToTalkVk)}";

    private void Quit()
    {
        _presence.Observe(DateTime.Now, "off");
        _tray.Visible = false;
        // Stop any in-flight meeting jobs so their background continuations don't touch the disposing panel.
        foreach (var cts in _jobCts.Values) { try { cts.Cancel(); } catch { /* already disposed */ } }
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _quitWatcher?.Dispose();
            _kumoTimer.Dispose();
            _kumo.Dispose();
            _hook.Dispose();
            _previewTimer?.Dispose();
            _recorder.Dispose();
            _meeting.Dispose();
            _ = _transcriber.DisposeAsync();
            _ = _cleaner.DisposeAsync();
            _tray.Dispose();
            _overlay?.Dispose();
            _meetingOverlay?.Dispose();
            _panel.Dispose();
            _notes.Dispose();
            _marshal.Dispose();
        }
        base.Dispose(disposing);
    }
}
