using System.Globalization;
using System.Runtime.InteropServices;
using AppKit;
using CoreGraphics;
using Foundation;
using ObjCRuntime;

namespace Huginn;

/// <summary>
/// Shared AppKit helpers for the Mac windows: the main-thread hop, Finder / open / clipboard, the dark palette and
/// fonts (ported from the Windows MeetingPanel), the drawing primitives the custom views use, the Record / Stop
/// pill and the two-click trash both windows draw, and the "x,y" position files.
/// </summary>
internal static class MacUi
{
    // ---- threads ----

    /// <summary>Runs <paramref name="a"/> on the main thread: now when already there, else queued.</summary>
    public static void OnMain(Action a)
    {
        if (NSThread.IsMain) // VERIFY: static NSThread.IsMain (+isMainThread).
        {
            Run(a);
            return;
        }
        NSApplication.SharedApplication.BeginInvokeOnMainThread(() => Run(a));
    }

    // A UI callback must never take the app down; the failure goes to the log.
    private static void Run(Action a)
    {
        try { a(); }
        catch (Exception ex) { HuginnLog.Write("Mac UI callback failed", ex); }
    }

    // ---- Finder, open, clipboard ----

    /// <summary>Shows the file selected in a Finder window.</summary>
    public static void Reveal(string path)
    {
        try { NSWorkspace.SharedWorkspace.ActivateFileViewer(new[] { NSUrl.FromFilename(path) }); }
        catch (Exception ex) { HuginnLog.Write("Reveal failed", ex); }
    }

    /// <summary>Opens the file with its default app.</summary>
    public static void Open(string path)
    {
        try { NSWorkspace.SharedWorkspace.OpenUrl(NSUrl.FromFilename(path)); }
        catch (Exception ex) { HuginnLog.Write("Open failed", ex); }
    }

    /// <summary>Puts plain text on the general pasteboard.</summary>
    public static void Copy(string text)
    {
        try
        {
            var pb = NSPasteboard.GeneralPasteboard;
            pb.ClearContents();
            // VERIFY: NSPasteboardTypeString is an NSString (ToString() gives the UTI either way).
            pb.SetStringForType(text, NSPasteboard.NSPasteboardTypeString.ToString());
        }
        catch (Exception ex) { HuginnLog.Write("Copy failed", ex); }
    }

    // ---- palette (MeetingPanel's colours) ----

    public static readonly NSColor Bg = Rgb(30, 30, 32);
    public static readonly NSColor Border = Rgb(255, 255, 255, 20);
    public static readonly NSColor Divider = Rgb(255, 255, 255, 16);
    public static readonly NSColor TitleCol = Rgb(234, 234, 236);
    public static readonly NSColor RowName = Rgb(214, 214, 218);
    public static readonly NSColor Muted = Rgb(140, 140, 144);
    public static readonly NSColor VMuted = Rgb(104, 104, 108);
    public static readonly NSColor Red = Rgb(224, 72, 77);
    public static readonly NSColor RedHover = Rgb(236, 92, 96);
    public static readonly NSColor Amber = Rgb(226, 170, 92);
    public static readonly NSColor Green = Rgb(122, 196, 143);
    public static readonly NSColor Skeleton = Rgb(255, 255, 255, 34);
    public static readonly NSColor Tile = Rgb(44, 44, 47);
    public static readonly NSColor IconIdle = Rgb(150, 150, 154);
    public static readonly NSColor HoverFill = Rgb(255, 255, 255, 36);
    public static readonly NSColor GhostBorder = Rgb(255, 255, 255, 56);
    public static readonly NSColor White = Rgb(255, 255, 255);

    /// <summary>An sRGB colour from 0..255 channels.</summary>
    public static NSColor Rgb(int r, int g, int b, int a = 255) =>
        // VERIFY: NSColor.FromSrgb(nfloat, nfloat, nfloat, nfloat) (colorWithSRGBRed:green:blue:alpha:); fallback FromCalibratedRgba.
        NSColor.FromSrgb((NFloat)(r / 255.0), (NFloat)(g / 255.0), (NFloat)(b / 255.0), (NFloat)(a / 255.0));

    // ---- fonts (the Windows point sizes as pixels: 10.5 pt at 96 dpi = 14 px) ----

    public static readonly NSFont TitleFont = Semibold(14);
    public static readonly NSFont SubFont = Regular(12);
    public static readonly NSFont PillFont = Semibold(12.5);
    public static readonly NSFont FootFont = Regular(10.5);
    public static readonly NSFont RowNameFont = Semibold(12);
    public static readonly NSFont RowMetaFont = Regular(10.5);
    public static readonly NSFont PadFont = Regular(13);
    public static readonly NSFont FooterButtonFont = Semibold(11.5);

    public static NSFont Regular(double size) => NSFont.SystemFontOfSize((NFloat)size)!;

    // VERIFY: NSFont.SystemFontOfSize(nfloat size, nfloat weight) and NSFontWeight.Semibold (nfloat constant).
    public static NSFont Semibold(double size) => NSFont.SystemFontOfSize((NFloat)size, NSFontWeight.Semibold)!;

    // ---- drawing primitives (call inside DrawRect; views are flipped, so y runs down) ----

    // NFloat and double mix badly in overloads: geometry math runs in double and goes through these two.
    public static CGRect R(double x, double y, double w, double h) => new(x, y, w, h);
    public static CGPoint P(double x, double y) => new(x, y);

    public static void FillRect(CGRect r, NSColor c)
    {
        c.SetFill();
        NSBezierPath.FillRect(r);
    }

    public static void FillRounded(CGRect r, double radius, NSColor c)
    {
        c.SetFill();
        NSBezierPath.FromRoundedRect(r, (NFloat)radius, (NFloat)radius).Fill();
    }

    public static void StrokeRounded(CGRect r, double radius, NSColor c, double width = 1)
    {
        var p = NSBezierPath.FromRoundedRect(r, (NFloat)radius, (NFloat)radius);
        p.LineWidth = (NFloat)width;
        c.SetStroke();
        p.Stroke();
    }

    public static void FillOval(CGRect r, NSColor c)
    {
        c.SetFill();
        NSBezierPath.FromOvalInRect(r).Fill();
    }

    public static void Line(double x1, double y1, double x2, double y2, NSColor c, double width)
    {
        var p = new NSBezierPath();
        p.MoveTo(new CGPoint(x1, y1));
        p.LineTo(new CGPoint(x2, y2));
        p.LineWidth = (NFloat)width;
        p.LineCapStyle = NSLineCapStyle.Round;
        c.SetStroke();
        p.Stroke();
    }

    public static void Polyline(NSColor c, double width, params (double x, double y)[] pts)
    {
        if (pts.Length < 2) return;
        var p = new NSBezierPath();
        p.MoveTo(new CGPoint(pts[0].x, pts[0].y));
        for (int i = 1; i < pts.Length; i++) p.LineTo(new CGPoint(pts[i].x, pts[i].y));
        p.LineWidth = (NFloat)width;
        p.LineCapStyle = NSLineCapStyle.Round;
        p.LineJoinStyle = NSLineJoinStyle.Round;
        c.SetStroke();
        p.Stroke();
    }

    /// <summary>An arc as a polyline (angles in degrees, y-down, clockwise on screen).</summary>
    public static void Arc(double cx, double cy, double r, double startDeg, double sweepDeg, NSColor c, double width)
    {
        const int steps = 24;
        var pts = new (double x, double y)[steps + 1];
        for (int i = 0; i <= steps; i++)
        {
            double a = (startDeg + sweepDeg * i / steps) * Math.PI / 180.0;
            pts[i] = (cx + r * Math.Cos(a), cy + r * Math.Sin(a));
        }
        Polyline(c, width, pts);
    }

    // ---- text ----

    public static NSAttributedString Text(string s, NSFont f, NSColor c,
        NSLineBreakMode mode = NSLineBreakMode.Clipping, NSTextAlignment align = NSTextAlignment.Left)
    {
        var ps = new NSMutableParagraphStyle { LineBreakMode = mode, Alignment = align };
        return new NSAttributedString(s, new NSStringAttributes { Font = f, ForegroundColor = c, ParagraphStyle = ps });
    }

    // VERIFY: NSAttributedString.Size is a property (AppKit NSStringDrawing category, -size).
    public static CGSize Measure(string s, NSFont f) => Text(s, f, White).Size;

    public static double TextWidth(string s, NSFont f) => Measure(s, f).Width;

    /// <summary>One line with its top-left at (x, y).</summary>
    // VERIFY: NSAttributedString.DrawAtPoint(CGPoint) / DrawInRect(CGRect) (AppKit NSStringDrawing category).
    public static void DrawText(string s, NSFont f, NSColor c, double x, double y) =>
        Text(s, f, c).DrawAtPoint(new CGPoint(x, y));

    /// <summary>One line in a box: clipped to its width with the given truncation, centred vertically.</summary>
    public static void DrawText(string s, NSFont f, NSColor c, CGRect box,
        NSLineBreakMode mode = NSLineBreakMode.TruncatingTail, NSTextAlignment align = NSTextAlignment.Left)
    {
        var a = Text(s, f, c, mode, align);
        double h = Math.Ceiling((double)a.Size.Height);
        double bx = box.X, by = box.Y, bw = box.Width, bh = box.Height;
        a.DrawInRect(R(bx, by + Math.Max(0, (bh - h) / 2), bw, h));
    }

    // ---- the controls both windows share ----

    /// <summary>The red pill with a record dot or stop square and its label, centred together.</summary>
    public static void DrawActionPill(CGRect rect, bool isStop, bool hover)
    {
        FillRounded(rect, 8, hover ? RedHover : Red);
        string text = isStop ? "Stop" : "Record";
        const double glyph = 9, gap = 7;
        var size = Measure(text, PillFont);
        double tw = size.Width, th = size.Height;
        double rx = rect.X, ry = rect.Y, rw = rect.Width, rh = rect.Height;
        double total = glyph + gap + tw;
        double sx = rx + (rw - total) / 2;
        double cy = ry + rh / 2;
        if (isStop) FillRounded(R(sx, cy - glyph / 2, glyph, glyph), 2, White);
        else FillOval(R(sx, cy - glyph / 2, glyph, glyph), White);
        DrawText(text, PillFont, White, sx + glyph + gap, cy - th / 2);
    }

    /// <summary>The discard trash: ghost, faint on hover, red-filled once armed (the first click).</summary>
    public static void DrawTrash(CGRect rect, bool hover, bool armed)
    {
        if (armed) FillRounded(rect, 8, hover ? RedHover : Red);
        else if (hover) FillRounded(rect, 8, HoverFill);
        var col = armed ? White : (hover ? RedHover : Muted);
        double rx = rect.X, ry = rect.Y, rw = rect.Width, rh = rect.Height;
        double cx = Math.Floor(rx + rw / 2), cy = Math.Floor(ry + rh / 2);
        Line(cx - 6, cy - 4, cx + 6, cy - 4, col, 1.5);          // lid
        Line(cx - 2, cy - 6, cx + 2, cy - 6, col, 1.5);          // handle
        Polyline(col, 1.5, (cx - 4, cy - 4), (cx - 3, cy + 6), (cx + 3, cy + 6), (cx + 4, cy - 4)); // can
        Line(cx - 1, cy - 1, cx - 1, cy + 3, col, 1.5);          // ribs
        Line(cx + 1, cy - 1, cx + 1, cy + 3, col, 1.5);
    }

    /// <summary>The card: the dark rounded body and its hairline border.</summary>
    public static void DrawCard(CGRect bounds)
    {
        FillRounded(bounds, 14, Bg);
        double x = bounds.X, y = bounds.Y, w = bounds.Width, h = bounds.Height;
        StrokeRounded(R(x + 0.5, y + 0.5, w - 1, h - 1), 14, Border);
    }

    public static string Elapsed(DateTime startedLocal)
    {
        var t = DateTime.Now - startedLocal;
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}";
    }

    // ---- panels ----

    /// <summary>A borderless, non-activating floating panel with a clear background (the view draws the card).
    /// <paramref name="canBecomeKey"/> = the panel can take the keyboard (text entry).</summary>
    public static NSPanel MakePanel(CGRect frame, bool canBecomeKey)
    {
        var style = NSWindowStyle.Borderless | NSWindowStyle.NonactivatingPanel;
        NSPanel panel = canBecomeKey
            ? new MacKeyPanel(frame, style, NSBackingStore.Buffered, false)
            : new NSPanel(frame, style, NSBackingStore.Buffered, false);
        panel.ReleasedWhenClosed = false;
        panel.FloatingPanel = true;
        panel.Level = NSWindowLevel.Floating;
        panel.HidesOnDeactivate = false;
        panel.CollectionBehavior = NSWindowCollectionBehavior.CanJoinAllSpaces | NSWindowCollectionBehavior.FullScreenAuxiliary;
        panel.BackgroundColor = NSColor.Clear;
        panel.IsOpaque = false; // VERIFY: NSWindow.opaque is bound as IsOpaque.
        panel.HasShadow = true;
        return panel;
    }

    /// <summary>A repeating timer on the main run loop that also runs while a menu is open.</summary>
    public static NSTimer Repeat(TimeSpan every, Action tick)
    {
        var t = NSTimer.CreateRepeatingScheduledTimer(every, _ => Run(tick));
        NSRunLoop.Main.AddTimer(t, NSRunLoopMode.Common); // VERIFY: AddTimer(NSTimer, NSRunLoopMode) overload.
        return t;
    }

    // ---- screens and the "x,y" position files (AppKit screen coordinates: the panel's bottom-left origin) ----

    public static NSScreen? MainScreen()
    {
        var main = NSScreen.MainScreen;
        if (main != null) return main;
        var all = NSScreen.Screens;
        return all.Length > 0 ? all[0] : null;
    }

    /// <summary>The screen holding the centre of the rectangle, else the main screen.</summary>
    public static NSScreen? ScreenFor(CGRect r)
    {
        double x = r.X, y = r.Y, w = r.Width, h = r.Height;
        var c = P(x + w / 2, y + h / 2);
        foreach (var s in NSScreen.Screens)
            if (s.Frame.Contains(c)) return s;
        return MainScreen();
    }

    public static bool SameScreen(NSScreen? a, NSScreen? b) => a != null && b != null && a.Frame == b.Frame;

    /// <summary>True when the window's top-left 40 px (its header) is on some screen.</summary>
    public static bool HeaderOnAScreen(CGRect frame)
    {
        double x = frame.X, y = frame.Y, h = frame.Height;
        var head = R(x, y + h - 40, 40, 40);
        foreach (var s in NSScreen.Screens)
            if (s.VisibleFrame.Contains(head)) return true;
        return false;
    }

    public static void SavePosition(string file, CGPoint origin)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, string.Create(CultureInfo.InvariantCulture,
                $"{(int)Math.Round((double)origin.X)},{(int)Math.Round((double)origin.Y)}"));
        }
        catch { /* non-fatal */ }
    }

    public static CGPoint? LoadPosition(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            var parts = File.ReadAllText(file).Trim().Split(',');
            if (parts.Length == 2
                && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y))
                return new CGPoint(x, y);
        }
        catch { /* fall through */ }
        return null;
    }
}

/// <summary>A borderless panel that can still take the keyboard (a borderless window refuses it by default).</summary>
[Register("HuginnKeyPanel")]
internal sealed class MacKeyPanel : NSPanel
{
    public MacKeyPanel(CGRect contentRect, NSWindowStyle style, NSBackingStore backing, bool defer)
        : base(contentRect, style, backing, defer) { }

    // VERIFY: the NativeHandle constructor of NSPanel is reachable from a subclass (protected internal).
    public MacKeyPanel(NativeHandle handle) : base(handle) { }

    public override bool CanBecomeKeyWindow => true;
    public override bool CanBecomeMainWindow => false;
}
