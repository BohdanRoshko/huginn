using System.Diagnostics;
using System.Text;
using Whisper.net;
using Whisper.net.Ggml;

namespace Huginn;

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

    /// <summary>When true, the idle timer will NOT unload the model. Set while a meeting records, so the model
    /// loaded at Record is still there at Stop.</summary>
    public bool KeepWarm
    {
        get => _keepWarm;
        set
        {
            _keepWarm = value;
            // A meeting can outlast the countdown, and a discarded one never transcribes; start a fresh one.
            if (!value) _idleTimer.Change(TimeSpan.FromMinutes(Config.IdleUnloadMinutes), Timeout.InfiniteTimeSpan);
        }
    }
    private volatile bool _keepWarm;

    private Task? _loading;

    /// <summary>The warm-up loads the small preview model too. Dictation shows a live preview; a computer that only
    /// transcribes meetings has no use for it.</summary>
    public bool WithPreview { get; init; } = true;

    public Transcriber(string modelPath, string vadModelPath, string previewModelPath)
    {
        _modelPath = modelPath;
        _vadModelPath = vadModelPath;
        _previewModelPath = previewModelPath;
        _idleTimer = new System.Threading.Timer(_ => { if (!KeepWarm) _ = UnloadAsync(); }, null, Timeout.Infinite, Timeout.Infinite);
    }

    public bool IsLoaded => _factory != null;

    /// <summary>Starts the warm-up load unless the model is loaded or already loading. Called at Record and on
    /// the dictation key, so the load runs while the user speaks. The load runs on a pool thread: reading the
    /// model takes seconds, and the caller is the window thread, which also owns the dictation key's hook.</summary>
    public Task EnsureLoadedAsync()
    {
        if (IsLoaded) return Task.CompletedTask;
        var t = _loading;
        if (t != null && !t.IsCompleted) return t;
        return _loading = Task.Run(WarmupAsync);
    }

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
        if (!WithPreview) return;
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
    ///
    /// A long track is NOT fed to Whisper whole: the decoder can lock into a repetition loop for minutes.
    /// The VAD splits it into speech chunks of at most 28 s (<see cref="SpeechChunker"/>), each transcribed on
    /// its own, and <see cref="SpeechChunker.Tidy"/> collapses what still repeats. Without a usable VAD the
    /// track goes through whole, as before.
    /// </summary>
    /// <param name="o">The language, prompt, threads and rest of this job; null = this computer's settings.</param>
    public async Task<IReadOnlyList<TranscriptSegment>> TranscribeSegmentsAsync(Stream wavStream, CancellationToken ct = default, MeetingOptions? o = null)
    {
        int rest = o?.RestPercent ?? Config.MeetingRestPercent;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _factory ??= WhisperFactory.FromPath(_modelPath);
            var chunks = await SpeechChunksAsync(wavStream, ct).ConfigureAwait(false);
            var segs = new List<TranscriptSegment>();
            // ONE processor per track: each chunk is a standalone WAV slice fed to it (a processor per chunk
            // would pay a whisper state, ~2 s of GPU setup, per chunk).
            await using var processor = MeetingBuilder(o).Build();
            if (chunks == null)
            {
                wavStream.Position = 0;
                await CollectAsync(processor, wavStream, TimeSpan.Zero, segs, ct).ConfigureAwait(false);
            }
            else if (wavStream is MemoryStream ms)
            {
                var bytes = WavSlice.Bytes(ms);
                var (dataOffset, dataLength) = WavSlice.FindData(bytes);
                foreach (var (start, end) in chunks)
                {
                    var work = Stopwatch.StartNew();
                    using var slice = WavSlice.Slice(bytes, dataOffset, dataLength, start, end);
                    await CollectAsync(processor, slice, start, segs, ct).ConfigureAwait(false);
                    await RestAsync(work.Elapsed, rest, ct).ConfigureAwait(false);
                }
            }
            else
            {
                foreach (var (start, end) in chunks)
                {
                    var work = Stopwatch.StartNew();
                    wavStream.Position = 0;
                    await using var chunkProcessor = MeetingBuilder(o).WithOffset(start).WithDuration(end - start).Build();
                    await CollectAsync(chunkProcessor, wavStream, TimeSpan.Zero, segs, ct).ConfigureAwait(false);
                    await RestAsync(work.Elapsed, rest, ct).ConfigureAwait(false);
                }
            }
            var tidy = SpeechChunker.Tidy(segs);
            HuginnLog.Write($"transcribe: {(chunks == null ? "whole track" : $"{chunks.Count} speech chunks")}, {segs.Count} segments, {tidy.LoopsCollapsed} loop repeats collapsed, {tidy.OverlapsDropped} overlaps dropped");
            return tidy.Segments;
        }
        finally
        {
            _gate.Release();
            _idleTimer.Change(TimeSpan.FromMinutes(Config.IdleUnloadMinutes), Timeout.InfiniteTimeSpan);
        }
    }

    private WhisperProcessorBuilder MeetingBuilder(MeetingOptions? o)
    {
        var builder = _factory!.CreateBuilder().WithLanguage(o?.Language ?? Config.Language);
        int threads = o?.Threads ?? Config.MeetingThreads;
        if (threads > 0) builder = builder.WithThreads(threads);
        var prompt = o != null ? o.Prompt : InitialPrompt;
        if (!string.IsNullOrEmpty(prompt)) builder = builder.WithPrompt(prompt);
        return builder;
    }

    // Gives the GPU and CPU back to the desktop between chunks, so a video does not stall during a long job.
    private static Task RestAsync(TimeSpan work, int percent, CancellationToken ct) =>
        percent > 0 ? Task.Delay(work * (percent / 100.0), ct) : Task.CompletedTask;

    private static async Task CollectAsync(WhisperProcessor processor, Stream wav, TimeSpan offset, List<TranscriptSegment> into, CancellationToken ct)
    {
        await foreach (var segment in processor.ProcessAsync(wav, ct).ConfigureAwait(false))
        {
            var text = segment.Text.Trim();
            if (text.Length > 0) into.Add(new TranscriptSegment(segment.Start + offset, segment.End + offset, text));
        }
    }

    /// <summary>Speech chunks for a meeting track, or null when the VAD is unavailable (then the whole track
    /// is transcribed at once, the old behaviour). An empty list means the track holds no speech.</summary>
    private async Task<List<(TimeSpan Start, TimeSpan End)>?> SpeechChunksAsync(Stream wavStream, CancellationToken ct)
    {
        try
        {
            await EnsureVadModelAsync(ct).ConfigureAwait(false);
            _vadFactory ??= WhisperVadFactory.FromPath(_vadModelPath);
            await using var vad = _vadFactory.CreateBuilder()
                .WithThreshold(0.5f)
                .WithUseGpu(false)
                .WithMinSilenceDuration(TimeSpan.FromMilliseconds(400))
                .WithMinSpeechDuration(TimeSpan.FromMilliseconds(200))
                .WithMaxSpeechDuration(SpeechChunker.MaxChunk)
                .WithSpeechPadding(TimeSpan.FromMilliseconds(250))
                .Build();
            wavStream.Position = 0;
            var speech = await vad.DetectSpeechAsync(wavStream, ct).ConfigureAwait(false);
            return SpeechChunker.Group(speech.Select(s => (s.Start, s.End)));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            HuginnLog.Write("transcribe: VAD unavailable, transcribing the whole track", ex);
            return null;
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
