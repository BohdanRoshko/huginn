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
/// other choices). It never takes focus from the call. It goes away on a choice, when the call ends, or after
/// <see cref="ShowSeconds"/> on its own.
/// </summary>
internal sealed class CallPrompt : Form, ICallPrompt
{
    private const int ShowSeconds = 45;
    private const int Pad = 14;                 // room for the shadow around the pill
    private const int PillW = 460, PillH = 60;
    private const int W = PillW + 2 * Pad, H = PillH + 2 * Pad;
    private static readonly Rectangle Pill = new(Pad, Pad, PillW, PillH);
    private static readonly Rectangle Button = new(Pad + PillW - 16 - 178, Pad + 13, 178, 34);
    private static readonly Rectangle Arrow = new(Button.Right - 34, Button.Y, 34, Button.Height);
    private static readonly Rectangle Main = new(Button.X, Button.Y, Button.Width - Arrow.Width, Button.Height);

    private static readonly Color Bg = Color.FromArgb(37, 37, 37);
    private static readonly Color Edge = Color.FromArgb(60, 255, 255, 255);
    private static readonly Color Blue = Color.FromArgb(35, 131, 226);
    private static readonly Color BlueHover = Color.FromArgb(52, 146, 238);
    private static readonly Color Sub = Color.FromArgb(165, 165, 165);

    private readonly Font _title = new("Segoe UI Semibold", 10f);
    private readonly Font _sub = new("Segoe UI", 8.75f);
    private readonly Font _button = new("Segoe UI Semibold", 10f);
    private readonly System.Windows.Forms.Timer _timeout = new() { Interval = ShowSeconds * 1000 };
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
        // Top centre of the screen the user is looking at (the one with the foreground window), like Notion's.
        var screen = Screen.FromHandle(GetForegroundWindow());
        Location = new Point(screen.Bounds.Left + (screen.Bounds.Width - W) / 2, screen.Bounds.Top + 16 - Pad);
        if (!Visible) Show();
        Render();
        _timeout.Stop();
        _timeout.Start();
    });

    public void HidePrompt() => Ui(() =>
    {
        _timeout.Stop();
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
            using var edge = new Pen(Edge, 1f);
            g.DrawPath(edge, pill);
        }

        // Icon tile: white rounded square with Huginn's waveform.
        var tile = new Rectangle(Pill.X + 20, Pill.Y + 13, 34, 34);
        using (var tp = Rounded(tile, 8))
        using (var white = new SolidBrush(Color.White))
            g.FillPath(white, tp);
        int[] bars = { 8, 16, 24, 14, 20, 10 };
        using (var ink = new SolidBrush(Color.FromArgb(25, 25, 25)))
            for (int i = 0; i < bars.Length; i++)
            {
                float bx = tile.X + 7 + i * 3.8f, bh = bars[i];
                using var bar = Rounded(new RectangleF(bx, tile.Y + (tile.Height - bh) / 2f, 2.4f, bh), 1.2f);
                g.FillPath(ink, bar);
            }

        int tx = tile.Right + 12;
        // GDI+ text, not TextRenderer: GDI writes alpha 0 into a 32-bit layered bitmap and the letters would vanish.
        using (var w = new SolidBrush(Color.White)) g.DrawString("Start Meeting Recording", _title, w, tx - 2, Pill.Y + 10);
        using (var sb = new SolidBrush(Sub))
        using (var fmt = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            g.DrawString($"{_app} detected", _sub, sb, new RectangleF(tx - 1, Pill.Y + 31, Button.X - tx - 8, 18), fmt);

        // Split button: "Start recording" | ⌄
        using (var bp = Rounded(Button, 8))
        using (var blue = new SolidBrush(Blue))
            g.FillPath(blue, bp);
        if (_hover != 0)
        {
            var part = _hover == 1 ? Main : Arrow;
            using var hp = Rounded(Button, 8);
            var clip = g.Clip;
            g.SetClip(part);
            using var hb = new SolidBrush(BlueHover);
            g.FillPath(hb, hp);
            g.Clip = clip;
        }
        using (var div = new Pen(Color.FromArgb(90, 255, 255, 255), 1f))
            g.DrawLine(div, Arrow.X, Arrow.Y + 7, Arrow.X, Arrow.Bottom - 7);
        using (var w = new SolidBrush(Color.White))
        using (var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            g.DrawString("Start recording", _button, w, new RectangleF(Main.X + 2, Main.Y, Main.Width, Main.Height), center);
        using (var chevron = new Pen(Color.White, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            float cx = Arrow.X + Arrow.Width / 2f, cy = Arrow.Y + Arrow.Height / 2f;
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
        if (disposing) { _timeout.Dispose(); _menu.Dispose(); _title.Dispose(); _sub.Dispose(); _button.Dispose(); }
        base.Dispose(disposing);
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
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
