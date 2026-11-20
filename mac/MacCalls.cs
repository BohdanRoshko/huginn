using System.Runtime.InteropServices;
using AppKit;
using CoreGraphics;
using Foundation;
using ObjCRuntime;

namespace Huginn;

/// <summary>
/// Which apps hold the microphone on the Mac: Core Audio's process list (macOS 14.2+), each process's bundle id and
/// whether it is running input. Browsers also report their window titles (needs Screen Recording, which Huginn already
/// has for the system audio), so a Meet tab can be named. On older macOS this returns nobody and calls are not followed.
/// </summary>
internal sealed class MacMicUsers : IMicUsers
{
    private const string CoreAudioLib = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
    private const string CoreGraphicsLib = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [StructLayout(LayoutKind.Sequential)]
    private struct Address { public uint Selector, Scope, Element; }

    [DllImport(CoreAudioLib)] private static extern int AudioObjectGetPropertyDataSize(uint obj, ref Address a, uint qualSize, IntPtr qual, out uint size);
    [DllImport(CoreAudioLib)] private static extern int AudioObjectGetPropertyData(uint obj, ref Address a, uint qualSize, IntPtr qual, ref uint size, IntPtr data);
    [DllImport(CoreGraphicsLib)] private static extern IntPtr CGWindowListCopyWindowInfo(uint option, uint relativeToWindow);
    [DllImport(CoreFoundationLib)] private static extern void CFRelease(IntPtr cf);

    private const uint SystemObject = 1;
    private const uint ScopeGlobal = 0x676c6f62;     // 'glob'
    private const uint ProcessObjectList = 0x70727323; // 'prs#'  kAudioHardwarePropertyProcessObjectList (14.2+)
    private const uint ProcessBundleId = 0x70626964;   // 'pbid'  kAudioProcessPropertyBundleID
    private const uint ProcessPid = 0x70706964;        // 'ppid'  kAudioProcessPropertyPID
    private const uint ProcessRunningInput = 0x70697269; // 'piri' kAudioProcessPropertyIsRunningInput
    private const uint OnScreenOnly = 1;               // kCGWindowListOptionOnScreenOnly

    private static readonly string[] BrowserIds =
        { "com.google.chrome", "com.microsoft.edgemac", "org.mozilla.firefox", "com.brave.browser", "com.apple.safari", "company.thebrowser.browser" };

    private bool _warned;

    public IReadOnlyList<MicUser> Current()
    {
        var users = new List<MicUser>();
        foreach (var process in Processes())
        {
            if (Read<uint>(process, ProcessRunningInput) != 1) continue;
            var id = BundleId(process);
            if (string.IsNullOrEmpty(id)) continue;
            var titles = BrowserIds.Any(b => id.StartsWith(b, StringComparison.OrdinalIgnoreCase))
                ? WindowTitles(Read<int>(process, ProcessPid)) : (IReadOnlyList<string>)Array.Empty<string>();
            users.Add(new MicUser(id.ToLowerInvariant(), titles));
        }
        return users;
    }

    private uint[] Processes()
    {
        var a = new Address { Selector = ProcessObjectList, Scope = ScopeGlobal, Element = 0 };
        if (AudioObjectGetPropertyDataSize(SystemObject, ref a, 0, IntPtr.Zero, out var size) != 0 || size == 0)
        {
            if (!_warned) { _warned = true; HuginnLog.Write("calls: Core Audio has no process list here (macOS 14.2+ needed); meeting apps are not followed"); }
            return Array.Empty<uint>();
        }
        var ids = new uint[size / 4];
        var h = GCHandle.Alloc(ids, GCHandleType.Pinned);
        try { return AudioObjectGetPropertyData(SystemObject, ref a, 0, IntPtr.Zero, ref size, h.AddrOfPinnedObject()) == 0 ? ids[..(int)(size / 4)] : Array.Empty<uint>(); }
        finally { h.Free(); }
    }

    private static T Read<T>(uint obj, uint selector) where T : unmanaged
    {
        var a = new Address { Selector = selector, Scope = ScopeGlobal, Element = 0 };
        uint size = (uint)Marshal.SizeOf<T>();
        var buf = Marshal.AllocHGlobal((int)size);
        try { return AudioObjectGetPropertyData(obj, ref a, 0, IntPtr.Zero, ref size, buf) == 0 ? Marshal.PtrToStructure<T>(buf) : default; }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static string? BundleId(uint obj)
    {
        var a = new Address { Selector = ProcessBundleId, Scope = ScopeGlobal, Element = 0 };
        uint size = (uint)IntPtr.Size;
        var buf = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            if (AudioObjectGetPropertyData(obj, ref a, 0, IntPtr.Zero, ref size, buf) != 0) return null;
            var cf = Marshal.ReadIntPtr(buf);
            if (cf == IntPtr.Zero) return null;
            var s = Runtime.GetNSObject<NSString>(cf)?.ToString();
            CFRelease(cf);
            return s;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // The on-screen window titles of one process (kCGWindowName is filled only with Screen Recording permission).
    private static IReadOnlyList<string> WindowTitles(int pid)
    {
        var titles = new List<string>();
        var list = CGWindowListCopyWindowInfo(OnScreenOnly, 0);
        if (list == IntPtr.Zero) return titles;
        try
        {
            var windows = Runtime.GetNSObject<NSArray>(list);
            if (windows == null) return titles;
            for (nuint i = 0; i < windows.Count; i++)
            {
                var w = windows.GetItem<NSDictionary>(i);
                if (w?["kCGWindowOwnerPID"] is NSNumber owner && owner.Int32Value == pid && w["kCGWindowName"] is NSString name && name.Length > 0)
                    titles.Add(name.ToString());
            }
        }
        finally { CFRelease(list); }
        return titles;
    }
}

/// <summary>The "Start meeting recording" pop-up on the Mac, laid out like Notion's: a dark pill at the top centre of the
/// screen, under the menu bar, with an icon tile, a title, a subtitle, a grey "Not now" button and a blue split button.
/// Never takes focus. Under the mouse it grows as Notion's does (the Windows pop-up's measures, 2026-11-20): a lighter
/// strip under the pill with a note running across it, and a round close button on the top-left corner. The countdown
/// waits while the mouse is on it.</summary>
internal sealed class MacCallPrompt : ICallPrompt
{
    // The layout in design units (Notion's sizes); the window shows it 15% smaller (Bohdan, 2026-11-18).
    internal const double Pad = 14;                      // room around the pill for the close button
    internal const double PillW = 556, PillH = 61;       // Notion's 460 plus room for "Not now"
    internal const double ExtraH = 27;                    // the strip the pill grows under the mouse
    internal const double W = PillW + 2 * Pad, H = PillH + ExtraH + 2 * Pad;
    private const double Scale = 0.85;
    internal const int ShowSeconds = 15;                  // half of Notion's 30 (Bohdan, 2026-11-19)
    private const double ExpandSeconds = 0.18;
    internal const double RunPerSecond = 34 / Scale, RunPauseSeconds = 0.7, RunGap = 21;
    private readonly NSPanel _panel;
    private readonly PromptView _view;
    private readonly NSMenu _menu;
    private NSTimer? _tick;
    private readonly System.Diagnostics.Stopwatch _left = new(), _lastFrame = new();
    private bool _visible, _expired, _held;

    public event Action? StartRequested;
    public event Action<string>? OptionChosen;

    public MacCallPrompt()
    {
        _panel = new NSPanel(new CGRect(0, 0, W * Scale, H * Scale), NSWindowStyle.Borderless | NSWindowStyle.NonactivatingPanel, NSBackingStore.Buffered, false)
        {
            Level = NSWindowLevel.Floating,
            BackgroundColor = NSColor.Clear,
            HasShadow = true,
            HidesOnDeactivate = false,
            AcceptsMouseMovedEvents = true,
            CollectionBehavior = NSWindowCollectionBehavior.CanJoinAllSpaces | NSWindowCollectionBehavior.FullScreenAuxiliary,
        };
        _panel.IsOpaque = false;
        _menu = new NSMenu { AutoEnablesItems = false };
        _menu.AddItem(new NSMenuItem("Always Record Meetings Automatically", (_, _) => OptionChosen?.Invoke("auto")));
        _menu.AddItem(new NSMenuItem("Don't Ask Again", (_, _) => OptionChosen?.Invoke("off")));
        _view = new PromptView(new CGRect(0, 0, W * Scale, H * Scale))
        {
            OnStart = () => StartRequested?.Invoke(),
            OnNotNow = () => OptionChosen?.Invoke("not now"),
            OnArrow = point => _menu.PopUpMenu(null, point, _view),
            OnOver = SetOver,
            Left = _left,
        };
        // Bounds in design units, frame 15% smaller: AppKit scales the drawing and the mouse points.
        _view.SetBoundsSize(new CGSize(W, H));
        _panel.ContentView = _view;
    }

    public void ShowPrompt(string app) => MacUi.OnMain(() =>
    {
        _view.App = app;
        _view.Over = false;
        _view.Expand = 0;
        _view.Hover = 0;
        _view.NeedsDisplay = true;
        // The main screen (the one with the menu bar), always: NSScreen.MainScreen is the screen with the active window.
        // The pill's top sits 10 points under the menu bar; the window reaches Pad further out for the close button.
        var screen = NSScreen.Screens[0];
        var f = screen.VisibleFrame;
        _panel.SetFrameOrigin(new CGPoint(f.X + (f.Width - W * Scale) / 2, f.Y + f.Height - 10 + Pad * Scale - H * Scale));
        _panel.OrderFrontRegardless();
        _panel.InvalidateShadow();
        _visible = true;
        _expired = false;
        _held = false;
        _left.Restart();
        _lastFrame.Restart();
        _tick?.Invalidate();
        _tick = NSTimer.CreateRepeatingScheduledTimer(TimeSpan.FromSeconds(1.0 / 60), _ => Frame()); // once per frame
        NSRunLoop.Main.AddTimer(_tick, NSRunLoopMode.Common);
    });

    public void HidePrompt() => MacUi.OnMain(() =>
    {
        _visible = false;
        _view.Over = false; // a mouse leaving a closed pop-up must not start its countdown again
        _tick?.Invalidate();
        _tick = null;
        _menu.CancelTracking();
        _panel.OrderOut(null);
    });

    /// <summary>For the CI photo: the pop-up grown, as under the mouse, and kept that way.</summary>
    internal void HoldOpen() => MacUi.OnMain(() => { SetOver(true); _held = true; });

    // The countdown waits while the mouse is on the pill, so the note can be read; it goes on where it stopped.
    private void SetOver(bool over)
    {
        if (_held || over == _view.Over || !_visible) return;
        _view.Over = over;
        if (over) { _left.Stop(); _view.ExpandClock.Restart(); }
        else _left.Start();
    }

    // The grow and shrink: a constant rate toward the target, eased when drawn. The shadow follows the new shape.
    private void Frame()
    {
        var dt = Math.Min(0.1, _lastFrame.Elapsed.TotalSeconds);
        _lastFrame.Restart();
        var before = _view.Expand;
        _view.Expand = Math.Clamp(before + (_view.Over ? dt : -dt) / ExpandSeconds, 0, 1);
        _view.NeedsDisplay = true;
        if (_view.Expand != before) _panel.InvalidateShadow();
        if (!_expired && _left.Elapsed.TotalSeconds >= ShowSeconds)
        {
            _expired = true;
            OptionChosen?.Invoke("not now");
        }
    }

    private sealed class PromptView : NSView
    {
        public string App = "";
        public bool Over;
        public double Expand;           // 0 = a pill, 1 = grown by ExtraH
        public int Hover;               // 0 none, 1 main, 2 arrow, 3 not now, 4 close
        public System.Diagnostics.Stopwatch Left = new();
        public readonly System.Diagnostics.Stopwatch ExpandClock = new();
        public Action? OnStart;
        public Action? OnNotNow;
        public Action<CGPoint>? OnArrow;
        public Action<bool>? OnOver;
        // Measured from Notion's pop-up (2026-11-18): button 176 x 36, fully rounded, 15 px from the right.
        private static readonly CGRect Pill = new(Pad, Pad, PillW, PillH);
        private static readonly CGRect Button = new(Pad + PillW - 15 - 176, Pad + 12, 176, 36);
        private static readonly CGRect Arrow = new(Button.Right - 31, Button.Y, 31, Button.Height);
        private static readonly CGRect Main = new(Button.X, Button.Y, Button.Width - Arrow.Width, Button.Height);
        private static readonly CGRect NotNow = new(Button.X - 8 - 88, Button.Y, 88, Button.Height);
        private static readonly CGRect Close = new(Pad + 7 - 12, Pad + 9 - 12, 24, 24); // Notion's close button, centred on the corner

        public PromptView(CGRect frame) : base(frame)
        {
            AddTrackingArea(new NSTrackingArea(frame,
                NSTrackingAreaOptions.MouseEnteredAndExited | NSTrackingAreaOptions.MouseMoved |
                NSTrackingAreaOptions.ActiveAlways | NSTrackingAreaOptions.InVisibleRect,
                this, null));
        }

        public override bool IsFlipped => true;
        public override bool AcceptsFirstMouse(NSEvent? theEvent) => true;

        private static NSColor C(int r, int g, int b, double a = 1) => NSColor.FromSrgb(r / 255f, g / 255f, b / 255f, (nfloat)a);

        private string Note => $"Huginn records your microphone and the {App} audio on this computer. By starting, you confirm everyone in the call knows it is recorded.";

        public override void DrawRect(CGRect dirtyRect)
        {
            var grow = Expand * Expand * (3 - 2 * Expand); // smoothstep
            var body = new CGRect(Pill.X, Pill.Y, Pill.Width, Pill.Height + ExtraH * grow);
            var radius = (nfloat)(PillH / 2); // Notion's keeps its round ends when it grows
            var pill = NSBezierPath.FromRoundedRect(body, radius, radius);
            C(32, 32, 32).SetFill();
            pill.Fill();
            NSGraphicsContext.CurrentContext?.SaveGraphicsState();
            pill.AddClip();
            if (grow > 0.01) DrawNote(body, grow);
            // The countdown: a thin warm-grey bar along the bottom edge, filling left to right as the time runs out.
            var elapsed = Math.Clamp(Left.Elapsed.TotalSeconds / ShowSeconds, 0, 1);
            C(125, 122, 117).SetFill();
            NSBezierPath.FillRect(new CGRect(body.X, body.Bottom - 2, body.Width * elapsed, 2));
            NSGraphicsContext.CurrentContext?.RestoreGraphicsState();
            var edge = NSBezierPath.FromRoundedRect(body.Inset((nfloat)0.5, (nfloat)0.5), radius - (nfloat)0.5, radius - (nfloat)0.5);
            C(255, 255, 255, 0.16).SetStroke();
            edge.LineWidth = 1;
            edge.Stroke();

            // Icon tile with Huginn's waveform.
            var tile = new CGRect(Pill.X + 22, Pill.Y + 14, 32, 33);
            NSColor.White.SetFill();
            NSBezierPath.FromRoundedRect(tile, 7, 7).Fill();
            C(25, 25, 25).SetFill();
            double[] bars = { 8, 16, 24, 14, 20, 10 };
            for (int i = 0; i < bars.Length; i++)
                NSBezierPath.FromRoundedRect(new CGRect(tile.X + 6 + i * 3.8, tile.Y + (tile.Height - bars[i]) / 2, 2.4, bars[i]), (nfloat)1.2, (nfloat)1.2).Fill();

            var tx = Pill.X + 64.0;
            new NSAttributedString("Start Meeting Recording", new NSStringAttributes
            {
                Font = NSFont.SystemFontOfSize(14, NSFontWeight.Bold), ForegroundColor = NSColor.White,
            }).DrawAtPoint(new CGPoint(tx, Pill.Y + 12));
            new NSAttributedString($"{App} detected", new NSStringAttributes
            {
                Font = NSFont.SystemFontOfSize((nfloat)12.5), ForegroundColor = C(155, 154, 152),
            }).DrawAtPoint(new CGPoint(tx + 1, Pill.Y + 32));

            // "Not now": the same shape as the blue button, grey and quiet, so Start recording stays the focus.
            C(255, 255, 255, Hover == 3 ? 0.14 : 0.08).SetFill();
            NSBezierPath.FromRoundedRect(NotNow, NotNow.Height / 2, NotNow.Height / 2).Fill();
            var quiet = new NSAttributedString("Not now", new NSStringAttributes
            {
                Font = NSFont.SystemFontOfSize(13.5f, NSFontWeight.Semibold), ForegroundColor = C(226, 224, 220),
            });
            var qs = quiet.Size;
            quiet.DrawAtPoint(new CGPoint(NotNow.X + (NotNow.Width - qs.Width) / 2, NotNow.Y + (NotNow.Height - qs.Height) / 2));

            var blue = NSBezierPath.FromRoundedRect(Button, Button.Height / 2, Button.Height / 2);
            C(39, 131, 222).SetFill();
            blue.Fill();
            if (Hover is 1 or 2)
            {
                NSGraphicsContext.CurrentContext?.SaveGraphicsState();
                NSBezierPath.ClipRect(Hover == 1 ? Main : Arrow);
                C(58, 145, 230).SetFill();
                blue.Fill();
                NSGraphicsContext.CurrentContext?.RestoreGraphicsState();
            }
            C(255, 255, 255, 0.18).SetStroke();
            var div = new NSBezierPath();
            div.MoveTo(new CGPoint(Arrow.X, Arrow.Y + 9));
            div.LineTo(new CGPoint(Arrow.X, Arrow.Bottom - 9));
            div.Stroke();
            var label = new NSAttributedString("Start recording", new NSStringAttributes
            {
                Font = NSFont.SystemFontOfSize(13.5f, NSFontWeight.Bold), ForegroundColor = NSColor.White,
            });
            var size = label.Size;
            label.DrawAtPoint(new CGPoint(Main.X + (Main.Width - size.Width) / 2 + 2, Main.Y + (Main.Height - size.Height) / 2));
            NSColor.White.SetStroke();
            var chevron = new NSBezierPath { LineWidth = (nfloat)1.4, LineCapStyle = NSLineCapStyle.Round, LineJoinStyle = NSLineJoinStyle.Round };
            var cx = Arrow.X + Arrow.Width / 2; var cy = Arrow.Y + Arrow.Height / 2;
            chevron.MoveTo(new CGPoint(cx - 4.5, cy - 2));
            chevron.LineTo(new CGPoint(cx, cy + 2.5));
            chevron.LineTo(new CGPoint(cx + 4.5, cy - 2));
            chevron.Stroke();
            if (grow > 0.01) DrawClose(grow);
        }

        // The grown strip: lighter grey from the pill's bottom edge down, with the note running right to left across its
        // whole width when it is wider than the strip. The pill's round ends clip it (the caller set that clip).
        private void DrawNote(CGRect body, double grow)
        {
            var strip = new CGRect(body.X, Pill.Bottom, body.Width, body.Bottom - Pill.Bottom);
            if (strip.Height <= 0) return;
            NSGraphicsContext.CurrentContext?.SaveGraphicsState();
            NSBezierPath.ClipRect(strip);
            C(56, 56, 54).SetFill();
            NSBezierPath.FillRect(strip);
            var note = new NSAttributedString(Note, new NSStringAttributes
            {
                Font = NSFont.SystemFontOfSize(13), ForegroundColor = C(172, 171, 167, grow),
            });
            var size = note.Size;
            double start = body.X + 22, x = start, width = size.Width;
            bool runs = start + width > body.Right - 22;
            if (runs)
            {
                var run = Math.Max(0, ExpandClock.Elapsed.TotalSeconds - RunPauseSeconds) * RunPerSecond;
                x = start - run % (width + RunGap);
            }
            // The text sits in the middle of the full strip, so it slides up into place as the strip opens.
            double y = Math.Round(Pill.Bottom + (ExtraH - size.Height) / 2);
            note.DrawAtPoint(new CGPoint(x, y));
            if (runs) note.DrawAtPoint(new CGPoint(x + width + RunGap, y));
            NSGraphicsContext.CurrentContext?.RestoreGraphicsState();
        }

        // Notion's round close button, on the pill's top-left corner while the mouse is on it.
        private void DrawClose(double grow)
        {
            var circle = NSBezierPath.FromOvalInRect(Close.Inset((nfloat)0.5, (nfloat)0.5));
            (Hover == 4 ? C(44, 44, 44, grow) : C(32, 32, 32, grow)).SetFill();
            circle.Fill();
            C(56, 56, 56, grow).SetStroke();
            circle.LineWidth = 1;
            circle.Stroke();
            double cx = Close.X + Close.Width / 2, cy = Close.Y + Close.Height / 2, h = 4.5 * 0.85;
            var mark = new NSBezierPath { LineWidth = (nfloat)(1.5 * 0.85), LineCapStyle = NSLineCapStyle.Round };
            mark.MoveTo(new CGPoint(cx - h, cy - h)); mark.LineTo(new CGPoint(cx + h, cy + h));
            mark.MoveTo(new CGPoint(cx + h, cy - h)); mark.LineTo(new CGPoint(cx - h, cy + h));
            C(172, 172, 172, grow).SetStroke();
            mark.Stroke();
        }

        private CGPoint Local(NSEvent e) => ConvertPointFromView(e.LocationInWindow, null);

        private void Track(CGPoint p)
        {
            bool onClose = Expand > 0.5 && Close.Contains(p);
            var grown = new CGRect(Pill.X, Pill.Y, Pill.Width, Pill.Height + ExtraH * Expand);
            OnOver?.Invoke(onClose || grown.Contains(p));
            int h = onClose ? 4 : Main.Contains(p) ? 1 : Arrow.Contains(p) ? 2 : NotNow.Contains(p) ? 3 : 0;
            (h == 0 ? NSCursor.ArrowCursor : NSCursor.PointingHandCursor).Set();
            Hover = h;
        }

        public override void MouseMoved(NSEvent theEvent) => Track(Local(theEvent));
        public override void MouseEntered(NSEvent theEvent) => Track(Local(theEvent));

        public override void MouseExited(NSEvent theEvent)
        {
            Hover = 0;
            NSCursor.ArrowCursor.Set();
            OnOver?.Invoke(false);
        }

        public override void MouseUp(NSEvent theEvent)
        {
            var p = Local(theEvent);
            if (Expand > 0.5 && Close.Contains(p)) OnNotNow?.Invoke();
            else if (Main.Contains(p)) OnStart?.Invoke();
            else if (NotNow.Contains(p)) OnNotNow?.Invoke();
            else if (Arrow.Contains(p)) OnArrow?.Invoke(new CGPoint(Arrow.X, Arrow.Bottom + 6));
        }
    }
}
