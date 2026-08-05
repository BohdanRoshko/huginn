import Foundation

/// Runs the whisper-cli binary bundled inside the app. Keeping transcription
/// behind a subprocess boundary (rather than linking libwhisper) means the
/// exact command and its output land in the log, which is the only way to
/// diagnose a failure on a machine we cannot attach a debugger to.
enum Transcriber {

    static func transcribe(url: URL, speaker: Segment.Speaker) throws -> [Segment] {
        guard let binary = Paths.whisperBinary else {
            throw HuginnError.transcription(
                "whisper-cli is missing from the app bundle. Re-download Huginn."
            )
        }
        guard FileManager.default.fileExists(atPath: Paths.modelFile.path) else {
            throw HuginnError.model("the speech model has not finished downloading yet")
        }

        let outputBase = url.deletingPathExtension()
        let jsonURL = URL(fileURLWithPath: outputBase.path + ".json")
        try? FileManager.default.removeItem(at: jsonURL)

        let process = Process()
        process.executableURL = binary
        process.arguments = [
            "-m", Paths.modelFile.path,
            "-f", url.path,
            "-l", Config.language,
            "-t", String(max(4, ProcessInfo.processInfo.activeProcessorCount - 2)),
            "-oj",
            "-of", outputBase.path,
            "-np"
        ]

        let pipe = Pipe()
        process.standardOutput = pipe
        process.standardError = pipe

        try process.run()
        // Drain before waiting so a chatty run cannot fill the pipe and deadlock.
        let output = pipe.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()

        guard process.terminationStatus == 0 else {
            let detail = String(data: output, encoding: .utf8) ?? ""
            log("whisper-cli exit \(process.terminationStatus): \(detail)")
            throw HuginnError.transcription(
                "whisper-cli exited with code \(process.terminationStatus)"
            )
        }

        guard let data = try? Data(contentsOf: jsonURL) else {
            throw HuginnError.transcription("whisper-cli produced no JSON output")
        }
        defer { try? FileManager.default.removeItem(at: jsonURL) }

        return try parse(data, speaker: speaker)
    }

    private static func parse(_ data: Data, speaker: Segment.Speaker) throws -> [Segment] {
        let decoded = try JSONDecoder().decode(WhisperOutput.self, from: data)
        return decoded.transcription.map {
            Segment(speaker: speaker, start: $0.offsets.from, text: $0.text)
        }
    }

    private struct WhisperOutput: Decodable {
        let transcription: [Entry]

        struct Entry: Decodable {
            let offsets: Offsets
            let text: String
        }

        struct Offsets: Decodable {
            /// Milliseconds from the start of the file.
            let from: Int
            let to: Int
        }
    }
}
