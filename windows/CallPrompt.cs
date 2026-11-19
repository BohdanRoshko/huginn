using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Huginn;

/// <summary>
/// The "Start meeting recording" pop-up, laid out like Notion's meeting pop-up: a dark pill at the top centre of the
/// screen with an icon tile, a title and a subtitle, a grey "Not now" button, and a blue split button ("Start recording"
/// and an arrow with the other choices). Sizes and colours were measured from Notion's pop-up on this screen (2026-11-18);
/// "Not now" is ours, so a dismiss is one click (2026-11-19). It never takes focus from the call. It goes away on a
/// choice, when the call ends, or after <see cref="ShowSeconds"/>; a thin bar along its bottom edge fills as that time
/// runs out, as Notion's does. It fades and drops in, and fades out. Under the mouse it grows as Notion's does
/// (measured from Notion's on this screen, 2026-11-20): a lighter strip under the pill with a note running across it,
/// and a round close button on the top-left corner. The countdown waits while the mouse is on it.
/// </summary>
internal sealed class CallPrompt : Form, ICallPrompt
{
    private const int ShowSeconds = 15;      // half of Notion's 30 (Bohdan, 2026-11-19)
    private const double FadeInSeconds = 0.18, FadeOutSeconds = 0.12, DropPixels = 8;

    // The layout, in design units (Notion's sizes). It is drawn at Shrink x the screen's DPI scale, in whole pixels.
    private const int Pad = 14;                 // room for the shadow around the pill
    private const int PillW = 556, PillH = 61;   // Notion's 460 plus room for "Not now"
    private const int ExtraH = 27;                // the strip the pill grows under the mouse
    private const double ExpandSeconds = 0.18, RunPixelsPerSecond = 34, RunPauseSeconds = 0.7;
    private const int RunGap = 21;                // between the end of the note and its next start, as Notion's
    private const int W = PillW + 2 * Pad, H = PillH + ExtraH + 2 * Pad;
    private const float Shrink = 0.85f;          // 15% smaller than Notion's (Bohdan, 2026-11-18)
    private static readonly Rectangle Pill = new(Pad, Pad, PillW, PillH);
    private static readonly Rectangle Button = new(Pad + PillW - 15 - 176, Pad + 12, 176, 36);
    private static readonly Rectangle Arrow = new(Button.Right - 31, Button.Y, 31, Button.Height);
    private static readonly Rectangle Main = new(Button.X, Button.Y, Button.Width - Arrow.Width, Button.Height);
    private static readonly Rectangle NotNow = new(Button.X - 8 - 88, Button.Y, 88, Button.Height);
    private static readonly Rectangle Tile = new(Pill.X + 22, Pill.Y + 14, 32, 33);
    private static readonly Rectangle Close = new(Pill.X + 7 - 12, Pill.Y + 9 - 12, 24, 24); // Notion's close button, centred on the corner

    private static readonly Color Bg = Color.FromArgb(32, 32, 32);          // #202020
    private static readonly Color Edge = Color.FromArgb(40, 255, 255, 255);
    private static readonly Color Blue = Color.FromArgb(39, 131, 222);      // #2783DE
    private static readonly Color BlueHover = Color.FromArgb(58, 145, 230);
    private static readonly Color Sub = Color.FromArgb(155, 154, 152);
    private static readonly Color Countdown = Color.FromArgb(125, 122, 117);
    private static readonly Color Quiet = Color.FromArgb(20, 255, 255, 255);       // "Not now": a quiet fill, so it reads as a button
    private static readonly Color QuietHover = Color.FromArgb(36, 255, 255, 255);
    private static readonly Color QuietText = Color.FromArgb(226, 224, 220);
    private static readonly Color Strip = Color.FromArgb(56, 56, 54);         // #383836, the grown strip
    private static readonly Color NoteText = Color.FromArgb(172, 171, 167);
    private static readonly Color CloseRing = Color.FromArgb(56, 56, 56);
    private static readonly Color CloseMark = Color.FromArgb(172, 172, 172);

    private float _k;                 // device pixels per design unit
    private int _sw, _sh;             // window size in device pixels
    private Point _home;              // where the window rests once it has dropped in
    private Font? _title, _sub, _button, _quiet, _note;
    private readonly System.Windows.Forms.Timer _timeout = new() { Interval = ShowSeconds * 1000 };
    private readonly ContextMenuStrip _menu = new();
    private readonly Stopwatch _shown = new(), _hiding = new(), _left = new(), _expandClock = new(), _lastFrame = new();
    private bool _over;               // the mouse is on the pill
    private double _expand;           // 0 = a pill, 1 = grown by ExtraH
    private string _app = "";
    private int _hover; // 0 none, 1 main, 2 arrow, 3 not now, 4 close

    // Frames: a background thread waits for each screen refresh (DwmFlush) and asks the UI thread for one frame, so
    // the countdown bar moves once per refresh and never stutters. It runs only while the pop-up is on screen.
    private Thread? _frames;
    private volatile bool _running;
    private int _framePending;

    // The window's pixels: a premultiplied 32-bit DIB that UpdateLayeredWindow reads directly.
    private IntPtr _memDc, _dib, _oldBmp, _bits;
    private Bitmap? _canvas;

    public event Action? StartRequested;
    public event Action<string>? OptionChosen;

    public CallPrompt()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        _timeout.Tick += (_, _) => { _timeout.Stop(); OptionChosen?.Invoke("not now"); };
        _menu.Items.Add("Always record meetings automatically", null, (_, _) => OptionChosen?.Invoke("auto"));
        _menu.Items.Add("Don't ask again", null, (_, _) => OptionChosen?.Invoke("off"));
        _ = Handle;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOPMOST = 0x00000008, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_LAYERED = 0x00080000, WS_EX_NOACTIVATE = 0x08000000;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    public void ShowPrompt(string app) => Ui(() =>
    {
        _app = app;
        _hover = 0;
        _over = false;
        _expand = 0;
        _left.Restart();
        // Top centre of the main screen, always (Bohdan's choice, 2026-11-18; not the screen with the active window).
        var screen = Screen.PrimaryScreen ?? Screen.AllScreens[0];
        Layout(GetDpiForWindow(Handle) / 96f);
        _home = new Point(screen.Bounds.Left + (screen.Bounds.Width - _sw) / 2, screen.Bounds.Top + (int)Math.Round(16 * _k / Shrink) - Px(Pad));
        Bounds = new Rectangle(_home, new Size(_sw, _sh));
        _hiding.Reset();
        _shown.Restart();
        Frame();
        if (!Visible) Show();
        _timeout.Stop();
        _timeout.Interval = ShowSeconds * 1000;
        _timeout.Start();
        StartFrames();
    });

    // The countdown waits while the mouse is on the pill, so the note can be read; it goes on where it stopped.
    private void SetOver(bool over)
    {
        if (over == _over || _hiding.IsRunning || !_shown.IsRunning) return;
        _over = over;
        if (over)
        {
            _left.Stop();
            _timeout.Stop();
            _expandClock.Restart();
        }
        else
        {
            _left.Start();
            _timeout.Interval = Math.Max(500, ShowSeconds * 1000 - (int)_left.ElapsedMilliseconds);
            _timeout.Start();
        }
    }

    private string Note => $"Huginn records your microphone and the {_app} audio on this computer. By starting, you confirm everyone in the call knows it is recorded.";

    public void HidePrompt() => Ui(() =>
    {
        _timeout.Stop();
        _menu.Close();
        _over = false; // a mouse leaving a closed pop-up must not start its countdown again
        if (Visible && !_hiding.IsRunning) _hiding.Restart();   // the frame loop fades it out, then hides it
    });

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var at = Design(e.Location);
        bool onClose = _expand > 0.5 && Close.Contains(at);
        SetOver(onClose || new Rectangle(Pill.X, Pill.Y, Pill.Width, Pill.Height + (int)(ExtraH * _expand)).Contains(at));
        int h = onClose ? 4 : Main.Contains(at) ? 1 : Arrow.Contains(at) ? 2 : NotNow.Contains(at) ? 3 : 0;
        Cursor = h == 0 ? Cursors.Default : Cursors.Hand;
        _hover = h;
    }

    protected override void OnMouseLeave(EventArgs e) { _hover = 0; SetOver(false); }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || _hiding.IsRunning) return;
        var at = Design(e.Location);
        if (_expand > 0.5 && Close.Contains(at)) OptionChosen?.Invoke("not now");
        else if (Main.Contains(at)) StartRequested?.Invoke();
        else if (NotNow.Contains(at)) OptionChosen?.Invoke("not now");
        else if (Arrow.Contains(at)) _menu.Show(this, new Point(Px(Arrow.Left), Px(Arrow.Bottom + 6)));
    }

    // ---- frames ----

    private void StartFrames()
    {
        if (_running) return;
        _running = true;
        _frames = new Thread(() =>
        {
            var gap = Stopwatch.StartNew();
            while (_running)
            {
                // DwmFlush returns at the next refresh; if it returns early (no composition), wait a frame by hand.
                if (DwmFlush() != 0 || gap.Elapsed.TotalMilliseconds < 4) Thread.Sleep(8);
                gap.Restart();
                if (Interlocked.Exchange(ref _framePending, 1) == 0)
                    try { BeginInvoke(Frame); } catch (InvalidOperationException) { _running = false; }
            }
        }) { IsBackground = true, Name = "Huginn pop-up frames" };
        _frames.Start();
    }

    private void Frame()
    {
        _framePending = 0;
        if (IsDisposed) return;
        // The grow and shrink: a constant rate toward the target, eased when drawn.
        var dt = _lastFrame.IsRunning ? Math.Min(0.1, _lastFrame.Elapsed.TotalSeconds) : 0;
        _lastFrame.Restart();
        _expand = Math.Clamp(_expand + (_over ? dt : -dt) / ExpandSeconds, 0, 1);
        double opacity, drop;
        if (_hiding.IsRunning)
        {
            var p = Math.Min(1, _hiding.Elapsed.TotalSeconds / FadeOutSeconds);
            opacity = 1 - p * p;           // ease-in on the way out
            drop = 0;
            if (p >= 1)
            {
                _running = false;
                _hiding.Reset();
                _shown.Stop();
                Hide();
                return;
            }
        }
        else
        {
            var p = Math.Min(1, _shown.Elapsed.TotalSeconds / FadeInSeconds);
            var eased = 1 - Math.Pow(1 - p, 3);   // ease-out on the way in
            opacity = eased;
            drop = (1 - eased) * DropPixels * _k / Shrink;
        }
        Render((byte)Math.Round(opacity * 255), (int)Math.Round(drop));
    }

    // ---- drawing: a per-pixel-alpha layered window, so the rounded pill and its shadow have soft edges ----

    private void Layout(float dpiScale)
    {
        var k = Shrink * dpiScale;
        if (k == _k && _canvas != null) return;
        _k = k;
        _sw = Px(W);
        _sh = Px(H);
        // Fonts in pixels, so the text is hinted at the size it is drawn, not scaled after.
        _title?.Dispose(); _sub?.Dispose(); _button?.Dispose(); _quiet?.Dispose();
        _title = new Font("Segoe UI Semibold", 14f * k, FontStyle.Regular, GraphicsUnit.Pixel);
        _sub = new Font("Segoe UI", 12.7f * k, FontStyle.Regular, GraphicsUnit.Pixel);
        _button = new Font("Segoe UI Semibold", 13.3f * k, FontStyle.Regular, GraphicsUnit.Pixel);
        _quiet = new Font("Segoe UI Semibold", 13.3f * k, FontStyle.Regular, GraphicsUnit.Pixel);
        _note?.Dispose();
        _note = new Font("Segoe UI", 13f * k, FontStyle.Regular, GraphicsUnit.Pixel);
        FreeSurface();
        _canvas = new Bitmap(_sw, _sh, PixelFormat.Format32bppPArgb);
        var info = new BITMAPINFO { biSize = 40, biWidth = _sw, biHeight = -_sh, biPlanes = 1, biBitCount = 32 };
        var screenDc = GetDC(IntPtr.Zero);
        _memDc = CreateCompatibleDC(screenDc);
        _dib = CreateDIBSection(screenDc, ref info, 0, out _bits, IntPtr.Zero, 0);
        _oldBmp = SelectObject(_memDc, _dib);
        ReleaseDC(IntPtr.Zero, screenDc);
    }

    private void Render(byte opacity, int drop)
    {
        if (_canvas == null) return;
        using (var g = Graphics.FromImage(_canvas))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            Draw(g);
        }
        // Copy the premultiplied pixels into the DIB the window shows.
        var data = _canvas.LockBits(new Rectangle(0, 0, _sw, _sh), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var bytes = _sw * 4;
            for (int y = 0; y < _sh; y++)
                CopyMemory(_bits + y * bytes, data.Scan0 + y * data.Stride, (uint)bytes);
        }
        finally { _canvas.UnlockBits(data); }

        var size = new SIZE { cx = _sw, cy = _sh };
        var src = new POINT { x = 0, y = 0 };
        var dst = new POINT { x = _home.X, y = _home.Y - drop };
        var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = opacity, AlphaFormat = 1 };
        UpdateLayeredWindow(Handle, IntPtr.Zero, ref dst, ref size, _memDc, ref src, 0, ref blend, 2);
    }

    private void Draw(Graphics g)
    {
        var grow = (float)(_expand * _expand * (3 - 2 * _expand)); // smoothstep
        var pill = D(Pill);
        pill.Height = MathF.Round(pill.Height + ExtraH * _k * grow);
        var radius = Px(PillH) / 2f; // Notion's keeps its round ends when it grows

        // Soft shadow: a few widening, fading rounded rects under the pill.
        for (int i = Pad; i > 0; i -= 2)
        {
            var r = new RectangleF(pill.X - i * _k / 2, pill.Y - i * _k / 4, pill.Width + i * _k, pill.Height + i * _k);
            using var shadow = Rounded(r, r.Height / 2 < radius + i * _k / 2 ? r.Height / 2 : radius + i * _k / 2);
            using var b = new SolidBrush(Color.FromArgb(Math.Max(2, 22 - i), 0, 0, 0));
            g.FillPath(b, shadow);
        }

        using (var body = Rounded(pill, radius))
        {
            using (var bg = new SolidBrush(Bg)) g.FillPath(bg, body);
            if (grow > 0.01f) DrawNote(g, body, pill, grow);

            // The countdown: a thin warm-grey bar along the bottom edge, filling left to right as the time runs out.
            // Its width is fractional, so its edge moves smoothly between pixels.
            var elapsed = Math.Clamp(_left.Elapsed.TotalSeconds / ShowSeconds, 0, 1);
            var clip = g.Clip;
            g.SetClip(body);
            float barH = Math.Max(2, MathF.Round(2 * _k));
            using (var bar = new SolidBrush(Countdown))
                g.FillRectangle(bar, pill.X, pill.Bottom - barH, (float)(pill.Width * elapsed), barH);
            g.Clip = clip;
        }
        // The edge: a 1-pixel line on the pixel grid, half a pixel inside the pill.
        using (var edgePath = Rounded(RectangleF.Inflate(pill, -0.5f, -0.5f), radius - 0.5f))
        using (var edge = new Pen(Edge, 1f))
            g.DrawPath(edge, edgePath);

        // Icon tile: white rounded square with Huginn's waveform (Notion's tile sits at the same place and size).
        var tile = D(Tile);
        using (var tp = Rounded(tile, 7 * _k))
        using (var white = new SolidBrush(Color.White))
            g.FillPath(white, tp);
        int[] bars = { 8, 16, 24, 14, 20, 10 };
        using (var ink = new SolidBrush(Color.FromArgb(25, 25, 25)))
            for (int i = 0; i < bars.Length; i++)
            {
                float bx = tile.X + (6 + i * 3.8f) * _k, bh = bars[i] * _k, bw = 2.4f * _k;
                using var bar = Rounded(new RectangleF(bx, tile.Y + (tile.Height - bh) / 2f, bw, bh), bw / 2);
                g.FillPath(ink, bar);
            }

        // GDI+ text, not TextRenderer: GDI writes alpha 0 into a 32-bit layered bitmap and the letters would vanish.
        float tx = Px(Pill.X + 64);
        using (var w = new SolidBrush(Color.White)) g.DrawString("Start Meeting Recording", _title!, w, tx, Px(Pill.Y + 13));
        using (var sb = new SolidBrush(Sub))
        using (var fmt = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            g.DrawString($"{_app} detected", _sub!, sb, new RectangleF(tx + 1, Px(Pill.Y + 33), Px(NotNow.X) - tx - Px(12), Px(20)), fmt);

        using var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

        // "Not now": the same height and shape as the blue button, grey and quiet, so Start recording stays the focus.
        var notNow = D(NotNow);
        using (var np = Rounded(notNow, notNow.Height / 2))
        using (var nb = new SolidBrush(_hover == 3 ? QuietHover : Quiet))
            g.FillPath(nb, np);
        using (var qt = new SolidBrush(QuietText))
            g.DrawString("Not now", _quiet!, qt, notNow, center);

        // Split button, fully rounded: "Start recording" | ⌄
        var button = D(Button);
        var arrow = D(Arrow);
        var main = D(Main);
        using (var bp = Rounded(button, button.Height / 2))
        {
            using (var blue = new SolidBrush(Blue)) g.FillPath(blue, bp);
            if (_hover is 1 or 2)
            {
                var clip = g.Clip;
                g.SetClip(_hover == 1 ? main : arrow);
                using var hb = new SolidBrush(BlueHover);
                g.FillPath(hb, bp);
                g.Clip = clip;
            }
        }
        using (var div = new Pen(Color.FromArgb(46, 255, 255, 255), 1f))
            g.DrawLine(div, arrow.X + 0.5f, arrow.Y + 9 * _k, arrow.X + 0.5f, arrow.Bottom - 9 * _k);
        using (var w = new SolidBrush(Color.White))
            g.DrawString("Start recording", _button!, w, new RectangleF(main.X + 3 * _k, main.Y, main.Width, main.Height), center);
        using (var chevron = new Pen(Color.White, 1.4f * _k) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            float cx = arrow.X + arrow.Width / 2f - _k, cy = arrow.Y + arrow.Height / 2f;
            g.DrawLines(chevron, new[] { new PointF(cx - 4.5f * _k, cy - 2f * _k), new PointF(cx, cy + 2.5f * _k), new PointF(cx + 4.5f * _k, cy - 2f * _k) });
        }
        if (grow > 0.01f) DrawClose(g, grow);
    }

    // The grown strip: lighter grey from the pill's bottom edge down, with the note running right to left across
    // its whole width when it is wider than the strip. The pill's round ends clip it, as Notion's do.
    private void DrawNote(Graphics g, GraphicsPath body, RectangleF pill, float grow)
    {
        var strip = new RectangleF(pill.X, Px(Pill.Y + PillH), pill.Width, pill.Bottom - Px(Pill.Y + PillH));
        if (strip.Height <= 0) return;
        var clip = g.Clip;
        g.SetClip(body);
        g.IntersectClip(strip);
        using (var sb = new SolidBrush(Strip)) g.FillRectangle(sb, strip);
        var size = g.MeasureString(Note, _note!, PointF.Empty, StringFormat.GenericTypographic);
        float start = pill.X + Px(22), x = start, gap = Px(RunGap);
        bool runs = start + size.Width > pill.Right - Px(22);
        if (runs)
        {
            var run = Math.Max(0, _expandClock.Elapsed.TotalSeconds - RunPauseSeconds) * RunPixelsPerSecond * _k / Shrink;
            x = start - (float)(run % (size.Width + gap));
        }
        // The text sits in the middle of the full strip, so it slides up into place as the strip opens.
        float y = MathF.Round(Px(Pill.Y + PillH) + (Px(ExtraH) - size.Height) / 2);
        using (var ink = new SolidBrush(Color.FromArgb((int)(255 * grow), NoteText)))
        {
            g.DrawString(Note, _note!, ink, x, y, StringFormat.GenericTypographic);
            if (runs) g.DrawString(Note, _note!, ink, x + size.Width + gap, y, StringFormat.GenericTypographic);
        }
        g.Clip = clip;
    }

    // Notion's round close button, on the pill's top-left corner while the mouse is on it.
    private void DrawClose(Graphics g, float grow)
    {
        var c = D(Close);
        int a = (int)(255 * grow);
        using (var circle = new GraphicsPath())
        {
            circle.AddEllipse(c);
            using var fill = new SolidBrush(Color.FromArgb(a, _hover == 4 ? Color.FromArgb(44, 44, 44) : Bg));
            g.FillPath(fill, circle);
        }
        using (var ring = new Pen(Color.FromArgb(a, CloseRing), 1f))
            g.DrawEllipse(ring, RectangleF.Inflate(c, -0.5f, -0.5f));
        float cx = c.X + c.Width / 2f, cy = c.Y + c.Height / 2f, h = 4.5f * _k / Shrink * 0.85f;
        using var mark = new Pen(Color.FromArgb(a, CloseMark), 1.5f * _k / Shrink * 0.85f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(mark, cx - h, cy - h, cx + h, cy + h);
        g.DrawLine(mark, cx + h, cy - h, cx - h, cy + h);
    }

    // Design units → device pixels, snapped to whole pixels so edges land on the grid.
    private int Px(float v) => (int)MathF.Round(v * _k);
    private RectangleF D(Rectangle r) => RectangleF.FromLTRB(Px(r.Left), Px(r.Top), Px(r.Right), Px(r.Bottom));

    // A window point back in design units, for hit-testing the layout.
    private Point Design(Point p) => new((int)(p.X / _k), (int)(p.Y / _k));

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private void FreeSurface()
    {
        if (_memDc != IntPtr.Zero)
        {
            SelectObject(_memDc, _oldBmp);
            DeleteObject(_dib);
            DeleteDC(_memDc);
            _memDc = _dib = _bits = IntPtr.Zero;
        }
        _canvas?.Dispose();
        _canvas = null;
    }

    private void Ui(Action a)
    {
        if (InvokeRequired) BeginInvoke(a); else a();
    }

    protected override void Dispose(bool disposing)
    {
        _running = false;
        if (disposing)
        {
            _timeout.Dispose(); _menu.Dispose();
            _title?.Dispose(); _sub?.Dispose(); _button?.Dispose(); _quiet?.Dispose(); _note?.Dispose();
        }
        FreeSurface();
        base.Dispose(disposing);
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        public int bmiColors;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pprSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")] private static extern void CopyMemory(IntPtr dest, IntPtr src, uint count);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
}
