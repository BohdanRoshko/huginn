using System.Runtime.InteropServices;
using AppKit;
using CoreGraphics;
using Foundation;
using ObjCRuntime;
using static Huginn.MacUi;

namespace Huginn;

/// <summary>
/// The Mac port of the Windows MeetingPanel: a compact dark card. Top row = Huginn's white tile (with a red badge and
/// "REC 00:01:23" while recording) and a red Record / Stop pill (with the two-click trash left of Stop while
/// recording). The band under it is the live voice meter in a darker well (flat when idle). Below that, the
/// <see cref="RecentMeetings.DisplayMax"/> most recent recordings, parted by thin lines and lit under the mouse: name,
/// then the 24-hour time and the status as chips; a ✕ to cancel an in-flight one; open and copy-path buttons on a
/// finished one. Double-click a row (or right-click → Rename) to name it. Drag anywhere that is not a control to move
/// it; right-click outside the rows offers to hide it. It is not always on top: other windows can cover it, and the
/// menu bar icon brings it back (Bohdan, 2026-11-20). Construct on the main thread; every public member is safe from
/// any thread.
/// </summary>
internal sealed class MacRecorderPanel : IRecorderView
{
    public event Action? RecordToggleRequested;
    /// <summary>Raised with a job id when the user clicks ✕ on an in-flight row.</summary>
    public event Action<int>? CancelRequested;
    /// <summary>Raised with a row id when the user clicks the open button on a finished row.</summary>
    public event Action<int>? OpenRequested;
    /// <summary>Shows the task-count chip (only when settings.json names an Odin: Bohdan's machine).</summary>
    public bool ShowOdin { get; set; }
    /// <summary>The two-click discard (trash) was confirmed while recording.</summary>
    public event Action? DiscardRequested;
    /// <summary>The panel was shown or hidden.</summary>
    public event Action? VisibilityChanged;
    /// <summary>The panel moved or changed size.</summary>
    public event Action? FrameChanged;

    private const int W = 352;
    private const int DisplayMax = RecentMeetings.DisplayMax;
    // The Windows panel's measures (Bohdan, 2026-11-20): 72 + 8 rows of 41 + 2 = 402, the VPN window's height.
    private const int MeterCy = 53;    // the meter band between the header and the list
    private const int MeterMaxH = 22;  // bar height at full level: room above and below inside the 32 px well
    private const int JobsTop = 72;
    private const int RowH = 41;       // 6 px between a row's text and the line above it, and 6 px to the line below
    private const int BottomPad = 2;   // with the row's own 6 px, 8 px under the last row

    // Surfaces, darkest to lightest: the meter well, the panel, a hovered row.
    private static readonly NSColor Well = Rgb(24, 24, 25);
    private static readonly NSColor WellEdge = Rgb(255, 255, 255, 10);
    private static readonly NSColor RowHover = Rgb(46, 46, 47);
    private static readonly NSColor RowLine = Rgb(255, 255, 255, 16);
    private static readonly NSColor ChipBg = Rgb(255, 255, 255, 16);
    private static readonly NSColor ChipText = Rgb(170, 170, 174);
    private static readonly NSColor TileWhite = Rgb(236, 236, 236);
    private static readonly NSColor Ink = Rgb(28, 28, 28);

    private readonly RecentMeetings _recent;
    private readonly NSPanel _panel;
    private readonly RecorderView _view;
    private readonly NSTimer _tick;
    private List<RecentMeeting> _entries;   // a snapshot: the engine changes the list, the panel draws it

    private readonly float[] _wave;
    private volatile float _incoming;

    private bool _recording;
    private DateTime _startedLocal;
    private bool _discardArmed;
    private DateTime _discardArmedAt;
    private bool _discardHover;
    private bool _btnHover;
    private bool _dragging, _dragMoved;
    private CGPoint _dragMouse, _dragOrigin;   // screen coordinates
    private int _hoverCancelId = -1;
    private int _hoverCopyId = -1;
    private int _hoverOpenId = -1;
    private int _hoverRow = -1;              // the row under the mouse: it lights up
    private int _copiedId = -1;              // the row whose path was just copied shows a check briefly
    private DateTime _copiedAt;
    private NSTextField? _editor;            // the inline rename box over a row's name line
    private RenameDelegate? _editorDelegate;
    private int _editingId = -1;
    private NSMenu? _rowMenu;
    private bool _placed;

    // Read from any thread: cached on the main thread.
    private readonly object _gate = new();
    private volatile bool _shown;
    private volatile bool _key;
    private CGRect _frame;
    private NSScreen? _screen;

    public MacRecorderPanel(RecentMeetings recent)
    {
        _recent = recent;
        _entries = recent.Entries.ToList();
        _wave = new float[Math.Max(8, (W - 32) / 6)];

        int h = PanelHeight();
        _panel = MakePanel(R(0, 0, W, h), canBecomeKey: true, floating: false); // other windows can cover it, as on Windows
        _panel.BecomesKeyOnlyIfNeeded = true;   // a click does not take the keyboard; only the rename box does
        _panel.AcceptsMouseMovedEvents = true;
        _view = new RecorderView(this, R(0, 0, W, h));
        _view.AutoresizingMask = NSViewResizingMask.WidthSizable | NSViewResizingMask.HeightSizable;
        _panel.ContentView = _view;

        // VERIFY: NSWindow exposes DidBecomeKey / DidResignKey / DidMove as C# events.
        _panel.DidBecomeKey += (_, _) => _key = true;
        _panel.DidResignKey += (_, _) => { _key = false; CommitRename(); };
        _panel.DidMove += (_, _) => UpdateCache();

        _recent.Changed += () => OnMain(() =>
        {
            _entries = _recent.Entries.ToList();
            if (_editingId != -1 && !_entries.Any(e => e.Id == _editingId)) CancelRename();
            RecalcHeight();
            _view.NeedsDisplay = true;
        });

        _tick = Repeat(TimeSpan.FromMilliseconds(55), OnTick);
        UpdateCache();
    }

    // ---- public API ----

    public bool IsShown => _shown;

    public CGRect Frame { get { lock (_gate) return _frame; } }

    public NSScreen? Screen { get { lock (_gate) return _screen; } }

    /// <summary>True while the panel has the keyboard (the rename box is open).</summary>
    public bool IsKeyWindow => NSThread.IsMain ? _panel.IsKeyWindow : _key;

    public void ShowPanel() => OnMain(() =>
    {
        bool wasShown = _panel.IsVisible;
        if (!wasShown) RestorePos();
        _panel.OrderFrontRegardless();
        if (!wasShown) SetShown(true);
    });

    /// <summary>Puts the panel above other windows once, without taking the keyboard (the menu bar icon was clicked).</summary>
    public void Raise() => OnMain(() => { if (_panel.IsVisible) _panel.OrderFrontRegardless(); });

    public void HidePanel() => OnMain(() =>
    {
        CancelRename();
        SavePos();
        if (!_panel.IsVisible) return;
        _panel.OrderOut(null);
        SetShown(false);
    });

    public void SetIdle() => OnMain(() =>
    {
        _recording = false;
        _discardArmed = false;
        _view.NeedsDisplay = true;
    });

    public void ShowRecording(DateTime startedLocal) => OnMain(() =>
    {
        _recording = true;
        _discardArmed = false;
        _startedLocal = startedLocal;
        Array.Clear(_wave);
        _incoming = 0;
        if (!_panel.IsVisible && Settings.MeetingPanelVisible) // the menu's "Show Recorder Panel" switch
        {
            RestorePos();
            _panel.OrderFrontRegardless();
            SetShown(true);
        }
        _view.NeedsDisplay = true;
    });

    /// <summary>A live input level (0..1) for the meter. Any thread: a single float write.</summary>
    public void SetLevel(float level01)
    {
        float v = Math.Clamp((float)Math.Sqrt(Math.Max(0f, level01)) * 2.4f, 0f, 1f); // normal speech reaches most of the well
        if (v > _incoming) _incoming = v;
    }

    private void SetShown(bool shown)
    {
        _shown = shown;
        UpdateCache();
        VisibilityChanged?.Invoke();
        FrameChanged?.Invoke();
    }

    private void UpdateCache()
    {
        var f = _panel.Frame;
        var s = _panel.Screen ?? ScreenFor(f);
        lock (_gate) { _frame = f; _screen = s; }
    }

    private void OnTick()
    {
        Array.Copy(_wave, 1, _wave, 0, _wave.Length - 1);
        _wave[^1] = _incoming;
        _incoming *= 0.55f;
        bool redraw = false;
        // A first trash click ARMS the discard; it disarms itself after a few seconds unconfirmed.
        if (_discardArmed && (DateTime.Now - _discardArmedAt).TotalSeconds > 3.5) { _discardArmed = false; redraw = true; }
        if (_copiedId != -1 && (DateTime.Now - _copiedAt).TotalSeconds > 1.4) { _copiedId = -1; redraw = true; }
        if (_recording || _wave.Any(v => v > 0.01f)) redraw = true;
        if (redraw && _panel.IsVisible) _view.NeedsDisplay = true;
    }

    // ---- layout (flipped view: y runs down, the Windows coordinates) ----

    // The header: Huginn's tile and the Record pill, both 24 px tall on one line, 6 px from the top and 6 px from the
    // meter well (Bohdan, 2026-11-20).
    private static CGRect IconTile => R(14, 7, 24, 24);
    private CGRect Pill => _recording ? R(W - 14 - 72, 7, 72, 24) : R(W - 14 - 88, 7, 88, 24);
    /// <summary>The trash: only while recording, left of the Stop pill.</summary>
    private static CGRect TrashRect => R(W - 14 - 72 - 8 - 24, 7, 24, 24);
    // Centred between the lines above and below the row. Open sits at the right edge, copy left of it.
    private static CGRect CancelRect(int index) => R(W - 36, JobsTop + index * RowH + RowH / 2 - 11, 22, 22);
    private static CGRect OpenRect(int index) => CancelRect(index);
    private static CGRect CopyRect(int index) { var r = CancelRect(index); return R((double)r.X - 26, r.Y, r.Width, r.Height); }

    // A finished meeting with a transcript can be opened (the meeting window) and its path copied.
    private static bool CanOpen(RecentMeeting e) => !e.Cancelable && e.Transcript != null;

    // Fixed, as on Windows: the header + DisplayMax row slots; empty slots draw a skeleton, so the height never
    // changes with how many meetings exist.
    private static int PanelHeight() => JobsTop + DisplayMax * RowH + BottomPad;

    private void RecalcHeight()
    {
        int h = PanelHeight();
        var f = _panel.Frame;
        double fx = f.X, fy = f.Y, fh = f.Height;
        if (Math.Abs(fh - h) > 0.5)
        {
            // Keep the top edge where it is (AppKit frames grow from the bottom-left).
            _panel.SetFrame(R(fx, fy + fh - h, W, h), true);
            _panel.InvalidateShadow();
            UpdateCache();
            FrameChanged?.Invoke();
        }
        _view.NeedsDisplay = true;
    }

    // ---- painting ----

    internal void Draw()
    {
        DrawCard(R(0, 0, W, PanelHeight()));
        // The meter's well: a darker inset band, so the level reads as a live instrument, not loose bars.
        var well = R(14, MeterCy - 16, W - 28, 32);
        FillRounded(well, 9, Well);
        StrokeRounded(R(14.5, MeterCy - 15.5, W - 29, 31), 8.5, WellEdge);
        DrawHeader();
        // While the trash is armed the meter band shows the confirm hint: the icon-only button needs it.
        if (_recording && _discardArmed)
            DrawText("Click the trash again to DELETE this recording.", FootFont, Red, R(16, MeterCy - 7, W - 32, 14));
        else
            DrawMeter(22, W - 22, MeterCy);
        DrawEntries();
    }

    private void DrawHeader()
    {
        // Huginn's tile, the same one the meeting pop-up shows: white, with the waveform in ink.
        var tile = IconTile;
        double tx = tile.X, ty = tile.Y, tw = tile.Width, th = tile.Height;
        FillRounded(tile, 6, TileWhite);
        double[] bars = { 5.5, 11, 16, 10, 13, 6.5 };
        double barsX = tx + (tw - (5 * 2.8 + 1.7)) / 2; // the waveform centred in the tile
        for (int i = 0; i < bars.Length; i++)
            FillRounded(R(barsX + i * 2.8, ty + (th - bars[i]) / 2, 1.7, bars[i]), 0.85, Ink);
        if (_recording)
        {
            // A red badge on the tile's corner, ringed in the panel colour so it sits on top.
            FillOval(R(tx + tw - 7, ty - 3, 11, 11), Bg);
            FillOval(R(tx + tw - 5, ty - 1, 7, 7), Red);
            DrawText($"REC  {Elapsed(_startedLocal)}", TitleFont, Red, R(tx + tw + 10, ty, 200, th), NSLineBreakMode.Clipping);
        }

        DrawPill(Pill, _recording, _btnHover);
        if (_recording) DrawTrash(TrashRect, _discardHover, _discardArmed);
    }

    // The red pill, fully round, with a hairline of light along its top so it reads as raised.
    private static void DrawPill(CGRect rect, bool isStop, bool hover)
    {
        double rx = rect.X, ry = rect.Y, rw = rect.Width, rh = rect.Height;
        FillRounded(rect, rh / 2, hover ? RedHover : Red);
        Line(rx + rh / 2, ry + 0.5, rx + rw - rh / 2, ry + 0.5, Rgb(255, 255, 255, 56), 1);
        string text = isStop ? "Stop" : "Record";
        const double glyph = 8, gap = 6;
        var size = Measure(text, RowNameFont);
        double total = glyph + gap + size.Width;
        double sx = Math.Round(rx + (rw - total) / 2), cy = ry + rh / 2;
        if (isStop) FillRounded(R(sx, cy - glyph / 2, glyph, glyph), 2, White);
        else FillOval(R(sx, cy - glyph / 2, glyph, glyph), White);
        DrawText(text, RowNameFont, White, sx + glyph + gap, cy - (double)size.Height / 2);
    }

    private void DrawMeter(int x0, int x1, int cy)
    {
        const int step = 6, barW = 3;
        int n = Math.Min(_wave.Length, (x1 - x0) / step);
        for (int i = 0; i < n; i++)
        {
            float lvl = _wave[_wave.Length - n + i];
            double h = Math.Max(2f, lvl * MeterMaxH);
            int a = 60 + (int)(195 * Math.Min(1f, lvl));
            FillRounded(R(x0 + i * step, cy - h / 2, barW, h), 1.5, Rgb(240, 240, 242, a));
        }
    }

    private void DrawEntries()
    {
        // A thin line between rows, so the buttons on the right are easy to match to their meeting.
        for (int i = 1; i < DisplayMax; i++)
            FillRect(R(16, JobsTop + i * RowH, W - 32, 1), RowLine);
        for (int i = 0; i < DisplayMax; i++)
        {
            int top = JobsTop + i * RowH;
            if (i < _entries.Count)
            {
                if (i == _hoverRow && _editingId == -1) FillRounded(R(10, top + 1, W - 20, RowH - 2), 6, RowHover);
                DrawEntryRow(_entries[i], i, top);
            }
            else DrawSkeletonRow(i, top);
        }
    }

    private void DrawEntryRow(RecentMeeting entry, int i, int top)
    {
        if (entry.Cancelable) FillOval(R(16, top + 12, 13, 13), ToneColor(entry.Tone).ColorWithAlphaComponent((NFloat)0.22));
        FillOval(R(19, top + 15, 7, 7), ToneColor(entry.Tone));

        bool cancelable = entry.Cancelable;
        bool open = CanOpen(entry);
        int rightPad = open ? 66 : cancelable ? 40 : 16;
        int nameW = W - 34 - rightPad;

        // Line 1 = the meeting NAME once known; the live status before that.
        string name = entry.Title ?? entry.Status;
        var nameCol = entry.Title != null ? TitleCol : RowName;
        if (entry.Id != _editingId)
            DrawText(name, RowNameFont, nameCol, R(34, top + 4, nameW, 16));

        // Line 2 = the 24-hour time, then the status as chips when the name line is already the title.
        var time = entry.Started.ToString("HH:mm");
        DrawText(time, RowMetaFont, Muted, R(34, top + 21, 40, 14), NSLineBreakMode.Clipping);
        if (entry.Title != null)
        {
            double x = 34 + TextWidth(time, RowMetaFont) + 8;
            foreach (var chip in RecentMeetings.StatusChips(entry.Status, ShowOdin))
            {
                double w = Math.Ceiling(TextWidth(chip, RowMetaFont)) + 12;
                if (x + w > 34 + nameW) break;
                var r = R(x, top + 20, w, 15);
                FillRounded(r, 4, ChipBg);
                DrawText(chip, RowMetaFont, ChipText, r, NSLineBreakMode.Clipping, NSTextAlignment.Center);
                x += w + 4;
            }
        }

        if (cancelable) DrawCancel(CancelRect(i), entry.Id == _hoverCancelId);
        if (open)
        {
            DrawCopy(CopyRect(i), entry.Id == _hoverCopyId, entry.Id == _copiedId, i == _hoverRow);
            DrawOpen(OpenRect(i), entry.Id == _hoverOpenId);
        }
    }

    // "Open": a rounded square with an arrow leaving its top-right corner.
    private static void DrawOpen(CGRect hit, bool hover)
    {
        if (hover) FillRounded(hit, 6, HoverFill);
        double hx = hit.X, hy = hit.Y, hw = hit.Width, hh = hit.Height;
        double cx = hx + hw / 2, cy = hy + hh / 2;
        var col = hover ? TitleCol : Muted;
        Polyline(col, 1.3, (cx - 0.5, cy - 5), (cx - 5, cy - 5), (cx - 5, cy + 5), (cx + 5, cy + 5), (cx + 5, cy + 0.5));
        Line(cx - 1, cy + 1, cx + 5.5, cy - 5.5, col, 1.3);
        Polyline(col, 1.3, (cx + 1.5, cy - 5.5), (cx + 5.5, cy - 5.5), (cx + 5.5, cy - 1.5));
    }

    // An empty slot: a muted dot + two rounded "loading" bars, widths varied per slot.
    private static readonly double[] SkelName = { 0.62, 0.50, 0.68, 0.55, 0.60, 0.47, 0.64, 0.52 };

    private static void DrawSkeletonRow(int i, int top)
    {
        int nameW = W - 34 - 16;
        FillRounded(R(19, top + 14, 7, 7), 3.5, Skeleton);
        FillRounded(R(34, top + 7, nameW * SkelName[i % SkelName.Length], 8), 4, Skeleton);
        FillRounded(R(34, top + 22, nameW * 0.34, 7), 3.5, Skeleton);
    }

    // A small ✕ to stop an in-flight recording's processing.
    private static void DrawCancel(CGRect hit, bool hover)
    {
        if (hover) FillRounded(hit, 6, HoverFill);
        double hx = hit.X, hy = hit.Y, hw = hit.Width, hh = hit.Height;
        double cx = hx + hw / 2, cy = hy + hh / 2;
        const double r = 4;
        var col = hover ? RedHover : Muted;
        Line(cx - r, cy - r, cx + r, cy + r, col, 1.6);
        Line(cx - r, cy + r, cx + r, cy - r, col, 1.6);
    }

    // The copy icon: two overlapping rounded squares; right after a copy it shows a check instead.
    private static void DrawCopy(CGRect hit, bool hover, bool copied, bool rowLit)
    {
        if (hover) FillRounded(hit, 6, HoverFill);
        double hx = hit.X, hy = hit.Y, hw = hit.Width, hh = hit.Height;
        double cx = Math.Floor(hx + hw / 2), cy = Math.Floor(hy + hh / 2);
        if (copied)
        {
            Polyline(Green, 1.6, (cx - 4, cy), (cx - 1, cy + 3), (cx + 4, cy - 3));
            return;
        }
        var col = hover ? TitleCol : Muted;
        StrokeRounded(R(cx - 2.5, cy - 5.5, 8, 8), 1.8, col, 1.3);
        var front = R(cx - 5.5, cy - 2.5, 8, 8);
        FillRounded(front, 1.8, rowLit ? RowHover : Bg); // hides the back square's corner behind the front one
        if (hover) FillRounded(front, 1.8, HoverFill);
        StrokeRounded(front, 1.8, col, 1.3);
    }

    private static NSColor ToneColor(JobTone t) => t switch
    {
        JobTone.Good => Green,
        JobTone.Bad => Red,
        JobTone.Muted => Muted,
        _ => Amber, // Busy / Warn
    };

    // ---- mouse (points are in the flipped view) ----

    internal void OnMouseDown(CGPoint p, NSEvent e)
    {
        _panel.OrderFrontRegardless(); // a click brings the panel above the windows covering it
        if (_editor != null && !EditorFrame().Contains(p)) CommitRename();
        if (Pill.Contains(p)) { RecordToggleRequested?.Invoke(); return; }
        if (_recording && TrashRect.Contains(p))
        {
            if (_discardArmed) { _discardArmed = false; DiscardRequested?.Invoke(); }
            else { _discardArmed = true; _discardArmedAt = DateTime.Now; }
            _view.NeedsDisplay = true;
            return;
        }
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].Cancelable && CancelRect(i).Contains(p)) { CancelRequested?.Invoke(_entries[i].Id); return; }
            if (CanOpen(_entries[i]) && CopyRect(i).Contains(p)) { CopyPath(_entries[i]); return; }
            if (CanOpen(_entries[i]) && OpenRect(i).Contains(p)) { OpenRequested?.Invoke(_entries[i].Id); return; }
        }
        if (e.ClickCount == 2)
        {
            int row = RowAt(p);
            if (row >= 0 && !CancelRect(row).Contains(p) && !CopyRect(row).Contains(p))
            {
                _dragging = false; // a rename, not a move
                BeginRename(_entries[row], row);
                return;
            }
        }
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
        UpdateCache();
        FrameChanged?.Invoke();
    }

    internal void OnRightMouseDown(CGPoint p, NSEvent e)
    {
        CommitRename();
        int row = RowAt(p);
        if (row >= 0) ShowRowMenu(_entries[row], e);
        else ShowPanelMenu(e); // a menu, not an instant hide: a stray right-click lost the panel
    }

    private void ShowPanelMenu(NSEvent e)
    {
        var menu = new NSMenu { AutoEnablesItems = false };
        menu.AddItem(new NSMenuItem("Hide panel", (_, _) => HidePanel()));
        _rowMenu = menu;
        NSMenu.PopUpContextMenu(menu, e, _view);
    }

    internal void OnMouseMoved(CGPoint p)
    {
        if (_dragging) return;
        bool redraw = false;
        bool b = Pill.Contains(p);
        if (b != _btnHover) { _btnHover = b; redraw = true; }
        bool th = _recording && TrashRect.Contains(p);
        if (th != _discardHover) { _discardHover = th; redraw = true; }

        int row = RowAt(p);
        if (row != _hoverRow) { _hoverRow = row; redraw = true; }

        int hover = -1, copyHover = -1, openHover = -1;
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].Cancelable && CancelRect(i).Contains(p)) { hover = _entries[i].Id; break; }
            if (CanOpen(_entries[i]) && CopyRect(i).Contains(p)) { copyHover = _entries[i].Id; break; }
            if (CanOpen(_entries[i]) && OpenRect(i).Contains(p)) { openHover = _entries[i].Id; break; }
        }
        if (hover != _hoverCancelId) { _hoverCancelId = hover; redraw = true; }
        if (copyHover != _hoverCopyId || openHover != _hoverOpenId)
        {
            _hoverCopyId = copyHover;
            _hoverOpenId = openHover;
            (copyHover == -1 && openHover == -1 ? NSCursor.ArrowCursor : NSCursor.PointingHandCursor).Set();
            redraw = true;
        }
        if (redraw) _view.NeedsDisplay = true;
    }

    internal void OnMouseExited()
    {
        if (_dragging) return;
        bool changed = _btnHover || _discardHover || _hoverCancelId != -1 || _hoverCopyId != -1 || _hoverOpenId != -1 || _hoverRow != -1;
        if (_hoverCopyId != -1 || _hoverOpenId != -1) NSCursor.ArrowCursor.Set();
        _btnHover = false;
        _discardHover = false;
        _hoverCancelId = -1;
        _hoverCopyId = -1;
        _hoverOpenId = -1;
        _hoverRow = -1;
        if (changed) _view.NeedsDisplay = true;
    }

    private int RowAt(CGPoint p)
    {
        double x = p.X, y = p.Y;
        if (x < 16 || x > W - 16 || y < JobsTop) return -1;
        int i = (int)((y - JobsTop) / RowH);
        return i < _entries.Count && i < DisplayMax ? i : -1;
    }

    // ---- copy path / row menu ----

    private void CopyPath(RecentMeeting entry)
    {
        var path = RecentMeetings.ResolveTranscript(entry);
        if (!File.Exists(path)) return;
        Copy(path);
        _copiedId = entry.Id;
        _copiedAt = DateTime.Now;
        _view.NeedsDisplay = true;
    }

    private void ShowRowMenu(RecentMeeting entry, NSEvent e)
    {
        int row = _entries.IndexOf(entry);
        var path = RecentMeetings.ResolveTranscript(entry);
        bool hasFile = File.Exists(path);
        var menu = new NSMenu { AutoEnablesItems = false };
        menu.AddItem(new NSMenuItem("Rename", (_, _) => BeginRename(entry, row)));
        menu.AddItem(new NSMenuItem("Open transcript", (_, _) => Open(path)) { Enabled = hasFile });
        menu.AddItem(new NSMenuItem("Show in Finder", (_, _) => Reveal(path)) { Enabled = hasFile });
        menu.AddItem(NSMenuItem.SeparatorItem);
        menu.AddItem(new NSMenuItem("Hide panel", (_, _) => HidePanel()));
        _rowMenu = menu;
        NSMenu.PopUpContextMenu(menu, e, _view);
    }

    // ---- rename (double-click a row, or right-click → Rename) ----

    private CGRect EditorFrame() => _editor?.Frame ?? CGRect.Empty;

    private void BeginRename(RecentMeeting entry, int row)
    {
        CancelRename();
        if (row < 0 || row >= _entries.Count || _entries[row].Id != entry.Id) return;
        int top = JobsTop + row * RowH;
        int nameW = W - 34 - (CanOpen(entry) ? 66 : entry.Cancelable ? 40 : 16);
        _editingId = entry.Id;
        var box = new NSTextField(R(34, top + 2, nameW, 18))
        {
            Bordered = false,
            Bezeled = false,
            DrawsBackground = true,
            BackgroundColor = RowHover,
            TextColor = TitleCol,
            Font = RowNameFont,
            StringValue = entry.Title ?? "",
            Editable = true,
            Selectable = true,
            FocusRingType = NSFocusRingType.None,
        };
        // VERIFY: NSTextField.Cell is an NSTextFieldCell with Scrollable / Wraps setters (one line that scrolls).
        box.Cell.Scrollable = true;
        box.Cell.Wraps = false;
        _editorDelegate = new RenameDelegate(CommitRename, CancelRename);
        box.Delegate = _editorDelegate;
        _editor = box;
        _view.AddSubview(box);
        _view.NeedsDisplay = true;
        _panel.MakeKeyWindow();           // a non-activating panel takes the keyboard without activating the app
        _panel.MakeFirstResponder(box);
        box.SelectText(null);
    }

    // Enter / focus loss commits. RemoveEditor nulls _editor FIRST, so the end-editing it triggers is a no-op.
    private void CommitRename()
    {
        var box = _editor;
        if (box == null) return;
        var text = box.StringValue.Trim();
        var id = _editingId;
        RemoveEditor();
        if (text.Length > 0) _recent.Rename(id, text);
    }

    private void CancelRename() => RemoveEditor();

    private void RemoveEditor()
    {
        var box = _editor;
        _editor = null;
        _editingId = -1;
        if (box == null) return;
        _panel.MakeFirstResponder(null);  // ends the field editor's session before the box goes
        box.RemoveFromSuperview();
        _editorDelegate = null;
        _view.NeedsDisplay = true;
    }

    // ---- position persistence ("x,y" = the panel's bottom-left in AppKit screen coordinates) ----

    private static string PosFile => Paths.File("meeting_panel_pos.txt");

    private void SavePos()
    {
        if (!_placed) return;
        SavePosition(PosFile, _panel.Frame.Location);
    }

    private void RestorePos()
    {
        int h = PanelHeight();
        if (LoadPosition(PosFile) is { } pt)
        {
            double x = pt.X, y = pt.Y;
            if (HeaderOnAScreen(R(x, y, W, h)))
            {
                _panel.SetFrameOrigin(pt);
                _placed = true;
                UpdateCache();
                return;
            }
        }
        DefaultPlace(h);
    }

    // Top-right of the main screen.
    private void DefaultPlace(int h)
    {
        var screen = MainScreen();
        if (screen != null)
        {
            var vf = screen.VisibleFrame;
            double x = vf.X, y = vf.Y, w = vf.Width, vh = vf.Height;
            _panel.SetFrameOrigin(P(x + w - W - 24, y + vh - h - 24));
        }
        _placed = true;
        UpdateCache();
    }

    // ---- the rename box's Enter / Esc / focus loss ----

    [Register("HuginnRenameDelegate")]
    private sealed class RenameDelegate : NSTextFieldDelegate
    {
        private readonly Action _commit;
        private readonly Action _cancel;

        public RenameDelegate(Action commit, Action cancel)
        {
            _commit = commit;
            _cancel = cancel;
        }

        // VERIFY: NSTextFieldDelegate.DoCommandBySelector(NSControl, NSTextView, Selector) is overridable.
        public override bool DoCommandBySelector(NSControl control, NSTextView textView, Selector commandSelector)
        {
            var name = commandSelector.Name;
            if (name == "insertNewline:") { _commit(); return true; }
            if (name == "cancelOperation:") { _cancel(); return true; }
            return false;
        }

        // VERIFY: NSTextFieldDelegate.EditingEnded(NSNotification) = controlTextDidEndEditing:.
        public override void EditingEnded(NSNotification notification) => _commit();
    }
}

/// <summary>The recorder's content view: draws the card and forwards the mouse to the panel.</summary>
[Register("HuginnRecorderView")]
internal sealed class RecorderView : NSView
{
    private readonly MacRecorderPanel _owner;

    public RecorderView(MacRecorderPanel owner, CGRect frame) : base(frame)
    {
        _owner = owner;
        AddTrackingArea(new NSTrackingArea(frame,
            NSTrackingAreaOptions.MouseEnteredAndExited | NSTrackingAreaOptions.MouseMoved |
            NSTrackingAreaOptions.ActiveAlways | NSTrackingAreaOptions.InVisibleRect,
            this, null));
    }

    public override bool IsFlipped => true;

    // The first click on the (never key) panel acts at once instead of only focusing it.
    public override bool AcceptsFirstMouse(NSEvent? theEvent) => true;

    public override void DrawRect(CGRect dirtyRect) => _owner.Draw();

    private CGPoint Local(NSEvent e) => ConvertPointFromView(e.LocationInWindow, null);

    public override void MouseDown(NSEvent theEvent) => _owner.OnMouseDown(Local(theEvent), theEvent);
    public override void MouseDragged(NSEvent theEvent) => _owner.OnMouseDragged();
    public override void MouseUp(NSEvent theEvent) => _owner.OnMouseUp();
    public override void RightMouseDown(NSEvent theEvent) => _owner.OnRightMouseDown(Local(theEvent), theEvent);
    public override void MouseMoved(NSEvent theEvent) => _owner.OnMouseMoved(Local(theEvent));
    public override void MouseExited(NSEvent theEvent) => _owner.OnMouseExited();
}
