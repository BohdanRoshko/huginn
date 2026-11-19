using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text.Json;
using System.Windows.Forms;

namespace Huginn;

/// <summary>Dot/severity of a recording row.</summary>

/// <summary>
/// A compact, Notion-styled meeting-recorder control for a spare / 3rd monitor. Top row = icon ·
/// "Meeting" · live time (left) and a red Record / Stop pill (right). The body is a live voice-level
/// meter (mic + system audio) that is FLAT when idle and animates while recording. The MAIN control is
/// only ever Idle or Recording — it is NEVER blocked by processing.
///
/// Below that is a PERSISTENT list of the <see cref="DisplayMax"/> most recent recordings (newest first,
/// survives restarts via <c>recent.json</c>). Each row shows the meeting's NAME (the Odin note title, once
/// filed; the live status before that), the time it was recorded, and a ✕ to STOP an in-flight recording's
/// processing (transcribe/file) if it isn't worth keeping. Starting a new meeting pushes a new row on top
/// and drops the oldest off the list. So you can start the next meeting while a previous one transcribes.
/// Click the pill = start/stop (<see cref="RecordToggleRequested"/>); click a row's ✕ = cancel
/// (<see cref="CancelRequested"/>). Double-click a row (or right-click it → Rename) to name the meeting; the
/// transcript file takes that name (<see cref="MeetingProcessor.RenameTranscript"/>) and Odin's title no longer
/// overwrites it. Drag to move; right-click outside the rows to hide.
///
/// It is an ordinary window, not always on top (Bohdan, 2026-11-20): other windows can cover it, and the tray icon
/// brings it back. Depth comes from layers, not lines: Windows rounds it and gives it a shadow, the level meter
/// sits in a darker well, and thin lines part the recordings, whose rows light up under the mouse. A row's second
/// line is the 24-hour time and the status as chips (Bohdan, 2026-11-20).
/// </summary>
internal sealed class MeetingPanel : Form, IRecorderView
{
    public event Action? RecordToggleRequested;
    /// <summary>Raised with a job id when the user clicks ✕ on an in-flight row.</summary>
    public event Action<int>? CancelRequested;
    /// <summary>Raised when the user confirms the two-click discard (trash) while recording: stop AND
    /// delete the audio — no transcription, no filing.</summary>
    public event Action? DiscardRequested;
    /// <summary>Raised with a row id when the user clicks the open button on a finished row.</summary>
    public event Action<int>? OpenRequested;
    /// <summary>Show the "open in Odin" button (only when settings.json names an Odin: Bohdan's machine).</summary>
    public bool ShowOdin { get; set; }

    private bool _recording;
    private bool _discardArmed;
    private DateTime _discardArmedAt;
    private bool _discardHover;
    private DateTime _startedLocal;
    private bool _btnHover;
    private bool _dragging;
    private bool _dragMoved;
    private Point _dragOrigin;
    private int _hoverCancelId = -1;
    private int _hoverCopyId = -1;
    private int _hoverOdinId = -1;
    private int _hoverRow = -1;             // the row under the mouse: it lights up
    private int _copiedId = -1;             // the row whose path was just copied: its icon shows a check briefly
    private DateTime _copiedAt;
    private TextBox? _editor;              // inline rename box over a row's name line
    private int _editingId = -1;
    private ContextMenuStrip? _rowMenu;

    private const int DisplayMax = RecentMeetings.DisplayMax;
    private readonly RecentMeetings _recent;
    // A snapshot of the rows; the engine changes the list, the panel only draws and hit-tests it.
    private List<RecentMeeting> _entries = new();

    private readonly float[] _wave;
    private float _incoming;

    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 55 };
    private readonly Font _titleFont = new("Segoe UI Semibold", 10.5f);
    private readonly Font _subFont = new("Segoe UI", 9f);
    private readonly Font _pillFont = new("Segoe UI Semibold", 9.5f);
    private readonly Font _footFont = new("Segoe UI", 8f);
    private readonly Font _rowNameFont = new("Segoe UI Semibold", 9f);
    private readonly Font _rowMetaFont = new("Segoe UI", 8f);

    // Surfaces, darkest to lightest: the meter well, the panel, a hovered row.
    private static readonly Color Well = Color.FromArgb(24, 24, 25);
    private static readonly Color Bg = Color.FromArgb(32, 32, 33);           // #202021, the pop-up's surface
    private static readonly Color RowHover = Color.FromArgb(46, 46, 47);
    private static readonly Color Border = Color.FromArgb(20, 255, 255, 255);
    private static readonly Color WellEdge = Color.FromArgb(10, 255, 255, 255);
    private static readonly Color RowLine = Color.FromArgb(16, 255, 255, 255);
    private static readonly Color ChipBg = Color.FromArgb(16, 255, 255, 255);
    private static readonly Color ChipText = Color.FromArgb(170, 170, 174);
    private static readonly Color TitleCol = Color.FromArgb(234, 234, 236);
    private static readonly Color RowName = Color.FromArgb(214, 214, 218);
    private static readonly Color Muted = Color.FromArgb(140, 140, 144);
    private static readonly Color VMuted = Color.FromArgb(104, 104, 108);
    private static readonly Color Red = Color.FromArgb(224, 72, 77);
    private static readonly Color RedHover = Color.FromArgb(236, 92, 96);
    private static readonly Color Amber = Color.FromArgb(226, 170, 92);
    private static readonly Color Green = Color.FromArgb(122, 196, 143);
    private static readonly Color Skeleton = Color.FromArgb(22, 255, 255, 255); // muted placeholder bar on the list surface

    private const int BaseH = 112;
    private const int MeterCy = 58;   // mic-level meter center: the band between the header and the list
    private const int MeterMaxH = 22; // bar height at full level: room above and below inside the 32 px well
    private const int JobsDividerY = 77;
    private const int JobsTop = 77;
    private const int RowH = 41;       // 6 px between a row's text and the line above it, and 6 px to the line below (Bohdan, 2026-11-20)
    private const int BottomPad = 2;   // with the row's own 6 px, 8 px under the last row: the same as above the header

    public MeetingPanel(RecentMeetings recent)
    {
        _recent = recent;
        _entries = recent.Entries.ToList();
        _recent.Changed += () => Ui(() => { _entries = _recent.Entries.ToList(); RecalcHeight(); Invalidate(); });
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        BackColor = Bg;
        Size = new Size(352, BaseH);

        _wave = new float[Math.Max(8, (Width - 32) / 6)];

        RecalcHeight();
        ApplyRegion();

        _tick.Tick += OnTick;
        _tick.Start();

        _ = Handle;
        Visible = false;
    }

    // ---- layout ----
    private Rectangle IconTile => new(15, 10, 22, 22);
    private Rectangle Pill => _recording
        ? new Rectangle(Width - 14 - 80, 8, 80, 28)
        : new Rectangle(Width - 14 - 96, 8, 96, 28);
    /// <summary>The discard (trash) button — only while recording; sits left of the Stop pill. Icon-sized so
    /// it never collides with the elapsed-time header text.</summary>
    private Rectangle TrashRect => new(Width - 14 - 80 - 8 - 28, 8, 28, 28);

    // ---- public API ----
    public void ShowPanel() => Ui(() => { if (!Visible) { RestorePosition(); Show(); } Raise(); });

    /// <summary>Puts the panel above other windows once, without taking the keyboard (the tray icon, a show).</summary>
    public void Raise() => Ui(() =>
    {
        if (!Visible) return;
        SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // HWND_TOP; NOSIZE | NOMOVE | NOACTIVATE
    });

    // Windows 11 rounds the window and draws its shadow, so the corners are smooth; elsewhere a region cuts them.
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2, border = 0x00303032; // DWMWCP_ROUND; a quiet edge, COLORREF 0x00BBGGRR
        _dwmRounded = DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int)) == 0;
        if (_dwmRounded) DwmSetWindowAttribute(Handle, 34, ref border, sizeof(int));
        ApplyRegion();
    }

    private bool _dwmRounded;

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    public void HidePanel() => Ui(() => { CancelRename(); SavePosition(); if (Visible) Hide(); });

    public void SetIdle() => Ui(() => { _recording = false; _discardArmed = false; Invalidate(); });
    public void ShowRecording(DateTime startedLocal) => Ui(() => { _recording = true; _discardArmed = false; _startedLocal = startedLocal; Array.Clear(_wave); _incoming = 0; if (!Visible && Settings.MeetingPanelVisible) { RestorePosition(); Show(); } Invalidate(); });

    /// <summary>Push a live input level (0..1) into the meter. Thread-safe (single float write).</summary>
    public void SetLevel(float level01)
    {
        float v = Math.Clamp((float)Math.Sqrt(Math.Max(0f, level01)) * 2.4f, 0f, 1f); // normal speech reaches most of the well
        if (v > _incoming) _incoming = v;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        Array.Copy(_wave, 1, _wave, 0, _wave.Length - 1);
        _wave[^1] = _incoming;
        _incoming *= 0.55f;
        // A first trash click ARMS the discard; it disarms itself after a few seconds un-confirmed.
        if (_discardArmed && (DateTime.Now - _discardArmedAt).TotalSeconds > 3.5) { _discardArmed = false; Invalidate(); }
        if (_copiedId != -1 && (DateTime.Now - _copiedAt).TotalSeconds > 1.4) { _copiedId = -1; Invalidate(); }
        if (_recording || _wave.Any(v => v > 0.01f)) Invalidate();
    }

    // ---- painting ----
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Bg);

        if (!_dwmRounded)
        {
            using var bp = Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 14);
            using var pen = new Pen(Border);
            g.DrawPath(pen, bp);
        }

        // The meter's well: a darker inset band, so the level reads as a live instrument, not loose bars.
        var well = new Rectangle(14, MeterCy - 16, Width - 28, 32);
        using (var wp = Rounded(well, 9))
        {
            using (var wb = new SolidBrush(Well)) g.FillPath(wb, wp);
            using (var we = new Pen(WellEdge)) g.DrawPath(we, wp);
        }

        DrawHeader(g);
        // The band between the header and the list is the meter. While the trash is armed it shows the confirm
        // hint instead: the icon-only button needs the hint to make the two-click contract legible.
        if (_recording && _discardArmed)
            TextRenderer.DrawText(g, "Click the trash again to DELETE this recording.", _footFont,
                new Rectangle(16, MeterCy - 7, Width - 32, 14), Red, TextFormatFlags.NoPrefix | TextFormatFlags.Left);
        else
            DrawMeter(g, 22, Width - 22, MeterCy);

        DrawEntries(g);
    }

    private void DrawHeader(Graphics g)
    {
        // Huginn's tile, the same one the meeting pop-up shows: white, with the waveform in ink.
        using (var tile = new SolidBrush(Color.FromArgb(236, 236, 236)))
        using (var tp = Rounded(IconTile, 6)) g.FillPath(tile, tp);
        float[] bars = { 5, 10, 15, 9, 12, 6 };
        using (var ink = new SolidBrush(Color.FromArgb(28, 28, 28)))
            for (int i = 0; i < bars.Length; i++)
            {
                float bx = IconTile.X + 4.2f + i * 2.55f, bh = bars[i];
                using var bar = Rounded(new RectangleF(bx, IconTile.Y + (IconTile.Height - bh) / 2f, 1.6f, bh), 0.8f);
                g.FillPath(ink, bar);
            }
        if (_recording)
        {
            // A red badge on the tile's corner, ringed in the panel colour so it sits on top.
            using var ring = new SolidBrush(Bg);
            using var dot = new SolidBrush(Red);
            g.FillEllipse(ring, IconTile.Right - 7, IconTile.Y - 3, 11, 11);
            g.FillEllipse(dot, IconTile.Right - 5, IconTile.Y - 1, 7, 7);
        }

        if (_recording)
            TextRenderer.DrawText(g, $"REC  {Elapsed()}", _titleFont, new Point(46, 11), Red, TextFormatFlags.NoPrefix);

        DrawActionPill(g, Pill, _recording);
        if (_recording) DrawTrash(g, TrashRect, _discardHover, _discardArmed);
    }

    // The discard (trash) button: ghost while idle-armed=false; red-filled once ARMED (first click) so the
    // destructive second click is visually loud. Icon-only — the footer line carries the confirm hint.
    private void DrawTrash(Graphics g, Rectangle rect, bool hover, bool armed)
    {
        if (armed)
        {
            using var fb = new SolidBrush(hover ? RedHover : Red);
            using var fp = Rounded(rect, rect.Height / 2);
            g.FillPath(fb, fp);
        }
        else if (hover)
        {
            using var hb = new SolidBrush(Color.FromArgb(36, 255, 255, 255));
            using var hp = Rounded(rect, rect.Height / 2);
            g.FillPath(hb, hp);
        }
        var col = armed ? Color.White : (hover ? RedHover : Muted);
        int cx = rect.X + rect.Width / 2, cy = rect.Y + rect.Height / 2;
        using var pen = new Pen(col, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        // lid + handle
        g.DrawLine(pen, cx - 6, cy - 4, cx + 6, cy - 4);
        g.DrawLine(pen, cx - 2, cy - 6, cx + 2, cy - 6);
        // can body + two ribs
        g.DrawLines(pen, new[] { new Point(cx - 4, cy - 4), new Point(cx - 3, cy + 6), new Point(cx + 3, cy + 6), new Point(cx + 4, cy - 4) });
        g.DrawLine(pen, cx - 1, cy - 1, cx - 1, cy + 3);
        g.DrawLine(pen, cx + 1, cy - 1, cx + 1, cy + 3);
    }

    // red pill with a manually-centered record dot / stop square + label
    private void DrawActionPill(Graphics g, Rectangle rect, bool isStop)
    {
        using (var pp = Rounded(rect, rect.Height / 2))
        {
            using (var b = new SolidBrush(_btnHover ? RedHover : Red)) g.FillPath(b, pp);
            // a hairline of light along the top, so the pill reads as raised
            var clip = g.Clip;
            g.SetClip(pp);
            using (var hl = new Pen(Color.FromArgb(56, 255, 255, 255), 1f))
                g.DrawLine(hl, rect.X + rect.Height / 2f, rect.Y + 0.5f, rect.Right - rect.Height / 2f, rect.Y + 0.5f);
            g.Clip = clip;
        }

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

    private void DrawMeter(Graphics g, int x0, int x1, int cy)
    {
        const int step = 6, barW = 3, maxH = MeterMaxH;
        int n = Math.Min(_wave.Length, (x1 - x0) / step);
        for (int i = 0; i < n; i++)
        {
            float lvl = _wave[_wave.Length - n + i];
            float h = Math.Max(2f, lvl * maxH);
            int a = 60 + (int)(195 * Math.Min(1f, lvl));
            using var br = new SolidBrush(Color.FromArgb(a, 240, 240, 242));
            using var bar = Rounded(new RectangleF(x0 + i * step, cy - h / 2f, barW, h), 1.5f);
            g.FillPath(br, bar);
        }
    }

    private void DrawEntries(Graphics g)
    {
        // Fixed-size panel: always the divider + DisplayMax row slots. A slot with a recording renders it;
        // an empty slot renders a skeleton placeholder so the window is a constant height regardless of count.
        // A thin line between rows, so the buttons on the right are easy to match to their meeting.
        using (var lp = new Pen(RowLine))
            for (int i = 1; i < DisplayMax; i++)
                g.DrawLine(lp, 16, JobsTop + i * RowH, Width - 16, JobsTop + i * RowH);

        for (int i = 0; i < DisplayMax; i++)
        {
            int top = JobsTop + i * RowH;
            if (i < _entries.Count)
            {
                if (i == _hoverRow && _editingId == -1)
                {
                    using var hp = Rounded(new Rectangle(10, top + 1, Width - 20, RowH - 2), 6);
                    using var hb = new SolidBrush(RowHover);
                    g.FillPath(hb, hp);
                }
                DrawEntryRow(g, _entries[i], i, top);
            }
            else DrawSkeletonRow(g, i, top);
        }
    }

    private void DrawEntryRow(Graphics g, RecentMeeting entry, int i, int top)
    {
        if (entry.Cancelable)
            using (var halo = new SolidBrush(Color.FromArgb(56, ToneColor(entry.Tone))))
                g.FillEllipse(halo, 16, top + 12, 13, 13);
        using (var d = new SolidBrush(ToneColor(entry.Tone)))
            g.FillEllipse(d, 19, top + 15, 7, 7);

        bool cancelable = entry.Cancelable;
        bool copyable = CanCopy(entry);
        bool odin = CanOpenOdin(entry);
        int rightPad = odin ? 66 : cancelable || copyable ? 40 : 16;
        int nameW = Width - 34 - rightPad;

        // Line 1 = the meeting NAME (Odin note title) once known; the live status before that.
        string name = entry.Title ?? entry.Status;
        Color nameCol = entry.Title != null ? TitleCol : RowName;
        TextRenderer.DrawText(g, name, _rowNameFont, new Rectangle(34, top + 4, nameW, 16), nameCol,
            TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        // Line 2 = the 24-hour time, then the status as chips when the name line is already the title.
        var time = entry.Started.ToString("HH:mm");
        TextRenderer.DrawText(g, time, _rowMetaFont, new Rectangle(34, top + 21, 40, 14), Muted,
            TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.NoPadding);
        if (entry.Title != null)
        {
            int x = 34 + TextRenderer.MeasureText(g, time, _rowMetaFont, Size.Empty, TextFormatFlags.NoPadding).Width + 8;
            foreach (var chip in StatusChips(entry.Status, ShowOdin))
            {
                int w = TextRenderer.MeasureText(g, chip, _rowMetaFont, Size.Empty, TextFormatFlags.NoPadding).Width + 12;
                if (x + w > 34 + nameW) break;
                var r = new Rectangle(x, top + 20, w, 15);
                using (var cp = Rounded(r, 4))
                using (var cb = new SolidBrush(ChipBg)) g.FillPath(cb, cp);
                TextRenderer.DrawText(g, chip, _rowMetaFont, r, ChipText,
                    TextFormatFlags.NoPrefix | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                x += w + 4;
            }
        }

        if (cancelable) DrawCancel(g, CancelRect(i), entry.Id == _hoverCancelId);
        else if (copyable) DrawCopy(g, CopyRect(i), entry.Id == _hoverCopyId, entry.Id == _copiedId);
        if (odin) DrawOpenOdin(g, OpenRect(i), entry.Id == _hoverOdinId);
    }

    // "✓ internal-dev · 1 task(s)" → [1 task] [internal-dev]: the task count first (Bohdan, 2026-11-20), and only when
    // there is an Odin to make tasks; "Saved · Odin offline" → [Saved] [Odin offline].
    internal static IEnumerable<string> StatusChips(string status, bool withTasks)
    {
        var parts = new List<string>();
        string? taskChip = null;
        foreach (var raw in status.Split('·', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var part = raw.TrimStart('✓', ' ');
            var tasks = System.Text.RegularExpressions.Regex.Match(part, @"^(\d+) task\(s\)$");
            if (tasks.Success) { taskChip = tasks.Groups[1].Value == "1" ? "1 task" : $"{tasks.Groups[1].Value} tasks"; continue; }
            if (part.Length > 0) parts.Add(part);
        }
        if (taskChip != null && withTasks) parts.Insert(0, taskChip);
        return parts;
    }

    // A finished meeting with a transcript opens in the meeting window (or, when chosen, in Odin).
    private bool CanOpenOdin(RecentMeeting e) => !e.Cancelable && e.Transcript != null;

    private Rectangle OpenRect(int index) => CancelRect(index); // open sits at the right edge, copy left of it (Bohdan, 2026-11-20)
    private Rectangle CopyRect(int index) { var r = CancelRect(index); r.X -= 26; return r; }

    // "Open in Odin": a rounded square with an arrow leaving its top-right corner.
    private void DrawOpenOdin(Graphics g, Rectangle hit, bool hover)
    {
        if (hover)
        {
            using var hb = new SolidBrush(Color.FromArgb(36, 255, 255, 255));
            using var hp = Rounded(hit, 6);
            g.FillPath(hb, hp);
        }
        float cx = hit.X + hit.Width / 2f, cy = hit.Y + hit.Height / 2f;
        using var pen = new Pen(hover ? TitleCol : Muted, 1.3f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        // the box, open at its top-right corner
        g.DrawLines(pen, new[] { new PointF(cx - 0.5f, cy - 5f), new PointF(cx - 5f, cy - 5f), new PointF(cx - 5f, cy + 5f),
            new PointF(cx + 5f, cy + 5f), new PointF(cx + 5f, cy + 0.5f) });
        // the arrow
        g.DrawLine(pen, cx - 1f, cy + 1f, cx + 5.5f, cy - 5.5f);
        g.DrawLines(pen, new[] { new PointF(cx + 1.5f, cy - 5.5f), new PointF(cx + 5.5f, cy - 5.5f), new PointF(cx + 5.5f, cy - 1.5f) });
    }

    // A finished row with a transcript gets the copy-path button where an in-flight row has its ✕.
    private static bool CanCopy(RecentMeeting e) => !e.Cancelable && e.Transcript != null;

    // The copy icon: two overlapping rounded squares. Right after a copy it shows a check instead.
    private void DrawCopy(Graphics g, Rectangle hit, bool hover, bool copied)
    {
        if (hover)
        {
            using var hb = new SolidBrush(Color.FromArgb(36, 255, 255, 255));
            using var hp = Rounded(hit, 6);
            g.FillPath(hb, hp);
        }
        int cx = hit.X + hit.Width / 2, cy = hit.Y + hit.Height / 2;
        if (copied)
        {
            using var ok = new Pen(Green, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(ok, new[] { new Point(cx - 4, cy), new Point(cx - 1, cy + 3), new Point(cx + 4, cy - 3) });
            return;
        }
        using var pen = new Pen(hover ? TitleCol : Muted, 1.3f);
        using (var back = Rounded(new RectangleF(cx - 2.5f, cy - 5.5f, 8, 8), 1.8f)) g.DrawPath(pen, back);
        using (var fill = new SolidBrush(_hoverRow >= 0 && _hoverRow < _entries.Count && CopyRect(_hoverRow).Contains(hit.Location) ? RowHover : Bg))
        using (var front = Rounded(new RectangleF(cx - 5.5f, cy - 2.5f, 8, 8), 1.8f))
        {
            g.FillPath(fill, front); // hides the back square's corner behind the front one
            if (hover) { using var hb = new SolidBrush(Color.FromArgb(36, 255, 255, 255)); g.FillPath(hb, front); }
            g.DrawPath(pen, front);
        }
    }

    private void CopyPath(RecentMeeting entry)
    {
        var path = ResolveTranscript(entry);
        if (!File.Exists(path)) return;
        try
        {
            Clipboard.SetText(path);
            _copiedId = entry.Id;
            _copiedAt = DateTime.Now;
            Invalidate();
        }
        catch { /* clipboard busy; the user can click again */ }
    }

    // An empty slot's skeleton: a muted dot + two rounded "loading" bars (name + meta line), widths lightly
    // varied per slot so the empty state reads as placeholders rather than real data.
    private static readonly float[] SkelName = { 0.62f, 0.50f, 0.68f, 0.55f, 0.60f, 0.47f, 0.64f, 0.52f };
    private void DrawSkeletonRow(Graphics g, int i, int top)
    {
        int nameW = Width - 34 - 16;
        using var sk = new SolidBrush(Skeleton);
        using (var dot = Rounded(new RectangleF(19, top + 14, 7, 7), 3.5f)) g.FillPath(sk, dot);
        using (var b1 = Rounded(new RectangleF(34, top + 7, nameW * SkelName[i % SkelName.Length], 8), 4f)) g.FillPath(sk, b1);
        using (var b2 = Rounded(new RectangleF(34, top + 22, nameW * 0.34f, 7), 3.5f)) g.FillPath(sk, b2);
    }

    // a small ✕ to stop an in-flight recording's processing
    private void DrawCancel(Graphics g, Rectangle hit, bool hover)
    {
        if (hover)
        {
            using var hb = new SolidBrush(Color.FromArgb(36, 255, 255, 255));
            using var hp = Rounded(hit, 6);
            g.FillPath(hb, hp);
        }
        var c = new Point(hit.X + hit.Width / 2, hit.Y + hit.Height / 2);
        const int r = 4;
        using var pen = new Pen(hover ? RedHover : Muted, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(pen, c.X - r, c.Y - r, c.X + r, c.Y + r);
        g.DrawLine(pen, c.X - r, c.Y + r, c.X + r, c.Y - r);
    }

    private Rectangle CancelRect(int index)
    {
        int top = JobsTop + index * RowH;
        // Centred between the lines above and below the row (they sit at top and top + RowH).
        return new Rectangle(Width - 36, top + RowH / 2 - 11, 22, 22);
    }

    private static Color ToneColor(JobTone t) => t switch
    {
        JobTone.Good => Green,
        JobTone.Bad => Red,
        JobTone.Muted => Muted,
        _ => Amber, // Busy / Warn
    };

    private void RecalcHeight()
    {
        // FIXED size, always: the header + DisplayMax row slots. Slots without a recording render as skeleton
        // placeholders (see DrawSkeletonRow), so the window height never changes with how many meetings exist.
        int h = JobsTop + DisplayMax * RowH + BottomPad;
        if (Height != h) { Height = h; ApplyRegion(); }
        Invalidate();
    }

    private void ApplyRegion()
    {
        if (_dwmRounded) { Region = null; return; }
        using var p = Rounded(new Rectangle(0, 0, Width, Height), 14);
        Region = new Region(p);
    }

    private string Elapsed()
    {
        var t = DateTime.Now - _startedLocal;
        return $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}";
    }

    // ---- mouse ----
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
        {
            int row = RowAt(e.Location);
            if (row >= 0) ShowRowMenu(_entries[row], e.Location);
            else ShowPanelMenu(e.Location); // a menu, not an instant hide: a stray right-click lost the panel
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        if (Pill.Contains(e.Location)) { RecordToggleRequested?.Invoke(); return; }
        if (_recording && TrashRect.Contains(e.Location))
        {
            if (_discardArmed) { _discardArmed = false; DiscardRequested?.Invoke(); }
            else { _discardArmed = true; _discardArmedAt = DateTime.Now; }
            Invalidate();
            return;
        }
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].Cancelable && CancelRect(i).Contains(e.Location))
            {
                CancelRequested?.Invoke(_entries[i].Id);
                return;
            }
            if (CanCopy(_entries[i]) && CopyRect(i).Contains(e.Location))
            {
                CopyPath(_entries[i]);
                return;
            }
            if (CanOpenOdin(_entries[i]) && OpenRect(i).Contains(e.Location))
            {
                OpenRequested?.Invoke(_entries[i].Id);
                return;
            }
        }
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
        bool b = Pill.Contains(e.Location);
        if (b != _btnHover) { _btnHover = b; Invalidate(); }
        bool th = _recording && TrashRect.Contains(e.Location);
        if (th != _discardHover) { _discardHover = th; Invalidate(); }

        int row = RowAt(e.Location);
        if (row != _hoverRow) { _hoverRow = row; Invalidate(); }

        int hover = -1;
        for (int i = 0; i < _entries.Count; i++)
            if (_entries[i].Cancelable && CancelRect(i).Contains(e.Location)) { hover = _entries[i].Id; break; }
        if (hover != _hoverCancelId) { _hoverCancelId = hover; Invalidate(); }

        int copyHover = -1;
        for (int i = 0; i < _entries.Count; i++)
            if (CanCopy(_entries[i]) && CopyRect(i).Contains(e.Location)) { copyHover = _entries[i].Id; break; }
        int odinHover = -1;
        for (int i = 0; i < _entries.Count; i++)
            if (CanOpenOdin(_entries[i]) && OpenRect(i).Contains(e.Location)) { odinHover = _entries[i].Id; break; }
        if (copyHover != _hoverCopyId || odinHover != _hoverOdinId)
        {
            _hoverCopyId = copyHover;
            _hoverOdinId = odinHover;
            Cursor = copyHover == -1 && odinHover == -1 ? Cursors.Default : Cursors.Hand;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_dragging) { _dragging = false; if (_dragMoved) SavePosition(); }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button != MouseButtons.Left) return;
        int row = RowAt(e.Location);
        if (row < 0 || CancelRect(row).Contains(e.Location) || CopyRect(row).Contains(e.Location)) return;
        _dragging = false; // the second mouse-down armed a drag; this is a rename, not a move
        BeginRename(_entries[row], row);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_dragging) return;
        bool changed = _btnHover || _discardHover || _hoverCancelId != -1 || _hoverCopyId != -1 || _hoverOdinId != -1 || _hoverRow != -1;
        _hoverRow = -1;
        _hoverOdinId = -1;
        _btnHover = false;
        _discardHover = false;
        _hoverCancelId = -1;
        _hoverCopyId = -1;
        Cursor = Cursors.Default;
        if (changed) Invalidate();
    }

    // ---- rename (double-click a row, or right-click → Rename) ----
    private int RowAt(Point p)
    {
        if (p.X < 16 || p.X > Width - 16 || p.Y < JobsTop) return -1;
        int i = (p.Y - JobsTop) / RowH; // a row runs from the line above it to the line below
        return i < _entries.Count ? i : -1;
    }

    private static string ResolveTranscript(RecentMeeting e) => RecentMeetings.ResolveTranscript(e);

    private void ShowRowMenu(RecentMeeting entry, Point at)
    {
        _rowMenu?.Dispose();
        _rowMenu = new ContextMenuStrip();
        int row = _entries.IndexOf(entry);
        var path = ResolveTranscript(entry);
        bool hasFile = File.Exists(path);
        _rowMenu.Items.Add("Rename", null, (_, _) => BeginRename(entry, row));
        _rowMenu.Items.Add(new ToolStripMenuItem("Open transcript", null, (_, _) => OpenPath(path)) { Enabled = hasFile });
        _rowMenu.Items.Add(new ToolStripMenuItem("Show in folder", null, (_, _) => RevealPath(path)) { Enabled = hasFile });
        _rowMenu.Items.Add(new ToolStripSeparator());
        _rowMenu.Items.Add("Hide panel", null, (_, _) => HidePanel());
        _rowMenu.Show(this, at);
    }

    // Right-click outside the rows: the same "Hide panel", but only when picked.
    private void ShowPanelMenu(Point at)
    {
        _rowMenu?.Dispose();
        _rowMenu = new ContextMenuStrip();
        _rowMenu.Items.Add("Hide panel", null, (_, _) => HidePanel());
        _rowMenu.Show(this, at);
    }

    private static void OpenPath(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { /* non-fatal */ }
    }

    private static void RevealPath(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch { /* non-fatal */ }
    }

    private void BeginRename(RecentMeeting entry, int row)
    {
        CancelRename();
        if (row < 0) return;
        int top = JobsTop + row * RowH;
        int nameW = Width - 34 - (entry.Cancelable ? 40 : 16);
        _editingId = entry.Id;
        _editor = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = RowHover,
            ForeColor = TitleCol,
            Font = _rowNameFont,
            Text = entry.Title ?? "",
            Location = new Point(34, top + 3),
            Width = nameW,
        };
        _editor.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; CommitRename(); }
            else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; CancelRename(); }
        };
        _editor.LostFocus += (_, _) => CommitRename();
        Controls.Add(_editor);
        _editor.BringToFront();
        _editor.SelectAll();
        _editor.Focus();
    }

    // Enter / focus loss commits. RemoveEditor nulls _editor FIRST, so the LostFocus this triggers is a no-op.
    private void CommitRename()
    {
        var box = _editor;
        if (box == null) return;
        var text = box.Text.Trim();
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
        Controls.Remove(box);
        box.Dispose();
    }

    // ---- position persistence (defaults to the 3rd monitor) ----
    private static string PosFile => Paths.File("meeting_panel_pos.txt");

    private void SavePosition()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(PosFile)!); File.WriteAllText(PosFile, $"{Location.X},{Location.Y}"); }
        catch { /* non-fatal */ }
    }

    public void RestorePosition()
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

    private void DefaultPlace()
    {
        var screens = Screen.AllScreens;
        var target = screens.Length >= 3 ? screens[2] : screens[^1];
        var wa = target.WorkingArea;
        Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + 48);
    }

    private void Ui(Action a)
    {
        if (IsDisposed || Disposing) return; // a late callback from an in-flight job after the panel is gone
        if (IsHandleCreated && InvokeRequired)
        {
            // The panel can be disposed between the guard above and the marshal — swallow that race only.
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
            _titleFont.Dispose();
            _subFont.Dispose();
            _pillFont.Dispose();
            _footFont.Dispose();
            _rowNameFont.Dispose();
            _rowMetaFont.Dispose();
            _editor?.Dispose();
            _rowMenu?.Dispose();
        }
        base.Dispose(disposing);
    }
}
