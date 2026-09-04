import Foundation

/// One stamp per logical line of a notepad: the offset (seconds from the recording start) at which the
/// line was begun. Kept aligned to the text as it is edited, using the caret to locate where lines were
/// inserted or removed — the same heuristic Munin uses on Windows. Pure, so it is testable without AppKit.
///
/// Offsets are UTF-16 units, which is what NSTextView's `selectedRange()` reports.
public struct LineStamps: Equatable {
    public private(set) var stamps: [Int] = []

    public init() {}

    public var count: Int { stamps.count }

    /// Call after every edit with the full text, the caret's UTF-16 offset, and the current offset in seconds.
    public mutating func textChanged(_ text: String, caretUTF16: Int, now: Int) {
        let newline: UInt16 = 10
        let units = text.utf16
        let lines = units.isEmpty ? 0 : units.filter { $0 == newline }.count + 1
        let clampedCaret = max(0, min(caretUTF16, units.count))
        let caretLine = units.prefix(clampedCaret).filter { $0 == newline }.count

        if lines > stamps.count {
            let add = lines - stamps.count
            let at = max(0, min(caretLine - add + 1, stamps.count))
            stamps.insert(contentsOf: Array(repeating: now, count: add), at: at)
        } else if lines < stamps.count {
            let remove = stamps.count - lines
            let at = max(0, min(caretLine + 1, stamps.count - remove))
            stamps.removeSubrange(at..<(at + remove))
        }
    }

    /// Lines typed before Record carry the recording's first second.
    public mutating func zeroAll() {
        stamps = Array(repeating: 0, count: stamps.count)
    }

    public mutating func removeAll() {
        stamps.removeAll()
    }

    /// The non-empty lines with their stamps; a line with no stamp (text changed outside `textChanged`) gets `now`.
    public func snapshot(_ text: String, now: Int) -> [(seconds: Int, text: String)] {
        var out: [(seconds: Int, text: String)] = []
        for (index, raw) in text.components(separatedBy: "\n").enumerated() {
            let line = raw.trimmingCharacters(in: .whitespacesAndNewlines)
            if line.isEmpty { continue }
            out.append((seconds: index < stamps.count ? stamps[index] : now, text: line))
        }
        return out
    }
}
