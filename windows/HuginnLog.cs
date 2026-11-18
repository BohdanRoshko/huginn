namespace Huginn;

/// <summary>Append-only diagnostics log at %LOCALAPPDATA%\Huginn\huginn.log (see <see cref="Paths.LogPath"/>).
/// Never throws; rotates once to huginn.log.1 past 2 MB. This is where a failure's exception text goes, since a
/// tray balloon is gone in seconds.</summary>
internal static class HuginnLog
{
    private const long RotateBytes = 2 * 1024 * 1024;
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var path = Paths.LogPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > RotateBytes)
                    File.Move(path, path + ".1", overwrite: true);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never take the app down */ }
    }

    public static void Write(string message, Exception ex) => Write($"{message}: {ex}");
}
