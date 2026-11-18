using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Huginn;

/// <summary>
/// A small, always-visible "● REC 00:03:21" pill pinned to the top-center of the screen while a meeting
/// is being recorded — the clear on-screen indicator that recording is happening (part of addressing
/// meeting-recording consent). Like <see cref="RecordingOverlay"/> it NEVER takes keyboard focus, so it
/// can't interfere with the call window. Flips to "Transcribing…" after Stop.
/// </summary>
internal sealed class MeetingOverlay : Form, IMeetingIndicator
{
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 500 };
    private readonly Font _font = new("Segoe UI Semibold", 10.5f);
    private DateTime _started;
    private bool _transcribing;
    private float _pulse;

    public MeetingOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(20, 20, 22);
        Opacity = 0.95;
        Size = new Size(168, 40);
        using (var path = RoundedRect(new Rectangle(0, 0, Width, Height), 14))
            Region = new Region(path);

        _tick.Tick += (_, _) => { _pulse = _pulse > 0.5f ? 0f : 1f; Invalidate(); };
        _ = Handle;
        Visible = false;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOPMOST = 0x00000008;
            const int WS_EX_TOOLWINDOW = 0x00000080;
            const int WS_EX_NOACTIVATE = 0x08000000;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    private void PositionTopCenter()
    {
        var wa = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + 12);
    }

    public void ShowRecording(DateTime startedLocal)
    {
        _started = startedLocal;
        _transcribing = false;
        _pulse = 1f;
        PositionTopCenter();
        if (!Visible) Show();
        _tick.Start();
        Invalidate();
    }

    public void ShowTranscribing()
    {
        _transcribing = true;
        _pulse = 1f;
        Invalidate();
    }

    void IMeetingIndicator.HideIndicator() => HideOverlay();

    public void HideOverlay()
    {
        _tick.Stop();
        if (Visible) Hide();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        // pulsing red dot (steady while transcribing)
        int alpha = _transcribing ? 235 : 120 + (int)(135 * _pulse);
        using (var dot = new SolidBrush(Color.FromArgb(alpha, 235, 60, 60)))
            g.FillEllipse(dot, 16, Height / 2 - 6, 12, 12);

        string label = _transcribing
            ? "Transcribing…"
            : $"REC  {Elapsed():hh\\:mm\\:ss}";
        var rect = new Rectangle(38, 0, Width - 46, Height);
        TextRenderer.DrawText(g, label, _font, rect, Color.FromArgb(238, 238, 238),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    private TimeSpan Elapsed() => DateTime.Now - _started;

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = radius * 2;
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
        if (disposing) { _tick.Dispose(); _font.Dispose(); }
        base.Dispose(disposing);
    }
}
