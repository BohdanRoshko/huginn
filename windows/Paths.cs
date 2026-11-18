namespace Huginn;

internal static class Paths
{
    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Huginn");

    /// <summary>%LOCALAPPDATA%\Huginn\&lt;whisper model&gt; — kept out of the build output.</summary>
    public static string ModelPath() => Path.Combine(Dir, Config.ModelFileName);

    /// <summary>%LOCALAPPDATA%\Huginn\&lt;cleanup LLM gguf&gt;.</summary>
    public static string CleanupModelPath() => Path.Combine(Dir, Config.CleanupModelFileName);

    /// <summary>%LOCALAPPDATA%\Huginn\ggml-silero-v5.1.2.bin — the Silero VAD model (neural speech gate).</summary>
    public static string VadModelPath() => Path.Combine(Dir, "ggml-silero-v5.1.2.bin");

    /// <summary>%LOCALAPPDATA%\Huginn\&lt;tiny model&gt; — the fast live-preview model (separate from the final pass).</summary>
    public static string PreviewModelPath() => Path.Combine(Dir, Config.PreviewModelFileName);

    /// <summary>%LOCALAPPDATA%\Huginn\Meetings — captured meeting WAV tracks + transcripts.</summary>
    public static string MeetingsDir() => Path.Combine(Dir, "Meetings");

    /// <summary>%LOCALAPPDATA%\Huginn\quit.flag — the programmatic quit channel (Huginn's SKILL lifecycle
    /// contract): a host app (Odin's companion toggle) writes this file; Huginn flushes any active recording
    /// to disk (no transcription) and exits. Deleted on consumption; a stale one is cleared at startup.</summary>
    public static string QuitFlagPath() => Path.Combine(Dir, "quit.flag");

    /// <summary>%LOCALAPPDATA%\Huginn\Presence — my Kumospace status, one file per day (<see cref="PresenceLog"/>).</summary>
    public static string PresenceDir() => Path.Combine(Dir, "Presence");

    /// <summary>The %LOCALAPPDATA%\Huginn folder itself (for watchers).</summary>
    public static string BaseDir() => Dir;

    /// <summary>%LOCALAPPDATA%\Huginn\huginn.log: the append-only diagnostics log (<see cref="HuginnLog"/>).</summary>
    public static string LogPath() => Path.Combine(Dir, "huginn.log");
}
