using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Huginn;

/// <summary>
/// The notepad that pairs with <see cref="MeetingPanel"/>: the same size and skin, placed beside it. The
/// header mirrors the recorder's — "Notes", the running time, and the same Record / Stop + trash controls —
/// but those controls hide while the recorder is visible on the same screen, so each screen has one set of
/// buttons. The body is a plain notepad (the Granola idea: what you write during the call is what the
/// summary must contain). Behind the scenes every line is stamped with the offset from the recording start
/// at the moment the line was begun — the transcript's own [hh:mm:ss] clock — and <see cref="Snapshot"/>
/// hands the stamped lines to the app, which keeps {stamp}_notes.md current and posts it with the transcript.
///
/// Lifecycle (Settings.NotesMode decides whether the window is always shown, shown when a recording starts,
/// or never): Record clears the pad only when it still holds the previous meeting's untouched notes — lines
/// typed since (an agenda) carry [00:00:00] and join the recording. After Stop the pad keeps the notes while
/// the status line follows the job (Transcribing… → Filing to Odin… → the result); once the transcript
/// exists, <b>Open transcript</b> / <b>Copy path</b> appear in the footer until the next recording starts.
/// </summary>
internal sealed class NotesPanel : Form, INotesView
{
    public event Action? RecordToggleRequested;
    /// <summary>The two-click discard confirmed (trash while recording): stop AND delete, nothing filed.</summary>
    public event Action? DiscardRequested;
    /// <summary>The pad changed (debounced); the app persists <see cref="Snapshot"/>.</summary>
    public event Action? NotesChanged;
    /// <summary>Right-click, then "Hide panel": hide this window (and the recorder, when they are paired).</summary>
    public event Action? HideRequested;

    /// <summary>Stamp → the transcript's CURRENT path (it moves on rename), or null when there is none yet.</summary>
    public Func<string, string?>? TranscriptResolver { get; set; }

    private readonly TextBox _pad;
    private readonly LineStamps _lineStamps = new();   // one per logical line of the pad, in order
    private bool _recording;
    private DateTime _startedLocal;
    private bool _btnHover, _discardHover, _discardArmed;
    private DateTime _discardArmedAt;
    private bool _dragging, _dragMoved;
    private Point _dragOrigin;
    private Rectangle _recorderBounds;
    private bool _recorderVisible;
    private bool _hideActions;
    private bool _suppressTextChanged;
    private bool _stale;                  // the pad still shows the previous meeting's notes, untouched
    private string? _jobStamp;            // the finished meeting the status line + footer refer to
    private string _status = "";
    private bool _hasTranscript;
    private int _hoverFooter = -1;        // 0 = Open transcript, 1 = Copy path
    private string? _toast;
    private DateTime _toastAt;

    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 500 };
    private readonly System.Windows.Forms.Timer _save = new() { Interval = 400 };
    private readonly Font _titleFont = new("Segoe UI Semibold", 10.5f);
    private readonly Font _subFont = new("Segoe UI", 9f);
    private readonly Font _pillFont = new("Segoe UI Semibold", 9.5f);
    private readonly Font _hintFont = new("Segoe UI", 8f);
    private readonly Font _padFont = new("Segoe UI", 10f);
    private readonly Font _footFont = new("Segoe UI Semibold", 8.5f);

    // Same palette as MeetingPanel, so the two windows read as one pair.
    private static readonly Color Bg = Color.FromArgb(30, 30, 32);
    private static readonly Color Border = Color.FromArgb(20, 255, 255, 255);
    private static readonly Color Divider = Color.FromArgb(16, 255, 255, 255);
    private static readonly Color TitleCol = Color.FromArgb(234, 234, 236);
    private static readonly Color Muted = Color.FromArgb(140, 140, 144);
    private static readonly Color VMuted = Color.FromArgb(104, 104, 108);
    private static readonly Color Red = Color.FromArgb(224, 72, 77);
    private static readonly Color RedHover = Color.FromArgb(236, 92, 96);
    private static readonly Color Green = Color.FromArgb(122, 196, 143);

    // Exactly the recorder's idle size (its JobsTop + DisplayMax rows + BottomPad).
    private const int W = 352, H = 396;
    private const int HeaderDivY = 48;
    private const int HintY = 54;
    private const int PadTop = 72;
    private const int PadBottomPad = 14;
    private const int FooterH = 26;
    private int FooterY => H - PadBottomPad - FooterH;
    private int PadBottom => _hasTranscript ? FooterY - 8 : H - PadBottomPad;

    public NotesPanel()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        BackColor = Bg;
        Size = new Size(W, H);

        _pad = new TextBox
        {
            Multiline = true,
            WordWrap = true,
            ScrollBars = ScrollBars.None,      // the caret scrolls the text; a bar would break the card look
            BorderStyle = BorderStyle.None,
            BackColor = Bg,
            ForeColor = TitleCol,
            Font = _padFont,
            AcceptsTab = false,
        };
        LayoutPad();
        _pad.TextChanged += OnPadTextChanged;
        Controls.Add(_pad);

        ApplyRegion();
        _tick.Tick += OnTick;
        _tick.Start();
        _save.Tick += (_, _) => { _save.Stop(); NotesChanged?.Invoke(); };

        _ = Handle;
        Visible = false;
    }

    // Shown beside the recorder without taking the keyboard; a click into the pad activates it as usual.
    protected override bool ShowWithoutActivation => true;

    // ---- layout (the recorder's header geometry) ----
    private Rectangle IconTile => new(15, 13, 22, 22);
    private Rectangle Pill => _recording
        ? new Rectangle(Width - 14 - 80, 11, 80, 28)
        : new Rectangle(Width - 14 - 96, 11, 96, 28);
    private Rectangle TrashRect => new(Width - 14 - 80 - 8 - 28, 11, 28, 28);
    private Rectangle OpenRect => new(16, FooterY, 118, FooterH);
    private Rectangle CopyRect => new(16 + 118 + 8, FooterY, 88, FooterH);
    private bool ActionsShown => !_hideActions;

    private void LayoutPad() => _pad.SetBounds(16, PadTop, W - 32, PadBottom - PadTop);

    // ---- public API ----
    public void ShowPanel() => Ui(() => { if (!Visible) { RestorePosition(); Show(); } Raise(); });

    /// <summary>Puts the pad above other windows once, without taking the keyboard; like the recorder, it is not
    /// always on top (Bohdan, 2026-11-20).</summary>
    public void Raise() => Ui(() =>
    {
        if (Visible) SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // HWND_TOP; NOSIZE | NOMOVE | NOACTIVATE
    });

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    public void HidePanel() => Ui(() => { SavePosition(); if (Visible) Hide(); });

    /// <summary>Bring the pad to the keyboard (only when the user is already interacting with Huginn).</summary>
    public void FocusPad() => Ui(() => { if (!Visible) return; Activate(); _pad.Focus(); });

    /// <summary>The recorder moved or was shown/hidden: decide whether this window shows its own controls.</summary>
    public void RecorderChanged(Rectangle bounds, bool visible) => Ui(() =>
    {
        _recorderBounds = bounds;
        _recorderVisible = visible;
        UpdateHideActions();
    });

    /// <summary>A recording began. The previous meeting's untouched notes are cleared; anything typed since
    /// (an agenda) stays and gets [00:00:00]. The status line and footer buttons of the previous meeting go.</summary>
    public void ShowRecording(DateTime startedLocal) => Ui(() =>
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
        if (!Visible) { RestorePosition(); Show(); }
        Invalidate();
    });

    /// <summary>The recording ended and is being kept: the pad stays readable while the job runs; the status
    /// line follows that job (<see cref="SetJobStatus"/>) and the footer appears once its transcript exists.</summary>
    public void EndRecording(string stamp) => Ui(() =>
    {
        _recording = false;
        _discardArmed = false;
        _save.Stop();
        _stale = _pad.TextLength > 0;
        _jobStamp = stamp;
        _status = "Transcribing…";
        _hasTranscript = false;
        Invalidate();
    });

    /// <summary>The recording was discarded: nothing to follow, the notes went with the audio.</summary>
    public void Discarded() => Ui(() =>
    {
        _recording = false;
        _discardArmed = false;
        _save.Stop();
        ClearPad();
        _stale = false;
        _jobStamp = null;
        _status = "";
        _hasTranscript = false;
        LayoutPad();
        Invalidate();
    });

    /// <summary>Job progress for the meeting whose notes are on screen; other meetings' updates are ignored.</summary>
    public void SetJobStatus(string stamp, string status) => Ui(() =>
    {
        if (stamp != _jobStamp) return;
        _status = status;
        Invalidate();
    });

    /// <summary>That meeting's transcript is on disk: show Open transcript / Copy path.</summary>
    public void SetTranscriptReady(string stamp) => Ui(() =>
    {
        if (stamp != _jobStamp || _hasTranscript) return;
        _hasTranscript = true;
        LayoutPad();
        Invalidate();
    });

    /// <summary>The pad's non-empty lines with the stamp each was begun at. UI thread only.</summary>
    public IReadOnlyList<(TimeSpan at, string text)> Snapshot() => _lineStamps.Snapshot(_pad.Text, Now());

    private void ClearPad()
    {
        _suppressTextChanged = true;
        _pad.Clear();
        _suppressTextChanged = false;
        _lineStamps.Clear();
    }

    // ---- stamping: keep one stamp per logical line, inserted/removed where the caret edited ----
    private TimeSpan Now() => _recording ? DateTime.Now - _startedLocal : TimeSpan.Zero;

    private void OnPadTextChanged(object? sender, EventArgs e)
    {
        if (_suppressTextChanged) return;
        _stale = false; // the user is working on this text; Record keeps it
        _lineStamps.TextChanged(_pad.Text, _pad.SelectionStart, Now());
        _save.Stop();
        _save.Start();
    }


    private void UpdateHideActions()
    {
        bool hide = _recorderVisible && _recorderBounds.Width > 0 &&
            Screen.FromRectangle(_recorderBounds).DeviceName == Screen.FromRectangle(Bounds).DeviceName;
        if (hide != _hideActions) { _hideActions = hide; Invalidate(); }
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        UpdateHideActions();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_discardArmed && (DateTime.Now - _discardArmedAt).TotalSeconds > 3.5) { _discardArmed = false; Invalidate(); }
        if (_toast != null && (DateTime.Now - _toastAt).TotalSeconds > 1.5) { _toast = null; Invalidate(); }
        if (_recording) Invalidate(new Rectangle(0, 0, Width, PadTop));
    }

    // ---- footer actions ----
    private void OpenTranscript()
    {
        var path = _jobStamp == null ? null : TranscriptResolver?.Invoke(_jobStamp);
        if (path == null) { Toast("No transcript yet"); return; }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { Toast("Could not open it"); }
    }

    private void CopyPath()
    {
        var path = _jobStamp == null ? null : TranscriptResolver?.Invoke(_jobStamp);
        if (path == null) { Toast("No transcript yet"); return; }
        try { Clipboard.SetText(path); Toast("Path copied"); }
        catch { Toast("Clipboard busy, try again"); }
    }

    private void Toast(string text) { _toast = text; _toastAt = DateTime.Now; Invalidate(); }

    // ---- painting ----
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Bg);

        using (var bp = Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 14))
        using (var pen = new Pen(Border)) g.DrawPath(pen, bp);

        DrawHeader(g);
        using (var dp = new Pen(Divider)) g.DrawLine(dp, 16, HeaderDivY, Width - 16, HeaderDivY);

        (string hint, Color hintCol) = HintLine();
        TextRenderer.DrawText(g, hint, _hintFont, new Rectangle(16, HintY, Width - 32, 14), hintCol,
            TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

        if (_hasTranscript)
        {
            DrawFooterButton(g, OpenRect, "Open transcript", _hoverFooter == 0);
            DrawFooterButton(g, CopyRect, "Copy path", _hoverFooter == 1);
        }
    }

    // The one line under the header: the discard hint while armed, a transient toast, the job's progress after
    // a meeting, else nothing.
    private (string, Color) HintLine()
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

    private void DrawHeader(Graphics g)
    {
        using (var tile = new SolidBrush(Color.FromArgb(44, 44, 47)))
        using (var tp = Rounded(IconTile, 6)) g.FillPath(tile, tp);
        // A pen stroke in the tile: the notepad's glyph, the recorder's dot stays the recorder's.
        using (var pen = new Pen(_recording ? Red : Color.FromArgb(150, 150, 154), 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(pen, IconTile.X + 7, IconTile.Y + 15, IconTile.X + 15, IconTile.Y + 7);

        TextRenderer.DrawText(g, "Notes", _titleFont, new Point(46, 14), TitleCol, TextFormatFlags.NoPrefix);
        if (_recording)
        {
            int tw = TextRenderer.MeasureText(g, "Notes", _titleFont).Width;
            TextRenderer.DrawText(g, $"·  REC  {Elapsed()}", _subFont, new Point(46 + tw + 6, 16), Red, TextFormatFlags.NoPrefix);
        }

        if (!ActionsShown) return;
        DrawActionPill(g, Pill, _recording);
        if (_recording) DrawTrash(g, TrashRect, _discardHover, _discardArmed);
    }

    // A ghost pill: thin border, muted text, filled faintly on hover.
    private void DrawFooterButton(Graphics g, Rectangle rect, string text, bool hover)
    {
        using var path = Rounded(rect, 8);
        if (hover)
        {
            using var hb = new SolidBrush(Color.FromArgb(36, 255, 255, 255));
            g.FillPath(hb, path);
        }
        using (var pen = new Pen(Color.FromArgb(56, 255, 255, 255))) g.DrawPath(pen, path);
        TextRenderer.DrawText(g, text, _footFont, rect, hover ? TitleCol : Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
    }

    private void DrawTrash(Graphics g, Rectangle rect, bool hover, bool armed)
    {
        if (armed)
        {
            using var fb = new SolidBrush(hover ? RedHover : Red);
            using var fp = Rounded(rect, 8);
            g.FillPath(fb, fp);
        }
        else if (hover)
        {
            using var hb = new SolidBrush(Color.FromArgb(36, 255, 255, 255));
            using var hp = Rounded(rect, 8);
            g.FillPath(hb, hp);
        }
        var col = armed ? Color.White : (hover ? RedHover : Muted);
        int cx = rect.X + rect.Width / 2, cy = rect.Y + rect.Height / 2;
        using var pen = new Pen(col, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(pen, cx - 6, cy - 4, cx + 6, cy - 4);
        g.DrawLine(pen, cx - 2, cy - 6, cx + 2, cy - 6);
        g.DrawLines(pen, new[] { new Point(cx - 4, cy - 4), new Point(cx - 3, cy + 6), new Point(cx + 3, cy + 6), new Point(cx + 4, cy - 4) });
        g.DrawLine(pen, cx - 1, cy - 1, cx - 1, cy + 3);
        g.DrawLine(pen, cx + 1, cy - 1, cx + 1, cy + 3);
    }

    private void DrawActionPill(Graphics g, Rectangle rect, bool isStop)
    {
        using (var b = new SolidBrush(_btnHover ? RedHover : Red))
        using (var pp = Rounded(rect, 8)) g.FillPath(b, pp);

        string text = isStop ? "Stop" : "Record";
        const int glyph = 9, gap = 7;
        int tw = TextRenderer.MeasureText(g, text, _pillFont, Size.Empty, TextFormatFlags.NoPadding).Width;
        int total = glyph + gap + tw;
        int sx = rect.X + (rect.Width - total) / 2;
        int cy = rect.Y + rect.Height / 2;

        using (var w = new SolidBrush(Color.White))
        {
            if (isStop) { using var sq = Rounded(new Rectangle(sx, cy - glyph / 2, glyph, glyph), 2); g.FillPath(w, sq); }
            else g.FillEllipse(w, sx, cy - glyph / 2, glyph, glyph);
        }
        TextRenderer.DrawText(g, text, _pillFont, new Rectangle(sx + glyph + gap, rect.Y, tw + 6, rect.Height),
            Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
    }

    private string Elapsed()
    {
        var t = DateTime.Now - _startedLocal;
        return $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}";
    }

    // ---- mouse (the header + footer; the pad handles its own) ----
    private ContextMenuStrip? _hideMenu;

    // A menu, not an instant hide: a stray right-click lost the window.
    private void ShowHideMenu(Point at)
    {
        _hideMenu?.Dispose();
        _hideMenu = new ContextMenuStrip();
        _hideMenu.Items.Add("Hide panel", null, (_, _) => HideRequested?.Invoke());
        _hideMenu.Show(this, at);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right) { ShowHideMenu(e.Location); return; }
        if (e.Button != MouseButtons.Left) return;
        if (ActionsShown && Pill.Contains(e.Location)) { RecordToggleRequested?.Invoke(); return; }
        if (ActionsShown && _recording && TrashRect.Contains(e.Location))
        {
            if (_discardArmed) { _discardArmed = false; DiscardRequested?.Invoke(); }
            else { _discardArmed = true; _discardArmedAt = DateTime.Now; }
            Invalidate();
            return;
        }
        if (_hasTranscript && OpenRect.Contains(e.Location)) { OpenTranscript(); return; }
        if (_hasTranscript && CopyRect.Contains(e.Location)) { CopyPath(); return; }
        _dragging = true;
        _dragMoved = false;
        _dragOrigin = e.Location;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging)
        {
            _dragMoved = true;
            Location = new Point(Location.X + e.X - _dragOrigin.X, Location.Y + e.Y - _dragOrigin.Y);
            return;
        }
        bool b = ActionsShown && Pill.Contains(e.Location);
        if (b != _btnHover) { _btnHover = b; Invalidate(); }
        bool th = ActionsShown && _recording && TrashRect.Contains(e.Location);
        if (th != _discardHover) { _discardHover = th; Invalidate(); }
        int f = !_hasTranscript ? -1 : OpenRect.Contains(e.Location) ? 0 : CopyRect.Contains(e.Location) ? 1 : -1;
        if (f != _hoverFooter) { _hoverFooter = f; Invalidate(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_dragging) { _dragging = false; if (_dragMoved) SavePosition(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_dragging) return;
        bool changed = _btnHover || _discardHover || _hoverFooter != -1;
        _btnHover = false;
        _discardHover = false;
        _hoverFooter = -1;
        if (changed) Invalidate();
    }

    // ---- position persistence (defaults to beside the recorder) ----
    private static string PosFile => Paths.File("notes_panel_pos.txt");

    private void SavePosition()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(PosFile)!); File.WriteAllText(PosFile, $"{Location.X},{Location.Y}"); }
        catch { /* non-fatal */ }
    }

    private void RestorePosition()
    {
        try
        {
            if (File.Exists(PosFile))
            {
                var parts = File.ReadAllText(PosFile).Split(',');
                if (parts.Length == 2 && int.TryParse(parts[0], out int x) && int.TryParse(parts[1], out int y))
                {
                    var pt = new Point(x, y);
                    if (OnAScreen(pt)) { Location = pt; return; }
                }
            }
        }
        catch { /* fall through */ }
        DefaultPlace();
    }

    private static bool OnAScreen(Point p) =>
        Screen.AllScreens.Any(s => s.WorkingArea.Contains(new Rectangle(p, new Size(40, 40))));

    // Beside the recorder on its screen: right, else left, else below, else above (a portrait screen fits the
    // pair stacked, not side by side), else nudged over it.
    private void DefaultPlace()
    {
        if (_recorderBounds.Width > 0)
        {
            var wa = Screen.FromRectangle(_recorderBounds).WorkingArea;
            var r = _recorderBounds;
            const int gap = 12;
            var candidates = new[]
            {
                new Point(r.Right + gap, r.Top),
                new Point(r.Left - gap - Width, r.Top),
                new Point(r.Left, r.Bottom + gap),
                new Point(r.Left, r.Top - gap - Height),
            };
            foreach (var c in candidates)
                if (wa.Contains(new Rectangle(c, Size))) { Location = c; return; }
            Location = new Point(r.Left + 24, Math.Max(wa.Top, Math.Min(r.Top + 24, wa.Bottom - Height)));
            return;
        }
        var screens = Screen.AllScreens;
        var target = screens.Length >= 3 ? screens[2] : screens[^1];
        var area = target.WorkingArea;
        Location = new Point(area.Left + (area.Width - Width) / 2 + Width / 2 + 6, area.Top + 48);
    }

    private void ApplyRegion()
    {
        using var p = Rounded(new Rectangle(0, 0, Width, Height), 14);
        Region = new Region(p);
    }

    private void Ui(Action a)
    {
        if (IsDisposed || Disposing) return;
        if (IsHandleCreated && InvokeRequired)
        {
            try { BeginInvoke(a); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }
        else a();
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static GraphicsPath Rounded(Rectangle r, int radius) => Rounded((RectangleF)r, radius);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tick.Dispose();
            _save.Dispose();
            _titleFont.Dispose();
            _subFont.Dispose();
            _pillFont.Dispose();
            _hintFont.Dispose();
            _padFont.Dispose();
            _footFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
