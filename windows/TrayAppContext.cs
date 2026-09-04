using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using NAudio.Wave;

namespace Munin;

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
        MuninLog.Write("Munin started");

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
        // spelling authority for them. Edit %LOCALAPPDATA%\Munin\dictionary.txt to add terms.
        _transcriber.InitialPrompt = Dictionary.WhisperPrompt();
        _cleaner.Vocabulary = Dictionary.VocabularyLine();
        _meeting = new MeetingRecorder { MicDeviceNumber = MicSettings.Load() };
        _meetingOverlay = Config.ShowMeetingOverlay ? new MeetingOverlay() : null;
        _panel = new MeetingPanel();
        _panel.RecordToggleRequested += ToggleMeeting;
        _panel.CancelRequested += CancelJob;
        _panel.DiscardRequested += () => { if (_meeting.IsRecording) _ = DiscardMeetingAsync(); };
        _meeting.LevelChanged += _panel.SetLevel;
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
            Text = "Munin",
            ContextMenuStrip = BuildMenu(),
        };

        if (_panelVisible) _panel.ShowPanel();

        StartQuitWatcher();

        _ = InitializeAsync();
    }

    /// <summary>The programmatic quit channel (SKILL lifecycle contract): a host app writes
    /// %LOCALAPPDATA%\Munin\quit.flag → Munin flushes any active recording to disk (skipping
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
        MuninLog.Write("quit flag received");
        try { File.Delete(Paths.QuitFlagPath()); } catch { /* best effort */ }
        if (_meeting.IsRecording)
        {
            _meeting.Stop();
            await _meeting.FinalizeAsync(); // flush the WAV writers — the recording survives on disk unprocessed
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
                MuninLog.Write("model download failed", ex);
                SetIcon(_iconOff);
                SetStatus($"Model download failed: {ex.Message}");
                ShowBalloon("Munin — model download failed", ex.Message);
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
        _discardItem = new ToolStripMenuItem("Stop && discard recording", null, (_, _) => { if (_meeting.IsRecording) _ = DiscardMeetingAsync(); }) { Enabled = false };
        menu.Items.Add(_discardItem);
        _sendMeetingsItem = new ToolStripMenuItem("Auto-file meetings to Odin", null, (_, _) => ToggleSendMeetings()) { Checked = _sendMeetings };
        menu.Items.Add(_sendMeetingsItem);
        _panelItem = new ToolStripMenuItem("Show recorder panel", null, (_, _) => TogglePanel()) { Checked = _panelVisible };
        menu.Items.Add(_panelItem);
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
        if (_meeting.IsRecording) _ = StopMeetingAsync();
        else StartMeeting();
    }

    private void ToggleSendMeetings()
    {
        _sendMeetings = !_sendMeetings;
        _sendMeetingsItem.Checked = _sendMeetings;
        Settings.SendMeetingsToOdin = _sendMeetings;
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
            ShowBalloon("Munin — can't record yet", "The Whisper model is still downloading.");
            return;
        }
        _meetingActive = true;
        if (_enabled) _hook.Uninstall(); // free the mic + keys for the call; dictation pauses while recording
        _meeting.MicDeviceNumber = _recorder.DeviceNumber;
        try
        {
            _meeting.Start();
        }
        catch (Exception ex)
        {
            MuninLog.Write("meeting start failed", ex);
            _meetingActive = false;
            if (_enabled) _hook.Install();
            SetStatus($"Meeting start failed: {ex.Message}");
            ShowBalloon("Munin — meeting recording failed", ex.Message);
            return;
        }
        _meetingItem.Text = "Stop meeting recording";
        _discardItem.Enabled = true;
        SetIcon(_iconRec);
        SetStatus("● Recording meeting…");
        OnUi(() => _meetingOverlay?.ShowRecording(_meeting.StartedLocal));
        _panel.ShowRecording(_meeting.StartedLocal);
    }

    // Shared teardown for BOTH stop paths: capture flushed + UI back to ready IMMEDIATELY — the next
    // meeting can start while (if kept) this one processes in the background.
    private async Task<MeetingCapture?> EndMeetingAsync()
    {
        _meeting.Stop();
        var cap = await _meeting.FinalizeAsync();   // fast: flush the WAV writers, free the recorder

        _meetingActive = false;
        if (_enabled) { _hook.Install(); SetIcon(_iconIdle); }
        else SetIcon(_iconOff);
        _meetingItem.Enabled = true;
        _meetingItem.Text = "Start meeting recording";
        _discardItem.Enabled = false;
        OnUi(() => _meetingOverlay?.HideOverlay());
        _panel.SetIdle();
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
        if (cap != null)
        {
            try { if (File.Exists(cap.MicPath)) File.Delete(cap.MicPath); } catch { /* best effort */ }
            try { if (File.Exists(cap.LoopPath)) File.Delete(cap.LoopPath); } catch { /* best effort */ }
        }
        SetStatus("Recording discarded");
        ShowInfo("Munin — recording discarded", "The meeting audio was deleted; nothing was transcribed or filed.");
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
        int jobId = _panel.AddJob(cap.Stamp, cap.StartedLocal);
        var cts = new CancellationTokenSource();
        _jobCts[jobId] = cts;
        var sw = Stopwatch.StartNew();
        MuninLog.Write($"meeting {cap.Stamp}: processing started (me={FileSize(cap.MicPath)}, them={FileSize(cap.LoopPath)})");
        try
        {
            var r = await MeetingProcessor.TranscribeAsync(cap, _transcriber, cts.Token);
            MuninLog.Write($"meeting {cap.Stamp}: transcribed {r.SegmentCount} segments in {sw.Elapsed:m\\:ss} -> {Path.GetFileName(r.TranscriptPath)}");
            if (_sendMeetings)
            {
                _panel.SetJobStatus(jobId, "Filing to Odin…", JobTone.Busy, cancelable: true);
                try
                {
                    var odin = await OdinClient.ProcessMeetingAsync(r.Text, cap.StartedLocal.ToString("yyyy-MM-dd"), cts.Token);
                    MuninLog.Write($"meeting {cap.Stamp}: filed to Odin ({odin.Classification}, {odin.TaskCount} task(s)) \"{odin.Title}\"");
                    if (!string.IsNullOrWhiteSpace(odin.Title)) _panel.SetJobTitle(jobId, odin.Title);
                    var sec = string.IsNullOrEmpty(odin.SecondaryType) ? "" : $" + {odin.SecondaryType}";
                    _panel.SetJobStatus(jobId, $"✓ {odin.Classification} · {odin.TaskCount} task(s)", JobTone.Good, cancelable: false);
                    ShowInfo("Munin — meeting filed to Odin", $"{odin.Classification} · {odin.TaskCount} task(s){sec}\n{odin.Title}");
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    MuninLog.Write($"meeting {cap.Stamp}: Odin filing failed", ex);
                    _panel.SetJobStatus(jobId, "Saved · Odin offline", JobTone.Warn, cancelable: false);
                    ShowBalloon("Munin — Odin not reachable", $"Saved the transcript to disk; file it later.\n{Path.GetFileName(r.TranscriptPath)}");
                    OpenInExplorer(r.TranscriptPath);
                }
            }
            else
            {
                _panel.SetJobStatus(jobId, "Saved", JobTone.Good, cancelable: false);
                ShowInfo("Munin — meeting transcript ready", Path.GetFileName(r.TranscriptPath));
                OpenInExplorer(r.TranscriptPath);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            MuninLog.Write($"meeting {cap.Stamp}: cancelled by user after {sw.Elapsed:m\\:ss}");
            _panel.SetJobStatus(jobId, "Cancelled", JobTone.Muted, cancelable: false);
        }
        catch (Exception ex)
        {
            MuninLog.Write($"meeting {cap.Stamp}: FAILED after {sw.Elapsed:m\\:ss}", ex);
            _panel.SetJobStatus(jobId, $"Failed · {ShortReason(ex)}", JobTone.Bad, cancelable: false);
            ShowBalloon("Munin — meeting processing failed", $"{ex.Message}\nDetails: {Paths.LogPath()}");
        }
        finally
        {
            _jobCts.TryRemove(jobId, out _);
            cts.Dispose();
            System.Threading.Interlocked.Decrement(ref _processingCount);
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
        _transcriber.KeepWarm = true; // stay loaded while enabled → the GPU-load freeze happens once, not on every reload
        _ = WarmupAsync();
        // The cleanup (Qwen) model is NOT preloaded here — it loads lazily on first use (Cleaner.CleanAsync) so it
        // doesn't pile onto the startup GPU load. Once loaded it stays warm until disabled / toggled off.
    }

    private async Task WarmupAsync()
    {
        SetStatus("Warming up GPU…");
        try { await _transcriber.WarmupAsync(); }
        catch { /* falls back to a cold first transcription */ }
        // Warm the cleanup LLM AFTER the speech models (staggered — so the two GPU loads don't pile into one
        // long stall). Only when cleanup is on. Removes the one-time ~3–5 s Qwen-load hit on the first cleaned
        // dictation of the session. The lazy-load in Cleaner.CleanAsync stays as a safety net.
        if (_cleanupEnabled)
        {
            SetStatus("Warming up cleanup…");
            try { await _cleaner.PrepareAsync(); }
            catch { /* falls back to a lazy first-use load */ }
        }
        if (_enabled && !_recorder.IsRecording)
        {
            SetIcon(_iconIdle);
            SetStatus("Idle — ready");
        }
    }

    private void Disable()
    {
        _enabled = false;
        _transcriber.KeepWarm = false; // allow VRAM to be freed now that we're disabling
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
            MuninLog.Write("dictation failed", ex);
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
            _marshal.Dispose();
        }
        base.Dispose(disposing);
    }
}
