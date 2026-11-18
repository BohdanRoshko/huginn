import AVFoundation

/// Takes PCM buffers in whatever format the source hands us (mic = hardware
/// format, system audio = 48 kHz float stereo) and writes a 16 kHz mono 16-bit
/// WAV, which is the only format whisper.cpp accepts.
///
/// One converter per writer, created lazily on the first buffer because the
/// source format is not known until audio actually flows.
final class AudioTrackWriter {
    private let url: URL
    private let queue: DispatchQueue
    private var file: AVAudioFile?
    private var converter: AVAudioConverter?
    private var sourceFormat: AVAudioFormat?
    private var closed = false

    private(set) var framesWritten: AVAudioFramePosition = 0

    private let targetFormat: AVAudioFormat = {
        // Interleaved Int16 mono. Force-unwrapped: these parameters are always
        // valid, so a nil here means the process is already unusable.
        AVAudioFormat(
            commonFormat: .pcmFormatInt16,
            sampleRate: Config.sampleRate,
            channels: 1,
            interleaved: true
        )!
    }()

    init(url: URL, label: String) {
        self.url = url
        self.queue = DispatchQueue(label: "huginn.writer.\(label)")
    }

    /// Safe to call from any thread; work is serialised onto the writer queue.
    func append(_ buffer: AVAudioPCMBuffer) {
        // Copy before hopping queues — SCStream and AVAudioEngine both reuse
        // their buffers as soon as the callback returns.
        guard let copy = Self.copy(buffer) else { return }
        queue.async { [weak self] in
            self?.write(copy)
        }
    }

    func finish() {
        queue.sync {
            guard !closed else { return }
            closed = true
            flushConverterTail()
            file = nil
        }
    }

    // MARK: - private

    private func write(_ buffer: AVAudioPCMBuffer) {
        guard !closed, buffer.frameLength > 0 else { return }

        do {
            if file == nil {
                try openFile(sourceFormat: buffer.format)
            }
            guard let file else { return }

            // Same format already? Write straight through.
            if converter == nil {
                try file.write(from: buffer)
                framesWritten += AVAudioFramePosition(buffer.frameLength)
                return
            }

            guard let converted = try convert(buffer) else { return }
            if converted.frameLength > 0 {
                try file.write(from: converted)
                framesWritten += AVAudioFramePosition(converted.frameLength)
            }
        } catch {
            log("writer(\(url.lastPathComponent)) failed: \(error.localizedDescription)")
            closed = true
            file = nil
        }
    }

    private func openFile(sourceFormat source: AVAudioFormat) throws {
        let settings: [String: Any] = [
            AVFormatIDKey: kAudioFormatLinearPCM,
            AVSampleRateKey: Config.sampleRate,
            AVNumberOfChannelsKey: 1,
            AVLinearPCMBitDepthKey: 16,
            AVLinearPCMIsFloatKey: false,
            AVLinearPCMIsBigEndianKey: false,
            AVLinearPCMIsNonInterleaved: false
        ]

        file = try AVAudioFile(
            forWriting: url,
            settings: settings,
            commonFormat: .pcmFormatInt16,
            interleaved: true
        )
        sourceFormat = source

        if source == targetFormat {
            converter = nil
        } else {
            guard let c = AVAudioConverter(from: source, to: targetFormat) else {
                throw HuginnError.audio("no converter from \(source) to 16 kHz mono")
            }
            // Downmixing multi-channel system audio to mono; the default matrix
            // is what we want, but be explicit about the sample-rate algorithm.
            c.sampleRateConverterQuality = AVAudioQuality.high.rawValue
            converter = c
        }

        log("writer(\(url.lastPathComponent)) opened; source=\(source.sampleRate)Hz "
            + "ch=\(source.channelCount) converting=\(converter != nil)")
    }

    private func convert(_ buffer: AVAudioPCMBuffer) throws -> AVAudioPCMBuffer? {
        guard let converter, let sourceFormat else { return nil }

        let ratio = targetFormat.sampleRate / sourceFormat.sampleRate
        // +1024 of slack: the resampler can emit slightly more than the naive
        // ratio suggests when it flushes internal state.
        let capacity = AVAudioFrameCount(Double(buffer.frameLength) * ratio) + 1024
        guard let out = AVAudioPCMBuffer(pcmFormat: targetFormat, frameCapacity: capacity) else {
            return nil
        }

        var supplied = false
        var conversionError: NSError?
        let status = converter.convert(to: out, error: &conversionError) { _, outStatus in
            if supplied {
                outStatus.pointee = .noDataNow
                return nil
            }
            supplied = true
            outStatus.pointee = .haveData
            return buffer
        }

        if let conversionError {
            throw conversionError
        }
        switch status {
        case .haveData, .inputRanDry:
            return out
        case .endOfStream, .error:
            return nil
        @unknown default:
            return nil
        }
    }

    /// Drain whatever the resampler is still holding so the tail of the
    /// recording is not silently dropped.
    private func flushConverterTail() {
        guard let converter, let file else { return }
        guard let out = AVAudioPCMBuffer(pcmFormat: targetFormat, frameCapacity: 4096) else { return }

        var error: NSError?
        let status = converter.convert(to: out, error: &error) { _, outStatus in
            outStatus.pointee = .endOfStream
            return nil
        }
        guard error == nil, status == .haveData || status == .endOfStream else { return }
        if out.frameLength > 0 {
            try? file.write(from: out)
            framesWritten += AVAudioFramePosition(out.frameLength)
        }
    }

    private static func copy(_ buffer: AVAudioPCMBuffer) -> AVAudioPCMBuffer? {
        guard let out = AVAudioPCMBuffer(
            pcmFormat: buffer.format,
            frameCapacity: buffer.frameLength
        ) else { return nil }
        out.frameLength = buffer.frameLength

        let channels = Int(buffer.format.channelCount)
        let frames = Int(buffer.frameLength)

        if let src = buffer.floatChannelData, let dst = out.floatChannelData {
            let per = buffer.format.isInterleaved ? frames * channels : frames
            for ch in 0..<(buffer.format.isInterleaved ? 1 : channels) {
                dst[ch].update(from: src[ch], count: per)
            }
        } else if let src = buffer.int16ChannelData, let dst = out.int16ChannelData {
            let per = buffer.format.isInterleaved ? frames * channels : frames
            for ch in 0..<(buffer.format.isInterleaved ? 1 : channels) {
                dst[ch].update(from: src[ch], count: per)
            }
        } else if let src = buffer.int32ChannelData, let dst = out.int32ChannelData {
            let per = buffer.format.isInterleaved ? frames * channels : frames
            for ch in 0..<(buffer.format.isInterleaved ? 1 : channels) {
                dst[ch].update(from: src[ch], count: per)
            }
        } else {
            return nil
        }
        return out
    }
}

enum HuginnError: LocalizedError {
    case audio(String)
    case permission(String)
    case transcription(String)
    case model(String)

    var errorDescription: String? {
        switch self {
        case .audio(let m): return "Audio: \(m)"
        case .permission(let m): return m
        case .transcription(let m): return "Transcription: \(m)"
        case .model(let m): return "Model: \(m)"
        }
    }
}
