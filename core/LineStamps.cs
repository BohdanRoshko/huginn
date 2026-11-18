namespace Huginn;

/// <summary>One stamp per logical line of the notes pad: the offset from the recording start at which the line was
/// begun. Kept aligned as the text is edited, using the caret to find where lines were inserted or removed.</summary>
internal sealed class LineStamps
{
    private readonly List<TimeSpan> _stamps = new();

    public int Count => _stamps.Count;
    public IReadOnlyList<TimeSpan> Stamps => _stamps;

    /// <summary>Call after every edit with the full text, the caret's offset in it, and the current offset.</summary>
    public void TextChanged(string text, int caret, TimeSpan now)
    {
        int n = text.Length == 0 ? 0 : CountNewlines(text, text.Length) + 1;
        int caretLine = CountNewlines(text, Math.Clamp(caret, 0, text.Length));
        if (n > _stamps.Count)
        {
            int add = n - _stamps.Count;
            int at = Math.Clamp(caretLine - add + 1, 0, _stamps.Count);
            for (int k = 0; k < add; k++) _stamps.Insert(at, now);
        }
        else if (n < _stamps.Count)
        {
            int remove = _stamps.Count - n;
            int at = Math.Clamp(caretLine + 1, 0, _stamps.Count - remove);
            _stamps.RemoveRange(at, remove);
        }
    }

    /// <summary>Lines typed before Record carry the recording's first second.</summary>
    public void ZeroAll()
    {
        for (int i = 0; i < _stamps.Count; i++) _stamps[i] = TimeSpan.Zero;
    }

    public void Clear() => _stamps.Clear();

    /// <summary>The non-empty lines with their stamps; a line with no stamp gets <paramref name="now"/>.</summary>
    public IReadOnlyList<(TimeSpan at, string text)> Snapshot(string text, TimeSpan now)
    {
        var lines = text.Split('\n');
        var list = new List<(TimeSpan, string)>();
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r').Trim();
            if (line.Length == 0) continue;
            list.Add((i < _stamps.Count ? _stamps[i] : now, line));
        }
        return list;
    }

    private static int CountNewlines(string s, int upTo)
    {
        int c = 0;
        for (int i = 0; i < upTo; i++) if (s[i] == '\n') c++;
        return c;
    }
}
