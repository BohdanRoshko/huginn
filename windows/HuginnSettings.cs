using System.Text.Json;

namespace Huginn;

/// <summary>
/// Huginn's load-once configuration, read from %LOCALAPPDATA%\Huginn\settings.json (created with defaults on
/// first run). Replaces the old compile-time <see cref="Config"/> consts so these knobs are editable without
/// recompiling — the enabler for a future settings UI. Read ONCE at startup; edit the file and relaunch to
/// apply. (Runtime tray toggles — cleanup, meeting auto-file, panel visibility, microphone — still persist to
/// their own small files via <see cref="Settings"/> / <see cref="MicSettings"/>; unifying those into this file
/// is a Phase-3 follow-up.) Never throws — a missing or malformed file falls back to defaults.
/// </summary>
internal sealed class HuginnSettings
{
    public HotkeySettings Hotkey { get; set; } = new();
    public int CancelVk { get; set; } = 0x1B; // key that cancels a dictation (default Escape 0x1B); map a mouse button to it in your mouse software
    public string Language { get; set; } = "en";
    public bool ShowOverlay { get; set; } = true;
    public bool ShowMeetingOverlay { get; set; } = false;
    public bool LivePreview { get; set; } = true;
    public bool PauseMediaWhileRecording { get; set; } = true;
    public int IdleUnloadMinutes { get; set; } = 30;
    public int MeetingThreads { get; set; } = 4; // CPU threads for meeting transcription; 0 = every hardware thread (the Whisper default)
    public int MeetingRestPercent { get; set; } = 50; // after each speech chunk, rest this share of the time the chunk took; 0 = full speed
    public string OdinBaseUrl { get; set; } = "http://localhost:5180";
    // Kumospace auto-record (KumoWatcher / KumoMeetingDetector). Levels are Daily's 0..1 audio level.
    public int KumoDebugPort { get; set; } = 9222;          // the DevTools port Kumospace is started with
    public double KumoTalkLevel { get; set; } = 0.03;       // a level at or above this counts as talking
    public double KumoStartSpeechSeconds { get; set; } = 0.2; // speech within 15 s, with someone around, starts a recording (0.2 = the first sample)
    public double KumoAloneStopSeconds { get; set; } = 5;  // an auto recording stops after this long with nobody around
    public double KumoQuietStopSeconds { get; set; } = 180; // ...or after this long with no speech
    public double KumoMinSpeechSeconds { get; set; } = 20;  // an auto recording with less speech than this is deleted

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static string FilePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Huginn", "settings.json");

    /// <summary>The single loaded instance, read at startup.</summary>
    public static HuginnSettings Current { get; } = Load();

    private static HuginnSettings Load()
    {
        try
        {
            var path = FilePath();
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<HuginnSettings>(File.ReadAllText(path), JsonOpts);
                if (s != null) { s.Write(); return s; } // rewrite to backfill any newly-added fields
            }
        }
        catch { /* fall through to defaults */ }
        var def = new HuginnSettings();
        def.Write();
        return def;
    }

    private void Write()
    {
        try
        {
            var path = FilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { /* non-fatal */ }
    }
}

/// <summary>Push-to-talk chord. Default: Left Ctrl (0xA2) + Space (0x20). ModifierVk 0 = single-key hotkey.</summary>
internal sealed class HotkeySettings
{
    public int MainVk { get; set; } = 0x20;      // Space
    public int ModifierVk { get; set; } = 0xA2;  // Left Ctrl (0 = no modifier)
    public bool Swallow { get; set; } = true;
}
