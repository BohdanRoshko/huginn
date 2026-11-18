using NAudio.Utils;
using NAudio.Wave;

namespace Huginn;

/// <summary>
/// Captures the microphone at 16 kHz / mono / 16-bit (exactly what Whisper wants) while the
/// push-to-talk key is held. On stop it raises <see cref="RecordingComplete"/> with an
/// in-memory WAV stream, on a background thread.
/// </summary>
internal sealed class AudioRecorder : IDisposable
{
    private static readonly WaveFormat Format = new(16000, 16, 1);

    /// <summary>-1 = Windows default recording device (WAVE_MAPPER); otherwise a WaveIn device index.</summary>
    public int DeviceNumber { get; set; } = -1;

    // Peak 16-bit amplitude below this = a dead/muted mic (essentially digital silence). This is now only a
    // fast "is the mic alive?" pre-check — the real speech-vs-silence decision is the Silero VAD in the
    // Transcriber. Kept very low so whispered/quiet dictation passes through to the VAD instead of being
    // dropped here (the old 500 threshold ate quiet speech).
    private const int SilenceThreshold = 40;

    private WaveInEvent? _waveIn;
    private MemoryStream? _pcm;
    private volatile bool _recording;
    private volatile bool _cancelled;
    private bool _captureAnnounced;
    private readonly object _pcmLock = new();

    public event Action<MemoryStream>? RecordingComplete;

    /// <summary>Raised (with the captured peak amplitude) when the clip was essentially silent.</summary>
    public event Action<int>? SilenceDetected;

    /// <summary>Raised when recording stopped with no usable audio (nothing captured, or a sub-200ms tap).
    /// The clip won't be transcribed, but the UI must still reset — otherwise a fast tap-and-release strands
    /// the overlay open. Distinct from <see cref="SilenceDetected"/> (a full-length clip that was silent).</summary>
    public event Action? RecordingAborted;

    /// <summary>Raised per audio buffer (~every 50ms) with the live level 0..1, for the overlay meter.</summary>
    public event Action<float>? LevelChanged;

    /// <summary>Raised on the first captured buffer — the mic is actually listening now.</summary>
    public event Action? CaptureStarted;

    public bool IsRecording => _recording;

    public void Start()
    {
        if (_recording) return;
        lock (_pcmLock) { _pcm = new MemoryStream(); }
        _captureAnnounced = false;
        _cancelled = false;
        // Fall back to the Windows default if a pinned device was unplugged.
        int device = DeviceNumber < WaveInEvent.DeviceCount ? DeviceNumber : -1;
        _waveIn = new WaveInEvent { DeviceNumber = device, WaveFormat = Format, BufferMilliseconds = 50 };
        _waveIn.DataAvailable += OnData;
        _waveIn.RecordingStopped += OnStopped;
        _recording = true;
        _waveIn.StartRecording();
    }

    public void Stop()
    {
        if (!_recording) return;
        _recording = false;
        _waveIn?.StopRecording();
    }

    /// <summary>Stops recording and DISCARDS the audio — raises no RecordingComplete/Silence/Aborted event. Used
    /// when the user cancels a dictation with Escape.</summary>
    public void Cancel()
    {
        if (!_recording) return;
        _cancelled = true;
        _recording = false;
        _waveIn?.StopRecording();
    }

    /// <summary>WAV of recent audio (thread-safe), for the live preview. Null if too short. When
    /// <paramref name="maxSeconds"/> &gt; 0, only the LAST that-many seconds are returned — so a long recording's
    /// preview stays constant-cost (re-transcribing the whole growing clip each tick was O(n²) and stuttered the
    /// UI). The authoritative final pass still transcribes the full clip.</summary>
    public MemoryStream? SnapshotWav(int maxSeconds = 0)
    {
        byte[] pcm;
        lock (_pcmLock)
        {
            if (_pcm == null || _pcm.Length < Format.AverageBytesPerSecond / 3) return null;
            pcm = _pcm.ToArray();
        }
        if (maxSeconds > 0)
        {
            int maxBytes = Format.AverageBytesPerSecond * maxSeconds;
            if (pcm.Length > maxBytes)
            {
                var tail = new byte[maxBytes];
                Array.Copy(pcm, pcm.Length - maxBytes, tail, 0, maxBytes);
                pcm = tail;
            }
        }
        var wav = new MemoryStream();
        using (var writer = new WaveFileWriter(new IgnoreDisposeStream(wav), Format))
            writer.Write(pcm, 0, pcm.Length);
        wav.Position = 0;
        return wav;
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        lock (_pcmLock) { _pcm?.Write(e.Buffer, 0, e.BytesRecorded); }

        if (!_captureAnnounced)
        {
            _captureAnnounced = true;
            CaptureStarted?.Invoke();
        }

        if (LevelChanged != null)
        {
            int peak = 0;
            for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
            {
                int sample = Math.Abs((short)(e.Buffer[i] | (e.Buffer[i + 1] << 8)));
                if (sample > peak) peak = sample;
            }
            LevelChanged(peak / 32768f);
        }
    }

    private static int PeakAmplitude(byte[] pcm16)
    {
        int peak = 0;
        for (int i = 0; i + 1 < pcm16.Length; i += 2)
        {
            int sample = Math.Abs((short)(pcm16[i] | (pcm16[i + 1] << 8)));
            if (sample > peak) peak = sample;
        }
        return peak;
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        var waveIn = _waveIn;
        _waveIn = null;
        if (waveIn != null)
        {
            waveIn.DataAvailable -= OnData;
            waveIn.RecordingStopped -= OnStopped;
            waveIn.Dispose();
        }

        MemoryStream? pcm;
        lock (_pcmLock) { pcm = _pcm; _pcm = null; }
        if (_cancelled) { _cancelled = false; pcm?.Dispose(); return; } // cancelled → discard, no event
        if (pcm == null) { RecordingAborted?.Invoke(); return; }

        byte[] pcmBytes = pcm.ToArray();
        pcm.Dispose();

        // Ignore accidental sub-200ms taps — nothing to transcribe, but still tell the UI to reset.
        if (pcmBytes.Length < Format.AverageBytesPerSecond / 5) { RecordingAborted?.Invoke(); return; }

        // Fast dead-mic reject (muted / wrong device). Quiet-but-real speech clears this low bar and is judged
        // by the VAD downstream; Whisper's silence-hallucinations are caught there + by the ghost-phrase filter.
        int peak = PeakAmplitude(pcmBytes);
        if (peak < SilenceThreshold)
        {
            SilenceDetected?.Invoke(peak);
            return;
        }

        var wav = new MemoryStream();
        using (var writer = new WaveFileWriter(new IgnoreDisposeStream(wav), Format))
        {
            writer.Write(pcmBytes, 0, pcmBytes.Length);
        }
        wav.Position = 0;
        RecordingComplete?.Invoke(wav);
    }

    public void Dispose()
    {
        _waveIn?.Dispose();
        _pcm?.Dispose();
    }
}
