namespace Huginn;

/// <summary>Tiny persisted runtime flags in the data folder (survive restarts), one small file each. These are the
/// switches the tray / menu bar flips; the load-once knobs live in settings.json (<see cref="HuginnSettings"/>).</summary>
internal static class Settings
{
    /// <summary>Push-to-talk dictation listens for its hotkey. Default ON; only "0" turns it off. Meetings, the
    /// Kumospace watcher and the work timer keep running while it is off.</summary>
    public static bool DictationEnabled { get => Flag("dictation.txt", true); set => SetFlag("dictation.txt", value); }

    /// <summary>Clean dictated text with the in-process LLM. Default OFF; only "1" turns it on.</summary>
    public static bool CleanupEnabled { get => Flag("cleanup.txt", false); set => SetFlag("cleanup.txt", value); }

    /// <summary>Auto-file a finished meeting recording to Odin (POST /api/meetings/process). Default ON.</summary>
    public static bool SendMeetingsToOdin { get => Flag("send_meetings.txt", true); set => SetFlag("send_meetings.txt", value); }

    /// <summary>Show the recorder panel. Default ON on Windows; OFF on the Mac, which is a menu bar app first.</summary>
    public static bool MeetingPanelVisible { get => Flag("panel.txt", !OperatingSystem.IsMacOS()); set => SetFlag("panel.txt", value); }

    /// <summary>Start and stop meeting recordings from Kumospace (KumoWatcher). Default ON.</summary>
    public static bool KumoAutoRecord { get => Flag("kumo_auto.txt", true); set => SetFlag("kumo_auto.txt", value); }

    /// <summary>When the Notes window shows: "always", "record" (opens when a recording starts), or "off". File
    /// notes.txt. Default "always" on Windows (beside the recorder), "off" on the Mac (as the Swift app had it).</summary>
    public static string NotesMode
    {
        get
        {
            var v = Read("notes.txt")?.ToLowerInvariant();
            return v is "off" or "record" or "always" ? v : OperatingSystem.IsMacOS() ? "off" : "always";
        }
        set => Write("notes.txt", value);
    }

    /// <summary>The chosen microphone: the platform's device id, or "" for the system default. File mic.txt
    /// (Windows wrote a WaveIn index there, "-1" = default; that still reads the same).</summary>
    public static string Microphone
    {
        get { var v = Read("mic.txt") ?? ""; return v == "-1" ? "" : v; }
        set => Write("mic.txt", value);
    }

    private static bool Flag(string file, bool dflt)
    {
        var v = Read(file);
        return v == null ? dflt : dflt ? v != "0" : v == "1";
    }

    private static void SetFlag(string file, bool value) => Write(file, value ? "1" : "0");

    private static string? Read(string file)
    {
        try { var f = Paths.File(file); return System.IO.File.Exists(f) ? System.IO.File.ReadAllText(f).Trim() : null; }
        catch { return null; }
    }

    private static void Write(string file, string value)
    {
        try
        {
            Directory.CreateDirectory(Paths.BaseDir());
            System.IO.File.WriteAllText(Paths.File(file), value);
        }
        catch { /* non-fatal: the switch just won't persist */ }
    }
}
