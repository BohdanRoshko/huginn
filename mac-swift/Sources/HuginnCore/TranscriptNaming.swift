import Foundation

/// Where a transcript lives: `{stamp}_{name}.txt` once the user has named the meeting, else the default
/// `{stamp}_transcript.txt`. The stamp prefix keeps the file next to its WAV tracks. Mirrors Munin.
public enum TranscriptNaming {
    public static func fileName(stamp: String, name: String?) -> String {
        let safe = safeFileName(name)
        return safe.isEmpty ? "\(stamp)_transcript.txt" : "\(stamp)_\(safe).txt"
    }

    /// File-safe: path separators and control characters become spaces, runs of whitespace collapse,
    /// leading and trailing spaces and dots go, and the result is capped at 80 characters.
    public static func safeFileName(_ name: String?) -> String {
        guard let name else { return "" }
        let cleaned: [Character] = name.map { ch in
            if ch == "/" || ch == ":" || ch == "\\" { return " " }
            if let scalar = ch.unicodeScalars.first, scalar.value < 32 { return " " }
            return ch
        }
        let collapsed = String(cleaned)
            .split(whereSeparator: { $0.isWhitespace })
            .joined(separator: " ")
        let edge = CharacterSet(charactersIn: " .")
        var trimmed = collapsed.trimmingCharacters(in: edge)
        if trimmed.count > 80 {
            trimmed = String(trimmed.prefix(80)).trimmingCharacters(in: edge)
        }
        return trimmed
    }
}
