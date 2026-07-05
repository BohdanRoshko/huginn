namespace Munin;

internal static class Paths
{
    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Munin");

    /// <summary>%LOCALAPPDATA%\Munin\&lt;whisper model&gt; — kept out of the build output.</summary>
    public static string ModelPath() => Path.Combine(Dir, Config.ModelFileName);

    /// <summary>%LOCALAPPDATA%\Munin\&lt;cleanup LLM gguf&gt;.</summary>
    public static string CleanupModelPath() => Path.Combine(Dir, Config.CleanupModelFileName);

    /// <summary>%LOCALAPPDATA%\Munin\ggml-silero-v5.1.2.bin — the Silero VAD model (neural speech gate).</summary>
    public static string VadModelPath() => Path.Combine(Dir, "ggml-silero-v5.1.2.bin");

    /// <summary>%LOCALAPPDATA%\Munin\&lt;tiny model&gt; — the fast live-preview model (separate from the final pass).</summary>
    public static string PreviewModelPath() => Path.Combine(Dir, Config.PreviewModelFileName);

    /// <summary>%LOCALAPPDATA%\Munin\Meetings — captured meeting WAV tracks + transcripts.</summary>
    public static string MeetingsDir() => Path.Combine(Dir, "Meetings");

    /// <summary>%LOCALAPPDATA%\Munin\quit.flag — the programmatic quit channel (Munin's SKILL lifecycle
    /// contract): a host app (Odin's companion toggle) writes this file; Munin flushes any active recording
    /// to disk (no transcription) and exits. Deleted on consumption; a stale one is cleared at startup.</summary>
    public static string QuitFlagPath() => Path.Combine(Dir, "quit.flag");

    /// <summary>The %LOCALAPPDATA%\Munin folder itself (for watchers).</summary>
    public static string BaseDir() => Dir;
}
