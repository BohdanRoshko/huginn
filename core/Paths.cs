namespace Huginn;

/// <summary>Where Huginn keeps its files. Windows: %LOCALAPPDATA%\Huginn. Mac: ~/Library/Application Support/Huginn.
/// Every file the app reads or writes lives under <see cref="BaseDir"/>.</summary>
internal static class Paths
{
    // HUGINN_DATA_DIR moves everything elsewhere: the tests and the CI self-test use it, so they never touch real data.
    private static readonly string Dir = Environment.GetEnvironmentVariable("HUGINN_DATA_DIR") is { Length: > 0 } custom
        ? custom
        : Path.Combine(
            OperatingSystem.IsMacOS()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support")
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Huginn");

    /// <summary>The data folder itself (for watchers).</summary>
    public static string BaseDir() => Dir;

    /// <summary>A file directly in the data folder (settings, flags, positions).</summary>
    public static string File(string name) => Path.Combine(Dir, name);

    /// <summary>The Whisper model, kept out of the build output.</summary>
    public static string ModelPath() => File(Config.ModelFileName);

    /// <summary>The cleanup LLM gguf.</summary>
    public static string CleanupModelPath() => File(Config.CleanupModelFileName);

    /// <summary>The Silero VAD model (neural speech gate).</summary>
    public static string VadModelPath() => File("ggml-silero-v5.1.2.bin");

    /// <summary>The fast live-preview model (separate from the final pass).</summary>
    public static string PreviewModelPath() => File(Config.PreviewModelFileName);

    /// <summary>Meetings: captured WAV tracks, notes, speaker timelines, transcripts, recent.json.</summary>
    public static string MeetingsDir() => Path.Combine(Dir, "Meetings");

    /// <summary>The programmatic quit channel: another app writes quit.flag; Huginn flushes any active recording to
    /// disk (no transcription) and exits. Deleted on consumption; a stale one is cleared at startup.</summary>
    public static string QuitFlagPath() => File("quit.flag");

    /// <summary>My Kumospace status, one file per day (<see cref="PresenceLog"/>).</summary>
    public static string PresenceDir() => Path.Combine(Dir, "Presence");

    /// <summary>huginn.log: the append-only diagnostics log (<see cref="HuginnLog"/>).</summary>
    public static string LogPath() => File("huginn.log");
}
