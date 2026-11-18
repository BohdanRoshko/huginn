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

    private lazy var notes: NotesWindow = makeNotesWindow()
    private var notesMode = NotesMode.load()
    /// The finished meeting the notes window's status and footer buttons refer to.
    private var notesJobID: UUID?

    func applicationDidFinishLaunching(_ notification: Notification) {
        Paths.ensureDirectories()
        log("Huginn launched")

        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        statusItem.menu = menu
        menu.autoenablesItems = false

        jobs.onChange = { [weak self] in self?.rebuildMenu() }
        downloader.onChange = { [weak self] _ in self?.rebuildMenu() }

        // Pull the model down in the background on first launch so the first
        // real meeting is not stuck behind a 1.6 GB download. (Skipped when the
        // model file is already present, which is how the UI probe below avoids
        // fetching 1.6 GB on a CI runner.)
        downloader.startIfNeeded()

        updateIcon()
        rebuildMenu()
        if notesMode == .always { notes.show() }

        // CI-only: drive a whole record/transcribe cycle and exit, to find out
        // whether audio capture actually works on this machine.
        if ProcessInfo.processInfo.environment["HUGINN_SELFTEST"] == "1" {
            let recorder = self.recorder
            Task { await SelfTest.run(recorder: recorder) }
            return
        }

        // CI-only: open the menu so `screencapture` has something to photograph.
        // There is no other way to see this UI without a Mac in hand — a menu
        // bar app cannot be driven from a script without Accessibility consent,
        // which CI cannot grant.
        let probe = ProcessInfo.processInfo.environment["HUGINN_UI_PROBE"]
        if probe == "1" {
            DispatchQueue.main.asyncAfter(deadline: .now() + 2) { [weak self] in
                self?.statusItem.button?.performClick(nil)
            }
        } else if probe == "recording" {
            // Photograph the RECORDING state. The menu bar clock is the whole
            // point of this indicator, and it only exists while recording, so
            // an idle screenshot can never show whether it works.
            Task { @MainActor [weak self] in
                guard let self else { return }
                try? await self.recorder.start()
                self.recordingStarted = self.recorder.startedAt ?? Date()
                self.startTicking()
                self.updateIcon()
                self.rebuildMenu()
                try? await Task.sleep(nanoseconds: 3_000_000_000)
                self.statusItem.button?.performClick(nil)
            }
        } else if probe == "notes" {
            // Photograph the NOTES WINDOW as it looks mid-meeting: mode forced on, a recording running so the
            // header shows the clock and Stop / Discard, and two lines typed the way a user would.
            notesMode = .always
            Task { @MainActor [weak self] in
                guard let self else { return }
                try? await self.recorder.start()
                self.recordingStarted = self.recorder.startedAt ?? Date()
                self.startTicking()
                self.updateIcon()
                self.rebuildMenu()
                self.notes.startRecording(at: self.recordingStarted ?? Date())
                self.notes.focus()
                try? await Task.sleep(nanoseconds: 1_500_000_000)
                self.notes.probeType("decision: ship the smaller version Friday\n")
                try? await Task.sleep(nanoseconds: 2_000_000_000)
                self.notes.probeType("ask Priya about churn")
                self.saveNotes()
            }
        }
    }

    func applicationWillTerminate(_ notification: Notification) {
        if recorder.isRecording {
            saveNotes()
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
                // The recorder's own start is the stamp on the WAVs, so the notes file must use it too.
                recordingStarted = recorder.startedAt ?? Date()
                startTicking()
                if notesMode != .off {
                    notes.startRecording(at: recordingStarted ?? Date())
                    notes.focus()
                    saveNotes() // an agenda typed before Start joins the file at [00:00:00]
                }
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
            saveNotes() // the last edits, before the recorder stops accepting them
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
            if notesMode != .off {
                notesJobID = capture.id
                notes.endRecording()
            }

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
        let stamp = recorder.startedAt.map(Paths.stamp)
        Task { @MainActor in
            await recorder.discard()
            if let stamp { try? FileManager.default.removeItem(at: Paths.notesURL(stamp: stamp)) }
            if notesMode != .off { notes.discarded() }
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
            // A name given while this was still transcribing applies now.
            let name = jobs.job(capture.id)?.name
            let out = base.appendingPathComponent(
                TranscriptNaming.fileName(stamp: Paths.stamp(capture.startedAt), name: name)
            )
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
            notesFollow(capture.id) { $0.setStatus("Transcript ready"); $0.setTranscriptReady() }
            notifyDone(at: out)
        } catch {
            jobs.update(capture.id) {
                $0.status = .failed
                $0.detail = error.localizedDescription
            }
            log("transcription failed: \(error.localizedDescription)")
            notesFollow(capture.id) { $0.setStatus("Failed — \(error.localizedDescription)") }
        }
    }

    /// Update the notes window only when it is still showing THIS meeting's notes.
    private func notesFollow(_ id: UUID, _ apply: @escaping (NotesWindow) -> Void) {
        DispatchQueue.main.async { [weak self] in
            guard let self, self.notesMode != .off, self.notesJobID == id else { return }
            apply(self.notes)
        }
    }

    // MARK: - notes

    private func makeNotesWindow() -> NotesWindow {
        let w = NotesWindow()
        w.onStop = { [weak self] in self?.toggleRecording() }
        w.onDiscard = { [weak self] in self?.discardRecording() }
        w.onNotesChanged = { [weak self] in self?.saveNotes() }
        w.transcriptResolver = { [weak self] in
            guard let self, let id = self.notesJobID, let path = self.jobs.job(id)?.transcriptPath else { return nil }
            return URL(fileURLWithPath: path)
        }
        return w
    }

    /// Rewrite {stamp}_notes.md from the pad — on every debounced change, before Stop, and on quit — so a
    /// crash keeps everything typed and the file holds exactly what was on screen.
    private func saveNotes() {
        guard notesMode != .off, recorder.isRecording, let started = recorder.startedAt else { return }
        let url = Paths.notesURL(stamp: Paths.stamp(started))
        if let text = MeetingNotes.render(startedAt: started, lines: notes.snapshot()) {
            try? text.write(to: url, atomically: true, encoding: .utf8)
        } else {
            try? FileManager.default.removeItem(at: url)
        }
    }

    @objc private func setNotesMode(_ sender: NSMenuItem) {
        guard let raw = sender.representedObject as? String, let mode = NotesMode(rawValue: raw) else { return }
        notesMode = mode
        mode.save()
        switch mode {
        case .always: notes.show()
        case .off: notes.hide()
        case .onRecord: break // it opens with the next recording
        }
        rebuildMenu()
    }

    @objc private func showNotes() {
        notes.focus()
    }

    // MARK: - rename

    private func job(from sender: NSMenuItem) -> Job? {
        guard let raw = sender.representedObject as? String, let id = UUID(uuidString: raw) else { return nil }
        return jobs.job(id)
    }

    @objc private func renameJob(_ sender: NSMenuItem) {
        guard let job = job(from: sender) else { return }
        let alert = NSAlert()
        alert.messageText = "Rename meeting"
        alert.informativeText = "The transcript file takes this name too."
        let field = NSTextField(frame: NSRect(x: 0, y: 0, width: 280, height: 24))
        field.stringValue = job.name ?? ""
        field.placeholderString = job.timeTitle
        alert.accessoryView = field
        alert.addButton(withTitle: "Rename")
        alert.addButton(withTitle: "Cancel")
        alert.window.initialFirstResponder = field
        NSApp.activate(ignoringOtherApps: true)
        guard alert.runModal() == .alertFirstButtonReturn else { return }
        let typed = field.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
        apply(name: typed.isEmpty ? nil : typed, to: job)
    }

    /// Store the name and move the transcript to match, when it exists. A name given while the meeting
    /// is still transcribing applies when the transcript is written (see `process`).
    private func apply(name: String?, to job: Job) {
        var newPath = job.transcriptPath
        if let current = job.transcriptPath, FileManager.default.fileExists(atPath: current) {
            var target = Paths.transcriptURL(stamp: job.stamp, name: name)
            if target.path != current {
                if FileManager.default.fileExists(atPath: target.path) {
                    let base = target.deletingPathExtension().lastPathComponent
                    target = target.deletingLastPathComponent().appendingPathComponent("\(base) (2).txt")
                }
                do {
                    try FileManager.default.moveItem(at: URL(fileURLWithPath: current), to: target)
                    newPath = target.path
                    log("transcript renamed -> \(target.lastPathComponent)")
                } catch {
                    log("transcript rename failed: \(error.localizedDescription)")
                }
            }
        }
        jobs.update(job.id) {
            $0.name = name
            $0.transcriptPath = newPath
        }
    }

    @objc private func revealJob(_ sender: NSMenuItem) {
        guard let path = job(from: sender)?.transcriptPath else { return }
        NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: path)])
    }

    @objc private func copyJobPath(_ sender: NSMenuItem) {
        guard let path = job(from: sender)?.transcriptPath else { return }
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(path, forType: .string)
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
        guard let path = job(from: sender)?.transcriptPath else { return }
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
                let item = NSMenuItem(title: "   \(job.menuTitle)", action: nil, keyEquivalent: "")
                item.submenu = jobMenu(for: job)
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

        let notesItem = NSMenuItem(title: "Notes", action: nil, keyEquivalent: "")
        notesItem.submenu = notesModeMenu()
        menu.addItem(notesItem)

        menu.addItem(.separator())

        let quitItem = NSMenuItem(title: "Quit Huginn", action: #selector(quit), keyEquivalent: "q")
        quitItem.target = self
        menu.addItem(quitItem)
    }

    /// Per-meeting actions. A submenu rather than a click action, so Rename sits next to Open.
    private func jobMenu(for job: Job) -> NSMenu {
        let sub = NSMenu()
        sub.autoenablesItems = false
        let hasTranscript = job.transcriptPath.map { FileManager.default.fileExists(atPath: $0) } ?? false
        let id = job.id.uuidString

        let entries: [(String, Selector, Bool)] = [
            ("Open Transcript", #selector(openJob(_:)), hasTranscript),
            ("Rename…", #selector(renameJob(_:)), true),
            ("Show in Finder", #selector(revealJob(_:)), hasTranscript),
            ("Copy Path", #selector(copyJobPath(_:)), hasTranscript),
        ]
        for (title, action, enabled) in entries {
            let item = NSMenuItem(title: title, action: action, keyEquivalent: "")
            item.target = self
            item.representedObject = id
            item.isEnabled = enabled
            sub.addItem(item)
        }
        return sub
    }

    private func notesModeMenu() -> NSMenu {
        let sub = NSMenu()
        sub.autoenablesItems = false
        let options: [(String, NotesMode)] = [
            ("Off", .off),
            ("Always On", .always),
            ("Open When Recording Starts", .onRecord),
        ]
        for (title, mode) in options {
            let item = NSMenuItem(title: title, action: #selector(setNotesMode(_:)), keyEquivalent: "")
            item.target = self
            item.representedObject = mode.rawValue
            item.state = notesMode == mode ? .on : .off
            sub.addItem(item)
        }
        sub.addItem(.separator())
        let show = NSMenuItem(title: "Show Notes Window", action: #selector(showNotes), keyEquivalent: "n")
        show.target = self
        show.isEnabled = notesMode != .off
        sub.addItem(show)
        return sub
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

    /// Rolls over to h:mm:ss past an hour. A forgotten recording showing
    /// "1:47:03" reads as obviously wrong in a way "107:03" does not.
    private func elapsed(since date: Date) -> String {
        let seconds = Int(Date().timeIntervalSince(date))
        if seconds >= 3600 {
            return String(format: "%d:%02d:%02d", seconds / 3600, (seconds % 3600) / 60, seconds % 60)
        }
        return String(format: "%02d:%02d", seconds / 60, seconds % 60)
    }

    private func updateIcon() {
        let name = recorder.isRecording ? "record.circle.fill" : "waveform"
        let image = NSImage(systemSymbolName: name, accessibilityDescription: "Huginn")
        image?.isTemplate = !recorder.isRecording
        statusItem.button?.image = image
        statusItem.button?.contentTintColor = recorder.isRecording ? .systemRed : nil
        updateButtonTitle()
    }

    /// The running clock lives in the MENU BAR, not just inside the menu.
    /// A red dot alone is easy to stop noticing among a dozen other icons —
    /// and it says nothing about how long this has been going. A timer that
    /// visibly climbs past an hour is what actually gets noticed.
    private func updateButtonTitle() {
        guard let button = statusItem.button else { return }
        guard recorder.isRecording, let started = recordingStarted else {
            button.title = ""
            button.imagePosition = .imageOnly
            return
        }
        button.imagePosition = .imageLeading
        button.title = " " + elapsed(since: started)
    }

    private func startTicking() {
        stopTicking()
        let timer = Timer(timeInterval: 1, repeats: true) { [weak self] _ in
            guard let self else { return }
            self.updateButtonTitle()
            guard self.menu.numberOfItems > 0 else { return }
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

/// When the Notes window shows. Off is the default on the Mac; Munin on Windows defaults to always on.
enum NotesMode: String {
    case off
    case always
    case onRecord

    private static let key = "notesMode"

    static func load() -> NotesMode {
        NotesMode(rawValue: UserDefaults.standard.string(forKey: key) ?? "") ?? .off
    }

    func save() {
        UserDefaults.standard.set(rawValue, forKey: NotesMode.key)
    }
}
