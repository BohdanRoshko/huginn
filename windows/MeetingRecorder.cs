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

    // Both tracks are kept on one clock that starts at the plan's start time (see TrackClock).
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private TimeSpan _clockOffset, _stoppedAt;
    private TrackClock? _micClock, _loopClock;
    private static readonly byte[] Silence = new byte[64 * 1024];

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
        _mic.RecordingStopped += (_, _) => { lock (_lock) { Close(ref _micWriter, _micClock); } _micStopped.TrySetResult(); };

        _loop = new WasapiLoopbackCapture();
        _loopWriter = new WaveFileWriter(_loopPath, _loop.WaveFormat);
        _loop.DataAvailable += OnLoopData;
        _loop.RecordingStopped += (_, _) => { lock (_lock) { Close(ref _loopWriter, _loopClock); } _loopStopped.TrySetResult(); };

        _micClock = new TrackClock(WhisperFormat.AverageBytesPerSecond, WhisperFormat.BlockAlign);
        _loopClock = new TrackClock(_loop.WaveFormat.AverageBytesPerSecond, _loop.WaveFormat.BlockAlign);
        var sinceStart = DateTime.Now - plan.StartedLocal;
        _clockOffset = sinceStart > TimeSpan.Zero && sinceStart < TimeSpan.FromSeconds(10) ? sinceStart : TimeSpan.Zero;
        _stoppedAt = TimeSpan.Zero;
        _clock.Restart();

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
        lock (_lock) { WriteOnClock(_micWriter, _micClock, e.Buffer, e.BytesRecorded); }
        RaiseLevel(e.Buffer, e.BytesRecorded, WhisperFormat);
    }

    private void OnLoopData(object? sender, WaveInEventArgs e)
    {
        lock (_lock) { WriteOnClock(_loopWriter, _loopClock, e.Buffer, e.BytesRecorded); }
        var fmt = _loop?.WaveFormat;
        if (fmt != null) RaiseLevel(e.Buffer, e.BytesRecorded, fmt);
    }

    // Audio that never arrived becomes silence of the same length, so what comes after it keeps its time.
    private void WriteOnClock(WaveFileWriter? writer, TrackClock? clock, byte[] buffer, int count)
    {
        if (writer == null) return;
        if (clock != null) WriteSilence(writer, clock.SilenceBefore(_clockOffset + _clock.Elapsed, count));
        writer.Write(buffer, 0, count);
    }

    private static void WriteSilence(WaveFileWriter writer, long bytes)
    {
        for (; bytes > 0; bytes -= Math.Min(bytes, Silence.Length))
            writer.Write(Silence, 0, (int)Math.Min(bytes, Silence.Length));
    }

    // Brings the track to the length of the recording (the speakers may have been silent at the end), then closes it.
    private void Close(ref WaveFileWriter? writer, TrackClock? clock)
    {
        if (writer == null) return;
        try { if (clock != null && _stoppedAt > TimeSpan.Zero) WriteSilence(writer, clock.SilenceAtEnd(_stoppedAt)); }
        catch (Exception ex) { HuginnLog.Write("meeting: could not pad a track's end", ex); }
        writer.Dispose();
        writer = null;
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
        _stoppedAt = _clockOffset + _clock.Elapsed;
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
        if (_plan != null && (_micClock?.Gaps > 0 || _loopClock?.Gaps > 0))
            HuginnLog.Write($"meeting {_plan.Stamp}: audio that did not arrive was filled with silence: " +
                            $"microphone {_micClock?.Padded.TotalSeconds:F1} s in {_micClock?.Gaps} place(s), " +
                            $"system audio {_loopClock?.Padded.TotalSeconds:F1} s in {_loopClock?.Gaps} place(s)");
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
