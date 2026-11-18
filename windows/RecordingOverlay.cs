using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Munin;

/// <summary>
/// The on-demand dictation overlay: a SMALL dark pill near the bottom of the screen (state indicator + live
/// mic waveform), with the live-transcription text floating in a separate caption card ABOVE it — like
/// Whisper Flow floats its context actions above the bar. Appears while you dictate, fades away when done.
///
/// Rendered as a per-pixel-alpha LAYERED window (UpdateLayeredWindow) so the pill + caption have soft
/// anti-aliased edges and a subtle shadow over any desktop content, with nothing but them visible (no opaque
/// rectangle). One 30fps loop drives fade+slide, the waveform, and the processing spinner.
///
/// CRITICAL invariants: never takes keyboard focus (no-activate) AND is click-through (WS_EX_TRANSPARENT), so
/// the app you're typing into stays focused and the Ctrl+V paste lands in the right place. FIXED window size —
/// appearance is animated via the layer's alpha + window position + painted content, never by resizing.
/// </summary>
internal sealed class RecordingOverlay : Form
{
    private enum State { Hidden, Starting, Listening, Transcribing, Cleaning }

    private const int W = 460, H = 116, Pad = 14;
    private const int PillW = 220, PillH = 30, PillRadius = 15;
    private const int PillX = (W - PillW) / 2;
    private const int PillY = H - Pad - PillH;
    private const int PillContentLeft = 28;   // pill-left → content (past the indicator)
    private const int PillContentRight = 14;
    private const int BarW = 3, BarGap = 2;
    private const int MaxTextW = 360; // fixed caption text width — the card never resizes horizontally (no flicker)

    private State _state = State.Hidden;
    private string _preview = "";
    private int _captionLines = 1; // ratchets 1→2 within a dictation, never back → no vertical oscillation
    private float _incoming;
    private readonly float[] _wave;
    private float _shown;
    private float _shownTarget;
    private int _frame;
    private Point _base;
    private readonly Font _font = new("Segoe UI", 10.5f);
    private readonly Font _verbFont = new("Segoe UI Semibold", 9.5f);
    private readonly System.Windows.Forms.Timer _anim;

    private static readonly Color Bg = Color.FromArgb(244, 24, 24, 27);
    private static readonly Color Border = Color.FromArgb(70, 255, 255, 255);
    private static readonly Color RecDot = Color.FromArgb(235, 68, 68);
    private static readonly Color TextColor = Color.FromArgb(236, 236, 239);
    private static readonly Color MutedColor = Color.FromArgb(150, 150, 156);

    public RecordingOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(W, H);

        _wave = new float[(PillW - PillContentLeft - PillContentRight) / (BarW + BarGap)];

        _anim = new System.Windows.Forms.Timer { Interval = 33 }; // ~30 fps
        _anim.Tick += OnTick;

        _ = Handle; // create the handle now so the first Show is instant
        Visible = false;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOPMOST = 0x00000008;
            const int WS_EX_TOOLWINDOW = 0x00000080; // no alt-tab / taskbar entry
            const int WS_EX_LAYERED = 0x00080000;    // per-pixel alpha via UpdateLayeredWindow
            const int WS_EX_TRANSPARENT = 0x00000020; // click-through
            const int WS_EX_NOACTIVATE = 0x08000000; // never activate on show
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    // ---- public state-machine API (called from TrayAppContext, always on the UI thread) ----

    public void ShowStarting()
    {
        _preview = "";
        _captionLines = 1;
        Array.Clear(_wave);
        _incoming = 0f;
        EnterState(State.Starting);
    }

    public void ShowListening() => EnterState(State.Listening);
    public void ShowTranscribing() => EnterState(State.Transcribing);
    public void ShowCleaning() => EnterState(State.Cleaning);

    public void HideOverlay()
    {
        _state = State.Hidden;
        _shownTarget = 0f;
        if (Visible) _anim.Start();
    }

    public void SetLevel(float level01)
    {
        float scaled = Math.Clamp((float)Math.Sqrt(Math.Max(0f, level01)) * 1.9f, 0f, 1f);
        if (scaled > _incoming) _incoming = scaled;
    }

    public void SetPreviewText(string text)
    {
        _preview = text ?? "";
    }

    // ---- internals ----

    private void EnterState(State state)
    {
        _state = state;
        _shownTarget = 1f;
        PositionBase();
        if (!Visible) Show(); // no-activate → doesn't steal focus
        _anim.Start();
    }

    private void PositionBase()
    {
        var wa = Screen.PrimaryScreen!.WorkingArea;
        _base = new Point(wa.Left + (wa.Width - W) / 2, wa.Bottom - H - 60);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _frame++;

        _shown += (_shownTarget - _shown) * 0.28f;
        if (_shownTarget == 0f && _shown < 0.03f)
        {
            _shown = 0f;
            _anim.Stop();
            if (Visible) Hide();
            return;
        }

        int slide = (int)Math.Round((1f - _shown) * 12f);
        Location = new Point(_base.X, _base.Y + slide);

        Array.Copy(_wave, 1, _wave, 0, _wave.Length - 1);
        _wave[^1] = _state == State.Listening ? _incoming : 0f;
        _incoming *= 0.6f;

        Render((byte)(Math.Clamp(_shown, 0f, 1f) * 247f));
    }

    // ---- compose the frame into a 32bpp ARGB bitmap and push it to the layered window ----

    private void Render(byte alpha)
    {
        using var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            DrawContent(g);
        }

        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
        IntPtr oldBitmap = SelectObject(memDc, hBitmap);
        try
        {
            var size = new SIZE { cx = W, cy = H };
            var src = new POINT { x = 0, y = 0 };
            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = alpha,
                AlphaFormat = AC_SRC_ALPHA,
            };
            UpdateLayeredWindow(Handle, screenDc, IntPtr.Zero, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            SelectObject(memDc, oldBitmap);
            DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private void DrawContent(Graphics g)
    {
        // ---- the pill ----
        var pill = new Rectangle(PillX, PillY, PillW, PillH);
        DrawShadow(g, pill, PillRadius);
        FillRounded(g, pill, PillRadius, Bg);
        DrawBorder(g, pill, PillRadius, Border);

        float cy = PillY + PillH / 2f;
        DrawIndicator(g, PillX + 15, cy);

        var content = new Rectangle(PillX + PillContentLeft, PillY, PillW - PillContentLeft - PillContentRight, PillH);
        switch (_state)
        {
            case State.Starting:
                DrawWaveform(g, content); // flat until audio arrives
                break;
            case State.Listening:
                DrawWaveform(g, content); // waveform ALWAYS lives in the pill now
                break;
            case State.Transcribing:
                DrawProcessing(g, content, "Transcribing");
                break;
            case State.Cleaning:
                DrawProcessing(g, content, "Cleaning");
                break;
        }

        // ---- caption card floating ABOVE the pill (live transcription text) ----
        // Shown through processing too (not just Listening), so the real-time text doesn't vanish the instant
        // you release — it stays until the result is pasted and the overlay fades.
        if (_state != State.Starting && !string.IsNullOrEmpty(_preview))
            DrawCaption(g);
    }

    private void DrawCaption(Graphics g)
    {
        var flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.Left;
        int lineH = _font.Height;

        // FIXED text width → the card never resizes/re-centers horizontally as words change (that was the
        // flicker). Text is left-aligned; the card is bottom-anchored just above the pill, so it only grows
        // UPWARD to a 2nd line (the pill stays put). Between the 700ms preview updates the frame is rock-steady.
        const int px = 13, py = 8;
        const int textW = MaxTextW;
        Size measured = TextRenderer.MeasureText(g, _preview, _font, new Size(textW, 0), flags);
        if (measured.Height > lineH) _captionLines = 2; // ratchet to 2 lines and STAY (no 1↔2 oscillation flicker)
        int textH = lineH * _captionLines;

        int cardW = textW + 2 * px;
        int cardH = textH + 2 * py;
        int cardX = (W - cardW) / 2;
        int cardY = PillY - 8 - cardH;

        var card = new Rectangle(cardX, cardY, cardW, cardH);
        DrawShadow(g, card, 12);
        FillRounded(g, card, 12, Bg);
        DrawBorder(g, card, 12, Border);

        var textRect = new Rectangle(cardX + px, cardY + py, textW, textH);
        TextRenderer.DrawText(g, _preview, _font, textRect, TextColor, flags | TextFormatFlags.Bottom);
    }

    private void DrawIndicator(Graphics g, float cx, float cy)
    {
        switch (_state)
        {
            case State.Starting:
            {
                float pulse = 0.45f + 0.55f * (float)Math.Abs(Math.Sin(_frame * 0.16));
                using var b = new SolidBrush(Color.FromArgb((int)(120 + 120 * pulse), 200, 200, 205));
                float r = 4f;
                g.FillEllipse(b, cx - r, cy - r, 2 * r, 2 * r);
                break;
            }
            case State.Listening:
            {
                using (var glow = new SolidBrush(Color.FromArgb(55, RecDot)))
                    g.FillEllipse(glow, cx - 8, cy - 8, 16, 16);
                using var b = new SolidBrush(RecDot);
                float r = 4.5f;
                g.FillEllipse(b, cx - r, cy - r, 2 * r, 2 * r);
                break;
            }
            case State.Transcribing:
            case State.Cleaning:
            {
                using var pen = new Pen(Color.FromArgb(215, 215, 219), 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                float r = 6f;
                float start = (_frame * 11f) % 360f;
                g.DrawArc(pen, cx - r, cy - r, 2 * r, 2 * r, start, 270f);
                break;
            }
        }
    }

    private void DrawProcessing(Graphics g, Rectangle area, string verb)
    {
        var sz = TextRenderer.MeasureText(g, verb, _verbFont);
        var textRect = new Rectangle(area.X, area.Y, sz.Width, area.Height);
        TextRenderer.DrawText(g, verb, _verbFont, textRect, TextColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        float dotY = area.Y + area.Height / 2f;
        float x0 = area.X + sz.Width + 9;
        int active = (_frame / 4) % 5;
        for (int i = 0; i < 5; i++)
        {
            int a = i == active ? 235 : 70;
            using var b = new SolidBrush(Color.FromArgb(a, 210, 210, 214));
            float r = 2f;
            float x = x0 + i * 8;
            g.FillEllipse(b, x - r, dotY - r, 2 * r, 2 * r);
        }
    }

    private void DrawWaveform(Graphics g, Rectangle area)
    {
        const int waveH = 16;
        float centerY = area.Y + area.Height / 2f;
        for (int i = 0; i < _wave.Length; i++)
        {
            float lvl = _wave[i];
            float h = Math.Max(2f, lvl * waveH);
            int a = 55 + (int)(190 * Math.Min(1f, lvl));
            using var bar = new SolidBrush(Color.FromArgb(a, 236, 236, 239));
            g.FillRectangle(bar, area.X + i * (BarW + BarGap), centerY - h / 2f, BarW, h);
        }
    }

    // ---- drawing helpers ----

    private static void DrawShadow(Graphics g, Rectangle r, int radius)
    {
        for (int i = 5; i >= 1; i--)
        {
            var rr = new Rectangle(r.X - i, r.Y - i + 2, r.Width + 2 * i, r.Height + 2 * i);
            using var path = RoundedRect(rr, radius + i);
            using var b = new SolidBrush(Color.FromArgb(10, 0, 0, 0));
            g.FillPath(b, path);
        }
    }

    private static void FillRounded(Graphics g, Rectangle r, int radius, Color color)
    {
        using var path = RoundedRect(r, radius);
        using var b = new SolidBrush(color);
        g.FillPath(b, path);
    }

    private static void DrawBorder(Graphics g, Rectangle r, int radius, Color color)
    {
        using var path = RoundedRect(new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1), radius);
        using var pen = new Pen(color);
        g.DrawPath(pen, path);
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = Math.Max(2, radius * 2);
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _anim.Dispose();
            _font.Dispose();
            _verbFont.Dispose();
        }
        base.Dispose(disposing);
    }

    // ---- layered-window interop ----

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] private struct BLENDFUNCTION
    {
        public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }

    private const byte AC_SRC_OVER = 0;
    private const byte AC_SRC_ALPHA = 1;
    private const int ULW_ALPHA = 2;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, IntPtr pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
}
