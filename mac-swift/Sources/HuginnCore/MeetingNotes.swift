import Foundation

/// The notes typed while a meeting records, as a file: a header line, then `[hh:mm:ss] text` per
/// non-empty line, each stamped with the offset from the recording start at which the line was begun.
/// The same shape Munin writes on Windows, so anything that reads one reads both.
public enum MeetingNotes {
    public static func stamp(seconds: Int) -> String {
        let s = max(0, seconds)
        return String(format: "[%02d:%02d:%02d]", s / 3600, (s % 3600) / 60, s % 60)
    }

    /// Nil when nothing was typed — the caller removes the file rather than writing an empty one.
    public static func render(startedAt: Date, lines: [(seconds: Int, text: String)]) -> String? {
        let kept = lines
            .map { (seconds: $0.seconds, text: $0.text.trimmingCharacters(in: .whitespacesAndNewlines)) }
            .filter { !$0.text.isEmpty }
        guard !kept.isEmpty else { return nil }

        var out = ["# Notes typed during the meeting of \(headerFormatter.string(from: startedAt))", ""]
        for line in kept {
            out.append("\(stamp(seconds: line.seconds)) \(line.text)")
        }
        return out.joined(separator: "\n") + "\n"
    }

    private static let headerFormatter: DateFormatter = {
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd HH:mm"
        f.locale = Locale(identifier: "en_US_POSIX")
        return f
    }()
}
