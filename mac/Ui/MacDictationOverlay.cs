using System.Runtime.InteropServices;
using AppKit;
using CoreGraphics;
using Foundation;
using static Huginn.MacUi;

namespace Huginn;

/// <summary>
/// The Mac port of the Windows RecordingOverlay: a small dark pill at the bottom centre of the screen with the mouse
/// (state indicator + live mic waveform while listening, "Starting" / "Transcribing" / "Cleaning" with running dots
/// otherwise), and the live preview text in a caption card above it (the last line, cut from the left so the newest
/// words show). One ~30 fps timer drives the fade + slide, the waveform and the spinner.
///
/// CRITICAL: it never takes the keyboard (a borderless, non-activating panel that refuses key) and it is
/// click-through, so the paste lands in the app the user is typing into. Fixed size: appearance is animated through
/// the window alpha, its position and the drawn content, never by resizing. Every method is safe from any thread.
/// </summary>
internal sealed class MacDictationOverlay : IDictationOverlay
{
    private enum State { Hidden, Starting, Listening, Transcribing, Cleaning }

    private const int W = 460, H = 116, Pad = 14;
    private const int PillW = 220, PillH = 30, PillRadius = 15;
    private const int PillX = (W - PillW) / 2;
    private const int PillY = H - Pad - PillH;
    private const int PillContentLeft = 28;   // pill-left → content (past the indicator)
    private const int PillContentRight = 14;
    private const int BarW = 3, BarGap = 2;
    private const int MaxTextW = 360;         // fixed caption width: the card never resizes as words change

    private static readonly NSColor OverlayBg = Rgb(24, 24, 27, 244);
    private static readonly NSColor OverlayBorder = Rgb(255, 255, 255, 70);
    private static readonly NSColor RecDot = Rgb(235, 68, 68);
    private static readonly NSColor RecGlow = Rgb(235, 68, 68, 55);
    private static readonly NSColor TextColor = Rgb(236, 236, 239);
    private static readonly NSColor Spinner = Rgb(215, 215, 219);
    private static readonly NSColor ShadowCol = Rgb(0, 0, 0, 10);
    private static readonly NSFont CaptionFont = Regular(14);
    private static readonly NSFont VerbFont = Semibold(12.5);

    private readonly NSPanel _panel;
    private readonly OverlayView _view;
    private readonly float[] _wave;
    private NSTimer? _anim;

    private State _state = State.Hidden;      // main thread only
    private volatile string _preview = "";
    private volatile float _incoming;
    private float _shown;
    private float _shownTarget;
    private int _frame;
    private CGPoint _base;                    // the panel's resting origin (AppKit screen coordinates)

    /// <summary>Construct on the main thread.</summary>
    public MacDictationOverlay()
    {
        _wave = new float[(PillW - PillContentLeft - PillContentRight) / (BarW + BarGap)];

        _panel = MakePanel(R(0, 0, W, H), canBecomeKey: false); // a borderless NSPanel refuses key by default
        _panel.IgnoresMouseEvents = true;                        // click-through
        _panel.BecomesKeyOnlyIfNeeded = true;
        _panel.HasShadow = false;                                // the view draws its own soft shadow
        _panel.AlphaValue = 0;
        _view = new OverlayView(this, R(0, 0, W, H));
        _panel.ContentView = _view;
    }

    // ---- IDictationOverlay (any thread) ----

    public void ShowStarting() => OnMain(() =>
    {
        _preview = "";
        Array.Clear(_wave);
        _incoming = 0f;
        EnterState(State.Starting);
    });

    public void ShowListening() => OnMain(() => EnterState(State.Listening));
    public void ShowTranscribing() => OnMain(() => EnterState(State.Transcribing));
    public void ShowCleaning() => OnMain(() => EnterState(State.Cleaning));

    public void HideOverlay() => OnMain(() =>
    {
        _state = State.Hidden;
        _shownTarget = 0f;
        if (_panel.IsVisible) StartAnim();
    });

    /// <summary>A live mic level (0..1). Any thread: a single float write.</summary>
    public void SetLevel(float level01)
    {
        float scaled = Math.Clamp((float)Math.Sqrt(Math.Max(0f, level01)) * 1.9f, 0f, 1f);
        if (scaled > _incoming) _incoming = scaled;
    }

    /// <summary>The live transcription so far. Any thread: a single reference write.</summary>
    public void SetPreviewText(string text) => _preview = text ?? "";

    // ---- internals (main thread) ----

    private void EnterState(State state)
    {
        _state = state;
        _shownTarget = 1f;
        PositionBase();
        if (!_panel.IsVisible)
        {
            _shown = 0f;
            _panel.AlphaValue = 0;
            double bx = _base.X, by = _base.Y;
            _panel.SetFrameOrigin(P(bx, by - 12));
            _panel.OrderFrontRegardless(); // shows without making it key or activating the app
        }
        StartAnim();
    }

    // Bottom centre of the screen the mouse is on, else the main screen.
    private void PositionBase()
    {
        var mouse = NSEvent.CurrentMouseLocation;
        NSScreen? screen = null;
        foreach (var s in NSScreen.Screens)
            if (s.Frame.Contains(mouse)) { screen = s; break; }
        screen ??= MainScreen();
        if (screen == null) return;
        var vf = screen.VisibleFrame;
        double x = vf.X, y = vf.Y, w = vf.Width;
        _base = P(Math.Round(x + (w - W) / 2), Math.Round(y + 60));
    }

    private void StartAnim() => _anim ??= Repeat(TimeSpan.FromMilliseconds(33), OnTick);

    private void StopAnim()
    {
        _anim?.Invalidate();
        _anim = null;
    }

    private void OnTick()
    {
        _frame++;

        _shown += (_shownTarget - _shown) * 0.28f;
        if (_shownTarget == 0f && _shown < 0.03f)
        {
            _shown = 0f;
            StopAnim();
            _panel.AlphaValue = 0;
            if (_panel.IsVisible) _panel.OrderOut(null);
            return;
        }

        // Slides up into place as it fades in (AppKit y runs up, so "below" is a smaller y).
        double slide = Math.Round((1f - _shown) * 12f);
        double bx = _base.X, by = _base.Y;
        _panel.SetFrameOrigin(P(bx, by - slide));

        Array.Copy(_wave, 1, _wave, 0, _wave.Length - 1);
        _wave[^1] = _state == State.Listening ? _incoming : 0f;
        _incoming *= 0.6f;

        _panel.AlphaValue = (NFloat)(Math.Clamp(_shown, 0f, 1f) * 247f / 255f);
        _view.NeedsDisplay = true;
    }

    // ---- painting (flipped view: the Windows coordinates) ----

    internal void Draw()
    {
        var pill = R(PillX, PillY, PillW, PillH);
        DrawShadow(PillX, PillY, PillW, PillH, PillRadius);
        FillRounded(pill, PillRadius, OverlayBg);
        StrokeRounded(R(PillX + 0.5, PillY + 0.5, PillW - 1, PillH - 1), PillRadius, OverlayBorder);

        double cy = PillY + PillH / 2.0;
        DrawIndicator(PillX + 15, cy);

        const int cx0 = PillX + PillContentLeft;
        switch (_state)
        {
            case State.Starting:
                DrawProcessing(cx0, cy, "Starting");
                break;
            case State.Listening:
                DrawWaveform(cx0, cy);
                break;
            case State.Transcribing:
                DrawProcessing(cx0, cy, "Transcribing");
                break;
            case State.Cleaning:
                DrawProcessing(cx0, cy, "Cleaning");
                break;
        }

        // The caption stays through processing too, so the live text does not vanish the moment the key is
        // released: it stays until the result is pasted and the overlay fades.
        if (_state != State.Starting) DrawCaption();
    }

    private void DrawCaption()
    {
        var line = LastLine(_preview);
        if (line.Length == 0) return;

        const int px = 13, py = 8;
        double lineH = Math.Ceiling((double)Measure("Ag", CaptionFont).Height);
        double cardW = MaxTextW + 2 * px;
        double cardH = lineH + 2 * py;
        double cardX = (W - cardW) / 2;
        double cardY = PillY - 8 - cardH;

        DrawShadow(cardX, cardY, cardW, cardH, 12);
        FillRounded(R(cardX, cardY, cardW, cardH), 12, OverlayBg);
        StrokeRounded(R(cardX + 0.5, cardY + 0.5, cardW - 1, cardH - 1), 12, OverlayBorder);
        // Cut from the LEFT: the newest words are the ones that matter while dictating.
        DrawText(line, CaptionFont, TextColor, R(cardX + px, cardY + py, MaxTextW, lineH), NSLineBreakMode.TruncatingHead);
    }

    private static string LastLine(string text)
    {
        var t = text.TrimEnd();
        int nl = t.LastIndexOfAny(new[] { '\n', '\r' });
        return (nl >= 0 ? t[(nl + 1)..] : t).Trim();
    }

    private void DrawIndicator(double cx, double cy)
    {
        switch (_state)
        {
            case State.Starting:
            {
                double pulse = 0.45 + 0.55 * Math.Abs(Math.Sin(_frame * 0.16));
                const double r = 4;
                FillOval(R(cx - r, cy - r, 2 * r, 2 * r), Rgb(200, 200, 205, (int)(120 + 120 * pulse)));
                break;
            }
            case State.Listening:
            {
                FillOval(R(cx - 8, cy - 8, 16, 16), RecGlow);
                const double r = 4.5;
                FillOval(R(cx - r, cy - r, 2 * r, 2 * r), RecDot);
                break;
            }
            case State.Transcribing:
            case State.Cleaning:
                Arc(cx, cy, 6, (_frame * 11.0) % 360.0, 270, Spinner, 2);
                break;
        }
    }

    private void DrawProcessing(double x, double cy, string verb)
    {
        var size = Measure(verb, VerbFont);
        double tw = size.Width, th = size.Height;
        DrawText(verb, VerbFont, TextColor, x, cy - th / 2);

        double x0 = x + tw + 9;
        int active = (_frame / 4) % 5;
        for (int i = 0; i < 5; i++)
        {
            int a = i == active ? 235 : 70;
            const double r = 2;
            double dx = x0 + i * 8;
            FillOval(R(dx - r, cy - r, 2 * r, 2 * r), Rgb(210, 210, 214, a));
        }
    }

    private void DrawWaveform(double x, double cy)
    {
        const int waveH = 16;
        for (int i = 0; i < _wave.Length; i++)
        {
            float lvl = _wave[i];
            double h = Math.Max(2f, lvl * waveH);
            int a = 55 + (int)(190 * Math.Min(1f, lvl));
            FillRect(R(x + i * (BarW + BarGap), cy - h / 2, BarW, h), Rgb(236, 236, 239, a));
        }
    }

    // A soft drop shadow: five widening rounded fills of faint black, nudged down 2 px.
    private static void DrawShadow(double x, double y, double w, double h, double radius)
    {
        for (int i = 5; i >= 1; i--)
            FillRounded(R(x - i, y - i + 2, w + 2 * i, h + 2 * i), radius + i, ShadowCol);
    }
}

/// <summary>The overlay's content view: transparent outside what <see cref="MacDictationOverlay"/> draws.</summary>
[Register("HuginnOverlayView")]
internal sealed class OverlayView : NSView
{
    private readonly MacDictationOverlay _owner;

    public OverlayView(MacDictationOverlay owner, CGRect frame) : base(frame) => _owner = owner;

    public override bool IsFlipped => true;

    public override void DrawRect(CGRect dirtyRect) => _owner.Draw();
}
