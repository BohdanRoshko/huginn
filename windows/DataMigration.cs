namespace Huginn;

/// <summary>One-time move of the old app's data folder (%LOCALAPPDATA%\Munin) to %LOCALAPPDATA%\Huginn, so the
/// recordings, settings, work-timer files and models carry over and nothing downloads again. Same drive, so it is
/// a rename, not a copy. A junction is left at the old path so old links (Odin notes, scripts) still open.</summary>
internal static class DataMigration
{
    /// <summary>Returns false when the move is needed but could not happen (the old app still holds its files).</summary>
    public static bool Run(out string? error)
    {
        error = null;
        var newDir = Paths.BaseDir();
        var oldDir = Path.Combine(Path.GetDirectoryName(newDir)!, "Munin");
        try
        {
            if (Directory.Exists(newDir) || !Directory.Exists(oldDir)) return true;
            if (new DirectoryInfo(oldDir).Attributes.HasFlag(FileAttributes.ReparsePoint)) return true;

            Directory.Move(oldDir, newDir);
            Rename(Path.Combine(newDir, "munin.log"), Path.Combine(newDir, "huginn.log"));
            Rename(Path.Combine(newDir, "munin.log.1"), Path.Combine(newDir, "huginn.log.1"));
            RewriteRecent(Path.Combine(newDir, "Meetings", "recent.json"), oldDir, newDir);
            var junction = MakeJunction(oldDir, newDir);
            HuginnLog.Write($"moved the old data folder {oldDir} -> {newDir}{(junction ? " (junction left at the old path)" : " (no junction)")}");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void Rename(string from, string to)
    {
        if (File.Exists(from) && !File.Exists(to)) File.Move(from, to);
    }

    // recent.json keeps each transcript's full path; point them at the new folder.
    private static void RewriteRecent(string path, string oldDir, string newDir)
    {
        if (!File.Exists(path)) return;
        var json = File.ReadAllText(path);
        var oldEsc = oldDir.Replace(@"\", @"\\");
        var newEsc = newDir.Replace(@"\", @"\\");
        var updated = json.Replace(oldEsc, newEsc, StringComparison.OrdinalIgnoreCase);
        if (updated != json) File.WriteAllText(path, updated);
    }

    private static bool MakeJunction(string link, string target)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.WaitForExit(10_000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
}
