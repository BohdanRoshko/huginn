import Foundation

/// The speech model is ~1.6 GB, so it is fetched on first use rather than
/// shipped in the app. Downloads to a .part file and moves it into place only
/// on success, so an interrupted download can never masquerade as a good model.
final class ModelDownloader: NSObject {
    enum State {
        case missing
        case downloading(fraction: Double)
        case ready
        case failed(String)
    }

    private(set) var state: State = .missing
    var onChange: ((State) -> Void)?

    private var session: URLSession?
    private var task: URLSessionDownloadTask?

    override init() {
        super.init()
        refresh()
    }

    var isReady: Bool {
        if case .ready = state { return true }
        return false
    }

    var isDownloading: Bool {
        if case .downloading = state { return true }
        return false
    }

    func refresh() {
        Paths.ensureDirectories()
        if FileManager.default.fileExists(atPath: Paths.modelFile.path) {
            set(.ready)
        } else if !isDownloading {
            set(.missing)
        }
    }

    func startIfNeeded() {
        guard !isReady, !isDownloading else { return }
        Paths.ensureDirectories()

        let config = URLSessionConfiguration.default
        config.timeoutIntervalForResource = 60 * 60
        let session = URLSession(configuration: config, delegate: self, delegateQueue: nil)
        self.session = session

        set(.downloading(fraction: 0))
        log("model download started")

        let task = session.downloadTask(with: Config.modelURL)
        task.resume()
        self.task = task
    }

    func cancel() {
        task?.cancel()
        task = nil
        session?.invalidateAndCancel()
        session = nil
        set(.missing)
    }

    private func set(_ new: State) {
        state = new
        DispatchQueue.main.async { [weak self] in
            guard let self else { return }
            self.onChange?(self.state)
        }
    }
}

extension ModelDownloader: URLSessionDownloadDelegate {
    func urlSession(
        _ session: URLSession,
        downloadTask: URLSessionDownloadTask,
        didWriteData bytesWritten: Int64,
        totalBytesWritten: Int64,
        totalBytesExpectedToWrite: Int64
    ) {
        let expected = totalBytesExpectedToWrite > 0
            ? totalBytesExpectedToWrite
            : Config.modelApproxBytes
        set(.downloading(fraction: min(1, Double(totalBytesWritten) / Double(expected))))
    }

    func urlSession(
        _ session: URLSession,
        downloadTask: URLSessionDownloadTask,
        didFinishDownloadingTo location: URL
    ) {
        // Reject an HTTP error page that would otherwise be saved as a "model".
        if let http = downloadTask.response as? HTTPURLResponse, http.statusCode != 200 {
            set(.failed("download failed (HTTP \(http.statusCode))"))
            log("model download HTTP \(http.statusCode)")
            return
        }

        let destination = Paths.modelFile
        let partial = destination.appendingPathExtension("part")
        do {
            try? FileManager.default.removeItem(at: partial)
            try FileManager.default.moveItem(at: location, to: partial)
            try? FileManager.default.removeItem(at: destination)
            try FileManager.default.moveItem(at: partial, to: destination)
            set(.ready)
            log("model download finished")
        } catch {
            try? FileManager.default.removeItem(at: partial)
            set(.failed(error.localizedDescription))
            log("model install failed: \(error.localizedDescription)")
        }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        guard let error else { return }
        let nsError = error as NSError
        if nsError.code == NSURLErrorCancelled { return }
        set(.failed(error.localizedDescription))
        log("model download failed: \(error.localizedDescription)")
    }
}
