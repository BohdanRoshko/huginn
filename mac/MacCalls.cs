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
/// screen, under the menu bar, with an icon tile, a title, a subtitle and a blue split button. Never takes focus.</summary>
internal sealed class MacCallPrompt : ICallPrompt
{
    private const double W = 460, H = 61;
    private const int ShowSeconds = 30;
    private readonly NSPanel _panel;
    private readonly PromptView _view;
    private readonly NSMenu _menu;
    private NSTimer? _timeout;
    private NSTimer? _tick;

    public event Action? StartRequested;
    public event Action<string>? OptionChosen;

    public MacCallPrompt()
    {
        _panel = new NSPanel(new CGRect(0, 0, W, H), NSWindowStyle.Borderless | NSWindowStyle.NonactivatingPanel, NSBackingStore.Buffered, false)
        {
            Level = NSWindowLevel.Floating,
            BackgroundColor = NSColor.Clear,
            HasShadow = true,
            HidesOnDeactivate = false,
            CollectionBehavior = NSWindowCollectionBehavior.CanJoinAllSpaces | NSWindowCollectionBehavior.FullScreenAuxiliary,
        };
        _panel.IsOpaque = false;
        _menu = new NSMenu { AutoEnablesItems = false };
        _menu.AddItem(new NSMenuItem("Not now", (_, _) => OptionChosen?.Invoke("not now")));
        _menu.AddItem(new NSMenuItem("Always Record Meetings Automatically", (_, _) => OptionChosen?.Invoke("auto")));
        _menu.AddItem(new NSMenuItem("Don't Ask Again", (_, _) => OptionChosen?.Invoke("off")));
        _view = new PromptView(new CGRect(0, 0, W, H))
        {
            OnStart = () => StartRequested?.Invoke(),
            OnArrow = point => _menu.PopUpMenu(null, point, _view),
        };
        _panel.ContentView = _view;
    }

    public void ShowPrompt(string app) => MacUi.OnMain(() =>
    {
        _view.App = app;
        _view.NeedsDisplay = true;
        var screen = NSScreen.MainScreen ?? NSScreen.Screens[0];
        var f = screen.VisibleFrame;
        _panel.SetFrameOrigin(new CGPoint(f.X + (f.Width - W) / 2, f.Y + f.Height - H - 10));
        _panel.OrderFrontRegardless();
        _view.ShownAt = DateTime.Now;
        _timeout?.Invalidate();
        _timeout = NSTimer.CreateScheduledTimer(ShowSeconds, _ => OptionChosen?.Invoke("not now"));
        _tick?.Invalidate();
        _tick = NSTimer.CreateRepeatingScheduledTimer(TimeSpan.FromMilliseconds(100), _ => _view.NeedsDisplay = true); // the countdown bar
    });

    public void HidePrompt() => MacUi.OnMain(() =>
    {
        _timeout?.Invalidate();
        _timeout = null;
        _tick?.Invalidate();
        _tick = null;
        _menu.CancelTracking();
        _panel.OrderOut(null);
    });

    private sealed class PromptView : NSView
    {
        public string App = "";
        public DateTime ShownAt = DateTime.Now;
        public Action? OnStart;
        public Action<CGPoint>? OnArrow;
        // Measured from Notion's pop-up (2026-11-18): button 176 x 36, fully rounded, 15 px from the right.
        private static readonly CGRect Button = new(W - 15 - 176, 12, 176, 36);
        private static readonly CGRect Arrow = new(Button.Right - 31, Button.Y, 31, Button.Height);
        private static readonly CGRect Main = new(Button.X, Button.Y, Button.Width - Arrow.Width, Button.Height);

        public PromptView(CGRect frame) : base(frame) { }

        public override bool IsFlipped => true;
        public override bool AcceptsFirstMouse(NSEvent? theEvent) => true;

        private static NSColor C(int r, int g, int b, double a = 1) => NSColor.FromSrgb(r / 255f, g / 255f, b / 255f, (nfloat)a);

        public override void DrawRect(CGRect dirtyRect)
        {
            var pill = NSBezierPath.FromRoundedRect(new CGRect(0.5, 0.5, W - 1, H - 1), (nfloat)((H - 1) / 2), (nfloat)((H - 1) / 2));
            C(32, 32, 32).SetFill();
            pill.Fill();
            // The countdown: a thin warm-grey bar along the bottom edge, filling left to right as the time runs out.
            var elapsed = Math.Clamp((DateTime.Now - ShownAt).TotalSeconds / ShowSeconds, 0, 1);
            NSGraphicsContext.CurrentContext?.SaveGraphicsState();
            pill.AddClip();
            C(125, 122, 117).SetFill();
            NSBezierPath.FillRect(new CGRect(0, H - 2, W * elapsed, 2));
            NSGraphicsContext.CurrentContext?.RestoreGraphicsState();
            C(255, 255, 255, 0.16).SetStroke();
            pill.LineWidth = 1;
            pill.Stroke();

            // Icon tile with Huginn's waveform.
            var tile = new CGRect(22, 14, 32, 33);
            NSColor.White.SetFill();
            NSBezierPath.FromRoundedRect(tile, 7, 7).Fill();
            C(25, 25, 25).SetFill();
            double[] bars = { 8, 16, 24, 14, 20, 10 };
            for (int i = 0; i < bars.Length; i++)
                NSBezierPath.FromRoundedRect(new CGRect(tile.X + 6 + i * 3.8, tile.Y + (tile.Height - bars[i]) / 2, 2.4, bars[i]), (nfloat)1.2, (nfloat)1.2).Fill();

            var tx = 64.0;
            new NSAttributedString("Start Meeting Recording", new NSStringAttributes
            {
                Font = NSFont.SystemFontOfSize(14, NSFontWeight.Bold), ForegroundColor = NSColor.White,
            }).DrawAtPoint(new CGPoint(tx, 12));
            new NSAttributedString($"{App} detected", new NSStringAttributes
            {
                Font = NSFont.SystemFontOfSize((nfloat)12.5), ForegroundColor = C(155, 154, 152),
            }).DrawAtPoint(new CGPoint(tx + 1, 32));

            C(39, 131, 222).SetFill();
            NSBezierPath.FromRoundedRect(Button, Button.Height / 2, Button.Height / 2).Fill();
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
        }

        public override void MouseUp(NSEvent theEvent)
        {
            var p = ConvertPointFromView(theEvent.LocationInWindow, null);
            if (Main.Contains(p)) OnStart?.Invoke();
            else if (Arrow.Contains(p)) OnArrow?.Invoke(new CGPoint(Arrow.X, Arrow.Bottom + 6));
        }
    }
}
