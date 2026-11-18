namespace Huginn;

/// <summary>Cuts a chunk out of an in-memory 16 kHz mono 16-bit WAV as a standalone WAV stream, so one
/// Whisper processor can take a track chunk by chunk. Building a processor per chunk costs a whisper state
/// each time (about two seconds of GPU setup); feeding slices to one processor does not.</summary>
internal static class WavSlice
{
    private const int SampleRate = 16000;
    private const int BlockAlign = 2;            // mono, 16-bit
    private const int BytesPerSecond = SampleRate * BlockAlign;

    /// <summary>The whole WAV as bytes: the MemoryStream's own buffer when it will give it, else a copy.</summary>
    public static ArraySegment<byte> Bytes(MemoryStream wav)
    {
        if (wav.TryGetBuffer(out var seg)) return new ArraySegment<byte>(seg.Array!, seg.Offset, (int)wav.Length);
        return new ArraySegment<byte>(wav.ToArray());
    }

    /// <summary>Offset and length of the data chunk inside a RIFF/WAVE byte array (any chunk layout).</summary>
    public static (int Offset, int Length) FindData(ArraySegment<byte> wav)
    {
        var a = wav.Array!;
        int pos = wav.Offset + 12, end = wav.Offset + wav.Count;
        while (pos + 8 <= end)
        {
            uint id = BitConverter.ToUInt32(a, pos);
            int size = BitConverter.ToInt32(a, pos + 4);
            if (id == 0x61746164 /* data */) return (pos + 8, Math.Min(size, end - pos - 8));
            pos += 8 + size + (size & 1);
        }
        throw new InvalidDataException("no data chunk");
    }

    /// <summary>A standalone WAV holding [start, end) of the track.</summary>
    public static MemoryStream Slice(ArraySegment<byte> wav, int dataOffset, int dataLength, TimeSpan start, TimeSpan end)
    {
        int from = Math.Clamp((int)(start.TotalSeconds * BytesPerSecond) & ~1, 0, dataLength);
        int to = Math.Clamp((int)(end.TotalSeconds * BytesPerSecond) & ~1, from, dataLength);
        int len = to - from;
        var ms = new MemoryStream(44 + len);
        using (var w = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            w.Write(0x46464952u);          // RIFF
            w.Write(36 + len);
            w.Write(0x45564157u);          // WAVE
            w.Write(0x20746d66u);          // fmt
            w.Write(16);
            w.Write((short)1);             // PCM
            w.Write((short)1);             // mono
            w.Write(SampleRate);
            w.Write(BytesPerSecond);
            w.Write((short)BlockAlign);
            w.Write((short)16);
            w.Write(0x61746164u);          // data
            w.Write(len);
            w.Write(wav.Array!, wav.Offset + dataOffset + from, len);
        }
        ms.Position = 0;
        return ms;
    }
}
