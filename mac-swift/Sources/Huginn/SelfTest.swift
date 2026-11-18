import AVFoundation
import Foundation
import HuginnCore

/// CI-only end-to-end check of the one thing nothing else can verify: whether
/// audio actually gets captured.
///
/// Everything else about this app is provable without a Mac in hand — the
/// de-echo has unit tests, the transcription contract is asserted against the
/// real whisper binary, the UI is screenshotted. Capture is the exception,
/// because it needs permissions a user grants interactively and hardware a
/// runner may not have.
///
/// So this drives the whole path itself: start recording, speak through system
/// audio, stop, transcribe, print. The frame counts are the real payload —
/// `themFrames > 0` means ScreenCaptureKit delivered audio, which is the
/// assumption the entire "Them" track rests on.
///
/// Exit codes are distinct so CI can tell a permission wall (which means "only
/// a real user can test this") from a genuine defect.
enum SelfTest {
    private enum Code: Int32 {
        case ok = 0
        case startFailed = 2
        case noCapture = 3
        case transcriptionFailed = 4
        case silentTracks = 5
    }

    static func run(recorder: Recorder) async -> Never {
        note("starting")
        note("microphone authorization = \(describe(AVCaptureDevice.authorizationStatus(for: .audio)))")

        do {
            try await recorder.start()
        } catch {
            note("FAILED to start recording: \(error.localizedDescription)")
            note("if this is a permissions refusal, CI cannot grant it — only a real user can")
            finish(.startFailed)
        }
        note("recording started")

        speak("The quarterly inventory report is ready for review. "
              + "This sentence is playing through system audio.")

        // Let the capture queues drain what was just played.
        try? await Task.sleep(nanoseconds: 2_500_000_000)

        guard let capture = await recorder.stop() else {
            note("recorder returned no capture")
            finish(.noCapture)
        }

        note("me   track: \(capture.meFrames) frames")
        note("them track: \(capture.themFrames) frames   <-- ScreenCaptureKit")

        if capture.themFrames == 0 {
            note("SYSTEM AUDIO WAS NOT CAPTURED — the 'Them' track is empty")
        }
        if capture.meFrames == 0 {
            note("MICROPHONE CAPTURED NOTHING — expected on a runner with no real input device")
        }

        let me = (try? Transcriber.transcribe(url: capture.meURL, speaker: .me)) ?? []
        let them: [Segment]
        do {
            them = try Transcriber.transcribe(url: capture.themURL, speaker: .them)
        } catch {
            note("transcription failed: \(error.localizedDescription)")
            finish(.transcriptionFailed)
        }

        let text = TranscriptBuilder.build(me: me, them: them, startedAt: capture.startedAt)
        let cleaned = TranscriptReconciler.deEcho(text)

        note("de-echo removed \(cleaned.removedEchoes) line(s)")
        note("----- transcript -----")
        for line in cleaned.text.components(separatedBy: "\n") {
            FileHandle.standardError.write(Data(("  " + line + "\n").utf8))
        }
        note("----------------------")

        if me.isEmpty && them.isEmpty {
            note("both tracks produced no speech")
            finish(.silentTracks)
        }
        note("captured audio and produced a transcript")
        finish(.ok)
    }

    /// Plays through the default output device, which is exactly what the
    /// "Them" track is supposed to pick up — a stand-in for the far side.
    private static func speak(_ phrase: String) {
        let say = Process()
        say.executableURL = URL(fileURLWithPath: "/usr/bin/say")
        say.arguments = [phrase]
        do {
            try say.run()
            say.waitUntilExit()
            note("spoke through system audio (exit \(say.terminationStatus))")
        } catch {
            note("could not run /usr/bin/say: \(error.localizedDescription)")
        }
    }

    private static func describe(_ status: AVAuthorizationStatus) -> String {
        switch status {
        case .authorized:    return "authorized"
        case .denied:        return "denied"
        case .restricted:    return "restricted"
        case .notDetermined: return "notDetermined"
        @unknown default:    return "unknown"
        }
    }

    private static func note(_ message: String) {
        let line = "SELFTEST: \(message)\n"
        FileHandle.standardError.write(Data(line.utf8))
        log("SELFTEST: \(message)")
    }

    private static func finish(_ code: Code) -> Never {
        note("exit \(code.rawValue)")
        exit(code.rawValue)
    }
}
