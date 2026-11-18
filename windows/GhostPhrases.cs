namespace Huginn;

/// <summary>
/// Filters known Whisper hallucinations — the phrases the model invents on short, low-content audio (breath,
/// keyboard noise, room tone that clears the amplitude silence gate). These are YouTube-caption artifacts
/// baked into Whisper's training data. Applied ONLY to short clips (ghosts don't appear in real multi-second
/// dictation) and ONLY on a whole-output exact match, so a genuine dictation is never eaten.
/// </summary>
internal static class GhostPhrases
{
    // Whole-output matches (compared after normalizing: lowercased, surrounding whitespace + trailing
    // sentence punctuation stripped). Deliberately limited to caption-artifact phrases nobody dictates into a
    // text field — plain "thank you" is intentionally NOT here, since it's a legitimate thing to dictate.
    private static readonly HashSet<string> Exact = new(StringComparer.OrdinalIgnoreCase)
    {
        "thanks for watching", "thank you for watching", "thank you for watching this video",
        "please subscribe", "subscribe to my channel", "don't forget to subscribe",
        "like and subscribe", "see you in the next video", "see you next time",
        "[music]", "[applause]", "[silence]", "[blank_audio]",
    };

    // Prefixes that mark a caption-credit ghost regardless of the tail.
    private static readonly string[] Prefixes = { "subtitles by", "transcription by", "captions by" };

    /// <summary>True if <paramref name="text"/> is almost certainly a hallucination, not speech. Gated on a
    /// short clip (<paramref name="clipSeconds"/>) — ghosts don't occur in real multi-second dictation.</summary>
    public static bool IsLikelyGhost(string text, double clipSeconds)
    {
        if (string.IsNullOrWhiteSpace(text) || clipSeconds > 3.0) return false;
        string norm = text.Trim().TrimEnd('.', '!', '?', '…').Trim().ToLowerInvariant();
        if (norm.Length == 0) return false;
        if (Exact.Contains(norm)) return true;
        foreach (var p in Prefixes)
            if (norm.StartsWith(p, StringComparison.Ordinal)) return true;
        return false;
    }
}
