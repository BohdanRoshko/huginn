import Foundation

struct Segment {
    enum Speaker: String {
        case me = "Me"
        case them = "Them"
    }

    let speaker: Speaker
    /// Milliseconds from the start of the recording.
    let start: Int
    let text: String
}

enum TranscriptBuilder {
    /// Interleave both tracks by timestamp into the same `[hh:mm:ss] Me/Them:`
    /// shape Munin writes on Windows, so transcripts from either machine read
    /// identically and the same downstream tooling parses both.
    static func build(me: [Segment], them: [Segment], startedAt: Date) -> String {
        let all = (me + them)
            .filter { !$0.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
            .sorted { $0.start < $1.start }

        let header = DateFormatter.headerFormatter.string(from: startedAt)
        var lines = ["Recorded \(header)", ""]

        if all.isEmpty {
            lines.append("(no speech detected)")
            return lines.joined(separator: "\n") + "\n"
        }

        for segment in all {
            let text = segment.text.trimmingCharacters(in: .whitespacesAndNewlines)
            lines.append("[\(clock(segment.start))] \(segment.speaker.rawValue): \(text)")
        }
        return lines.joined(separator: "\n") + "\n"
    }

    static func clock(_ milliseconds: Int) -> String {
        let total = max(0, milliseconds) / 1000
        return String(format: "%02d:%02d:%02d", total / 3600, (total % 3600) / 60, total % 60)
    }
}

private extension DateFormatter {
    static let headerFormatter: DateFormatter = {
        let f = DateFormatter()
        f.dateStyle = .medium
        f.timeStyle = .short
        return f
    }()
}
