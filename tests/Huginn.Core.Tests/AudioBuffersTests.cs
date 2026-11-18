using NAudio.Wave;
using Xunit;

namespace Huginn.Tests;

/// <summary>The PCM conversion the Mac capture relies on.</summary>
public class AudioBuffersTests
{
    [Fact]
    public void DownmixesInterleavedStereoInt16()
    {
        short[] pcm = { 16384, 0, -16384, -16384 }; // frame 0: L .5 R 0; frame 1: L -.5 R -.5
        var bytes = new byte[pcm.Length * 2];
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
        var mono = AudioBuffers.ToMono(bytes, frames: 2, channels: 2, isFloat: false, bitsPerChannel: 16, nonInterleaved: false);
        Assert.Equal(0.25f, mono[0], 3);
        Assert.Equal(-0.5f, mono[1], 3);
    }

    [Fact]
    public void DownmixesNonInterleavedFloatPlanes()
    {
        float[] planes = { 1f, 0.5f, 0f, -0.5f }; // L plane [1, .5], R plane [0, -.5]
        var bytes = new byte[planes.Length * 4];
        Buffer.BlockCopy(planes, 0, bytes, 0, bytes.Length);
        var mono = AudioBuffers.ToMono(bytes, frames: 2, channels: 2, isFloat: true, bitsPerChannel: 32, nonInterleaved: true);
        Assert.Equal(new[] { 0.5f, 0f }, mono);
    }

    [Fact]
    public void ResamplesToSixteenKilohertzMono16Bit()
    {
        var one = Enumerable.Range(0, 48000).Select(i => (float)Math.Sin(i * 2 * Math.PI * 440 / 48000) * 0.5f).ToArray();
        using var wav = AudioBuffers.ToWhisperWav(one, one.Length, 48000);
        using var reader = new WaveFileReader(wav);
        Assert.Equal(16000, reader.WaveFormat.SampleRate);
        Assert.Equal(1, reader.WaveFormat.Channels);
        Assert.Equal(16, reader.WaveFormat.BitsPerSample);
        Assert.InRange(reader.TotalTime.TotalSeconds, 0.95, 1.05);
    }

    [Fact]
    public void PeakIsTheLoudestMagnitude()
    {
        Assert.Equal(0.8f, AudioBuffers.Peak(new[] { 0.1f, -0.8f, 0.3f }));
    }
}
