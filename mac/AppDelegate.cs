using AppKit;
using CoreGraphics;
using Foundation;
using UserNotifications;

namespace Huginn;

/// <summary>
/// The Mac shell: the menu bar item and its menu, the recorder panel, the Notes window and the dictation pill, over
/// the shared <see cref="HuginnEngine"/>. The menu mirrors the Windows tray menu item for item.
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
    private NSTimer? _clock;
    private TrayState _state = TrayState.Off;
    private bool _panelVisible;
    private bool _exited;
    private bool _notificationsAsked;
    private readonly List<NSObject> _keep = new(); // NSMenu holds its delegate weakly

    private NSMenuItem _statusItem = null!, _dictationItem = null!, _micItem = null!, _cleanupItem = null!;
    private NSMenuItem _meetingItem = null!, _discardItem = null!, _sendItem = null!, _kumoAutoItem = null!;
    private NSMenuItem _kumoStatusItem = null!, _presenceItem = null!, _panelItem = null!;
    private NSMenuItem _notesOff = null!, _notesAlways = null!, _notesRecord = null!;

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
        _notes.HideRequested += () => { if (_engine.NotesMode == "always") HidePanelPair(); else _notes.HidePanel(); };
        _panel.FrameChanged += RecorderMoved;
        _panel.VisibilityChanged += () =>
        {
            RecorderMoved();
            if (_engine.NotesMode != "always") return;
            if (_panel.IsShown) _notes.ShowPanel(); else _notes.HidePanel();
        };

        _item.Menu = BuildMenu();
        _panelVisible = Settings.MeetingPanelVisible;
        if (_panelVisible) _panel.ShowPanel();

        _engine.Start();
        UiProbe.Run(this, _panel, _notes, _overlay, _recent);
    }

    public override void WillTerminate(NSNotification notification)
    {
        if (_exited) return;
        _exited = true;
        _engine.Shutdown();
    }

    private void RecorderMoved() => _notes.RecorderChanged(_panel.Frame, _panel.IsShown, _panel.Screen);

    private void HidePanelPair()
    {
        _panel.HidePanel();
        _notes.HidePanel();
    }

    // ---- IShell ----

    public void RunOnUi(Action action) => MacUi.OnMain(action);

    public void SetTrayState(TrayState state) => MacUi.OnMain(() =>
    {
        _state = state;
        SetIcon();
        if (state == TrayState.Recording && _engine.IsMeetingRecording)
        {
            _clock ??= NSTimer.CreateRepeatingScheduledTimer(TimeSpan.FromSeconds(1), _ => SetIcon());
            NSRunLoop.Current.AddTimer(_clock, NSRunLoopMode.Common);
        }
        else
        {
            _clock?.Invalidate();
            _clock = null;
        }
    });

    // The menu bar shows a waveform; red with the meeting clock while a meeting records.
    private void SetIcon()
    {
        var button = _item.Button;
        string symbol = _state switch
        {
            TrayState.Idle => "waveform",
            TrayState.Recording => "record.circle",
            TrayState.Busy => "hourglass",
            _ => "waveform.slash",
        };
        button.Image = NSImage.GetSystemSymbol(symbol, "Huginn");
        if (_state == TrayState.Recording && _engine?.MeetingStartedLocal is DateTime started)
        {
            var e = DateTime.Now - started;
            var text = $" {(int)e.TotalHours}:{e.Minutes:00}:{e.Seconds:00}";
            button.AttributedTitle = new NSAttributedString(text, new NSStringAttributes { ForegroundColor = NSColor.SystemRed });
            button.ContentTintColor = NSColor.SystemRed;
        }
        else
        {
            button.Title = "";
            button.ContentTintColor = null;
        }
    }

    public void SetStatus(string text) => MacUi.OnMain(() => { if (_statusItem != null) _statusItem.Title = text; });

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

    public void Refresh() => MacUi.OnMain(() =>
    {
        if (_dictationItem == null) return;
        _dictationItem.State = On(_engine.DictationEnabled);
        _cleanupItem.State = On(_engine.CleanupEnabled);
        _cleanupItem.Enabled = _engine.DictationEnabled;
        bool rec = _engine.IsMeetingRecording;
        _meetingItem.Title = rec ? "Stop Meeting Recording" : "Start Meeting Recording";
        _discardItem.Enabled = rec;
        _sendItem.State = On(_engine.SendMeetings);
        _kumoAutoItem.State = On(_engine.KumoAuto);
        _kumoStatusItem.Title = _engine.KumoStatusText;
        _kumoStatusItem.Enabled = _engine.KumoRestartAvailable;
        _presenceItem.Title = _engine.PresenceTodayText;
        _panelItem.State = On(_panelVisible);
        _notesOff.State = On(_engine.NotesMode == "off");
        _notesAlways.State = On(_engine.NotesMode == "always");
        _notesRecord.State = On(_engine.NotesMode == "record");
        SetIcon();
    });

    public void Exit() => MacUi.OnMain(Quit);

    // ---- menu ----

    private static NSCellStateValue On(bool on) => on ? NSCellStateValue.On : NSCellStateValue.Off;

    private static NSMenuItem Item(string title, Action? click = null)
    {
        var item = click == null ? new NSMenuItem(title) : new NSMenuItem(title, (_, _) => click());
        if (click == null) item.Enabled = false;
        return item;
    }

    private NSMenu BuildMenu()
    {
        var menu = new NSMenu { AutoEnablesItems = false };
        _statusItem = Item("Starting…");
        // The dictation switch: off stops the key from doing anything, without quitting the app.
        _dictationItem = Item($"Dictation (hold {_engine.HotkeyName})", () => _engine.SetDictationEnabled(!_engine.DictationEnabled));
        _micItem = Item("Microphone");
        _micItem.Enabled = true;
        _micItem.Submenu = new NSMenu { AutoEnablesItems = false, Delegate = Keep(new OnOpen(FillMicrophones)) };
        _cleanupItem = Item("Clean Up Text (AI)", () => _engine.SetCleanupEnabled(!_engine.CleanupEnabled));
        _meetingItem = Item("Start Meeting Recording", _engine.ToggleMeeting);
        _discardItem = Item("Stop & Discard Recording", _engine.Discard);
        _sendItem = Item("Auto-File Meetings to Odin", () => _engine.SetSendMeetings(!_engine.SendMeetings));
        _kumoAutoItem = Item("Auto-Record Kumospace Meetings", () => _engine.SetKumoAuto(!_engine.KumoAuto));
        _kumoStatusItem = Item("Kumospace: starting…", () => _ = _engine.RestartKumoAsync());
        _presenceItem = Item("Kumospace today: nothing yet");
        _presenceItem.Enabled = true;
        _presenceItem.Submenu = new NSMenu { AutoEnablesItems = false, Delegate = Keep(new OnOpen(FillPresence)) };
        _panelItem = Item("Show Recorder Panel", TogglePanel);
        var notes = Item("Notes Window");
        notes.Enabled = true;
        notes.Submenu = new NSMenu { AutoEnablesItems = false };
        _notesOff = Item("Off", () => SetNotesMode("off"));
        _notesAlways = Item("Always On, Beside the Recorder", () => SetNotesMode("always"));
        _notesRecord = Item("Open When a Recording Starts", () => SetNotesMode("record"));
        notes.Submenu.AddItem(_notesOff);
        notes.Submenu.AddItem(_notesAlways);
        notes.Submenu.AddItem(_notesRecord);

        foreach (var i in new[] { _statusItem, NSMenuItem.SeparatorItem, _dictationItem, _micItem, _cleanupItem, NSMenuItem.SeparatorItem,
                     _meetingItem, _discardItem, _sendItem, _kumoAutoItem, _kumoStatusItem, _presenceItem, _panelItem, notes,
                     NSMenuItem.SeparatorItem, Item("Open Log", () => MacUi.Open(Paths.LogPath())),
                     Item("Open Meetings Folder", () => { Directory.CreateDirectory(Paths.MeetingsDir()); MacUi.Open(Paths.MeetingsDir()); }),
                     Item("Quit Huginn", Quit) })
            menu.AddItem(i);
        menu.Delegate = Keep(new OnOpen(_ => Refresh()));
        return menu;
    }

    private OnOpen Keep(OnOpen d) { _keep.Add(d); return d; }

    private void FillMicrophones(NSMenu menu)
    {
        menu.RemoveAllItems();
        var def = Item("System Default", () => _engine.SelectMicrophone("", "System default"));
        def.State = On(_engine.Microphone.Length == 0);
        menu.AddItem(def);
        menu.AddItem(NSMenuItem.SeparatorItem);
        foreach (var (id, name) in MicCapture.Devices())
        {
            var item = Item(name, () => _engine.SelectMicrophone(id, name));
            item.State = On(_engine.Microphone == id);
            menu.AddItem(item);
        }
    }

    private void FillPresence(NSMenu menu)
    {
        menu.RemoveAllItems();
        foreach (var line in _engine.PresenceWeek()) menu.AddItem(Item(line));
        menu.AddItem(NSMenuItem.SeparatorItem);
        menu.AddItem(Item("Open the Presence Folder", () => { Directory.CreateDirectory(Paths.PresenceDir()); MacUi.Open(Paths.PresenceDir()); }));
    }

    private void SetNotesMode(string mode)
    {
        _engine.SetNotesMode(mode);
        if (mode == "always") { if (_panel.IsShown) _notes.ShowPanel(); }
        else if (mode == "off") _notes.HidePanel();
    }

    private void TogglePanel()
    {
        _panelVisible = !_panelVisible;
        Settings.MeetingPanelVisible = _panelVisible;
        if (_panelVisible) _panel.ShowPanel(); else _panel.HidePanel();
        Refresh();
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

    /// <summary>Fills a submenu (or refreshes the menu) right before it opens.</summary>
    private sealed class OnOpen : NSMenuDelegate
    {
        private readonly Action<NSMenu> _fill;
        public OnOpen(Action<NSMenu> fill) => _fill = fill;
        public override void MenuWillOpen(NSMenu menu) => _fill(menu);
    }
}
