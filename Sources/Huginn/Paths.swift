import Foundation

enum Paths {
    /// ~/Library/Application Support/Huginn — not TCC-protected, so the app can
    /// write here without tripping a Files-and-Folders permission prompt.
    static var support: URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        return base.appendingPathComponent("Huginn", isDirectory: true)
    }

    static var recordings: URL { support.appendingPathComponent("Recordings", isDirectory: true) }
    static var models: URL { support.appendingPathComponent("Models", isDirectory: true) }
    static var recentFile: URL { support.appendingPathComponent("recent.json") }
    static var logFile: URL { support.appendingPathComponent("huginn.log") }

    static var modelFile: URL { models.appendingPathComponent(Config.modelFileName) }

    /// The whisper-cli binary CI drops into the app bundle. Falls back to a
    /// Homebrew install so a dev build outside the bundle still runs.
    static var whisperBinary: URL? {
        if let bundled = Bundle.main.url(forAuxiliaryExecutable: "whisper-cli") {
            return bundled
        }
        let resources = Bundle.main.resourceURL?.appendingPathComponent("whisper-cli")
        if let r = resources, FileManager.default.isExecutableFile(atPath: r.path) {
            return r
        }
        for candidate in ["/opt/homebrew/bin/whisper-cli", "/usr/local/bin/whisper-cli"] {
            if FileManager.default.isExecutableFile(atPath: candidate) {
                return URL(fileURLWithPath: candidate)
            }
        }
        return nil
    }

    static func ensureDirectories() {
        for dir in [support, recordings, models] {
            try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        }
    }

    static func stamp(_ date: Date) -> String {
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd_HHmmss"
        f.locale = Locale(identifier: "en_US_POSIX")
        return f.string(from: date)
    }
}

enum Config {
    /// Same model Munin uses on Windows — the quality/speed sweet spot.
    static let modelFileName = "ggml-large-v3-turbo.bin"
    static let modelURL = URL(string:
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo.bin")!
    static let modelApproxBytes: Int64 = 1_624_555_275

    static let language = "en"

    /// Whisper wants 16 kHz mono; both tracks are converted to it on the way to disk.
    static let sampleRate: Double = 16_000

    static let recentLimit = 5
}

func log(_ message: String) {
    let line = "[\(ISO8601DateFormatter().string(from: Date()))] \(message)\n"
    FileHandle.standardError.write(Data(line.utf8))
    guard let data = line.data(using: .utf8) else { return }
    if let handle = try? FileHandle(forWritingTo: Paths.logFile) {
        defer { try? handle.close() }
        _ = try? handle.seekToEnd()
        try? handle.write(contentsOf: data)
    } else {
        try? data.write(to: Paths.logFile)
    }
}
