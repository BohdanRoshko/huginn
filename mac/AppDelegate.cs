using AppKit;
using CoreGraphics;
using Foundation;
using UserNotifications;

namespace Huginn;

/// <summary>
/// The Mac shell: a menu bar app, laid out like the Swift app it replaced (status line, Start/Stop Recording, Recent
/// with a submenu per meeting, Notes, Open Recordings Folder, Open Log, Quit), plus what the shared engine adds
/// (dictation, microphone, AI cleanup, Odin, Kumospace, the work timer). The recorder panel is a switch in the menu,
/// off by default; the Notes window and the dictation pill work as before. Every decision is HuginnEngine's.
/// </summary>
[Register("AppDelegate")]
internal sealed class AppDelegate : NSApplicationDelegate, IShell
{
    private HuginnEngine _engine = null!;
    private RecentMeetings _recent = null!;
    private MacRecorderPanel _panel = null!;
    private MacNotesWindow _notes = null!;
    private MacDictationOverlay? _overlay;
    private NSStatusItem _item = null!;
    private NSMenu _menu = null!;
    private NSMenuItem? _statusLine;
    private NSTimer? _clock;
    private TrayState _state = TrayState.Off;
    private string _status = "Starting…";
    private bool _exited;
    private bool _notificationsAsked;
    private readonly List<NSObject> _keep = new(); // NSMenu holds its delegate weakly

    public override void DidFinishLaunching(NSNotification notification)
    {
        var s = HuginnSettings.Current;
        _recent = new RecentMeetings();
        _panel = new MacRecorderPanel(_recent);
        _notes = new MacNotesWindow(_recent.TranscriptPathFor);
        _overlay = Config.ShowOverlay ? new MacDictationOverlay() : null;

        _item = NSStatusBar.SystemStatusBar.CreateStatusItem(-1); // NSVariableStatusItemLength
        SetIcon();

        _engine = new HuginnEngine(new Platform(
            Shell: this,
            Dictation: new MacDictationRecorder(),
            Meeting: new MacMeetingRecorder(),
            Hotkey: new MacHotkey(s.MacHotkeyKeyCode, s.MacHotkeyModifiers, s.MacCancelKeyCode),
            Injector: new MacTextInjector(),
            Kumo: new MacKumoApp(),
            Recorder: _panel,
            Notes: _notes,
            Overlay: _overlay,
            Media: null,       // macOS has no public API to pause whatever is playing
            Indicator: null),  // the menu bar shows the recording clock instead
            _recent);

        _panel.RecordToggleRequested += _engine.ToggleMeeting;
        _panel.CancelRequested += _engine.CancelJob;
        _panel.DiscardRequested += _engine.Discard;
        _notes.RecordToggleRequested += _engine.ToggleMeeting;
        _notes.DiscardRequested += _engine.Discard;
        _notes.NotesChanged += _engine.SaveNotes;
        _notes.HideRequested += _notes.HidePanel;
        // The notes window shows its own Stop / Discard when the recorder is not beside it on the same screen.
        _panel.FrameChanged += RecorderMoved;
        _panel.VisibilityChanged += RecorderMoved;

        _menu = new NSMenu { AutoEnablesItems = false, Delegate = Keep(new OnOpen(_ => RebuildMenu())) };
        _item.Menu = _menu;
        RebuildMenu();

        if (Settings.MeetingPanelVisible) _panel.ShowPanel();
        RecorderMoved();
        if (_engine.NotesMode == "always") _notes.ShowPanel(); // "Always On" keeps the notepad open, as before

        _engine.Start();
        UiProbe.Run(this, _item, _panel, _notes, _overlay, _recent);
    }

    public override void WillTerminate(NSNotification notification)
    {
        if (_exited) return;
        _exited = true;
        _engine.Shutdown();
    }

    private void RecorderMoved() => _notes.RecorderChanged(_panel.Frame, _panel.IsShown, _panel.Screen);

    // ---- IShell ----

    public void RunOnUi(Action action) => MacUi.OnMain(action);

    public void SetTrayState(TrayState state) => MacUi.OnMain(() =>
    {
        _state = state;
        SetIcon();
        if (_engine?.IsMeetingRecording == true)
        {
            if (_clock == null)
            {
                _clock = NSTimer.CreateRepeatingScheduledTimer(TimeSpan.FromSeconds(1), _ => { SetIcon(); UpdateStatusLine(); });
                NSRunLoop.Main.AddTimer(_clock, NSRunLoopMode.Common);
            }
        }
        else
        {
            _clock?.Invalidate();
            _clock = null;
        }
        UpdateStatusLine();
    });

    // A waveform; while a meeting records, a red dot and the running clock in the menu bar itself, so a forgotten
    // recording gets noticed.
    private void SetIcon()
    {
        var button = _item.Button;
        bool meeting = _engine?.IsMeetingRecording == true;
        var image = NSImage.GetSystemSymbol(meeting ? "record.circle.fill" : "waveform", "Huginn");
        if (image != null) image.Template = !meeting;
        button.Image = image;
        button.ContentTintColor = meeting || _state == TrayState.Recording ? NSColor.SystemRed : null;
        if (meeting && _engine!.MeetingStartedLocal is DateTime started)
        {
            button.ImagePosition = NSCellImagePosition.ImageLeading;
            button.Title = " " + Elapsed(started);
        }
        else
        {
            button.ImagePosition = NSCellImagePosition.ImageOnly;
            button.Title = "";
        }
    }

    /// <summary>mm:ss, rolling over to h:mm:ss past an hour.</summary>
    private static string Elapsed(DateTime started)
    {
        var e = DateTime.Now - started;
        return e.TotalHours >= 1 ? $"{(int)e.TotalHours}:{e.Minutes:00}:{e.Seconds:00}" : $"{e.Minutes:00}:{e.Seconds:00}";
    }

    public void SetStatus(string text) => MacUi.OnMain(() => { _status = text; UpdateStatusLine(); });

    private string StatusLine() =>
        _engine?.IsMeetingRecording == true && _engine.MeetingStartedLocal is DateTime started
            ? $"● Recording {Elapsed(started)}"
            : _status;

    private void UpdateStatusLine()
    {
        if (_statusLine != null) _statusLine.Title = StatusLine();
    }

    public void Warn(string title, string text) => Notify(title, text);

    public void Info(string title, string text) => Notify(title, text);

    private void Notify(string title, string text)
    {
        HuginnLog.Write($"notice: {title}: {text.Replace('\n', ' ')}");
        try
        {
            var center = UNUserNotificationCenter.Current;
            if (!_notificationsAsked)
            {
                _notificationsAsked = true;
                center.RequestAuthorization(UNAuthorizationOptions.Alert | UNAuthorizationOptions.Sound, (_, _) => { });
            }
            var content = new UNMutableNotificationContent { Title = title, Body = text };
            var request = UNNotificationRequest.FromIdentifier(Guid.NewGuid().ToString(), content, null);
            center.AddNotificationRequest(request, _ => { });
        }
        catch (Exception ex) { HuginnLog.Write("notice failed", ex); }
    }

    public void Reveal(string path) => MacUi.Reveal(path);

    public bool OwnWindowActive => _panel.IsKeyWindow || _notes.IsKeyWindow;

    // The menu is rebuilt each time it opens, so it always shows the current state; here only the menu bar changes.
    public void Refresh() => MacUi.OnMain(() => { SetIcon(); UpdateStatusLine(); });

    public void Exit() => MacUi.OnMain(Quit);

    // ---- menu ----

    private static NSCellStateValue On(bool on) => on ? NSCellStateValue.On : NSCellStateValue.Off;

    private static NSMenuItem Item(string title, Action? click = null, string key = "")
    {
        var item = click == null ? new NSMenuItem(title) : new NSMenuItem(title, key, (_, _) => click());
        item.Enabled = click != null;
        return item;
    }

    private static NSMenuItem Check(string title, bool on, Action click)
    {
        var item = Item(title, click);
        item.State = On(on);
        return item;
    }

    private static NSMenuItem Sub(string title, NSMenu submenu)
    {
        var item = new NSMenuItem(title) { Submenu = submenu, Enabled = true };
        return item;
    }

    private void RebuildMenu()
    {
        var m = _menu;
        m.RemoveAllItems();

        _statusLine = Item(StatusLine());
        m.AddItem(_statusLine);
        m.AddItem(NSMenuItem.SeparatorItem);

        bool rec = _engine.IsMeetingRecording;
        m.AddItem(Item(rec ? "Stop Recording" : "Start Recording", _engine.ToggleMeeting, "r"));
        if (rec) m.AddItem(Item("Stop & Discard", ConfirmDiscard));

        var recent = _recent.Entries;
        if (recent.Count > 0)
        {
            m.AddItem(NSMenuItem.SeparatorItem);
            m.AddItem(Item("Recent"));
            foreach (var job in recent.Take(5))
                m.AddItem(Sub("   " + JobTitle(job), JobMenu(job)));
        }

        m.AddItem(NSMenuItem.SeparatorItem);
        m.AddItem(Check($"Dictation (hold {_engine.HotkeyName})", _engine.DictationEnabled, () => _engine.SetDictationEnabled(!_engine.DictationEnabled)));
        m.AddItem(Sub("Microphone", MicrophoneMenu()));
        var cleanup = Check("Clean Up Text (AI)", _engine.CleanupEnabled, () => _engine.SetCleanupEnabled(!_engine.CleanupEnabled));
        cleanup.Enabled = _engine.DictationEnabled;
        m.AddItem(cleanup);

        m.AddItem(NSMenuItem.SeparatorItem);
        m.AddItem(Check("Auto-File Meetings to Odin", _engine.SendMeetings, () => _engine.SetSendMeetings(!_engine.SendMeetings)));
        if (_engine.KumoInstalled)
        {
            m.AddItem(Check("Auto-Record Kumospace Meetings", _engine.KumoAuto, () => _engine.SetKumoAuto(!_engine.KumoAuto)));
            var kumo = Item(_engine.KumoStatusText, () => _ = _engine.RestartKumoAsync());
            kumo.Enabled = _engine.KumoRestartAvailable;
            m.AddItem(kumo);
            m.AddItem(Sub(_engine.PresenceTodayText, PresenceMenu()));
        }

        m.AddItem(NSMenuItem.SeparatorItem);
        m.AddItem(Item("Open Recordings Folder", () => { Directory.CreateDirectory(Paths.MeetingsDir()); MacUi.Open(Paths.MeetingsDir()); }));
        m.AddItem(Item("Open Log", () => MacUi.Open(Paths.LogPath())));
        m.AddItem(Sub("Notes", NotesMenu()));
        m.AddItem(Check("Show Recorder Panel", Settings.MeetingPanelVisible, TogglePanel));

        m.AddItem(NSMenuItem.SeparatorItem);
        m.AddItem(Item("Quit Huginn", Quit, "q"));
    }

    private static string JobTitle(RecentMeeting job)
    {
        var name = string.IsNullOrWhiteSpace(job.Title) ? job.Started.ToString("MMM d, h:mm tt") : job.Title!;
        return $"{name} — {job.Status}";
    }

    /// <summary>Per-meeting actions: a submenu, so Rename sits next to Open (as in the Swift app).</summary>
    private NSMenu JobMenu(RecentMeeting job)
    {
        var sub = new NSMenu { AutoEnablesItems = false };
        var path = RecentMeetings.ResolveTranscript(job);
        bool has = File.Exists(path);
        var open = Item("Open Transcript", () => MacUi.Open(path)); open.Enabled = has; sub.AddItem(open);
        sub.AddItem(Item("Rename…", () => Rename(job)));
        var show = Item("Show in Finder", () => MacUi.Reveal(path)); show.Enabled = has; sub.AddItem(show);
        var copy = Item("Copy Path", () => MacUi.Copy(path)); copy.Enabled = has; sub.AddItem(copy);
        if (job.Cancelable)
        {
            sub.AddItem(NSMenuItem.SeparatorItem);
            sub.AddItem(Item("Cancel Processing", () => _engine.CancelJob(job.Id)));
        }
        return sub;
    }

    private void Rename(RecentMeeting job)
    {
        NSApplication.SharedApplication.ActivateIgnoringOtherApps(true);
        var alert = new NSAlert { MessageText = "Rename meeting", InformativeText = "The transcript file takes this name too." };
        var field = new NSTextField(new CGRect(0, 0, 280, 24)) { StringValue = job.Title ?? "" };
        alert.AccessoryView = field;
        alert.AddButton("Rename");
        alert.AddButton("Cancel");
        alert.Window.InitialFirstResponder = field;
        if (alert.RunModal() == (nint)(long)NSAlertButtonReturn.First) _recent.Rename(job.Id, field.StringValue);
    }

    private void ConfirmDiscard()
    {
        NSApplication.SharedApplication.ActivateIgnoringOtherApps(true);
        var alert = new NSAlert
        {
            MessageText = "Discard this recording?",
            InformativeText = "The audio will be deleted and nothing will be transcribed.",
            AlertStyle = NSAlertStyle.Warning,
        };
        alert.AddButton("Discard");
        alert.AddButton("Keep Recording");
        if (alert.RunModal() == (nint)(long)NSAlertButtonReturn.First) _engine.Discard();
    }

    private NSMenu MicrophoneMenu()
    {
        var menu = new NSMenu { AutoEnablesItems = false };
        menu.AddItem(Check("System Default", _engine.Microphone.Length == 0, () => _engine.SelectMicrophone("", "System default")));
        menu.AddItem(NSMenuItem.SeparatorItem);
        foreach (var (id, name) in MicCapture.Devices())
            menu.AddItem(Check(name, _engine.Microphone == id, () => _engine.SelectMicrophone(id, name)));
        return menu;
    }

    private NSMenu PresenceMenu()
    {
        var menu = new NSMenu { AutoEnablesItems = false };
        foreach (var line in _engine.PresenceWeek()) menu.AddItem(Item(line));
        menu.AddItem(NSMenuItem.SeparatorItem);
        menu.AddItem(Item("Open the Presence Folder", () => { Directory.CreateDirectory(Paths.PresenceDir()); MacUi.Open(Paths.PresenceDir()); }));
        return menu;
    }

    private NSMenu NotesMenu()
    {
        var menu = new NSMenu { AutoEnablesItems = false };
        menu.AddItem(Check("Off", _engine.NotesMode == "off", () => SetNotesMode("off")));
        menu.AddItem(Check("Always On", _engine.NotesMode == "always", () => SetNotesMode("always")));
        menu.AddItem(Check("Open When Recording Starts", _engine.NotesMode == "record", () => SetNotesMode("record")));
        menu.AddItem(NSMenuItem.SeparatorItem);
        var show = Item("Show Notes Window", _notes.ShowPanel, "n");
        show.Enabled = _engine.NotesMode != "off";
        menu.AddItem(show);
        return menu;
    }

    private void SetNotesMode(string mode)
    {
        _engine.SetNotesMode(mode);
        if (mode == "always") _notes.ShowPanel();
        else if (mode == "off") _notes.HidePanel();
        // "record": leave the window as it is; it opens with the next recording.
    }

    private void TogglePanel()
    {
        var show = !Settings.MeetingPanelVisible;
        Settings.MeetingPanelVisible = show;
        if (show) _panel.ShowPanel(); else _panel.HidePanel();
    }

    private void Quit()
    {
        if (!_exited)
        {
            _exited = true;
            _engine.Shutdown();
        }
        NSApplication.SharedApplication.Terminate(this);
    }

    private OnOpen Keep(OnOpen d) { _keep.Add(d); return d; }

    /// <summary>Rebuilds the menu right before it opens.</summary>
    private sealed class OnOpen : NSMenuDelegate
    {
        private readonly Action<NSMenu> _fill;
        public OnOpen(Action<NSMenu> fill) => _fill = fill;
        public override void MenuWillOpen(NSMenu menu) => _fill(menu);
    }
}
