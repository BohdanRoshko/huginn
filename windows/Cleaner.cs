using System.Text;
using LLama;
using LLama.Common;
using LLama.Sampling;

namespace Munin;

/// <summary>
/// Optional in-process cleanup LLM (Qwen2.5-3B via LLamaSharp + Vulkan). Tidies raw dictation —
/// punctuation, capitalization, filler/word-repeat removal — without changing the wording.
/// Runs INSIDE Munin's process, so it dies with the app; also unloaded when cleanup is toggled off, when
/// recording is disabled, or after Config.IdleUnloadMinutes without use. Passes text through unchanged if not ready.
/// </summary>
internal sealed class Cleaner : IAsyncDisposable
{
    private const string SystemPrompt =
        "You are a dictation cleanup tool. Given raw speech-to-text, return the SAME words with correct " +
        "punctuation and capitalization, and with filler words (um, uh, like, you know) and immediate word " +
        "repetitions removed. " +
        "If the speaker corrects themselves (e.g. 'meet at 2, uh, actually 3', or says 'scratch that' or " +
        "'I mean'), keep only the corrected version and drop the retracted words. " +
        "If the speaker clearly dictates a list (e.g. 'first, buy milk; second, call Alex'), format it as a " +
        "numbered or bulleted list. " +
        "Otherwise do not rephrase, summarize, add, translate, or answer anything. " +
        "Output ONLY the cleaned text — no quotes, no preamble, no notes.";

    private const int ContextTokens = 2048; // must match ModelParams.ContextSize in PrepareAsync

    private readonly string _modelPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LLamaWeights? _weights;
    private StatelessExecutor? _executor;
    private volatile bool _ready;

    public event Action<string>? Status;

    /// <summary>Optional comma-separated canonical terms (the personal dictionary). When set, the cleanup model
    /// is told to fix phonetically-close words to these exact spellings. Set before cleanup runs.</summary>
    public string? Vocabulary { get; set; }

    private readonly System.Threading.Timer _idleTimer;

    public Cleaner(string modelPath)
    {
        _modelPath = modelPath;
        _idleTimer = new System.Threading.Timer(_ => _ = UnloadAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    private void RestartIdleTimer() =>
        _idleTimer.Change(TimeSpan.FromMinutes(Config.IdleUnloadMinutes), Timeout.InfiniteTimeSpan);

    public async Task PrepareAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_weights != null) return;

            if (!File.Exists(_modelPath))
            {
                Status?.Invoke("Downloading cleanup model… (one-time)");
                await ModelDownloader.EnsureAsync(_modelPath, Config.CleanupModelUrl,
                    p => Status?.Invoke($"Downloading cleanup model… {p:P0}")).ConfigureAwait(false);
            }

            Status?.Invoke("Loading cleanup model…");
            var mp = new ModelParams(_modelPath) { ContextSize = ContextTokens, GpuLayerCount = 99 };
            _weights = LLamaWeights.LoadFromFile(mp);
            _executor = new StatelessExecutor(_weights, mp);

            // Warm pass: compile the Vulkan shaders so the first real cleanup is fast.
            var warm = new InferenceParams { MaxTokens = 1, AntiPrompts = new List<string> { "<|im_end|>" } };
            await foreach (var _ in _executor.InferAsync(
                "<|im_start|>user\nhi<|im_end|>\n<|im_start|>assistant\n", warm).ConfigureAwait(false)) { }

            _ready = true;
            Status?.Invoke("Idle — ready");
        }
        catch (Exception ex)
        {
            Status?.Invoke($"Cleanup model failed: {ex.Message}");
        }
        finally
        {
            _gate.Release();
            RestartIdleTimer();
        }
    }

    private string BuildSystemPrompt()
    {
        if (string.IsNullOrWhiteSpace(Vocabulary)) return SystemPrompt;
        return SystemPrompt +
            " The user frequently uses these exact terms — if a word sounds like one of them but is spelled " +
            $"differently, correct it to the spelling shown: {Vocabulary}.";
    }

    public async Task<string> CleanAsync(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        if (!_ready) await PrepareAsync().ConfigureAwait(false); // lazy first-use load (kept off the startup GPU load)
        if (!_ready) return raw;

        string system = BuildSystemPrompt();

        // Budget the output to the input length: cleanup is ~the same length as the input (it removes fillers
        // and adds punctuation, never expands much). A fixed 256-token cap silently truncated anything over
        // ~1 minute of speech. Estimate ~3 chars/token — deliberately conservative (undercounts tokens → a
        // roomier budget and an earlier skip, both safe: the raw text is always an acceptable fallback).
        int estInputTokens = raw.Length / 3;
        int estSystemTokens = system.Length / 3;
        int outputBudget = estInputTokens + 64; // headroom for added punctuation

        // The system prompt + chat scaffolding + the input all share the context window with the output. If it
        // can't all fit, cleanup WOULD truncate — skip it and return the raw text rather than lose the tail.
        const int templateOverhead = 32; // <|im_*|> scaffolding tokens
        if (estSystemTokens + estInputTokens + outputBudget + templateOverhead > ContextTokens) return raw;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_executor == null) return raw;
            var infer = new InferenceParams
            {
                MaxTokens = outputBudget,
                AntiPrompts = new List<string> { "<|im_end|>" },
                SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0.2f },
            };
            string prompt =
                $"<|im_start|>system\n{system}<|im_end|>\n<|im_start|>user\n{raw}<|im_end|>\n<|im_start|>assistant\n";

            var sb = new StringBuilder();
            int generated = 0;
            await foreach (var tok in _executor.InferAsync(prompt, infer).ConfigureAwait(false))
            {
                sb.Append(tok);
                generated++;
            }

            string cleaned = sb.ToString().Replace("<|im_end|>", "").Trim();
            if (string.IsNullOrWhiteSpace(cleaned)) return raw;

            // A complete response ends on the <|im_end|> stop token (generated < budget). If we instead ran
            // into the token budget, the output was cut off mid-text — return the raw words, not a fragment.
            if (generated >= outputBudget) return raw;

            return cleaned;
        }
        catch { return raw; } // never lose the user's words to a cleanup failure
        finally
        {
            _gate.Release();
            RestartIdleTimer();
        }
    }

    public async Task UnloadAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _ready = false;
            _executor = null;
            _weights?.Dispose();
            _weights = null;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _idleTimer.DisposeAsync().ConfigureAwait(false);
        await UnloadAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
