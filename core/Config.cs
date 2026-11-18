namespace Huginn;

/// <summary>
/// Config accessors. The user-facing knobs are now backed by runtime settings (<see cref="HuginnSettings"/>,
/// from settings.json) rather than compile-time consts — so they're editable without recompiling. Call sites
/// are unchanged (<c>Config.X</c>). Model filenames + download URLs stay constants here: they're download
/// internals, not user-facing knobs (model-choice UI is a later phase).
/// </summary>
internal static class Config
{
    private static HuginnSettings S => HuginnSettings.Current;

    // Push-to-talk: hold the modifier + main key, release to transcribe. VK codes.
    // VK list: https://learn.microsoft.com/windows/win32/inputdev/virtual-key-codes
    public static int PushToTalkVk => S.Hotkey.MainVk;
    public static int PushToTalkModifierVk => S.Hotkey.ModifierVk;

    // Swallow the push-to-talk key so the focused app never sees it.
    public static bool SwallowHotkey => S.Hotkey.Swallow;

    // Key that cancels the current dictation (default Escape). Map a mouse button to this key in your mouse
    // software (e.g. Logitech Options+ → Keystroke assignment) to cancel with the mouse.
    public static int CancelVk => S.CancelVk;

    // Show the floating "recording pill" overlay (live mic level + state) while dictating.
    public static bool ShowOverlay => S.ShowOverlay;

    // Show the top-center "● REC" pill while recording a MEETING (off by default).
    public static bool ShowMeetingOverlay => S.ShowMeetingOverlay;

    // Pause currently-playing media while recording, then resume it when you release the key.
    public static bool PauseMediaWhileRecording => S.PauseMediaWhileRecording;

    // Show a live (partial) transcription preview in the overlay while you speak.
    public static bool LivePreview => S.LivePreview;

    // Unload the model from VRAM after this many minutes of no dictation.
    public static int IdleUnloadMinutes => S.IdleUnloadMinutes;

    // Meeting transcription load: CPU threads (0 = all) and the rest after each chunk, as a % of its work time.
    public static int MeetingThreads => S.MeetingThreads;
    public static int MeetingRestPercent => S.MeetingRestPercent;

    // Whisper language. "en" is fastest/most reliable; "auto" to auto-detect, or a code like "uk"/"es".
    public static string Language => S.Language;

    // Odin (Integration Console) API base — a finished meeting recording is POSTed to {OdinBaseUrl}/api/meetings/process.
    public static string OdinBaseUrl => S.OdinBaseUrl;

    // ---- Model files + download sources: download internals, not user knobs → kept as constants. ----

    // large-v3-turbo = best speed/quality. For a smaller download swap to "ggml-large-v3-turbo-q5_0.bin".
    public const string ModelFileName = "ggml-large-v3-turbo.bin";
    public const string ModelUrl =
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo.bin";

    // Cleanup LLM — small instruct model run in-process via LLamaSharp/Vulkan (dies with the app).
    public const string CleanupModelFileName = "qwen2.5-3b-instruct-q4_k_m.gguf";
    public const string CleanupModelUrl =
        "https://huggingface.co/bartowski/Qwen2.5-3B-Instruct-GGUF/resolve/main/Qwen2.5-3B-Instruct-Q4_K_M.gguf";

    // Small, fast model for the throwaway LIVE PREVIEW — separate from the big final-pass model so the preview
    // never contends with (or delays) the authoritative transcription. Multilingual tiny (~75 MB), respects Language.
    public const string PreviewModelFileName = "ggml-tiny.bin";
    public const string PreviewModelUrl =
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.bin";
}
