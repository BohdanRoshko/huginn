namespace Huginn;

/// <summary>
/// The user's personal dictionary — domain terms Whisper tends to mis-spell (product names, brands, acronyms).
/// Loaded from %LOCALAPPDATA%\Huginn\dictionary.txt (one term per line; lines starting with '#' are comments).
/// Seeded with defaults on first run so the file exists to edit. Fed into Whisper as an initial prompt
/// (spelling bias) and into the cleanup LLM as a spelling-authority list. Never throws — a bad file falls
/// back to the defaults.
/// </summary>
internal static class Dictionary
{
    private static readonly string[] Defaults =
    {
        "Odin", "Huginn", "Mimir",
        "OAuth", "Bohdan",
    };

    private static string FilePath() => Paths.File("dictionary.txt");

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
                    new[] { "# Huginn personal dictionary — one term per line. Lines starting with # are ignored." }
                        .Concat(Defaults));
                return Defaults;
            }
            return TermsOf(File.ReadAllLines(path));
        }
        catch { return Defaults; }
    }

    /// <summary>The terms in a dictionary file's lines (the defaults when it holds none). A job from another computer
    /// brings its own dictionary file, read through here.</summary>
    public static IReadOnlyList<string> TermsOf(IEnumerable<string> lines)
    {
        var terms = lines
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return terms.Count > 0 ? terms : Defaults;
    }

    /// <summary>Comma-separated term list for the cleanup LLM's spelling-authority block.</summary>
    public static string VocabularyLine() => string.Join(", ", Terms());

    /// <summary>Initial prompt for Whisper — biases the decoder toward these spellings. whisper.cpp treats it
    /// as prior context, so a short glossary sentence works best.</summary>
    public static string WhisperPrompt() => WhisperPrompt(Terms());

    public static string WhisperPrompt(IReadOnlyList<string> terms) =>
        terms.Count == 0 ? "" : $"Glossary of terms: {string.Join(", ", terms)}.";
}
