using Xunit;

namespace Huginn.Tests;

/// <summary>The recorder's clock: audio that never arrived becomes silence, so a track stays as long as the meeting.</summary>
public class TrackClockTests
{
    [Fact]
    public void ATrackOnTimeGetsNoSilence()
    {
        var clock = new TrackClock(bytesPerSecond: 32000, blockAlign: 2);
        for (int i = 1; i <= 50; i++)
            Assert.Equal(0, clock.SilenceBefore(TimeSpan.FromMilliseconds(i * 100 + 40), 3200)); // 100 ms buffers, 40 ms late
        Assert.Equal(0, clock.Gaps);
    }

    [Fact]
    public void AudioThatNeverArrivedBecomesSilenceOfTheSameLength()
    {
        var clock = new TrackClock(bytesPerSecond: 32000, blockAlign: 2);
        Assert.Equal(0, clock.SilenceBefore(TimeSpan.FromMilliseconds(100), 3200));
        // Two seconds pass with nothing handed over, then one 100 ms buffer arrives.
        Assert.Equal(64000, clock.SilenceBefore(TimeSpan.FromMilliseconds(2200), 3200));
        Assert.Equal(0, clock.SilenceBefore(TimeSpan.FromMilliseconds(2300), 3200));
        Assert.Equal(1, clock.Gaps);
        Assert.Equal(2.0, clock.Padded.TotalSeconds, 3);
    }

    [Fact]
    public void SilenceIsWholeSampleFramesAndTheEndIsFilled()
    {
        var clock = new TrackClock(bytesPerSecond: 384000, blockAlign: 8); // 48 kHz stereo float
        long pad = clock.SilenceBefore(TimeSpan.FromMilliseconds(1234.567), 38400);
        Assert.True(pad > 0);
        Assert.Equal(0, pad % 8);
        long tail = clock.SilenceAtEnd(TimeSpan.FromSeconds(10));
        Assert.Equal(0, tail % 8);
        Assert.InRange((pad + 38400 + tail) / 384000.0, 9.99, 10.0);
    }
}
