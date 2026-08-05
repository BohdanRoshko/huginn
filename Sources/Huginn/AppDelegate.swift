import AppKit
import AVFoundation
import HuginnCore

final class AppDelegate: NSObject, NSApplicationDelegate {
    private var statusItem: NSStatusItem!
    private let menu = NSMenu()

    private let recorder = Recorder()
    private let jobs = JobStore()
    private let downloader = ModelDownloader()

    private var recordingStarted: Date?
    private var tickTimer: Timer?
    private var busy = false

    func applicationDidFinishLaunching(_ notification: Notification) {
        Paths.ensureDirectories()
        log("Huginn launched")

        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        statusItem.menu = menu
        menu.autoenablesItems = false

        jobs.onChange = { [weak self] in self?.rebuildMenu() }
        downloader.onChange = { [weak self] _ in self?.rebuildMenu() }

        // Pull the model down in the background on first launch so the first
        // real meeting is not stuck behind a 1.6 GB download.
        downloader.startIfNeeded()

        updateIcon()
        rebuildMenu()
    }

    func applicationWillTerminate(_ notification: Notification) {
        if recorder.isRecording {
            // Flush the WAVs so the audio survives even though we are quitting
            // before it could be transcribed. MUST be detached: a plain Task
            // would inherit the main actor and deadlock against the wait below.
            let semaphore = DispatchSemaphore(value: 0)
            let recorder = self.recorder
            Task.detached {
                _ = await recorder.stop()
                semaphore.signal()
            }
            _ = semaphore.wait(timeout: .now() + 8)
        }
    }

    // MARK: - actions

    @objc private func toggleRecording() {
        guard !busy else { return }
        if recorder.isRecording {
            stopRecording()
        } else {
            startRecording()
        }
    }

    private func startRecording() {
        busy = true
        rebuildMenu()

        Task { @MainActor in
            do {
                try await recorder.start()
                recordingStarted = Date()
                startTicking()
            } catch {
                present(error)
            }
            busy = false
            updateIcon()
            rebuildMenu()
        }
    }

    private func stopRecording() {
        busy = true
        rebuildMenu()

        Task { @MainActor in
            let capture = await recorder.stop()
            stopTicking()
            recordingStarted = nil
            busy = false
            updateIcon()
            rebuildMenu()

            guard let capture else { return }
            let job = Job(
                id: capture.id,
                startedAt: capture.startedAt,
                status: .transcribing,
                detail: nil,
                transcriptPath: nil
            )
            jobs.upsert(job)

            // Detached so the menu returns to idle at once and the next meeting
            // can start while this one is still being transcribed.
            Task.detached(priority: .utility) { [weak self] in
                await self?.process(capture)
            }
        }
    }

    @objc private func discardRecording() {
        guard recorder.isRecording, !busy else { return }

        let alert = NSAlert()
        alert.messageText = "Discard this recording?"
        alert.informativeText = "The audio will be deleted and nothing will be transcribed."
        alert.addButton(withTitle: "Discard")
        alert.addButton(withTitle: "Keep Recording")
        alert.alertStyle = .warning
        NSApp.activate(ignoringOtherApps: true)
        guard alert.runModal() == .alertFirstButtonReturn else { return }

        busy = true
        rebuildMenu()
        Task { @MainActor in
            await recorder.discard()
            stopTicking()
            recordingStarted = nil
            busy = false
            updateIcon()
            rebuildMenu()
        }
    }

    private func process(_ capture: Recorder.Capture) async {
        do {
            let me = try Transcriber.transcribe(url: capture.meURL, speaker: .me)
            let them = try Transcriber.transcribe(url: capture.themURL, speaker: .them)
            let text = TranscriptBuilder.build(
                me: me,
                them: them,
                startedAt: capture.startedAt,
                warnings: warnings(for: capture)
            )

            // On speakers the mic re-captures the far side, so most of their
            // lines come back as "Me" lines. Without this the transcript reads
            // fluently and is badly wrong about who said what.
            let cleaned = TranscriptReconciler.deEcho(text)

            let base = capture.meURL.deletingLastPathComponent()
            let out = base.appendingPathComponent(Paths.stamp(capture.startedAt) + "_transcript.txt")
            try cleaned.text.write(to: out, atomically: true, encoding: .utf8)

            // Keep the pre-de-echo version only when something was actually
            // dropped, so an over-aggressive removal is recoverable without
            // littering a second file next to every clean recording.
            if cleaned.removedEchoes > 0 {
                let raw = base.appendingPathComponent(
                    Paths.stamp(capture.startedAt) + "_transcript.raw.txt"
                )
                try? text.write(to: raw, atomically: true, encoding: .utf8)
                log("de-echo removed \(cleaned.removedEchoes) of \(cleaned.meLines) Me lines "
                    + "(\(cleaned.themLines) Them lines); raw kept alongside")
            }

            jobs.update(capture.id) {
                $0.status = .done
                $0.transcriptPath = out.path
            }
            log("transcript written -> \(out.lastPathComponent)")
            notifyDone(at: out)
        } catch {
            jobs.update(capture.id) {
                $0.status = .failed
                $0.detail = error.localizedDescription
            }
            log("transcription failed: \(error.localizedDescription)")
        }
    }

    /// An empty track still produces a perfectly readable one-sided transcript,
    /// which is why this is checked explicitly rather than left for someone to
    /// notice. Each case names the permission or device that actually failed.
    private func warnings(for capture: Recorder.Capture) -> [String] {
        var found: [String] = []

        if capture.themFrames == 0 {
            found.append(
                "The other side of the call was NOT captured — this transcript only has your voice. "
                + "Enable Huginn under System Settings > Privacy & Security > "
                + "Screen & System Audio Recording, then quit and reopen Huginn."
            )
        }
        if capture.meFrames == 0 {
            found.append(
                "Your microphone captured nothing. Check the input device, and that Huginn is "
                + "enabled under System Settings > Privacy & Security > Microphone."
            )
        }

        for warning in found {
            log("WARNING: \(warning)")
        }
        return found
    }

    private func notifyDone(at url: URL) {
        DispatchQueue.main.async {
            let alert = NSAlert()
            alert.messageText = "Transcript ready"
            alert.informativeText = url.lastPathComponent
            alert.addButton(withTitle: "Open")
            alert.addButton(withTitle: "Later")
            NSApp.activate(ignoringOtherApps: true)
            if alert.runModal() == .alertFirstButtonReturn {
                NSWorkspace.shared.open(url)
            }
        }
    }

    @objc private func openJob(_ sender: NSMenuItem) {
        guard let path = sender.representedObject as? String else { return }
        NSWorkspace.shared.open(URL(fileURLWithPath: path))
    }

    @objc private func openRecordings() {
        Paths.ensureDirectories()
        NSWorkspace.shared.open(Paths.recordings)
    }

    @objc private func openLog() {
        guard FileManager.default.fileExists(atPath: Paths.logFile.path) else { return }
        NSWorkspace.shared.open(Paths.logFile)
    }

    @objc private func retryModel() {
        downloader.startIfNeeded()
    }

    @objc private func quit() {
        NSApp.terminate(nil)
    }

    // MARK: - menu

    private func rebuildMenu() {
        menu.removeAllItems()

        let status = NSMenuItem(title: statusLine(), action: nil, keyEquivalent: "")
        status.isEnabled = false
        menu.addItem(status)
        menu.addItem(.separator())

        let canRecord = downloader.isReady || recorder.isRecording
        let toggle = NSMenuItem(
            title: recorder.isRecording ? "Stop Recording" : "Start Recording",
            action: #selector(toggleRecording),
            keyEquivalent: "r"
        )
        toggle.target = self
        toggle.isEnabled = !busy && canRecord
        menu.addItem(toggle)

        if recorder.isRecording {
            let discard = NSMenuItem(
                title: "Stop & Discard",
                action: #selector(discardRecording),
                keyEquivalent: ""
            )
            discard.target = self
            discard.isEnabled = !busy
            menu.addItem(discard)
        }

        if case .failed = downloader.state {
            let retry = NSMenuItem(title: "Retry Model Download", action: #selector(retryModel), keyEquivalent: "")
            retry.target = self
            menu.addItem(retry)
        }

        if !jobs.jobs.isEmpty {
            menu.addItem(.separator())
            let header = NSMenuItem(title: "Recent", action: nil, keyEquivalent: "")
            header.isEnabled = false
            menu.addItem(header)

            for job in jobs.jobs {
                let item = NSMenuItem(
                    title: "   \(job.title) — \(job.statusLine)",
                    action: job.transcriptPath == nil ? nil : #selector(openJob(_:)),
                    keyEquivalent: ""
                )
                item.target = self
                item.representedObject = job.transcriptPath
                item.isEnabled = job.transcriptPath != nil
                menu.addItem(item)
            }
        }

        menu.addItem(.separator())

        let folder = NSMenuItem(title: "Open Recordings Folder", action: #selector(openRecordings), keyEquivalent: "")
        folder.target = self
        menu.addItem(folder)

        let logItem = NSMenuItem(title: "Open Log", action: #selector(openLog), keyEquivalent: "")
        logItem.target = self
        menu.addItem(logItem)

        menu.addItem(.separator())

        let quitItem = NSMenuItem(title: "Quit Huginn", action: #selector(quit), keyEquivalent: "q")
        quitItem.target = self
        menu.addItem(quitItem)
    }

    private func statusLine() -> String {
        if recorder.isRecording {
            guard let started = recordingStarted else { return "Recording…" }
            return "● Recording \(elapsed(since: started))"
        }
        if busy { return "Working…" }

        switch downloader.state {
        case .downloading(let fraction):
            return String(format: "Downloading speech model… %.0f%%", fraction * 100)
        case .failed(let message):
            return "Model download failed — \(message)"
        case .missing:
            return "Speech model not downloaded"
        case .ready:
            return jobs.jobs.contains { $0.status == .transcribing }
                ? "Idle — transcribing in background"
                : "Idle"
        }
    }

    private func elapsed(since date: Date) -> String {
        let seconds = Int(Date().timeIntervalSince(date))
        return String(format: "%02d:%02d", seconds / 60, seconds % 60)
    }

    private func updateIcon() {
        let name = recorder.isRecording ? "record.circle.fill" : "waveform"
        let image = NSImage(systemSymbolName: name, accessibilityDescription: "Huginn")
        image?.isTemplate = !recorder.isRecording
        statusItem.button?.image = image
        statusItem.button?.contentTintColor = recorder.isRecording ? .systemRed : nil
    }

    private func startTicking() {
        stopTicking()
        let timer = Timer(timeInterval: 1, repeats: true) { [weak self] _ in
            guard let self, self.menu.numberOfItems > 0 else { return }
            self.menu.item(at: 0)?.title = self.statusLine()
        }
        RunLoop.main.add(timer, forMode: .common)
        tickTimer = timer
    }

    private func stopTicking() {
        tickTimer?.invalidate()
        tickTimer = nil
    }

    private func present(_ error: Error) {
        let alert = NSAlert()
        alert.messageText = "Huginn could not start recording"
        alert.informativeText = error.localizedDescription
        alert.alertStyle = .warning
        alert.addButton(withTitle: "OK")
        NSApp.activate(ignoringOtherApps: true)
        alert.runModal()
    }
}
