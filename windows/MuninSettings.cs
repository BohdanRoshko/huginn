using System.Text.Json;

namespace Munin;

/// <summary>
/// Munin's load-once configuration, read from %LOCALAPPDATA%\Munin\settings.json (created with defaults on
/// first run). Replaces the old compile-time <see cref="Config"/> consts so these knobs are editable without
/// recompiling — the enabler for a future settings UI. Read ONCE at startup; edit the file and relaunch to
/// apply. (Runtime tray toggles — cleanup, meeting auto-file, panel visibility, microphone — still persist to
/// their own small files via <see cref="Settings"/> / <see cref="MicSettings"/>; unifying those into this file
/// is a Phase-3 follow-up.) Never throws — a missing or malformed file falls back to defaults.
/// </summary>
internal sealed class MuninSettings
{
    public HotkeySettings Hotkey { get; set; } = new();
    public int CancelVk { get; set; } = 0x1B; // key that cancels a dictation (default Escape 0x1B); map a mouse button to it in your mouse software
    public string Language { get; set; } = "en";
    public bool ShowOverlay { get; set; } = true;
    public bool ShowMeetingOverlay { get; set; } = false;
    public bool LivePreview { get; set; } = true;
    public bool PauseMediaWhileRecording { get; set; } = true;
    public int IdleUnloadMinutes { get; set; } = 5;
    public string OdinBaseUrl { get; set; } = "http://localhost:5180";

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static string FilePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Munin", "settings.json");

    /// <summary>The single loaded instance, read at startup.</summary>
    public static MuninSettings Current { get; } = Load();

    private static MuninSettings Load()
    {
        try
        {
            var path = FilePath();
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<MuninSettings>(File.ReadAllText(path), JsonOpts);
                if (s != null) { s.Write(); return s; } // rewrite to backfill any newly-added fields
            }
        }
        catch { /* fall through to defaults */ }
        var def = new MuninSettings();
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
