using System.Diagnostics;
using System.Runtime.InteropServices;
using AppKit;
using AVFoundation;
using CoreFoundation;
using CoreGraphics;
using Foundation;
using ObjCRuntime;

namespace Huginn;

/// <summary>The three macOS permissions Huginn needs: Microphone (both modes), Accessibility (the dictation key and
/// the paste), Screen Recording (the other side of a call).</summary>
internal static class MacPermissions
{
    private const string AppServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string CoreGraphicsLib = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    [DllImport(AppServices)] private static extern bool AXIsProcessTrustedWithOptions(IntPtr options);
    [DllImport(CoreGraphicsLib)] private static extern bool CGPreflightScreenCaptureAccess();
    [DllImport(CoreGraphicsLib)] private static extern bool CGRequestScreenCaptureAccess();

    public static bool MicrophoneAllowed()
    {
        var status = AVCaptureDevice.GetAuthorizationStatus(AVAuthorizationMediaType.Audio);
        if (status == AVAuthorizationStatus.Authorized) return true;
        if (status != AVAuthorizationStatus.NotDetermined) return false;
        // First use: ask, and wait for the answer (off the main thread, the dialog needs it).
        return Task.Run(() => AVCaptureDevice.RequestAccessForMediaTypeAsync(AVAuthorizationMediaType.Audio)).GetAwaiter().GetResult();
    }

    /// <summary>True when Huginn may watch and post key events. With <paramref name="prompt"/> macOS shows its dialog.</summary>
    public static bool AccessibilityTrusted(bool prompt)
    {
        using var key = new NSString("AXTrustedCheckOptionPrompt");
        using var options = NSDictionary.FromObjectAndKey(NSNumber.FromBoolean(prompt), key);
        return AXIsProcessTrustedWithOptions(options.Handle);
    }

    public static bool ScreenRecordingAllowed(bool prompt) =>
        CGPreflightScreenCaptureAccess() || (prompt && CGRequestScreenCaptureAccess());
}

/// <summary>Push-to-talk on the Mac: a CGEvent tap. The key can be a modifier held alone (Right Option, the default)
/// or a key held with modifiers. The cancel key (Escape) is swallowed only while a dictation runs.</summary>
internal sealed class MacHotkey : IHotkey
{
    private const string AppServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    [DllImport(AppServices)] private static extern long CGEventGetIntegerValueField(IntPtr ev, int field);
    [DllImport(AppServices)] private static extern ulong CGEventGetFlags(IntPtr ev);
    private const int KeyboardEventKeycode = 9;
    private const int KeyboardEventAutorepeat = 8;

    // Modifier keys: key code → (the general flag, the device-specific bit that says which side is down).
    private static readonly Dictionary<int, (ulong Flag, ulong Device)> Modifiers = new()
    {
        [59] = (0x40000, 0x01), [62] = (0x40000, 0x2000),   // left / right Control
        [56] = (0x20000, 0x02), [60] = (0x20000, 0x04),     // left / right Shift
        [55] = (0x100000, 0x08), [54] = (0x100000, 0x10),   // left / right Command
        [58] = (0x80000, 0x20), [61] = (0x80000, 0x40),     // left / right Option
        [63] = (0x800000, 0),                               // fn
    };

    private readonly int _key;
    private readonly ulong _requiredFlags;
    private readonly int _cancelKey;
    private readonly CGEvent.CGEventTapCallback _callback;
    private CFMachPort? _tap;
    private CFRunLoopSource? _source;
    private bool _engaged;
    private volatile bool _cancelArmed;

    public MacHotkey(int keyCode, int modifiers, int cancelKeyCode)
    {
        _key = keyCode;
        _cancelKey = cancelKeyCode;
        if ((modifiers & 1) != 0) _requiredFlags |= 0x40000;
        if ((modifiers & 2) != 0) _requiredFlags |= 0x80000;
        if ((modifiers & 4) != 0) _requiredFlags |= 0x100000;
        if ((modifiers & 8) != 0) _requiredFlags |= 0x20000;
        _callback = OnEvent; // kept alive for the tap's lifetime
    }

    public string Name
    {
        get
        {
            var parts = new List<string>();
            if ((_requiredFlags & 0x40000) != 0) parts.Add("Control");
            if ((_requiredFlags & 0x80000) != 0) parts.Add("Option");
            if ((_requiredFlags & 0x100000) != 0) parts.Add("Command");
            if ((_requiredFlags & 0x20000) != 0) parts.Add("Shift");
            parts.Add(KeyName(_key));
            return string.Join(" + ", parts);
        }
    }

    private static string KeyName(int code) => code switch
    {
        61 => "Right Option", 58 => "Left Option", 54 => "Right Command", 55 => "Left Command",
        62 => "Right Control", 59 => "Left Control", 60 => "Right Shift", 56 => "Left Shift", 63 => "fn",
        49 => "Space", 53 => "Escape", 96 => "F5", 97 => "F6", 98 => "F7", 100 => "F8",
        _ => $"key {code}",
    };

    public bool CancelArmed { get => _cancelArmed; set => _cancelArmed = value; }

    public event Action? KeyDown;
    public event Action? KeyUp;
    public event Action? CancelRequested;

    public void Install()
    {
        if (_tap != null) return;
        if (!MacPermissions.AccessibilityTrusted(prompt: true))
            HuginnLog.Write("hotkey: Accessibility permission not granted yet; dictation starts working once it is");
        var mask = CGEventMask.KeyDown | CGEventMask.KeyUp | CGEventMask.FlagsChanged;
        _tap = CGEvent.CreateTap(CGEventTapLocation.Session, CGEventTapPlacement.HeadInsert, CGEventTapOptions.Default, mask, _callback, IntPtr.Zero);
        if (_tap == null)
        {
            HuginnLog.Write("hotkey: could not create the key tap (Accessibility permission?)");
            return;
        }
        _source = _tap.CreateRunLoopSource();
        CFRunLoop.Main.AddSource(_source, CFRunLoop.ModeCommon);
        CGEvent.TapEnable(_tap);
        HuginnLog.Write($"hotkey: listening for {Name}");
    }

    public void Uninstall()
    {
        if (_tap == null) return;
        CGEvent.TapDisable(_tap);
        if (_source != null) { CFRunLoop.Main.RemoveSource(_source, CFRunLoop.ModeCommon); _source.Dispose(); _source = null; }
        _tap.Dispose();
        _tap = null;
        if (_engaged) { _engaged = false; KeyUp?.Invoke(); }
    }

    private IntPtr OnEvent(IntPtr proxy, CGEventType type, IntPtr ev, IntPtr user)
    {
        try
        {
            if (type == CGEventType.TapDisabledByTimeout || type == CGEventType.TapDisabledByUserInput)
            {
                if (_tap != null) CGEvent.TapEnable(_tap);
                return ev;
            }
            int code = (int)CGEventGetIntegerValueField(ev, KeyboardEventKeycode);
            ulong flags = CGEventGetFlags(ev);

            if (type == CGEventType.KeyDown && code == _cancelKey && _cancelArmed)
            {
                CancelRequested?.Invoke();
                return IntPtr.Zero; // swallowed: the focused app never sees it
            }

            if (Modifiers.TryGetValue(_key, out var mod))
            {
                // A modifier held alone: watch its side's bit on every flags change. Never swallowed.
                if (type != CGEventType.FlagsChanged || code != _key) return ev;
                bool down = mod.Device != 0 ? (flags & mod.Device) != 0 : (flags & mod.Flag) != 0;
                down &= (flags & _requiredFlags) == _requiredFlags;
                Change(down);
                return ev;
            }

            if (code != _key || (type != CGEventType.KeyDown && type != CGEventType.KeyUp)) return ev;
            if (type == CGEventType.KeyDown)
            {
                if (!_engaged && (flags & _requiredFlags) != _requiredFlags) return ev;
                if (CGEventGetIntegerValueField(ev, KeyboardEventAutorepeat) == 0) Change(true);
                return IntPtr.Zero;
            }
            if (_engaged) { Change(false); return IntPtr.Zero; }
            return ev;
        }
        catch (Exception ex)
        {
            HuginnLog.Write("hotkey: event failed", ex);
            return ev;
        }
    }

    private void Change(bool down)
    {
        if (down == _engaged) return;
        _engaged = down;
        if (down) KeyDown?.Invoke(); else KeyUp?.Invoke();
    }

    public void Dispose() => Uninstall();
}

/// <summary>Types text at the cursor: puts it on the clipboard, presses Command+V, then puts the old clipboard back.</summary>
internal sealed class MacTextInjector : ITextInjector
{
    private const string AppServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    [DllImport(AppServices)] private static extern IntPtr CGEventCreateKeyboardEvent(IntPtr source, ushort keyCode, bool keyDown);
    [DllImport(AppServices)] private static extern void CGEventSetFlags(IntPtr ev, ulong flags);
    [DllImport(AppServices)] private static extern void CGEventPost(int tap, IntPtr ev);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] private static extern void CFRelease(IntPtr cf);
    private const ushort KeyV = 9;
    private const ulong CommandFlag = 0x100000;

    public void Inject(string text) => MacUi.OnMain(() =>
    {
        var pb = NSPasteboard.GeneralPasteboard;
        var type = NSPasteboard.NSPasteboardTypeString;
        var previous = pb.GetStringForType(type);
        pb.ClearContents();
        pb.SetStringForType(text, type);
        Press(KeyV, CommandFlag);
        // Give the target app time to read the clipboard before the old text goes back.
        NSTimer.CreateScheduledTimer(0.6, _ =>
        {
            if (previous == null) return;
            pb.ClearContents();
            pb.SetStringForType(previous, type);
        });
    });

    private static void Press(ushort key, ulong flags)
    {
        foreach (var down in new[] { true, false })
        {
            var ev = CGEventCreateKeyboardEvent(IntPtr.Zero, key, down);
            if (ev == IntPtr.Zero) continue;
            CGEventSetFlags(ev, flags);
            CGEventPost(0, ev); // kCGHIDEventTap
            CFRelease(ev);
        }
    }
}

/// <summary>The Kumospace desktop app on the Mac. Electron, so starting it with --remote-debugging-port exposes the
/// same DevTools page the Windows app reads.</summary>
internal sealed class MacKumoApp : IKumoApp
{
    private static readonly string[] Locations =
    {
        "/Applications/Kumospace.app",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications", "Kumospace.app"),
    };

    private static string? AppPath => Locations.FirstOrDefault(Directory.Exists);

    public bool IsInstalled => AppPath != null;

    public bool IsRunning()
    {
        var ps = Process.GetProcessesByName("Kumospace");
        foreach (var p in ps) p.Dispose();
        return ps.Length > 0;
    }

    public async Task<bool> RestartWithDebugPortAsync(int port)
    {
        MacUi.OnMain(() =>
        {
            foreach (var app in NSWorkspace.SharedWorkspace.RunningApplications)
                if (string.Equals(app.LocalizedName, "Kumospace", StringComparison.OrdinalIgnoreCase)) app.Terminate();
        });
        for (int i = 0; i < 40 && IsRunning(); i++) await Task.Delay(250).ConfigureAwait(false);
        if (IsRunning()) { HuginnLog.Write("kumo: Kumospace did not quit; restart skipped"); return false; }
        Start(port);
        return true;
    }

    private static void Start(int port)
    {
        var path = AppPath;
        if (path == null) return;
        var psi = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
        foreach (var a in new[] { "-a", path, "--args", $"--remote-debugging-port={port}" }) psi.ArgumentList.Add(a);
        Process.Start(psi)?.Dispose();
        HuginnLog.Write($"kumo: started Kumospace with --remote-debugging-port={port}");
    }

    /// <summary>The Dock and Spotlight start Kumospace without the port, and macOS gives no shortcut to edit. The
    /// menu's "restart it" line covers that.</summary>
    public void EnsureLaunchesWithPort(int port) { }
}

/// <summary>The Swift app kept its model in Models/; the shared core keeps it in the data folder itself.</summary>
internal static class MacMigration
{
    public static void Run()
    {
        try
        {
            var oldModels = Path.Combine(Paths.BaseDir(), "Models");
            if (!Directory.Exists(oldModels)) return;
            foreach (var f in Directory.GetFiles(oldModels, "*.bin"))
            {
                var target = Paths.File(Path.GetFileName(f));
                if (!File.Exists(target) && new FileInfo(f).Length > 1_000_000)
                {
                    File.Move(f, target);
                    HuginnLog.Write($"moved the Swift app's model {Path.GetFileName(f)} into the data folder");
                }
            }
        }
        catch (Exception ex) { HuginnLog.Write("could not move the Swift app's model", ex); }
    }
}
