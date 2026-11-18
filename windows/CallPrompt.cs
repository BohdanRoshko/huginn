using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Huginn;

/// <summary>
/// The "Start meeting recording" pop-up, laid out like Notion's meeting pop-up: a dark pill at the top centre of the
/// screen with an icon tile, a title and a subtitle, and a blue split button ("Start recording" and an arrow with the
/// other choices). Sizes and colours were measured from Notion's pop-up on this screen (2026-11-18). It never takes
/// focus from the call. It goes away on a choice, when the call ends, or after <see cref="ShowSeconds"/>; a thin bar
/// along its bottom edge fills as that time runs out, as Notion's does.
/// </summary>
internal sealed class CallPrompt : Form, ICallPrompt
{
    private const int ShowSeconds = 30;
    private const int Pad = 14;                 // room for the shadow around the pill
    private const int PillW = 460, PillH = 61;
    private const int W = PillW + 2 * Pad, H = PillH + 2 * Pad;
    private static readonly Rectangle Pill = new(Pad, Pad, PillW, PillH);
    private static readonly Rectangle Button = new(Pad + PillW - 15 - 176, Pad + 12, 176, 36);
    private static readonly Rectangle Arrow = new(Button.Right - 31, Button.Y, 31, Button.Height);
    private static readonly Rectangle Main = new(Button.X, Button.Y, Button.Width - Arrow.Width, Button.Height);

    private static readonly Color Bg = Color.FromArgb(32, 32, 32);          // #202020
    private static readonly Color Edge = Color.FromArgb(40, 255, 255, 255);
    private static readonly Color Blue = Color.FromArgb(39, 131, 222);      // #2783DE
    private static readonly Color BlueHover = Color.FromArgb(58, 145, 230);
    private static readonly Color Sub = Color.FromArgb(155, 154, 152);
    private static readonly Color Countdown = Color.FromArgb(125, 122, 117);

    private readonly Font _title = new("Segoe UI", 10.5f, FontStyle.Bold);
    private readonly Font _sub = new("Segoe UI", 9.5f);
    private readonly Font _button = new("Segoe UI", 10f, FontStyle.Bold);
    private readonly System.Windows.Forms.Timer _timeout = new() { Interval = ShowSeconds * 1000 };
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 100 }; // redraws the countdown bar
    private DateTime _shownAt;
    private readonly ContextMenuStrip _menu = new();
    private string _app = "";
    private int _hover; // 0 none, 1 main, 2 arrow

    public event Action? StartRequested;
    public event Action<string>? OptionChosen;

    public CallPrompt()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(W, H);
        _timeout.Tick += (_, _) => { _timeout.Stop(); OptionChosen?.Invoke("not now"); };
        _tick.Tick += (_, _) => { if (Visible) Render(); };
        _menu.Items.Add("Not now", null, (_, _) => OptionChosen?.Invoke("not now"));
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
        // Top centre of the main screen, always (Bohdan's choice, 2026-11-18; not the screen with the active window).
        var screen = Screen.PrimaryScreen ?? Screen.AllScreens[0];
        Location = new Point(screen.Bounds.Left + (screen.Bounds.Width - W) / 2, screen.Bounds.Top + 16 - Pad);
        _shownAt = DateTime.Now;
        if (!Visible) Show();
        Render();
        _timeout.Stop();
        _timeout.Start();
        _tick.Start();
    });

    public void HidePrompt() => Ui(() =>
    {
        _timeout.Stop();
        _tick.Stop();
        _menu.Close();
        if (Visible) Hide();
    });

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = Main.Contains(e.Location) ? 1 : Arrow.Contains(e.Location) ? 2 : 0;
        Cursor = h == 0 ? Cursors.Default : Cursors.Hand;
        if (h != _hover) { _hover = h; Render(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hover != 0) { _hover = 0; Render(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (Main.Contains(e.Location)) StartRequested?.Invoke();
        else if (Arrow.Contains(e.Location)) _menu.Show(this, new Point(Arrow.Left, Arrow.Bottom + 6));
    }

    // ---- drawing: a per-pixel-alpha layered window, so the rounded pill and its shadow have soft edges ----

    private void Render()
    {
        using var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            Draw(g);
        }
        Push(bmp);
    }

    private void Draw(Graphics g)
    {
        // Soft shadow: a few widening, fading rounded rects under the pill.
        for (int i = Pad; i > 0; i -= 2)
        {
            using var shadow = Rounded(new RectangleF(Pill.X - i / 2f, Pill.Y - i / 4f, Pill.Width + i, Pill.Height + i), (Pill.Height + i) / 2f);
            using var b = new SolidBrush(Color.FromArgb(Math.Max(2, 22 - i), 0, 0, 0));
            g.FillPath(b, shadow);
        }
        using (var pill = Rounded(Pill, Pill.Height / 2f))
        {
            using var bg = new SolidBrush(Bg);
            g.FillPath(bg, pill);

            // The countdown: a thin warm-grey bar along the bottom edge, filling left to right as the time runs out.
            var elapsed = Math.Clamp((DateTime.Now - _shownAt).TotalSeconds / ShowSeconds, 0, 1);
            var clip = g.Clip;
            g.SetClip(pill);
            using (var bar = new SolidBrush(Countdown))
                g.FillRectangle(bar, Pill.X, Pill.Bottom - 2, (float)(Pill.Width * elapsed), 2);
            g.Clip = clip;

            using var edge = new Pen(Edge, 1f);
            g.DrawPath(edge, pill);
        }

        // Icon tile: white rounded square with Huginn's waveform (Notion's tile sits at the same place and size).
        var tile = new Rectangle(Pill.X + 22, Pill.Y + 14, 32, 33);
        using (var tp = Rounded(tile, 7))
        using (var white = new SolidBrush(Color.White))
            g.FillPath(white, tp);
        int[] bars = { 8, 16, 24, 14, 20, 10 };
        using (var ink = new SolidBrush(Color.FromArgb(25, 25, 25)))
            for (int i = 0; i < bars.Length; i++)
            {
                float bx = tile.X + 6 + i * 3.8f, bh = bars[i];
                using var bar = Rounded(new RectangleF(bx, tile.Y + (tile.Height - bh) / 2f, 2.4f, bh), 1.2f);
                g.FillPath(ink, bar);
            }

        // GDI+ text, not TextRenderer: GDI writes alpha 0 into a 32-bit layered bitmap and the letters would vanish.
        float tx = Pill.X + 64;
        using (var w = new SolidBrush(Color.White)) g.DrawString("Start Meeting Recording", _title, w, tx, Pill.Y + 13);
        using (var sb = new SolidBrush(Sub))
        using (var fmt = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            g.DrawString($"{_app} detected", _sub, sb, new RectangleF(tx + 1, Pill.Y + 33, Button.X - tx - 8, 20), fmt);

        // Split button, fully rounded: "Start recording" | ⌄
        using (var bp = Rounded(Button, Button.Height / 2f))
        {
            using var blue = new SolidBrush(Blue);
            g.FillPath(blue, bp);
            if (_hover != 0)
            {
                var clip = g.Clip;
                g.SetClip(_hover == 1 ? Main : Arrow);
                using var hb = new SolidBrush(BlueHover);
                g.FillPath(hb, bp);
                g.Clip = clip;
            }
        }
        using (var div = new Pen(Color.FromArgb(46, 255, 255, 255), 1f))
            g.DrawLine(div, Arrow.X, Arrow.Y + 9, Arrow.X, Arrow.Bottom - 9);
        using (var w = new SolidBrush(Color.White))
        using (var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            g.DrawString("Start recording", _button, w, new RectangleF(Main.X + 3, Main.Y, Main.Width, Main.Height), center);
        using (var chevron = new Pen(Color.White, 1.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            float cx = Arrow.X + Arrow.Width / 2f - 1, cy = Arrow.Y + Arrow.Height / 2f;
            g.DrawLines(chevron, new[] { new PointF(cx - 4.5f, cy - 2f), new PointF(cx, cy + 2.5f), new PointF(cx + 4.5f, cy - 2f) });
        }
    }

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

    private void Push(Bitmap bmp)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
        IntPtr old = SelectObject(memDc, hBitmap);
        try
        {
            var size = new SIZE { cx = W, cy = H };
            var src = new POINT { x = 0, y = 0 };
            var dst = new POINT { x = Left, y = Top };
            var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2);
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private void Ui(Action a)
    {
        if (InvokeRequired) BeginInvoke(a); else a();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timeout.Dispose(); _tick.Dispose(); _menu.Dispose(); _title.Dispose(); _sub.Dispose(); _button.Dispose(); }
        base.Dispose(disposing);
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pprSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
}
