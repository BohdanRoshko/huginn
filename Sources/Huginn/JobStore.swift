import Foundation

/// The rolling list of recent meetings shown in the menu. Persisted so the list
/// survives a restart, same as Munin's recent.json on Windows.
struct Job: Codable, Identifiable {
    enum Status: String, Codable {
        case recording
        case transcribing
        case done
        case failed
        case cancelled
    }

    let id: UUID
    let startedAt: Date
    var status: Status
    var detail: String?
    var transcriptPath: String?
    /// The name the user gave it (Rename in the menu); the transcript file carries it too.
    var name: String?

    var stamp: String { Paths.stamp(startedAt) }

    /// The menu line: the name when there is one (the time moves into the detail), else the time.
    var menuTitle: String {
        if let name, !name.isEmpty { return "\(name) — \(timeTitle) · \(statusLine)" }
        return "\(timeTitle) — \(statusLine)"
    }

    /// Time alone is ambiguous the moment the list spans days — two entries
    /// reading "9:15 AM" and "2:02 PM" give no clue they are from different
    /// meetings on different dates. Today stays bare; anything older says so.
    var timeTitle: String {
        let f = DateFormatter()
        f.timeStyle = .short
        if Calendar.current.isDateInToday(startedAt) {
            f.dateStyle = .none
        } else {
            f.dateStyle = .short
            f.doesRelativeDateFormatting = true   // "Yesterday 2:02 PM"
        }
        return f.string(from: startedAt)
    }

    var statusLine: String {
        switch status {
        case .recording:    return "Recording…"
        case .transcribing: return "Transcribing…"
        case .done:         return "Ready"
        case .failed:       return detail.map { "Failed — \($0)" } ?? "Failed"
        case .cancelled:    return "Cancelled"
        }
    }
}

final class JobStore {
    private(set) var jobs: [Job] = []
    private let queue = DispatchQueue(label: "huginn.jobstore")
    var onChange: (() -> Void)?

    init() {
        load()
    }

    func upsert(_ job: Job) {
        queue.sync {
            if let index = jobs.firstIndex(where: { $0.id == job.id }) {
                jobs[index] = job
            } else {
                jobs.insert(job, at: 0)
            }
            trim()
            persist()
        }
        DispatchQueue.main.async { [weak self] in self?.onChange?() }
    }

    /// A snapshot of one job, safe from any thread (the transcription task reads it).
    func job(_ id: UUID) -> Job? {
        queue.sync { jobs.first { $0.id == id } }
    }

    func update(_ id: UUID, _ mutate: (inout Job) -> Void) {
        queue.sync {
            guard let index = jobs.firstIndex(where: { $0.id == id }) else { return }
            mutate(&jobs[index])
            persist()
        }
        DispatchQueue.main.async { [weak self] in self?.onChange?() }
    }

    // MARK: - private

    /// Drop the oldest FINISHED job first so an in-flight one is never lost
    /// from the list while it is still working.
    private func trim() {
        while jobs.count > Config.recentLimit {
            let finished = jobs.lastIndex { $0.status != .transcribing && $0.status != .recording }
            jobs.remove(at: finished ?? jobs.count - 1)
        }
    }

    private func persist() {
        Paths.ensureDirectories()
        guard let data = try? JSONEncoder.huginn.encode(jobs) else { return }
        try? data.write(to: Paths.recentFile, options: .atomic)
    }

    private func load() {
        guard let data = try? Data(contentsOf: Paths.recentFile),
              let decoded = try? JSONDecoder.huginn.decode([Job].self, from: data) else { return }

        // Anything still marked in-flight died with the previous process.
        jobs = decoded.map { job in
            guard job.status == .recording || job.status == .transcribing else { return job }
            var recovered = job
            recovered.status = .failed
            recovered.detail = "interrupted"
            return recovered
        }
    }
}

extension JSONEncoder {
    static let huginn: JSONEncoder = {
        let e = JSONEncoder()
        e.dateEncodingStrategy = .iso8601
        return e
    }()
}

extension JSONDecoder {
    static let huginn: JSONDecoder = {
        let d = JSONDecoder()
        d.dateDecodingStrategy = .iso8601
        return d
    }()
}
