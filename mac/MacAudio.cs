using AudioToolbox;
using AVFoundation;
using CoreFoundation;
using CoreMedia;
using Foundation;
using NAudio.Wave;
using ScreenCaptureKit;

namespace Huginn;

/// <summary>Turns a CoreMedia audio buffer into mono floats at its own sample rate.</summary>
internal static class SampleBuffers
{
    public static (float[] Mono, int Rate) ToMono(CMSampleBuffer sb)
    {
        var desc = sb.GetAudioFormatDescription();
        var asbd = desc?.AudioStreamBasicDescription;
        if (asbd == null) return (Array.Empty<float>(), 0);
        var a = asbd.Value;
        int frames = (int)sb.NumSamples;
        using var block = sb.GetDataBuffer();
        if (block == null || frames <= 0) return (Array.Empty<float>(), (int)a.SampleRate);
        var len = (int)block.DataLength;
        var bytes = new byte[len];
        block.CopyDataBytes(0, (nuint)len, bytes); // VERIFY: CMBlockBuffer.CopyDataBytes(nuint, nuint, byte[])
        bool isFloat = (a.FormatFlags & AudioFormatFlags.IsFloat) != 0;
        bool nonInterleaved = (a.FormatFlags & AudioFormatFlags.IsNonInterleaved) != 0;
        var mono = AudioBuffers.ToMono(bytes, frames, a.ChannelsPerFrame, isFloat, a.BitsPerChannel, nonInterleaved);
        return (mono, (int)a.SampleRate);
    }
}

/// <summary>The microphone through AVCaptureSession, so a specific device can be picked by its unique id.</summary>
internal sealed class MicCapture : NSObject, IAVCaptureAudioDataOutputSampleBufferDelegate
{
    private readonly DispatchQueue _queue = new("huginn.mic");
    private AVCaptureSession? _session;

    /// <summary>Mono floats and their sample rate, on the capture queue.</summary>
    public Action<float[], int>? Samples;

    public static IReadOnlyList<(string Id, string Name)> Devices()
    {
        var list = new List<(string, string)>();
        foreach (var d in AVCaptureDevice.DevicesWithMediaType(AVMediaTypes.Audio.GetConstant()!.ToString()))
            list.Add((d.UniqueID, d.LocalizedName));
        return list;
    }

    public void Start(string deviceId)
    {
        var device = deviceId.Length > 0 ? AVCaptureDevice.DeviceWithUniqueID(deviceId) : null;
        device ??= AVCaptureDevice.GetDefaultDevice(AVMediaTypes.Audio);
        if (device == null) throw new InvalidOperationException("No microphone found.");
        var input = AVCaptureDeviceInput.FromDevice(device, out var error);
        if (input == null) throw new InvalidOperationException($"Could not open the microphone: {error?.LocalizedDescription}");
        var session = new AVCaptureSession();
        if (!session.CanAddInput(input)) throw new InvalidOperationException("The microphone cannot be used right now.");
        session.AddInput(input);
        var output = new AVCaptureAudioDataOutput();
        output.SetSampleBufferDelegate(this, _queue);
        if (!session.CanAddOutput(output)) throw new InvalidOperationException("The microphone output cannot be added.");
        session.AddOutput(output);
        session.StartRunning();
        _session = session;
    }

    public void Stop()
    {
        var s = _session;
        _session = null;
        if (s == null) return;
        s.StopRunning();
        s.Dispose();
    }

    [Export("captureOutput:didOutputSampleBuffer:fromConnection:")]
    public void DidOutputSampleBuffer(AVCaptureOutput captureOutput, CMSampleBuffer sampleBuffer, AVCaptureConnection connection)
    {
        try
        {
            var (mono, rate) = SampleBuffers.ToMono(sampleBuffer);
            if (mono.Length > 0 && rate > 0) Samples?.Invoke(mono, rate);
        }
        catch (Exception ex) { HuginnLog.Write("mic: buffer failed", ex); }
        finally { sampleBuffer.Dispose(); }
    }
}

/// <summary>Everything the speakers play, through ScreenCaptureKit. No virtual audio driver needed; the cost is the
/// Screen Recording permission. A screen output must be registered too, or the audio buffers are dropped.</summary>
internal sealed class SystemAudioCapture : NSObject, ISCStreamOutput, ISCStreamDelegate
{
    private readonly DispatchQueue _audioQueue = new("huginn.system-audio");
    private readonly DispatchQueue _videoQueue = new("huginn.screen");
    private SCStream? _stream;

    public Action<float[], int>? Samples;

    public async Task StartAsync()
    {
        SCShareableContent content;
        try
        {
            content = await SCShareableContent.GetShareableContentAsync(false, false); // VERIFY: (excludingDesktopWindows, onScreenWindowsOnly)
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Screen Recording permission is needed to record the other side of the call. Open System Settings → " +
                "Privacy & Security → Screen & System Audio Recording, turn on Huginn, then quit and reopen Huginn. " + ex.Message);
        }
        var display = content.Displays.FirstOrDefault() ?? throw new InvalidOperationException("No display for system audio capture.");
        var filter = new SCContentFilter(display, Array.Empty<SCRunningApplication>(), Array.Empty<SCWindow>(), SCContentFilterOption.Exclude); // VERIFY
        var config = new SCStreamConfiguration
        {
            CapturesAudio = true,
            ExcludesCurrentProcessAudio = true,
            SampleRate = 48000,
            ChannelCount = 2,
            Width = 128,
            Height = 128,
            MinimumFrameInterval = new CMTime(1, 1),
            QueueDepth = 6,
        };
        var stream = new SCStream(filter, config, this);
        if (!stream.AddStreamOutput(this, SCStreamOutputType.Screen, _videoQueue, out var e1))
            throw new InvalidOperationException($"System audio: {e1?.LocalizedDescription}");
        if (!stream.AddStreamOutput(this, SCStreamOutputType.Audio, _audioQueue, out var e2))
            throw new InvalidOperationException($"System audio: {e2?.LocalizedDescription}");
        await stream.StartCaptureAsync();
        _stream = stream;
    }

    public async Task StopAsync()
    {
        var s = _stream;
        _stream = null;
        if (s == null) return;
        try { await s.StopCaptureAsync(); } catch (Exception ex) { HuginnLog.Write("system audio: stop failed", ex); }
        s.Dispose();
    }

    [Export("stream:didOutputSampleBuffer:ofType:")]
    public void DidOutputSampleBuffer(SCStream stream, CMSampleBuffer sampleBuffer, SCStreamOutputType type)
    {
        try
        {
            if (type != SCStreamOutputType.Audio) return; // screen frames exist only so the audio flows
            var (mono, rate) = SampleBuffers.ToMono(sampleBuffer);
            if (mono.Length > 0 && rate > 0) Samples?.Invoke(mono, rate);
        }
        catch (Exception ex) { HuginnLog.Write("system audio: buffer failed", ex); }
        finally { sampleBuffer.Dispose(); }
    }

    [Export("stream:didStopWithError:")]
    public void DidStop(SCStream stream, NSError error) => HuginnLog.Write($"system audio: stream stopped: {error.LocalizedDescription}");
}

/// <summary>Push-to-talk capture on the Mac: the mic into memory, handed over as a 16 kHz WAV on release.</summary>
internal sealed class MacDictationRecorder : IDictationRecorder
{
    private const float SilencePeak = 40f / 32768f; // the same dead-mic bar as Windows
    private readonly object _lock = new();
    private MicCapture? _mic;
    private List<float> _samples = new();
    private int _rate;
    private volatile bool _recording;
    private bool _announced;

    public string Device { get; set; } = "";
    public bool IsRecording => _recording;

    public event Action<MemoryStream>? RecordingComplete;
    public event Action<int>? SilenceDetected;
    public event Action? RecordingAborted;
    public event Action<float>? LevelChanged;
    public event Action? CaptureStarted;

    public void Start()
    {
        if (_recording) return;
        if (!MacPermissions.MicrophoneAllowed())
            throw new InvalidOperationException("Microphone access is off for Huginn (System Settings → Privacy & Security → Microphone).");
        lock (_lock) { _samples = new List<float>(16000 * 30); _rate = 0; }
        _announced = false;
        var mic = new MicCapture();
        mic.Samples = OnSamples;
        _recording = true;
        try { mic.Start(Device); }
        catch { _recording = false; throw; }
        _mic = mic;
    }

    private void OnSamples(float[] mono, int rate)
    {
        if (!_recording) return;
        lock (_lock) { _rate = rate; _samples.AddRange(mono); }
        if (!_announced) { _announced = true; CaptureStarted?.Invoke(); }
        LevelChanged?.Invoke(AudioBuffers.Peak(mono));
    }

    public void Stop()
    {
        if (!_recording) return;
        _recording = false;
        _mic?.Stop();
        _mic = null;
        float[] clip; int rate;
        lock (_lock) { clip = _samples.ToArray(); rate = _rate; _samples = new(); }
        _ = Task.Run(() => Finish(clip, rate));
    }

    private void Finish(float[] clip, int rate)
    {
        // A sub-200 ms tap: nothing to transcribe, but the UI must still reset.
        if (rate == 0 || clip.Length < rate / 5) { RecordingAborted?.Invoke(); return; }
        var peak = AudioBuffers.Peak(clip);
        if (peak < SilencePeak) { SilenceDetected?.Invoke((int)(peak * 32768)); return; }
        RecordingComplete?.Invoke(AudioBuffers.ToWhisperWav(clip, clip.Length, rate));
    }

    public void Cancel()
    {
        if (!_recording) return;
        _recording = false;
        _mic?.Stop();
        _mic = null;
        lock (_lock) _samples = new();
    }

    public MemoryStream? SnapshotWav(int maxSeconds)
    {
        float[] tail; int rate;
        lock (_lock)
        {
            rate = _rate;
            if (rate == 0 || _samples.Count < rate / 3) return null;
            int take = maxSeconds > 0 ? Math.Min(_samples.Count, rate * maxSeconds) : _samples.Count;
            tail = _samples.GetRange(_samples.Count - take, take).ToArray();
        }
        return AudioBuffers.ToWhisperWav(tail, tail.Length, rate);
    }

    public void Dispose() => Cancel();
}

/// <summary>Two-track meeting capture on the Mac: the mic is "Me", ScreenCaptureKit's system audio is "Them". Each
/// track is written as mono float WAV at its own rate; the core resamples when it transcribes.</summary>
internal sealed class MacMeetingRecorder : IMeetingRecorder
{
    private readonly object _lock = new();
    private MicCapture? _mic;
    private SystemAudioCapture? _system;
    private WaveFileWriter? _meWriter, _themWriter;
    private MeetingCapture? _plan;
    private volatile bool _recording;
    private Task _systemStop = Task.CompletedTask;

    public string Device { get; set; } = "";
    public bool IsRecording => _recording;
    public event Action<float>? LevelChanged;

    public void Start(MeetingCapture plan)
    {
        if (_recording) return;
        if (!MacPermissions.MicrophoneAllowed())
            throw new InvalidOperationException("Microphone access is needed. Open System Settings → Privacy & Security → Microphone and turn on Huginn.");
        _plan = plan;
        var mic = new MicCapture();
        mic.Samples = (mono, rate) => Write(ref _meWriter, plan.MicPath, mono, rate);
        var system = new SystemAudioCapture();
        system.Samples = (mono, rate) => Write(ref _themWriter, plan.LoopPath, mono, rate);
        mic.Start(Device);
        try
        {
            // The engine calls Start on the main thread and expects it to throw on failure, so wait here; the
            // ScreenCaptureKit calls complete on their own queues, not the main one.
            Task.Run(system.StartAsync).GetAwaiter().GetResult();
        }
        catch
        {
            mic.Stop();
            CloseWriters();
            throw;
        }
        _mic = mic;
        _system = system;
        _recording = true;
    }

    private void Write(ref WaveFileWriter? writer, string path, float[] mono, int rate)
    {
        lock (_lock)
        {
            if (!_recording && writer == null && _plan == null) return;
            writer ??= new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(rate, 1));
            writer.WriteSamples(mono, 0, mono.Length);
        }
        LevelChanged?.Invoke(AudioBuffers.Peak(mono));
    }

    public void Stop()
    {
        if (!_recording) return;
        _recording = false;
        _mic?.Stop();
        _mic = null;
        var system = _system;
        _system = null;
        _systemStop = system == null ? Task.CompletedTask : Task.Run(system.StopAsync);
    }

    public async Task<MeetingCapture?> FinalizeAsync(CancellationToken ct = default)
    {
        await Task.WhenAny(_systemStop, Task.Delay(5000, ct)).ConfigureAwait(false);
        CloseWriters();
        var plan = _plan;
        _plan = null;
        return plan;
    }

    private void CloseWriters()
    {
        lock (_lock)
        {
            _meWriter?.Dispose(); _meWriter = null;
            _themWriter?.Dispose(); _themWriter = null;
        }
    }

    public void Dispose()
    {
        try { Stop(); } catch { /* best effort */ }
        CloseWriters();
    }
}
