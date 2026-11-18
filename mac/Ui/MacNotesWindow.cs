using AppKit;
using CoreGraphics;
using Foundation;
using static Huginn.MacUi;

namespace Huginn;

/// <summary>
/// The Mac port of the Windows NotesPanel: the notepad that pairs with <see cref="MacRecorderPanel"/>, the same
/// size and skin, placed beside it. The header mirrors the recorder's ("Notes", the running time, the same
/// Record / Stop + trash), but those controls hide while the recorder is visible on the same screen. The body is a
/// plain text view; every line is stamped (<see cref="LineStamps"/>) with the offset from the recording start at
/// the moment it was begun, and <see cref="Snapshot"/> hands the stamped lines to the engine.
///
/// Record clears the pad only when it still holds the previous meeting's untouched notes; lines typed since (an
/// agenda) carry [00:00:00]. After Stop the pad keeps the notes while the hint line follows the job; once the
/// transcript exists, Open transcript / Copy path appear in the footer until the next recording starts.
/// Construct on the main thread; every public member except <see cref="Snapshot"/> is safe from any thread.
/// </summary>
internal sealed class MacNotesWindow : INotesView
{
    public event Action? RecordToggleRequested;
    /// <summary>The two-click discard confirmed (trash while recording): stop AND delete, nothing filed.</summary>
    public event Action? DiscardRequested;
    /// <summary>The pad changed (debounced 400 ms); the engine persists <see cref="Snapshot"/>.</summary>
    public event Action? NotesChanged;
    /// <summary>Right-click on the header: hide this window (and the recorder, when they are paired).</summary>
    public event Action? HideRequested;

    // Exactly the recorder's size (its JobsTop + DisplayMax rows + BottomPad).
    private const int W = 352, H = 396;
    private const int HeaderDivY = 48;
    private const int HintY = 54;
    private const int PadTop = 72;
    private const int PadBottomPad = 14;
    private const int FooterH = 26;
    private static int FooterY => H - PadBottomPad - FooterH;
    private int PadBottom => _hasTranscript ? FooterY - 8 : H - PadBottomPad;

    /// <summary>Stamp → the transcript's CURRENT path (it moves on rename), or null when there is none yet.</summary>
    private readonly Func<string, string?> _transcriptResolver;
    private readonly NSPanel _panel;
    private readonly NotesView _view;
    private readonly NSScrollView _scroll;
    private readonly NSTextView _text;
    private readonly LineStamps _lineStamps = new();   // one per logical line of the pad, in order
    private readonly NSTimer _tick;
    private NSTimer? _save;                             // the 400 ms debounce behind NotesChanged

    private bool _recording;
    private DateTime _startedLocal;
    private bool _btnHover, _discardHover, _discardArmed;
    private DateTime _discardArmedAt;
    private bool _dragging, _dragMoved;
    private CGPoint _dragMouse, _dragOrigin;           // screen coordinates
    private CGRect _recorderFrame;
    private bool _recorderVisible;
    private NSScreen? _recorderScreen;
    private bool _hideActions;
    private bool _suppressTextChanged;
    private bool _stale;                  // the pad still shows the previous meeting's notes, untouched
    private string? _jobStamp;            // the finished meeting the hint line + footer refer to
    private string _status = "";
    private bool _hasTranscript;
    private int _hoverFooter = -1;        // 0 = Open transcript, 1 = Copy path
    private string? _toast;
    private DateTime _toastAt;
    private bool _placed;

    private volatile bool _shown;
    private volatile bool _key;

    public MacNotesWindow(Func<string, string?> transcriptResolver)
    {
        _transcriptResolver = transcriptResolver;

        _panel = MakePanel(R(0, 0, W, H), canBecomeKey: true);
        _panel.BecomesKeyOnlyIfNeeded = false;  // a click into the pad takes the keyboard
        _panel.AcceptsMouseMovedEvents = true;
        _view = new NotesView(this, R(0, 0, W, H));
        _view.AutoresizingMask = NSViewResizingMask.WidthSizable | NSViewResizingMask.HeightSizable;
        _panel.ContentView = _view;

        _scroll = new NSScrollView(PadFrame())
        {
            HasVerticalScroller = true,
            HasHorizontalScroller = false,
            AutohidesScrollers = true,
            BorderType = NSBorderType.NoBorder,
            DrawsBackground = false,
        };
        var content = _scroll.ContentSize;
        double cw = content.Width, ch = content.Height;
        _text = new NSTextView(R(0, 0, cw, ch))
        {
            RichText = false,
            Editable = true,
            Selectable = true,
            AllowsUndo = true,
            Font = PadFont,
            TextColor = TitleCol,
            BackgroundColor = Bg,
            DrawsBackground = false,
            InsertionPointColor = TitleCol,
            MinSize = new CGSize(0.0, ch),
            MaxSize = new CGSize(1e7, 1e7),
            VerticallyResizable = true,
            HorizontallyResizable = false,
            AutoresizingMask = NSViewResizingMask.WidthSizable,
            TextContainerInset = new CGSize(0.0, 2.0),
        };
        // VERIFY: the automatic-substitution setters are bound with these names (isAutomaticQuoteSubstitutionEnabled etc.).
        _text.AutomaticQuoteSubstitutionEnabled = false;
        _text.AutomaticDashSubstitutionEnabled = false;
        _text.AutomaticTextReplacementEnabled = false;
        _text.AutomaticSpellingCorrectionEnabled = false;
        if (_text.TextContainer != null) _text.TextContainer.WidthTracksTextView = true;
        _scroll.DocumentView = _text;
        _view.AddSubview(_scroll);

        // VERIFY: NSTextView exposes the delegate's textDidChange: as the C# event TextDidChange.
        _text.TextDidChange += (_, _) => OnPadTextChanged();

        // VERIFY: NSWindow exposes DidBecomeKey / DidResignKey / DidMove as C# events.
        _panel.DidBecomeKey += (_, _) => _key = true;
        _panel.DidResignKey += (_, _) => _key = false;
        _panel.DidMove += (_, _) => UpdateHideActions();

        _tick = Repeat(TimeSpan.FromMilliseconds(500), OnTick);
    }

    // ---- layout (flipped view: the recorder's header geometry) ----

    private static CGRect IconTile => R(15, 13, 22, 22);
    private CGRect Pill => _recording ? R(W - 14 - 80, 11, 80, 28) : R(W - 14 - 96, 11, 96, 28);
    private static CGRect TrashRect => R(W - 14 - 80 - 8 - 28, 11, 28, 28);
    private static CGRect OpenRect => R(16, FooterY, 118, FooterH);
    private static CGRect CopyRect => R(16 + 118 + 8, FooterY, 88, FooterH);
    private bool ActionsShown => !_hideActions;

    private CGRect PadFrame() => R(16, PadTop, W - 32, PadBottom - PadTop);

    private void LayoutPad() => _scroll.Frame = PadFrame();

    // ---- public API ----

    public bool IsShown => _shown;

    /// <summary>True while the Notes window has the keyboard.</summary>
    public bool IsKeyWindow => NSThread.IsMain ? _panel.IsKeyWindow : _key;

    /// <summary>Shown beside the recorder without taking the keyboard; a click into the pad takes it.</summary>
    public void ShowPanel() => OnMain(() =>
    {
        if (!_panel.IsVisible) RestorePos();
        _panel.OrderFrontRegardless();
        _shown = true;
        UpdateHideActions();
    });

    public void HidePanel() => OnMain(() =>
    {
        SavePos();
        if (_panel.IsVisible) _panel.OrderOut(null);
        _shown = false;
    });

    /// <summary>Bring the pad to the keyboard (only when the user is already interacting with Huginn).</summary>
    public void FocusPad() => OnMain(() =>
    {
        if (!_panel.IsVisible) return;
        _panel.MakeKeyAndOrderFront(null);
        _panel.MakeFirstResponder(_text);
    });

    /// <summary>The recorder moved or was shown / hidden: decide whether this window shows its own controls.</summary>
    public void RecorderChanged(CGRect frame, bool visible, NSScreen? screen) => OnMain(() =>
    {
        _recorderFrame = frame;
        _recorderVisible = visible;
        _recorderScreen = screen;
        UpdateHideActions();
    });

    /// <summary>A recording began. The previous meeting's untouched notes are cleared; anything typed since
    /// (an agenda) stays and gets [00:00:00]. The previous meeting's hint line and footer buttons go.</summary>
    public void ShowRecording(DateTime startedLocal) => OnMain(() =>
    {
        _recording = true;
        _discardArmed = false;
        _startedLocal = startedLocal;
        if (_stale) ClearPad();
        _stale = false;
        _jobStamp = null;
        _status = "";
        _hasTranscript = false;
        _toast = null;
        _lineStamps.ZeroAll();
        LayoutPad();
        if (!_panel.IsVisible)
        {
            RestorePos();
            _panel.OrderFrontRegardless();
            _shown = true;
            UpdateHideActions();
        }
        _view.NeedsDisplay = true;
    });

    /// <summary>The recording ended and is kept: the pad stays readable while the job runs; the hint line follows
    /// that job (<see cref="SetJobStatus"/>) and the footer appears once its transcript exists.</summary>
    public void EndRecording(string stamp) => OnMain(() =>
    {
        _recording = false;
        _discardArmed = false;
        StopSave();
        _stale = PadText().Length > 0;
        _jobStamp = stamp;
        _status = "Transcribing…";
        _hasTranscript = false;
        _view.NeedsDisplay = true;
    });

    /// <summary>The recording was discarded: nothing to follow, the notes went with the audio.</summary>
    public void Discarded() => OnMain(() =>
    {
        _recording = false;
        _discardArmed = false;
        StopSave();
        ClearPad();
        _stale = false;
        _jobStamp = null;
        _status = "";
        _hasTranscript = false;
        LayoutPad();
        _view.NeedsDisplay = true;
    });

    /// <summary>Job progress for the meeting whose notes are on screen; other meetings' updates are ignored.</summary>
    public void SetJobStatus(string stamp, string status) => OnMain(() =>
    {
        if (stamp != _jobStamp) return;
        _status = status;
        _view.NeedsDisplay = true;
    });

    /// <summary>That meeting's transcript is on disk: show Open transcript / Copy path.</summary>
    public void SetTranscriptReady(string stamp) => OnMain(() =>
    {
        if (stamp != _jobStamp || _hasTranscript) return;
        _hasTranscript = true;
        LayoutPad();
        _view.NeedsDisplay = true;
    });

    /// <summary>The pad's non-empty lines with the stamp each was begun at. Main thread only.</summary>
    public IReadOnlyList<(TimeSpan at, string text)> Snapshot() => _lineStamps.Snapshot(PadText(), Now());

    // ---- the pad and its stamps ----

    private string PadText() => _text.Value ?? "";

    private void ClearPad()
    {
        _suppressTextChanged = true;
        _text.Value = "";
        _suppressTextChanged = false;
        _lineStamps.Clear();
        // An emptied plain-text view can drop its typing colour; set it again.
        _text.Font = PadFont;
        _text.TextColor = TitleCol;
    }

    private TimeSpan Now() => _recording ? DateTime.Now - _startedLocal : TimeSpan.Zero;

    private void OnPadTextChanged()
    {
        if (_suppressTextChanged) return;
        _stale = false; // the user is working on this text; Record keeps it
        _lineStamps.TextChanged(PadText(), (int)_text.SelectedRange.Location, Now());
        StopSave();
        _save = NSTimer.CreateScheduledTimer(TimeSpan.FromMilliseconds(400), _ =>
        {
            _save = null;
            NotesChanged?.Invoke();
        });
    }

    private void StopSave()
    {
        _save?.Invalidate();
        _save = null;
    }

    private void UpdateHideActions()
    {
        double rw = _recorderFrame.Width;
        var own = _panel.Screen ?? ScreenFor(_panel.Frame);
        var rec = _recorderScreen ?? ScreenFor(_recorderFrame);
        bool hide = _recorderVisible && rw > 0 && SameScreen(rec, own);
        if (hide == _hideActions) return;
        _hideActions = hide;
        if (hide) { _btnHover = false; _discardHover = false; }
        _view.NeedsDisplay = true;
    }

    private void OnTick()
    {
        bool redraw = false;
        if (_discardArmed && (DateTime.Now - _discardArmedAt).TotalSeconds > 3.5) { _discardArmed = false; redraw = true; }
        if (_toast != null && (DateTime.Now - _toastAt).TotalSeconds > 1.5) { _toast = null; redraw = true; }
        if (_recording) redraw = true;
        if (redraw && _panel.IsVisible) _view.NeedsDisplay = true;
    }

    // ---- footer actions ----

    private void OpenTranscript()
    {
        var path = _jobStamp == null ? null : _transcriptResolver(_jobStamp);
        if (path == null) { Toast("No transcript yet"); return; }
        if (!File.Exists(path)) { Toast("Could not open it"); return; }
        Open(path);
    }

    private void CopyTranscriptPath()
    {
        var path = _jobStamp == null ? null : _transcriptResolver(_jobStamp);
        if (path == null) { Toast("No transcript yet"); return; }
        Copy(path);
        Toast("Path copied");
    }

    private void Toast(string text)
    {
        _toast = text;
        _toastAt = DateTime.Now;
        _view.NeedsDisplay = true;
    }

    // ---- painting ----

    internal void Draw()
    {
        DrawCard(R(0, 0, W, H));
        DrawHeader();
        FillRect(R(16, HeaderDivY, W - 32, 1), Divider);

        var (hint, hintCol) = HintLine();
        if (hint.Length > 0) DrawText(hint, FootFont, hintCol, R(16, HintY, W - 32, 14));

        if (_hasTranscript)
        {
            DrawFooterButton(OpenRect, "Open transcript", _hoverFooter == 0);
            DrawFooterButton(CopyRect, "Copy path", _hoverFooter == 1);
        }
    }

    // The one line under the header: the discard hint while armed, a short toast, the job's progress after a
    // meeting, else nothing.
    private (string, NSColor) HintLine()
    {
        if (_recording && _discardArmed) return ("Click the trash again to DELETE this recording.", Red);
        if (_toast != null) return (_toast, Green);
        if (!_recording && _jobStamp != null && _status.Length > 0)
        {
            var col = _status.StartsWith("Failed", StringComparison.Ordinal) ? Red
                : _status.StartsWith("✓", StringComparison.Ordinal) ? Green : Muted;
            return (_status, col);
        }
        return ("", VMuted);
    }

    private void DrawHeader()
    {
        FillRounded(IconTile, 6, Tile);
        // A pen stroke in the tile: the notepad's glyph (the recorder's dot stays the recorder's).
        Line(15 + 7, 13 + 15, 15 + 15, 13 + 7, _recording ? Red : IconIdle, 2);

        DrawText("Notes", TitleFont, TitleCol, 46, 14);
        if (_recording)
        {
            double tw = TextWidth("Notes", TitleFont);
            DrawText($"·  REC  {Elapsed(_startedLocal)}", SubFont, Red, 46 + tw + 6, 16);
        }

        if (!ActionsShown) return;
        DrawActionPill(Pill, _recording, _btnHover);
        if (_recording) DrawTrash(TrashRect, _discardHover, _discardArmed);
    }

    // A ghost pill: thin border, muted text, filled faintly on hover.
    private static void DrawFooterButton(CGRect rect, string text, bool hover)
    {
        if (hover) FillRounded(rect, 8, HoverFill);
        double x = rect.X, y = rect.Y, w = rect.Width, h = rect.Height;
        StrokeRounded(R(x + 0.5, y + 0.5, w - 1, h - 1), 8, GhostBorder);
        DrawText(text, FooterButtonFont, hover ? TitleCol : Muted, rect, NSLineBreakMode.TruncatingTail, NSTextAlignment.Center);
    }

    // ---- mouse (the header + hint + footer; the pad handles its own) ----

    internal void OnMouseDown(CGPoint p)
    {
        if (ActionsShown && Pill.Contains(p)) { RecordToggleRequested?.Invoke(); return; }
        if (ActionsShown && _recording && TrashRect.Contains(p))
        {
            if (_discardArmed) { _discardArmed = false; DiscardRequested?.Invoke(); }
            else { _discardArmed = true; _discardArmedAt = DateTime.Now; }
            _view.NeedsDisplay = true;
            return;
        }
        if (_hasTranscript && OpenRect.Contains(p)) { OpenTranscript(); return; }
        if (_hasTranscript && CopyRect.Contains(p)) { CopyTranscriptPath(); return; }
        _dragging = true;
        _dragMoved = false;
        _dragMouse = NSEvent.CurrentMouseLocation;
        _dragOrigin = _panel.Frame.Location;
    }

    internal void OnMouseDragged()
    {
        if (!_dragging) return;
        var m = NSEvent.CurrentMouseLocation;
        double mx = m.X, my = m.Y, sx = _dragMouse.X, sy = _dragMouse.Y, ox = _dragOrigin.X, oy = _dragOrigin.Y;
        if (!_dragMoved && Math.Abs(mx - sx) < 1 && Math.Abs(my - sy) < 1) return;
        _dragMoved = true;
        _panel.SetFrameOrigin(P(ox + mx - sx, oy + my - sy));
    }

    internal void OnMouseUp()
    {
        if (!_dragging) return;
        _dragging = false;
        if (!_dragMoved) return;
        SavePos();
        UpdateHideActions();
    }

    private NSMenu? _hideMenu;

    // A menu, not an instant hide: a stray right-click lost the window.
    internal void OnRightMouseDown(NSEvent e)
    {
        var menu = new NSMenu { AutoEnablesItems = false };
        menu.AddItem(new NSMenuItem("Hide panel", (_, _) => HideRequested?.Invoke()));
        _hideMenu = menu;
        NSMenu.PopUpContextMenu(menu, e, _view);
    }

    internal void OnMouseMoved(CGPoint p)
    {
        if (_dragging) return;
        bool redraw = false;
        bool b = ActionsShown && Pill.Contains(p);
        if (b != _btnHover) { _btnHover = b; redraw = true; }
        bool th = ActionsShown && _recording && TrashRect.Contains(p);
        if (th != _discardHover) { _discardHover = th; redraw = true; }
        int f = !_hasTranscript ? -1 : OpenRect.Contains(p) ? 0 : CopyRect.Contains(p) ? 1 : -1;
        if (f != _hoverFooter) { _hoverFooter = f; redraw = true; }
        if (redraw) _view.NeedsDisplay = true;
    }

    internal void OnMouseExited()
    {
        if (_dragging) return;
        bool changed = _btnHover || _discardHover || _hoverFooter != -1;
        _btnHover = false;
        _discardHover = false;
        _hoverFooter = -1;
        if (changed) _view.NeedsDisplay = true;
    }

    // ---- position persistence ("x,y" = the panel's bottom-left in AppKit screen coordinates) ----

    private static string PosFile => Paths.File("notes_panel_pos.txt");

    private void SavePos()
    {
        if (!_placed) return;
        SavePosition(PosFile, _panel.Frame.Location);
    }

    private void RestorePos()
    {
        if (LoadPosition(PosFile) is { } pt)
        {
            double x = pt.X, y = pt.Y;
            if (HeaderOnAScreen(R(x, y, W, H)))
            {
                _panel.SetFrameOrigin(pt);
                _placed = true;
                return;
            }
        }
        DefaultPlace();
    }

    // Beside the recorder on its screen: right, else left, else below, else above (a portrait screen fits the pair
    // stacked), else nudged over it. Without a recorder: left of where the recorder sits by default.
    private void DefaultPlace()
    {
        _placed = true;
        double rx = _recorderFrame.X, ry = _recorderFrame.Y, rw = _recorderFrame.Width, rh = _recorderFrame.Height;
        if (rw > 0)
        {
            var screen = _recorderScreen ?? ScreenFor(_recorderFrame);
            if (screen != null)
            {
                var wa = screen.VisibleFrame;
                double top = ry + rh;
                const double gap = 12;
                var candidates = new[]
                {
                    (x: rx + rw + gap, y: top - H),   // right, tops aligned
                    (x: rx - gap - W, y: top - H),    // left
                    (x: rx, y: ry - gap - H),         // below
                    (x: rx, y: top + gap),            // above
                };
                foreach (var c in candidates)
                    if (wa.Contains(R(c.x, c.y, W, H))) { _panel.SetFrameOrigin(P(c.x, c.y)); return; }
                double wy = wa.Y, wh = wa.Height;
                double y = Math.Max(wy, Math.Min(top - 24 - H, wy + wh - H));
                _panel.SetFrameOrigin(P(rx + 24, y));
                return;
            }
        }
        var main = MainScreen();
        if (main == null) return;
        var vf = main.VisibleFrame;
        double vx = vf.X, vy = vf.Y, vw = vf.Width, vh = vf.Height;
        _panel.SetFrameOrigin(P(vx + vw - 24 - W - 12 - W, vy + vh - 24 - H));
    }
}

/// <summary>The Notes window's content view: draws the card, header, hint and footer; forwards the mouse.</summary>
[Register("HuginnNotesView")]
internal sealed class NotesView : NSView
{
    private readonly MacNotesWindow _owner;

    public NotesView(MacNotesWindow owner, CGRect frame) : base(frame)
    {
        _owner = owner;
        AddTrackingArea(new NSTrackingArea(frame,
            NSTrackingAreaOptions.MouseEnteredAndExited | NSTrackingAreaOptions.MouseMoved |
            NSTrackingAreaOptions.ActiveAlways | NSTrackingAreaOptions.InVisibleRect,
            this, null));
    }

    public override bool IsFlipped => true;

    public override bool AcceptsFirstMouse(NSEvent? theEvent) => true;

    public override void DrawRect(CGRect dirtyRect) => _owner.Draw();

    private CGPoint Local(NSEvent e) => ConvertPointFromView(e.LocationInWindow, null);

    public override void MouseDown(NSEvent theEvent) => _owner.OnMouseDown(Local(theEvent));
    public override void MouseDragged(NSEvent theEvent) => _owner.OnMouseDragged();
    public override void MouseUp(NSEvent theEvent) => _owner.OnMouseUp();
    public override void RightMouseDown(NSEvent theEvent) => _owner.OnRightMouseDown(theEvent);
    public override void MouseMoved(NSEvent theEvent) => _owner.OnMouseMoved(Local(theEvent));
    public override void MouseExited(NSEvent theEvent) => _owner.OnMouseExited();
}
