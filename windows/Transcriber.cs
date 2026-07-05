using System.Text;
using Whisper.net;
using Whisper.net.Ggml;

namespace Munin;

/// <summary>One transcribed chunk with its position in the audio (relative to the track's start).</summary>
internal readonly record struct TranscriptSegment(TimeSpan Start, TimeSpan End, string Text);

/// <summary>
/// Wraps Whisper.net. Loads the model lazily on first use (into VRAM via the Vulkan runtime)
/// and unloads it after <see cref="Config.IdleUnloadMinutes"/> of inactivity to free the GPU.
/// </summary>
internal sealed class Transcriber : IAsyncDisposable
{
    private readonly string _modelPath;
    private readonly string _vadModelPath;
    private readonly string _previewModelPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _previewGate = new(1, 1);
    private readonly System.Threading.Timer _idleTimer;
    private WhisperFactory? _factory;
    private WhisperFactory? _previewFactory;
    private WhisperVadFactory? _vadFactory;

    public event Action<string>? Status;

    /// <summary>Optional Whisper initial prompt (a glossary of domain terms) that biases the decoder toward
    /// their spellings. Applied to both dictation and meeting transcription. Null/empty = no biasing.</summary>
    public string? InitialPrompt { get; set; }

    /// <summary>When true, the idle timer will NOT unload the model — it stays warm in VRAM. Set while the app
    /// is enabled so the (desktop-stalling) GPU load happens once at startup, never again on reload. VRAM is
    /// still freed explicitly on Disable via <see cref="UnloadAsync"/>.</summary>
    public bool KeepWarm { get; set; }

    public Transcriber(string modelPath, string vadModelPath, string previewModelPath)
    {
        _modelPath = modelPath;
        _vadModelPath = vadModelPath;
        _previewModelPath = previewModelPath;
        _idleTimer = new System.Threading.Timer(_ => { if (!KeepWarm) _ = UnloadAsync(); }, null, Timeout.Infinite, Timeout.Infinite);
    }

    public bool IsLoaded => _factory != null;

    /// <summary>
    /// Loads the model and runs one throwaway pass on silence so the GPU shaders compile now,
    /// instead of costing the user ~8s on their first real dictation.
    /// </summary>
    public async Task WarmupAsync()
    {
        using var silent = new MemoryStream();
        WriteSilentWav(silent, 0.5);
        silent.Position = 0;
        await TranscribeAsync(silent).ConfigureAwait(false);
        // Prime the speech-detection VAD too (download + load) so the first real dictation's gate is instant.
        silent.Position = 0;
        try { await HasSpeechAsync(silent).ConfigureAwait(false); } catch { /* VAD optional — falls back to no gate */ }
        // Prime the fast preview model (download + load) so the first live-preview tick is instant.
        silent.Position = 0;
        try { await TranscribePreviewAsync(silent).ConfigureAwait(false); } catch { /* preview optional */ }
    }

    public async Task<string> TranscribeAsync(Stream wavStream, bool gateSpeech = false)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Neural voice-activity gate: skip the (expensive) Whisper pass on clips with no actual speech —
            // room tone, breath, a keyboard tap. Catches what the amplitude gate can't (whispered speech passes;
            // silence is rejected) and suppresses Whisper's silence-hallucinations. Degrades to "transcribe
            // anyway" if the VAD model is unavailable, so a dictation is never lost to VAD.
            if (gateSpeech && wavStream.CanSeek)
            {
                long pos = wavStream.Position;
                bool hasSpeech = true;
                try { hasSpeech = await HasSpeechAsync(wavStream).ConfigureAwait(false); }
                catch { hasSpeech = true; }
                wavStream.Position = pos;
                if (!hasSpeech) return "";
            }

            _factory ??= WhisperFactory.FromPath(_modelPath);
            var builder = _factory.CreateBuilder().WithLanguage(Config.Language);
            if (!string.IsNullOrEmpty(InitialPrompt)) builder = builder.WithPrompt(InitialPrompt);
            await using var processor = builder.Build();

            var sb = new StringBuilder();
            await foreach (var segment in processor.ProcessAsync(wavStream).ConfigureAwait(false))
                sb.Append(segment.Text);

            return sb.ToString().Trim();
        }
        finally
        {
            _gate.Release();
            // Restart the idle countdown after every transcription.
            _idleTimer.Change(TimeSpan.FromMinutes(Config.IdleUnloadMinutes), Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Like <see cref="TranscribeAsync"/> but keeps each segment's start/end timestamps. The meeting
    /// recorder needs these to interleave the two tracks (Me / Them) chronologically and to anchor each
    /// action-item citation to a moment in the audio. Same model + gate as dictation.
    /// </summary>
    public async Task<IReadOnlyList<TranscriptSegment>> TranscribeSegmentsAsync(Stream wavStream, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _factory ??= WhisperFactory.FromPath(_modelPath);
            var builder = _factory.CreateBuilder().WithLanguage(Config.Language);
            if (!string.IsNullOrEmpty(InitialPrompt)) builder = builder.WithPrompt(InitialPrompt);
            await using var processor = builder.Build();

            var segs = new List<TranscriptSegment>();
            await foreach (var segment in processor.ProcessAsync(wavStream, ct).ConfigureAwait(false))
            {
                var text = segment.Text.Trim();
                if (text.Length > 0) segs.Add(new TranscriptSegment(segment.Start, segment.End, text));
            }
            return segs;
        }
        finally
        {
            _gate.Release();
            _idleTimer.Change(TimeSpan.FromMinutes(Config.IdleUnloadMinutes), Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Fast, throwaway live-preview transcription on a SMALL model (tiny) with its OWN gate + factory — so it
    /// never contends with, or delays, the authoritative final pass (<see cref="TranscribeAsync"/>) on the big
    /// model. No VAD gate (the user is actively speaking). Downloads/loads the tiny model lazily. Returns "" on
    /// any failure — a dropped preview is harmless.
    /// </summary>
    public async Task<string> TranscribePreviewAsync(Stream wavStream)
    {
        await _previewGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!File.Exists(_previewModelPath))
                await ModelDownloader.EnsureAsync(_previewModelPath, Config.PreviewModelUrl, null).ConfigureAwait(false);
            _previewFactory ??= WhisperFactory.FromPath(_previewModelPath);
            var builder = _previewFactory.CreateBuilder().WithLanguage(Config.Language);
            if (!string.IsNullOrEmpty(InitialPrompt)) builder = builder.WithPrompt(InitialPrompt);
            await using var processor = builder.Build();

            var sb = new StringBuilder();
            await foreach (var segment in processor.ProcessAsync(wavStream).ConfigureAwait(false))
                sb.Append(segment.Text);

            return sb.ToString().Trim();
        }
        catch { return ""; }
        finally { _previewGate.Release(); }
    }

    /// <summary>Runs the Silero VAD over the clip and reports whether it contains any speech. Loads (and, on
    /// first use, downloads) the tiny VAD model lazily. Runs on CPU — no GPU/VRAM contention with Whisper.
    /// The caller already holds <see cref="_gate"/>, so this must NOT take it (it would deadlock).</summary>
    private async Task<bool> HasSpeechAsync(Stream wavStream, CancellationToken ct = default)
    {
        await EnsureVadModelAsync(ct).ConfigureAwait(false);
        _vadFactory ??= WhisperVadFactory.FromPath(_vadModelPath);
        await using var vad = _vadFactory.CreateBuilder()
            .WithThreshold(0.5f)
            .WithUseGpu(false)
            .Build();
        var segments = await vad.DetectSpeechAsync(wavStream, ct).ConfigureAwait(false);
        return segments.Count > 0;
    }

    private async Task EnsureVadModelAsync(CancellationToken ct = default)
    {
        if (File.Exists(_vadModelPath)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(_vadModelPath)!);
        string tmp = _vadModelPath + ".part";
        await using (var src = await WhisperGgmlDownloader.Default.GetGgmlSileroVadModelAsync(SileroVadType.V5_1_2, ct).ConfigureAwait(false))
        await using (var dst = File.Create(tmp))
            await src.CopyToAsync(dst, ct).ConfigureAwait(false);
        File.Move(tmp, _vadModelPath, overwrite: true);
    }

    public async Task UnloadAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        await _previewGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_factory != null)
            {
                _factory.Dispose();
                _factory = null;
                Status?.Invoke("Idle (model unloaded, VRAM freed)");
            }
            _previewFactory?.Dispose();
            _previewFactory = null;
            _vadFactory?.Dispose();
            _vadFactory = null;
        }
        finally
        {
            _previewGate.Release();
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _idleTimer.DisposeAsync().ConfigureAwait(false);
        await UnloadAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private static void WriteSilentWav(Stream s, double seconds)
    {
        const int sampleRate = 16000, channels = 1, bits = 16;
        int dataLen = (int)(sampleRate * channels * (bits / 8) * seconds);
        int byteRate = sampleRate * channels * bits / 8;
        var w = new BinaryWriter(s, Encoding.UTF8, leaveOpen: true);
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + dataLen);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);
        w.Write((short)1);              // PCM
        w.Write((short)channels);
        w.Write(sampleRate);
        w.Write(byteRate);
        w.Write((short)(channels * bits / 8));
        w.Write((short)bits);
        w.Write("data"u8.ToArray());
        w.Write(dataLen);
        w.Write(new byte[dataLen]);     // silence
        w.Flush();
    }
}
