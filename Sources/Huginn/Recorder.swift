import AVFoundation
import ScreenCaptureKit

/// Two-track capture, mirroring Munin's Windows design:
///   mic       -> "Me"    (your voice, labelled with zero ML)
///   system    -> "Them"  (everyone else, since they arrive through the speakers)
///
/// On macOS the system-audio side is ScreenCaptureKit, which means no virtual
/// audio driver (BlackHole/Loopback) and no Audio MIDI Setup — but it does sit
/// behind the Screen Recording permission.
final class Recorder: NSObject {
    struct Capture {
        let id: UUID
        let startedAt: Date
        let meURL: URL
        let themURL: URL
        let meFrames: AVAudioFramePosition
        let themFrames: AVAudioFramePosition
    }

    private let engine = AVAudioEngine()
    private var stream: SCStream?
    private var meWriter: AudioTrackWriter?
    private var themWriter: AudioTrackWriter?
    private let audioQueue = DispatchQueue(label: "huginn.scstream.audio")
    private let videoQueue = DispatchQueue(label: "huginn.scstream.video")

    private var currentID: UUID?
    private(set) var startedAt: Date?
    private var meURL: URL?
    private var themURL: URL?

    private(set) var isRecording = false

    // MARK: - permissions

    static func requestMicrophoneAccess() async -> Bool {
        switch AVCaptureDevice.authorizationStatus(for: .audio) {
        case .authorized:
            return true
        case .notDetermined:
            return await AVCaptureDevice.requestAccess(for: .audio)
        default:
            return false
        }
    }

    /// Touching SCShareableContent is what triggers (or fails) the Screen
    /// Recording grant. There is no separate "system audio" permission.
    static func screenRecordingContent() async throws -> SCShareableContent {
        do {
            return try await SCShareableContent.excludingDesktopWindows(
                false,
                onScreenWindowsOnly: false
            )
        } catch {
            throw HuginnError.permission(
                "Screen Recording permission is required to capture the other side of the call.\n\n"
                + "Open System Settings > Privacy & Security > Screen & System Audio Recording, "
                + "enable Huginn, then quit and reopen Huginn."
            )
        }
    }

    // MARK: - lifecycle

    func start() async throws {
        guard !isRecording else { return }

        guard await Self.requestMicrophoneAccess() else {
            throw HuginnError.permission(
                "Microphone access is required.\n\n"
                + "Open System Settings > Privacy & Security > Microphone and enable Huginn."
            )
        }

        let content = try await Self.screenRecordingContent()
        guard let display = content.displays.first else {
            throw HuginnError.audio("no display available for system-audio capture")
        }

        Paths.ensureDirectories()
        let now = Date()
        let base = Paths.stamp(now)
        let me = Paths.recordings.appendingPathComponent("\(base)_me.wav")
        let them = Paths.recordings.appendingPathComponent("\(base)_them.wav")

        let meW = AudioTrackWriter(url: me, label: "me")
        let themW = AudioTrackWriter(url: them, label: "them")
        meWriter = meW
        themWriter = themW

        try startMic(into: meW)
        do {
            try await startSystemAudio(display: display, into: themW)
        } catch {
            stopMic()
            meW.finish()
            themW.finish()
            meWriter = nil
            themWriter = nil
            throw error
        }

        currentID = UUID()
        startedAt = now
        meURL = me
        themURL = them
        isRecording = true
        log("recording started -> \(base)")
    }

    /// Returns the finished capture. Deliberately fast: transcription is the
    /// caller's problem, so the UI can go back to idle and start another
    /// meeting while the previous one is still processing.
    func stop() async -> Capture? {
        guard isRecording,
              let id = currentID,
              let startedAt,
              let meURL,
              let themURL else { return nil }

        isRecording = false
        stopMic()

        if let stream {
            do { try await stream.stopCapture() }
            catch { log("stopCapture failed: \(error.localizedDescription)") }
        }
        stream = nil

        meWriter?.finish()
        themWriter?.finish()
        let meFrames = meWriter?.framesWritten ?? 0
        let themFrames = themWriter?.framesWritten ?? 0
        meWriter = nil
        themWriter = nil

        self.currentID = nil
        self.startedAt = nil
        self.meURL = nil
        self.themURL = nil

        log("recording stopped; me=\(meFrames) frames them=\(themFrames) frames")

        return Capture(
            id: id,
            startedAt: startedAt,
            meURL: meURL,
            themURL: themURL,
            meFrames: meFrames,
            themFrames: themFrames
        )
    }

    /// Stop and delete — for a meeting you decide you don't want to keep.
    func discard() async {
        guard let capture = await stop() else { return }
        try? FileManager.default.removeItem(at: capture.meURL)
        try? FileManager.default.removeItem(at: capture.themURL)
        log("recording discarded")
    }

    // MARK: - mic

    private func startMic(into writer: AudioTrackWriter) throws {
        let input = engine.inputNode
        let format = input.outputFormat(forBus: 0)
        guard format.sampleRate > 0 else {
            throw HuginnError.audio("microphone reported a zero sample rate — no input device?")
        }

        input.installTap(onBus: 0, bufferSize: 4096, format: format) { buffer, _ in
            writer.append(buffer)
        }

        engine.prepare()
        do {
            try engine.start()
        } catch {
            input.removeTap(onBus: 0)
            throw HuginnError.audio("could not start the microphone: \(error.localizedDescription)")
        }
    }

    private func stopMic() {
        if engine.isRunning {
            engine.stop()
        }
        engine.inputNode.removeTap(onBus: 0)
    }

    // MARK: - system audio

    private func startSystemAudio(display: SCDisplay, into writer: AudioTrackWriter) async throws {
        let filter = SCContentFilter(display: display, excludingApplications: [], exceptingWindows: [])

        let config = SCStreamConfiguration()
        config.capturesAudio = true
        config.excludesCurrentProcessAudio = true
        config.sampleRate = 48_000
        config.channelCount = 2
        // A .screen output is registered below even though we only want audio,
        // so keep the video side as cheap as legally possible: tiny frame, 1 fps.
        config.width = 128
        config.height = 128
        config.minimumFrameInterval = CMTime(value: 1, timescale: 1)
        config.queueDepth = 6

        let s = SCStream(filter: filter, configuration: config, delegate: self)
        do {
            // Registering .screen is NOT optional. An audio-only stream drops
            // its buffers with "stream output NOT found. Dropping frame" —
            // the screen frames are discarded in the callback.
            try s.addStreamOutput(self, type: .screen, sampleHandlerQueue: videoQueue)
            try s.addStreamOutput(self, type: .audio, sampleHandlerQueue: audioQueue)
            try await s.startCapture()
        } catch {
            throw HuginnError.audio(
                "could not start system-audio capture: \(error.localizedDescription)\n\n"
                + "This usually means Screen Recording permission is off. Enable Huginn under "
                + "System Settings > Privacy & Security > Screen & System Audio Recording, "
                + "then quit and reopen Huginn."
            )
        }
        stream = s
    }
}

extension Recorder: SCStreamOutput {
    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        // Screen frames arrive only because registering that output is what
        // keeps audio flowing; nothing is done with them and nothing is stored.
        guard type == .audio, sampleBuffer.isValid, sampleBuffer.numSamples > 0 else { return }
        guard let buffer = sampleBuffer.asPCMBuffer else { return }
        themWriter?.append(buffer)
    }
}

extension Recorder: SCStreamDelegate {
    func stream(_ stream: SCStream, didStopWithError error: Error) {
        log("SCStream stopped with error: \(error.localizedDescription)")
    }
}

extension CMSampleBuffer {
    /// SCStream hands over float PCM in a CMSampleBuffer; AVAudioPCMBuffer is
    /// what the writer's converter understands.
    var asPCMBuffer: AVAudioPCMBuffer? {
        try? withAudioBufferList { list, _ -> AVAudioPCMBuffer? in
            guard var asbd = formatDescription?.audioStreamBasicDescription else { return nil }
            guard let format = AVAudioFormat(streamDescription: &asbd) else { return nil }
            return AVAudioPCMBuffer(pcmFormat: format, bufferListNoCopy: list.unsafePointer)
        }
    }
}
