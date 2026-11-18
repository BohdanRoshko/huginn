using NAudio.Wave;

namespace Huginn;

/// <summary>
/// Records a meeting as TWO tracks (the SKILL's free-diarization win): the microphone is "Me" and a
/// WASAPI loopback of the default render device is "Them" (every remote participant, mixed). Both are
/// written to WAV while the meeting runs. On <see cref="Stop"/> + <see cref="FinalizeAsync"/> it returns a
/// lightweight <see cref="MeetingCapture"/> (the two WAV paths) and the recorder is reusable at once —
/// transcription/filing happens OUTSIDE this class (<see cref="MeetingProcessor"/>) so recording is never
/// blocked by processing. Separate from the dictation hot path; only shares the <see cref="Transcriber"/>.
/// </summary>
internal sealed class MeetingRecorder : IMeetingRecorder
{
    private static readonly WaveFormat WhisperFormat = new(16000, 16, 1); // what Whisper wants

    /// <summary>-1 = Windows default recording device; otherwise a WaveIn device index (shared with dictation).</summary>
    public int MicDeviceNumber { get; set; } = -1;

    /// <summary>The WaveIn index as text; "" = the Windows default.</summary>
    public string Device
    {
        get => MicDeviceNumber < 0 ? "" : MicDeviceNumber.ToString();
        set => MicDeviceNumber = int.TryParse(value, out var n) ? n : -1;
    }

    private readonly object _lock = new();
    private WaveInEvent? _mic;
    private WasapiLoopbackCapture? _loop;
    private WaveFileWriter? _micWriter;
    private WaveFileWriter? _loopWriter;
    private TaskCompletionSource _micStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _loopStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string _micPath = "", _loopPath = "";
    private volatile bool _recording;

    public bool IsRecording => _recording;
    private MeetingCapture? _plan;


    /// <summary>Live combined input level 0..1 (max of mic + system audio per buffer) — drives the panel's
    /// voice-level meter. Raised from the capture threads.</summary>
    public event Action<float>? LevelChanged;

    public void Start(MeetingCapture plan)
    {
        if (_recording) return;

        _plan = plan;
        _micPath = plan.MicPath;
        _loopPath = plan.LoopPath;
        _micStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _loopStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

        int device = MicDeviceNumber < WaveInEvent.DeviceCount ? MicDeviceNumber : -1;
        _mic = new WaveInEvent { DeviceNumber = device, WaveFormat = WhisperFormat, BufferMilliseconds = 100 };
        _micWriter = new WaveFileWriter(_micPath, WhisperFormat);
        _mic.DataAvailable += OnMicData;
        _mic.RecordingStopped += (_, _) => { lock (_lock) { _micWriter?.Dispose(); _micWriter = null; } _micStopped.TrySetResult(); };

        _loop = new WasapiLoopbackCapture();
        _loopWriter = new WaveFileWriter(_loopPath, _loop.WaveFormat);
        _loop.DataAvailable += OnLoopData;
        _loop.RecordingStopped += (_, _) => { lock (_lock) { _loopWriter?.Dispose(); _loopWriter = null; } _loopStopped.TrySetResult(); };

        _recording = true;
        try
        {
            _mic.StartRecording();
            _loop.StartRecording();
        }
        catch
        {
            _recording = false;
            _micStopped.TrySetResult();
            _loopStopped.TrySetResult();
            throw;
        }
    }

    private void OnMicData(object? sender, WaveInEventArgs e)
    {
        lock (_lock) { _micWriter?.Write(e.Buffer, 0, e.BytesRecorded); }
        RaiseLevel(e.Buffer, e.BytesRecorded, WhisperFormat);
    }

    private void OnLoopData(object? sender, WaveInEventArgs e)
    {
        lock (_lock) { _loopWriter?.Write(e.Buffer, 0, e.BytesRecorded); }
        var fmt = _loop?.WaveFormat;
        if (fmt != null) RaiseLevel(e.Buffer, e.BytesRecorded, fmt);
    }

    private void RaiseLevel(byte[] buf, int count, WaveFormat fmt)
    {
        var h = LevelChanged;
        if (h == null) return;
        float peak = PeakFromBuffer(buf, count, fmt);
        if (peak > 0) h(peak);
    }

    private static float PeakFromBuffer(byte[] buf, int count, WaveFormat fmt)
    {
        float peak = 0;
        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat && fmt.BitsPerSample == 32)
        {
            for (int i = 0; i + 3 < count; i += 4)
            {
                float s = Math.Abs(BitConverter.ToSingle(buf, i));
                if (s > peak) peak = s;
            }
        }
        else if (fmt.Encoding == WaveFormatEncoding.Pcm && fmt.BitsPerSample == 16)
        {
            for (int i = 0; i + 1 < count; i += 2)
            {
                int s = Math.Abs((short)(buf[i] | (buf[i + 1] << 8)));
                float f = s / 32768f;
                if (f > peak) peak = f;
            }
        }
        return Math.Min(1f, peak);
    }

    /// <summary>Stops capture. Writers are finalized on the captures' RecordingStopped callbacks.</summary>
    public void Stop()
    {
        if (!_recording) return;
        _recording = false;
        try { _mic?.StopRecording(); } catch { _micStopped.TrySetResult(); }
        try { _loop?.StopRecording(); } catch { _loopStopped.TrySetResult(); }
    }

    /// <summary>
    /// Waits (briefly) for both tracks to flush + disposes the captures, then returns a snapshot of the
    /// recording. Fast — it does NOT transcribe. After this the recorder is free to <see cref="Start"/>
    /// a new meeting while the returned capture is processed in the background.
    /// </summary>
    public async Task<MeetingCapture?> FinalizeAsync(CancellationToken ct = default)
    {
        await Task.WhenAny(Task.WhenAll(_micStopped.Task, _loopStopped.Task), Task.Delay(5000, ct)).ConfigureAwait(false);
        lock (_lock)
        {
            _mic?.Dispose(); _mic = null;
            _loop?.Dispose(); _loop = null;
        }
        return _plan;
    }

    public void Dispose()
    {
        try { Stop(); } catch { /* best effort */ }
        lock (_lock)
        {
            _mic?.Dispose(); _mic = null;
            _loop?.Dispose(); _loop = null;
            _micWriter?.Dispose(); _micWriter = null;
            _loopWriter?.Dispose(); _loopWriter = null;
        }
    }
}
