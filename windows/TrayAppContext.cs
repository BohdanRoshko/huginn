using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using NAudio.Wave;

namespace Huginn;

/// <summary>
/// The Windows shell: the tray icon and its menu, the recorder panel, the Notes window and the dictation pill,
/// over the shared <see cref="HuginnEngine"/>. Every decision is the engine's; this class draws and forwards clicks.
/// </summary>
internal sealed class TrayAppContext : ApplicationContext, IShell
{
    private readonly NotifyIcon _tray;
    private readonly Form _marshal;            // hidden window: gives us an STA thread to Invoke onto
    private readonly HuginnEngine _engine;
    private readonly RecentMeetings _recent;
    private readonly RecordingOverlay? _overlay;
    private readonly MeetingOverlay? _meetingOverlay;
    private readonly MeetingPanel _panel;
    private readonly NotesPanel _notes;
    private bool _panelVisible;
    private bool _exited;

    private ToolStripMenuItem _statusItem = null!;
    private ToolStripMenuItem _dictationItem = null!;
    private ToolStripMenuItem _cleanupItem = null!;
    private ToolStripMenuItem _meetingItem = null!;
    private ToolStripMenuItem _discardItem = null!;
    private ToolStripMenuItem _sendMeetingsItem = null!;
    private ToolStripMenuItem _kumoStatusItem = null!;
    private ToolStripMenuItem _presenceItem = null!;
    private ToolStripMenuItem _recordingsItem = null!;
    private ToolStripMenuItem _panelItem = null!;
    private ToolStripMenuItem _notesOffItem = null!, _notesAlwaysItem = null!, _notesRecordItem = null!;
    private ToolStripMenuItem _callAskItem = null!, _callAutoItem = null!, _callOffItem = null!;
    private readonly CallPrompt _callPrompt = new();

    private readonly Icon _iconIdle;
    private readonly Icon _iconRec;
    private readonly Icon _iconBusy;
    private readonly Icon _iconOff;

    public TrayAppContext()
    {
        _marshal = new Form { ShowInTaskbar = false };
        _ = _marshal.Handle; // force handle creation on the UI thread so Invoke works

        _iconIdle = MakeIcon(Color.FromArgb(60, 160, 255));   // blue
        _iconRec = MakeIcon(Color.FromArgb(230, 60, 60));     // red
        _iconBusy = MakeIcon(Color.FromArgb(240, 190, 40));   // amber
        _iconOff = MakeIcon(Color.FromArgb(120, 120, 120));   // grey

        var s = HuginnSettings.Current;
        _recent = new RecentMeetings();
        _overlay = Config.ShowOverlay ? new RecordingOverlay() : null;
        _meetingOverlay = Config.ShowMeetingOverlay ? new MeetingOverlay() : null;
        _panel = new MeetingPanel(_recent);
        _notes = new NotesPanel();

        _tray = new NotifyIcon { Icon = _iconOff, Visible = true, Text = "Huginn" };
        // The panels are not always on top, so a left click on the tray icon brings them back up (or shows them).
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            if (!_panelVisible) { TogglePanel(); return; }
            _panel.Raise();
            _notes.Raise();
        };

        _engine = new HuginnEngine(new Platform(
            Shell: this,
            Dictation: new AudioRecorder(),
            Meeting: new MeetingRecorder(),
            Hotkey: new KeyboardHook(Config.PushToTalkVk, Config.PushToTalkModifierVk, Config.SwallowHotkey, Config.CancelVk),
            Injector: new TextInjector(_marshal),
            Kumo: new KumoLauncher(),
            Recorder: _panel,
            Notes: _notes,
            Overlay: _overlay,
            Media: Config.PauseMediaWhileRecording ? new MediaController() : null,
            Indicator: _meetingOverlay,
            MicUsers: new WindowsMicUsers(),
            CallPrompt: _callPrompt), _recent);

        _panel.RecordToggleRequested += _engine.ToggleMeeting;
        _panel.CancelRequested += _engine.CancelJob;
        _panel.DiscardRequested += _engine.Discard;
        _panel.ShowOdin = _engine.HasOdin;
        _panel.OpenInOdinRequested += id => _ = _engine.OpenInOdinAsync(id);
        // The notepad: the recorder's controls, beside it. NotesMode says when it shows: "always" (paired with the
        // recorder's show/hide), "record" (opens when a recording starts), or "off".
        _notes.RecordToggleRequested += _engine.ToggleMeeting;
        _notes.DiscardRequested += _engine.Discard;
        _notes.NotesChanged += _engine.SaveNotes;
        _notes.TranscriptResolver = _recent.TranscriptPathFor;
        _notes.HideRequested += () => { if (_engine.NotesMode == "always") _panel.HidePanel(); else _notes.HidePanel(); };
        _panel.LocationChanged += (_, _) => _notes.RecorderChanged(_panel.Bounds, _panel.Visible);
        _panel.VisibleChanged += (_, _) =>
        {
            _notes.RecorderChanged(_panel.Bounds, _panel.Visible);
            if (_engine.NotesMode != "always") return;
            if (_panel.Visible) _notes.ShowPanel(); else _notes.HidePanel();
        };

        _tray.ContextMenuStrip = BuildMenu();
        _panelVisible = Settings.MeetingPanelVisible;
        if (_panelVisible) _panel.ShowPanel();

        _engine.Start();
    }

    // ---- IShell ----

    public void RunOnUi(Action action)
    {
        if (_marshal.IsHandleCreated && _marshal.InvokeRequired)
            _marshal.BeginInvoke(action);
        else
            action();
    }

    public void SetTrayState(TrayState state) => RunOnUi(() => _tray.Icon = state switch
    {
        TrayState.Idle => _iconIdle,
        TrayState.Recording => _iconRec,
        TrayState.Busy => _iconBusy,
        _ => _iconOff,
    });

    public void SetStatus(string text) => RunOnUi(() => _statusItem.Text = text);

    public void Warn(string title, string text) =>
        RunOnUi(() => _tray.ShowBalloonTip(5000, title, text, ToolTipIcon.Warning));

    public void Reveal(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch { /* non-fatal — the file is still on disk */ }
    }

    public bool OwnWindowActive => Form.ActiveForm == _panel || Form.ActiveForm == _notes;

    public void Refresh() => RunOnUi(() =>
    {
        _dictationItem.Checked = _engine.DictationEnabled;
        _cleanupItem.Checked = _engine.CleanupEnabled;
        _cleanupItem.Enabled = _engine.DictationEnabled;
        bool rec = _engine.IsMeetingRecording;
        _meetingItem.Text = rec ? "Stop meeting recording" : "Start meeting recording";
        _discardItem.Enabled = rec;
        _sendMeetingsItem.Checked = _engine.SendMeetings;
        _kumoStatusItem.Text = _engine.KumoStatusText;
        _kumoStatusItem.Enabled = _engine.KumoRestartAvailable;
        _presenceItem.Text = _engine.PresenceTodayText;
        _recordingsItem.Text = _engine.RecordingsSizeText;
        _notesOffItem.Checked = _engine.NotesMode == "off";
        _notesAlwaysItem.Checked = _engine.NotesMode == "always";
        _notesRecordItem.Checked = _engine.NotesMode == "record";
        _callAskItem.Checked = _engine.DetectionMode == "ask";
        _callAutoItem.Checked = _engine.DetectionMode == "auto";
        _callOffItem.Checked = _engine.DetectionMode == "off";
    });

    public void Exit() => RunOnUi(ExitApp);

    // The Odin desktop window, started (or, when it is already open, poked) with --open-note; the web page when the
    // window's shortcut cannot be found.
    public void OpenOdinNote(string slug)
    {
        try
        {
            var lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Odin.lnk");
            string? exe = null;
            if (File.Exists(lnk))
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType != null)
                {
                    dynamic shell = Activator.CreateInstance(shellType)!;
                    exe = (string)shell.CreateShortcut(lnk).TargetPath;
                }
            }
            if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
            {
                var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
                psi.ArgumentList.Add("--open-note");
                psi.ArgumentList.Add(slug);
                Process.Start(psi)?.Dispose();
                return;
            }
            Process.Start(new ProcessStartInfo($"{Config.OdinBaseUrl}/?page=notes&note={Uri.EscapeDataString(slug)}") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) { HuginnLog.Write("odin: could not open the note", ex); }
    }

    // ---- menu ----

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        _statusItem = new ToolStripMenuItem("Starting…") { Enabled = false };
        // The dictation switch: off stops the hotkey from doing anything, without quitting the app.
        _dictationItem = new ToolStripMenuItem($"Dictation (hold {_engine.HotkeyName})", null,
            (_, _) => _engine.SetDictationEnabled(!_engine.DictationEnabled)) { Checked = _engine.DictationEnabled };

        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_dictationItem);
        menu.Items.Add(BuildMicMenu());
        _cleanupItem = new ToolStripMenuItem("Clean up text (AI)", null, (_, _) => _engine.SetCleanupEnabled(!_engine.CleanupEnabled));
        menu.Items.Add(_cleanupItem);
        menu.Items.Add(new ToolStripSeparator());
        _meetingItem = new ToolStripMenuItem("Start meeting recording", null, (_, _) => _engine.ToggleMeeting());
        menu.Items.Add(_meetingItem);
        _discardItem = new ToolStripMenuItem("Stop && discard recording", null, (_, _) => _engine.Discard()) { Enabled = false };
        menu.Items.Add(_discardItem);
        _sendMeetingsItem = new ToolStripMenuItem("Auto-file meetings to Odin", null, (_, _) => _engine.SetSendMeetings(!_engine.SendMeetings));
        if (_engine.HasOdin) menu.Items.Add(_sendMeetingsItem); // only Bohdan has Odin
        // Meeting detection, for every source (Kumospace, Meet, Teams, Slack huddles, Zoom): a Notion-style pop-up,
        // auto-record, or nothing.
        var callsMenu = new ToolStripMenuItem("Meeting detection");
        _callAskItem = new ToolStripMenuItem("Ask with a pop-up", null, (_, _) => _engine.SetDetectionMode("ask"));
        _callAutoItem = new ToolStripMenuItem("Record automatically", null, (_, _) => _engine.SetDetectionMode("auto"));
        _callOffItem = new ToolStripMenuItem("Off", null, (_, _) => _engine.SetDetectionMode("off"));
        callsMenu.DropDownItems.AddRange(new ToolStripItem[] { _callAskItem, _callAutoItem, _callOffItem });
        menu.Items.Add(callsMenu);
        _kumoStatusItem = new ToolStripMenuItem("Kumospace: starting…", null, (_, _) => _ = _engine.RestartKumoAsync()) { Enabled = false };
        menu.Items.Add(_kumoStatusItem);
        _presenceItem = new ToolStripMenuItem("Kumospace today: nothing yet");
        _presenceItem.DropDownItems.Add(new ToolStripMenuItem("…") { Enabled = false });
        _presenceItem.DropDownOpening += (_, _) => FillPresenceDays();
        menu.Items.Add(_presenceItem);
        _panelItem = new ToolStripMenuItem("Show recorder panel", null, (_, _) => TogglePanel()) { Checked = Settings.MeetingPanelVisible };
        menu.Items.Add(_panelItem);
        var notesMenu = new ToolStripMenuItem("Notes window");
        _notesOffItem = new ToolStripMenuItem("Off", null, (_, _) => SetNotesMode("off"));
        _notesAlwaysItem = new ToolStripMenuItem("Always on, beside the recorder", null, (_, _) => SetNotesMode("always"));
        _notesRecordItem = new ToolStripMenuItem("Open when a recording starts", null, (_, _) => SetNotesMode("record"));
        notesMenu.DropDownItems.AddRange(new ToolStripItem[] { _notesOffItem, _notesAlwaysItem, _notesRecordItem });
        menu.Items.Add(notesMenu);
        menu.Items.Add(new ToolStripSeparator());
        _recordingsItem = new ToolStripMenuItem("Recordings", null, (_, _) => { Directory.CreateDirectory(Paths.MeetingsDir()); OpenPath(Paths.MeetingsDir()); })
            { ToolTipText = "Open the recordings folder" };
        menu.Items.Add(_recordingsItem);
        menu.Items.Add(new ToolStripMenuItem("Open log", null, (_, _) => OpenPath(Paths.LogPath())));
        menu.Items.Add(new ToolStripMenuItem("Quit", null, (_, _) => ExitApp()));
        menu.Opening += (_, _) => Refresh();
        return menu;
    }

    private ToolStripMenuItem BuildMicMenu()
    {
        var mic = new ToolStripMenuItem("Microphone");

        void rebuild()
        {
            mic.DropDownItems.Clear();
            mic.DropDownItems.Add(new ToolStripMenuItem("System default", null, (_, _) => _engine.SelectMicrophone("", "System default"))
            {
                Checked = _engine.Microphone.Length == 0,
            });
            mic.DropDownItems.Add(new ToolStripSeparator());
            for (int i = 0; i < WaveInEvent.DeviceCount; i++)
            {
                var id = i.ToString();
                string name = WaveInEvent.GetCapabilities(i).ProductName;
                mic.DropDownItems.Add(new ToolStripMenuItem(name, null, (_, _) => _engine.SelectMicrophone(id, name))
                {
                    Checked = _engine.Microphone == id,
                });
            }
        }

        mic.DropDownOpening += (_, _) => rebuild(); // refresh if devices were plugged/unplugged
        rebuild();
        return mic;
    }

    private void FillPresenceDays()
    {
        _presenceItem.DropDownItems.Clear();
        foreach (var line in _engine.PresenceWeek())
            _presenceItem.DropDownItems.Add(new ToolStripMenuItem(line) { Enabled = false });
        _presenceItem.DropDownItems.Add(new ToolStripSeparator());
        _presenceItem.DropDownItems.Add(new ToolStripMenuItem("Open the presence folder", null, (_, _) =>
        {
            Directory.CreateDirectory(Paths.PresenceDir());
            Process.Start(new ProcessStartInfo("explorer.exe", Paths.PresenceDir()) { UseShellExecute = true });
        }));
    }

    private void SetNotesMode(string mode)
    {
        _engine.SetNotesMode(mode);
        if (mode == "always") { if (_panel.Visible) _notes.ShowPanel(); }
        else if (mode == "off") _notes.HidePanel();
        // "record": leave the window as it is; it opens with the next recording.
    }

    private void TogglePanel()
    {
        _panelVisible = !_panelVisible;
        _panelItem.Checked = _panelVisible;
        Settings.MeetingPanelVisible = _panelVisible;
        if (_panelVisible) _panel.ShowPanel();
        else _panel.HidePanel();
    }

    private static void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { /* non-fatal */ }
    }

    private void ExitApp()
    {
        if (_exited) return;
        _exited = true;
        _engine.Shutdown();
        _tray.Visible = false;
        ExitThread();
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _ = _engine.DisposeAsync(); // fire and forget, as before: blocking here would deadlock on the UI thread
            _tray.Dispose();
            _overlay?.Dispose();
            _meetingOverlay?.Dispose();
            _callPrompt.Dispose();
            _panel.Dispose();
            _notes.Dispose();
            _marshal.Dispose();
        }
        base.Dispose(disposing);
    }
}
