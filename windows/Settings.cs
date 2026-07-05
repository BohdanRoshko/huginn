namespace Munin;

/// <summary>Tiny persisted runtime flags in %LOCALAPPDATA%\Munin (survive restarts).</summary>
internal static class Settings
{
    private static string FilePath(string name) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Munin", name);

    public static bool CleanupEnabled
    {
        get
        {
            try { return File.Exists(FilePath("cleanup.txt")) && File.ReadAllText(FilePath("cleanup.txt")).Trim() == "1"; }
            catch { return false; }
        }
        set
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath("cleanup.txt"))!);
                File.WriteAllText(FilePath("cleanup.txt"), value ? "1" : "0");
            }
            catch { /* non-fatal */ }
        }
    }

    /// <summary>Auto-file a finished meeting recording to Odin (POST /api/meetings/process). Default ON
    /// (missing file = on); only "0" turns it off.</summary>
    public static bool SendMeetingsToOdin
    {
        get
        {
            try { var f = FilePath("send_meetings.txt"); return !File.Exists(f) || File.ReadAllText(f).Trim() != "0"; }
            catch { return true; }
        }
        set
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath("send_meetings.txt"))!);
                File.WriteAllText(FilePath("send_meetings.txt"), value ? "1" : "0");
            }
            catch { /* non-fatal */ }
        }
    }

    /// <summary>Show the Notion-style recorder panel (the 3rd-monitor HUD). Default ON; only "0" hides it.</summary>
    public static bool MeetingPanelVisible
    {
        get
        {
            try { var f = FilePath("panel.txt"); return !File.Exists(f) || File.ReadAllText(f).Trim() != "0"; }
            catch { return true; }
        }
        set
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath("panel.txt"))!);
                File.WriteAllText(FilePath("panel.txt"), value ? "1" : "0");
            }
            catch { /* non-fatal */ }
        }
    }
}
