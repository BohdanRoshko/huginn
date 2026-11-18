using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Huginn;

/// <summary>Raw PCM from a capture callback → mono float samples → the 16 kHz mono 16-bit WAV Whisper wants. The Mac
/// capture hands over whatever format the device or ScreenCaptureKit delivers; this turns it into one shape.</summary>
internal static class AudioBuffers
{
    /// <summary>Downmixes one buffer to mono floats. <paramref name="nonInterleaved"/> means each channel is its own
    /// plane, one after the other (how CoreAudio lays out a non-interleaved buffer list in one block).</summary>
    public static float[] ToMono(ReadOnlySpan<byte> data, int frames, int channels, bool isFloat, int bitsPerChannel, bool nonInterleaved)
    {
        if (frames <= 0 || channels <= 0) return Array.Empty<float>();
        int bytes = bitsPerChannel / 8;
        int needed = frames * channels * bytes;
        if (data.Length < needed) frames = data.Length / (channels * bytes);
        var mono = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++)
            {
                int index = nonInterleaved ? c * frames + f : f * channels + c;
                sum += Sample(data, index * bytes, isFloat, bitsPerChannel);
            }
            mono[f] = sum / channels;
        }
        return mono;
    }

    private static float Sample(ReadOnlySpan<byte> d, int at, bool isFloat, int bits)
    {
        if (isFloat && bits == 32) return BitConverter.ToSingle(d.Slice(at, 4));
        if (isFloat && bits == 64) return (float)BitConverter.ToDouble(d.Slice(at, 8));
        return bits switch
        {
            16 => BitConverter.ToInt16(d.Slice(at, 2)) / 32768f,
            32 => BitConverter.ToInt32(d.Slice(at, 4)) / 2147483648f,
            24 => ((d[at] | (d[at + 1] << 8) | (d[at + 2] << 16)) << 8 >> 8) / 8388608f,
            8 => (d[at] - 128) / 128f,
            _ => 0f,
        };
    }

    /// <summary>The loudest sample's magnitude, 0..1.</summary>
    public static float Peak(ReadOnlySpan<float> samples)
    {
        float peak = 0;
        foreach (var s in samples) { var a = Math.Abs(s); if (a > peak) peak = a; }
        return Math.Min(1f, peak);
    }

    /// <summary>A 16 kHz mono 16-bit WAV of the samples (resampled when needed).</summary>
    public static MemoryStream ToWhisperWav(float[] mono, int count, int sampleRate)
    {
        var bytes = new byte[count * 4];
        Buffer.BlockCopy(mono, 0, bytes, 0, bytes.Length);
        using var src = new RawSourceWaveStream(new MemoryStream(bytes), WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1));
        ISampleProvider samples = src.ToSampleProvider();
        if (sampleRate != 16000) samples = new WdlResamplingSampleProvider(samples, 16000);
        var ms = new MemoryStream();
        WaveFileWriter.WriteWavFileToStream(ms, new SampleToWaveProvider16(samples));
        ms.Position = 0;
        return ms;
    }
}
