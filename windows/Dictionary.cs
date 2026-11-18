namespace Munin;

/// <summary>
/// The user's personal dictionary — domain terms Whisper tends to mis-spell (product names, brands, acronyms).
/// Loaded from %LOCALAPPDATA%\Munin\dictionary.txt (one term per line; lines starting with '#' are comments).
/// Seeded with defaults on first run so the file exists to edit. Fed into Whisper as an initial prompt
/// (spelling bias) and into the cleanup LLM as a spelling-authority list. Never throws — a bad file falls
/// back to the defaults.
/// </summary>
internal static class Dictionary
{
    private static readonly string[] Defaults =
    {
        "Odin", "Munin", "Mimir",
        "OAuth", "Bohdan",
    };

    private static string FilePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Munin", "dictionary.txt");

    /// <summary>Terms from the file (seeded with defaults if the file is absent or empty).</summary>
    public static IReadOnlyList<string> Terms()
    {
        try
        {
            var path = FilePath();
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllLines(path,
                    new[] { "# Munin personal dictionary — one term per line. Lines starting with # are ignored." }
                        .Concat(Defaults));
                return Defaults;
            }
            var terms = File.ReadAllLines(path)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith('#'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return terms.Count > 0 ? terms : Defaults;
        }
        catch { return Defaults; }
    }

    /// <summary>Comma-separated term list for the cleanup LLM's spelling-authority block.</summary>
    public static string VocabularyLine() => string.Join(", ", Terms());

    /// <summary>Initial prompt for Whisper — biases the decoder toward these spellings. whisper.cpp treats it
    /// as prior context, so a short glossary sentence works best.</summary>
    public static string WhisperPrompt()
    {
        var terms = Terms();
        return terms.Count == 0 ? "" : $"Glossary of terms: {string.Join(", ", terms)}.";
    }
}
