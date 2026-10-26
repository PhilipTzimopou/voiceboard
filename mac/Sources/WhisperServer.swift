import Foundation

/// Κρατά το Whisper φορτωμένο στη μνήμη με το whisper-server του whisper.cpp, ώστε κάθε υπαγόρευση
/// να μη χάνει ~1 δευτ. για να φορτώσει το μοντέλο. Τρέχει μόνο όσο είναι επιλεγμένο το Whisper.
final class WhisperServer {
    static let shared = WhisperServer()
    static let port = 8178

    private var process: Process?
    private(set) var isReady = false
    private let queue = DispatchQueue(label: "philip-voice.whisper-server")

    var inferenceURL: URL { URL(string: "http://127.0.0.1:\(Self.port)/inference")! }

    func start() {
        queue.async {
            guard self.process == nil else { return }
            // Αν έμεινε ήδη ανοιχτό από προηγούμενη εκτέλεση, το χρησιμοποιούμε.
            if self.respondsNow() { self.isReady = true; Log.write("Whisper: βρέθηκε ήδη φορτωμένο"); return }
            let settings = Settings.shared
            let server = URL(fileURLWithPath: settings.whisperCLI).deletingLastPathComponent().appendingPathComponent("whisper-server")
            guard FileManager.default.isExecutableFile(atPath: server.path),
                  FileManager.default.fileExists(atPath: settings.whisperModel) else {
                Log.write("Whisper: δεν βρέθηκε το whisper-server ή το μοντέλο, χρησιμοποιώ το whisper-cli")
                return
            }
            let process = Process()
            process.executableURL = server
            process.arguments = ["-m", settings.whisperModel, "--host", "127.0.0.1", "--port", "\(Self.port)",
                                 "-l", settings.language, "-nt"]
            process.standardOutput = FileHandle.nullDevice
            process.standardError = FileHandle.nullDevice
            process.terminationHandler = { [weak self] _ in
                self?.queue.async { self?.process = nil; self?.isReady = false }
            }
            do { try process.run() } catch {
                Log.write("Whisper: δεν ξεκίνησε το whisper-server (\(error.localizedDescription))")
                return
            }
            self.process = process
            let started = Date()
            for _ in 0..<120 {
                if self.respondsNow() {
                    self.isReady = true
                    Log.write(String(format: "Whisper: φορτώθηκε στη μνήμη σε %.1f δευτ.", Date().timeIntervalSince(started)))
                    return
                }
                Thread.sleep(forTimeInterval: 0.25)
            }
            Log.write("Whisper: το whisper-server δεν απάντησε")
        }
    }

    func stop() {
        queue.sync {
            isReady = false
            if let process, process.isRunning { process.terminate() }
            process = nil
        }
    }

    private func respondsNow() -> Bool {
        var request = URLRequest(url: URL(string: "http://127.0.0.1:\(Self.port)/")!)
        request.timeoutInterval = 0.5
        let semaphore = DispatchSemaphore(value: 0)
        var ok = false
        URLSession.shared.dataTask(with: request) { _, response, _ in
            ok = (response as? HTTPURLResponse) != nil
            semaphore.signal()
        }.resume()
        _ = semaphore.wait(timeout: .now() + 1)
        return ok
    }
}
