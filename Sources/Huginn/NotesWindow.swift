import AppKit
import HuginnCore

/// The Notes window: a floating notepad for the meeting being recorded, Huginn's half of the Granola idea
/// (what you write during the call is what matters most). The header carries the running time on the left
/// and, on the right, the same Stop / Discard the menu offers while recording — then, once the finished
/// meeting's transcript exists, Open transcript / Copy path until the next recording starts. The body is a
/// plain text view. Every line is stamped, behind the scenes, with the second of the recording at which it
/// was begun (`LineStamps`); the app saves the snapshot as `{stamp}_notes.md` beside the WAV tracks.
///
/// Record clears the pad only when it still holds the previous meeting's untouched notes; lines typed since
/// (an agenda) carry [00:00:00] and join the recording. The close button hides the window; the Notes
/// setting decides when it comes back.
final class NotesWindow: NSObject, NSWindowDelegate, NSTextViewDelegate {
    var onStop: (() -> Void)?
    var onDiscard: (() -> Void)?
    /// The pad changed (debounced); the app persists `snapshot()`.
    var onNotesChanged: (() -> Void)?
    /// The finished meeting's CURRENT transcript (it moves on rename), or nil when none exists yet.
    var transcriptResolver: (() -> URL?)?

    private let window: NSWindow
    private let statusLabel = NSTextField(labelWithString: "Idle")
    private let hintLabel = NSTextField(labelWithString: "")
    private let stopButton = NSButton(title: "Stop", target: nil, action: nil)
    private let discardButton = NSButton(title: "Discard", target: nil, action: nil)
    private let openButton = NSButton(title: "Open transcript", target: nil, action: nil)
    private let copyButton = NSButton(title: "Copy path", target: nil, action: nil)
    private let textView: NSTextView

    private var stamps = LineStamps()
    private var recording = false
    private var startedAt: Date?
    private var stale = false
    private var transcriptReady = false
    private var status = ""
    private var suppressChange = false
    private var saveTimer: Timer?
    private var clockTimer: Timer?
    private var toastTimer: Timer?

    override init() {
        window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 352, height: 396),
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered,
            defer: false
        )
        let scroll = NSTextView.scrollableTextView()
        textView = scroll.documentView as! NSTextView
        super.init()

        window.title = "Notes"
        window.level = .floating
        window.isReleasedWhenClosed = false
        window.isMovableByWindowBackground = false
        window.minSize = NSSize(width: 280, height: 220)
        _ = window.setFrameAutosaveName("HuginnNotes")
        window.delegate = self

        statusLabel.font = NSFont.systemFont(ofSize: 13, weight: .semibold)
        statusLabel.lineBreakMode = .byTruncatingTail
        statusLabel.setContentHuggingPriority(.defaultLow, for: .horizontal)
        statusLabel.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)

        hintLabel.font = NSFont.systemFont(ofSize: 11)
        hintLabel.textColor = .secondaryLabelColor
        hintLabel.lineBreakMode = .byTruncatingTail

        for button in [stopButton, discardButton, openButton, copyButton] {
            button.bezelStyle = .rounded
            button.controlSize = .small
            button.font = NSFont.systemFont(ofSize: NSFont.systemFontSize(for: .small))
            button.target = self
        }
        stopButton.action = #selector(stopTapped)
        stopButton.bezelColor = .systemRed
        discardButton.action = #selector(discardTapped)
        discardButton.hasDestructiveAction = true
        openButton.action = #selector(openTapped)
        copyButton.action = #selector(copyTapped)

        let buttons = NSStackView(views: [stopButton, discardButton, openButton, copyButton])
        buttons.orientation = .horizontal
        buttons.spacing = 6

        let header = NSStackView(views: [statusLabel, buttons])
        header.orientation = .horizontal
        header.distribution = .fill
        header.spacing = 8

        textView.isRichText = false
        textView.font = NSFont.systemFont(ofSize: 13)
        textView.isAutomaticQuoteSubstitutionEnabled = false
        textView.isAutomaticDashSubstitutionEnabled = false
        textView.isAutomaticTextReplacementEnabled = false
        textView.isAutomaticSpellingCorrectionEnabled = false
        textView.allowsUndo = true
        textView.textContainerInset = NSSize(width: 6, height: 8)
        textView.delegate = self
        scroll.borderType = .noBorder
        scroll.hasVerticalScroller = true

        let column = NSStackView(views: [header, hintLabel, scroll])
        column.orientation = .vertical
        column.alignment = .leading
        column.spacing = 6
        column.edgeInsets = NSEdgeInsets(top: 12, left: 14, bottom: 12, right: 14)
        column.translatesAutoresizingMaskIntoConstraints = false

        let content = window.contentView!
        content.addSubview(column)
        NSLayoutConstraint.activate([
            column.leadingAnchor.constraint(equalTo: content.leadingAnchor),
            column.trailingAnchor.constraint(equalTo: content.trailingAnchor),
            column.topAnchor.constraint(equalTo: content.topAnchor),
            column.bottomAnchor.constraint(equalTo: content.bottomAnchor),
            header.widthAnchor.constraint(equalTo: column.widthAnchor, constant: -28),
            hintLabel.widthAnchor.constraint(equalTo: column.widthAnchor, constant: -28),
            scroll.widthAnchor.constraint(equalTo: column.widthAnchor, constant: -28),
        ])
        scroll.setContentHuggingPriority(.defaultLow, for: .vertical)

        updateChrome()
    }

    // MARK: - showing

    var isVisible: Bool { window.isVisible }
    private var placed = false

    /// Bring the window on screen without stealing the keyboard. First time: where it was last left, else centred.
    func show() {
        if !placed {
            if !window.setFrameUsingName("HuginnNotes") { window.center() }
            placed = true
        }
        window.orderFront(nil)
    }

    /// Bring the window on screen AND put the caret in the pad: the user just asked Huginn for it.
    func focus() {
        show()
        NSApp.activate(ignoringOtherApps: true)
        window.makeKeyAndOrderFront(nil)
        window.makeFirstResponder(textView)
    }

    func hide() {
        window.orderOut(nil)
    }

    func windowShouldClose(_ sender: NSWindow) -> Bool {
        hide()
        return false
    }

    // MARK: - lifecycle

    /// A recording began. The previous meeting's untouched notes go; anything typed since (an agenda)
    /// stays and gets [00:00:00]. The previous meeting's status and footer buttons go too.
    func startRecording(at date: Date) {
        recording = true
        startedAt = date
        if stale { clearPad() }
        stale = false
        transcriptReady = false
        status = ""
        stamps.zeroAll()
        startClock()
        updateChrome()
    }

    /// The recording ended and is being kept: the pad stays readable while the job runs; the status
    /// follows it via `setStatus`, and the footer appears once `setTranscriptReady` is called.
    func endRecording() {
        recording = false
        stale = !textView.string.isEmpty
        status = "Transcribing…"
        transcriptReady = false
        stopClock()
        saveTimer?.invalidate()
        updateChrome()
    }

    /// The recording was discarded: the notes went with the audio.
    func discarded() {
        recording = false
        clearPad()
        stale = false
        status = ""
        transcriptReady = false
        stopClock()
        saveTimer?.invalidate()
        updateChrome()
    }

    func setStatus(_ text: String) {
        status = text
        updateChrome()
    }

    func setTranscriptReady() {
        transcriptReady = true
        updateChrome()
    }

    /// The pad's non-empty lines with the second each was begun at. Main thread only.
    func snapshot() -> [(seconds: Int, text: String)] {
        stamps.snapshot(textView.string, now: nowSeconds())
    }

    // MARK: - text

    func textDidChange(_ notification: Notification) {
        if suppressChange { return }
        stale = false // the user is working on this text; Record keeps it
        stamps.textChanged(textView.string, caretUTF16: textView.selectedRange().location, now: nowSeconds())
        saveTimer?.invalidate()
        saveTimer = Timer.scheduledTimer(withTimeInterval: 0.4, repeats: false) { [weak self] _ in
            self?.onNotesChanged?()
        }
    }

    private func clearPad() {
        suppressChange = true
        textView.string = ""
        suppressChange = false
        stamps.removeAll()
    }

    private func nowSeconds() -> Int {
        guard recording, let startedAt else { return 0 }
        return Int(Date().timeIntervalSince(startedAt))
    }

    // MARK: - chrome

    private func updateChrome() {
        stopButton.isHidden = !recording
        discardButton.isHidden = !recording
        openButton.isHidden = recording || !transcriptReady
        copyButton.isHidden = recording || !transcriptReady

        if recording {
            statusLabel.stringValue = "● REC " + clock()
            statusLabel.textColor = .systemRed
            hintLabel.stringValue = "Every line is saved with the time you wrote it."
        } else if !status.isEmpty {
            statusLabel.stringValue = status
            statusLabel.textColor = status.hasPrefix("Failed") ? .systemRed : .labelColor
            hintLabel.stringValue = transcriptReady ? "Notes stay until the next recording starts." : ""
        } else {
            statusLabel.stringValue = "Idle"
            statusLabel.textColor = .secondaryLabelColor
            hintLabel.stringValue = "Type an agenda now; it joins the next recording."
        }
    }

    private func clock() -> String {
        let seconds = max(0, nowSeconds())
        if seconds >= 3600 {
            return String(format: "%d:%02d:%02d", seconds / 3600, (seconds % 3600) / 60, seconds % 60)
        }
        return String(format: "%02d:%02d", seconds / 60, seconds % 60)
    }

    private func startClock() {
        stopClock()
        let timer = Timer(timeInterval: 1, repeats: true) { [weak self] _ in
            guard let self, self.recording else { return }
            self.statusLabel.stringValue = "● REC " + self.clock()
        }
        RunLoop.main.add(timer, forMode: .common)
        clockTimer = timer
    }

    private func stopClock() {
        clockTimer?.invalidate()
        clockTimer = nil
    }

    private func toast(_ text: String) {
        hintLabel.stringValue = text
        toastTimer?.invalidate()
        toastTimer = Timer.scheduledTimer(withTimeInterval: 1.5, repeats: false) { [weak self] _ in
            self?.updateChrome()
        }
    }

    // MARK: - actions

    @objc private func stopTapped() { onStop?() }
    @objc private func discardTapped() { onDiscard?() }

    @objc private func openTapped() {
        guard let url = transcriptResolver?() else { toast("No transcript yet"); return }
        NSWorkspace.shared.open(url)
    }

    @objc private func copyTapped() {
        guard let url = transcriptResolver?() else { toast("No transcript yet"); return }
        let pasteboard = NSPasteboard.general
        pasteboard.clearContents()
        pasteboard.setString(url.path, forType: .string)
        toast("Path copied")
    }
}
