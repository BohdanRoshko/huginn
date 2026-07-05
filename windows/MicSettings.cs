namespace Munin;

/// <summary>
/// Remembers the chosen microphone across restarts. Stores a single WaveIn device index
/// (-1 = Windows default) in %LOCALAPPDATA%\Munin\mic.txt.
/// </summary>
internal static class MicSettings
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Munin", "mic.txt");

    public static int Load()
    {
        try
        {
            if (File.Exists(FilePath) && int.TryParse(File.ReadAllText(FilePath).Trim(), out int n))
                return n;
        }
        catch { /* fall through to default */ }
        return -1;
    }

    public static void Save(int deviceNumber)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, deviceNumber.ToString());
        }
        catch { /* non-fatal: selection just won't persist */ }
    }
}
