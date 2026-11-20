namespace Huginn;

/// <summary>
/// Keeps a recorded track on the clock. A capture device hands over its audio in buffers. When the PC is busy
/// some never arrive, and Windows hands over nothing from the speakers while nothing plays. Written one after
/// another, the buffers then make a track that is shorter than the meeting, and every word after a gap sits
/// earlier than it was said. On 2026-11-20 a 12-minute recording lost 4.8 s of microphone and 9 s of system
/// audio this way, in steps; the speaker names, which follow the clock, landed on the wrong lines.
///
/// This counts what was written against the time that has passed. When a buffer arrives and the track is more
/// than the tolerance behind, it says how much silence to write first.
/// </summary>
internal sealed class TrackClock
{
    private readonly int _bytesPerSecond, _blockAlign;
    private readonly long _toleranceBytes;
    private long _written;

    /// <summary>Silence added so far, and in how many places.</summary>
    public long PaddedBytes { get; private set; }
    public int Gaps { get; private set; }
    public TimeSpan Padded => TimeSpan.FromSeconds(PaddedBytes / (double)_bytesPerSecond);

    /// <param name="tolerance">How far the track may trail the clock before silence goes in. A buffer that is
    /// only late still arrives, so this has to be more than a late buffer: 300 ms unless said otherwise.</param>
    public TrackClock(int bytesPerSecond, int blockAlign, TimeSpan? tolerance = null)
    {
        _bytesPerSecond = bytesPerSecond;
        _blockAlign = Math.Max(1, blockAlign);
        _toleranceBytes = (long)((tolerance ?? TimeSpan.FromMilliseconds(300)).TotalSeconds * bytesPerSecond);
    }

    /// <summary>Bytes of silence to write before a buffer of <paramref name="count"/> bytes that arrived
    /// <paramref name="elapsed"/> after the recording began. Whole sample frames only.</summary>
    public long SilenceBefore(TimeSpan elapsed, int count)
    {
        long gap = (long)(elapsed.TotalSeconds * _bytesPerSecond) - (_written + count);
        long pad = gap > _toleranceBytes ? gap - gap % _blockAlign : 0;
        if (pad > 0) { PaddedBytes += pad; Gaps++; }
        _written += pad + count;
        return pad;
    }

    /// <summary>Bytes of silence that bring the track to its full length when the recording stops.</summary>
    public long SilenceAtEnd(TimeSpan elapsed) => SilenceBefore(elapsed, 0);
}
