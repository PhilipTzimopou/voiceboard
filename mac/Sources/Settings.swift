import Foundation

enum SpeechModel: String, CaseIterable {
    case scribe = "scribe_v2"
    case whisper = "whisper"

    var title: String {
        switch self {
        case .scribe: return "Scribe v2 της ElevenLabs (καλύτερο, με χρέωση)"
        case .whisper: return "Whisper της OpenAI (τοπικά, δωρεάν)"
        }
    }
}

/// Το config.json στο ~/Library/Application Support/Voiceboard/.
/// Το γράφει το install.sh με το κλειδί της ElevenLabs και τη διαδρομή του μοντέλου Whisper.
struct AppConfig: Codable {
    var elevenLabsAPIKey: String?
    var whisperCLI: String?
    var whisperModel: String?
    var language: String?
}

enum AppInfo {
    /// Το όνομα που βλέπεις (CFBundleDisplayName). Αλλάζει μόνο στο Info.plist.
    static let name = Bundle.main.object(forInfoDictionaryKey: "CFBundleDisplayName") as? String ?? "Voiceboard"
}

enum Paths {
    static let support: URL = {
        let url = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Voiceboard", isDirectory: true)
        try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }()
    static var config: URL { support.appendingPathComponent("config.json") }
    static var vocabulary: URL { support.appendingPathComponent("vocabulary.json") }
    static var log: URL { support.appendingPathComponent("philip-voice.log") }
    static var defaultWhisperModel: URL { support.appendingPathComponent("models/ggml-large-v3.bin") }
    static var wisprDatabase: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Wispr Flow/flow.sqlite")
    }
}

final class Settings {
    static let shared = Settings()
    private(set) var config = AppConfig()
    private let defaults = UserDefaults.standard

    private init() { reload() }

    func reload() {
        if let data = try? Data(contentsOf: Paths.config),
           let decoded = try? JSONDecoder().decode(AppConfig.self, from: data) {
            config = decoded
        }
    }

    var hasElevenLabsKey: Bool { !(config.elevenLabsAPIKey ?? "").isEmpty }

    var model: SpeechModel {
        get {
            if let raw = defaults.string(forKey: "model"), let model = SpeechModel(rawValue: raw) { return model }
            return hasElevenLabsKey ? .scribe : .whisper
        }
        set { defaults.set(newValue.rawValue, forKey: "model") }
    }

    /// UID του μικροφώνου· κενό = η προεπιλογή του συστήματος.
    var microphoneUID: String {
        get { defaults.string(forKey: "microphoneUID") ?? "" }
        set { defaults.set(newValue, forKey: "microphoneUID") }
    }

    var soundsEnabled: Bool {
        get { defaults.object(forKey: "soundsEnabled") as? Bool ?? true }
        set { defaults.set(newValue, forKey: "soundsEnabled") }
    }

    /// Τα πλήκτρα που κρατάς για να μιλήσεις (Shortcuts.pushToTalk).
    var pushToTalk: String {
        get { defaults.string(forKey: "pushToTalk") ?? "control-option" }
        set { defaults.set(newValue, forKey: "pushToTalk") }
    }

    /// Το κουμπί του ποντικιού για ηχογράφηση χωρίς χέρια (Shortcuts.handsFree).
    var handsFree: String {
        get { defaults.string(forKey: "handsFree") ?? "middle" }
        set { defaults.set(newValue, forKey: "handsFree") }
    }

    /// Ημερήσιος στόχος λέξεων για το habit tracker.
    var dailyGoal: Int {
        get { defaults.object(forKey: "dailyGoal") as? Int ?? 1000 }
        set { defaults.set(newValue, forKey: "dailyGoal") }
    }

    var language: String { config.language ?? "el" }
    var whisperCLI: String { config.whisperCLI ?? "/opt/homebrew/bin/whisper-cli" }
    var whisperModel: String { config.whisperModel ?? Paths.defaultWhisperModel.path }
}

enum Log {
    private static let formatter: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f
    }()

    static func write(_ message: String) {
        let line = "\(formatter.string(from: Date())) \(message)\n"
        guard let data = line.data(using: .utf8) else { return }
        if let handle = try? FileHandle(forWritingTo: Paths.log) {
            handle.seekToEndOfFile()
            handle.write(data)
            try? handle.close()
        } else {
            try? data.write(to: Paths.log)
        }
    }
}

struct VoiceError: LocalizedError {
    let message: String
    init(_ message: String) { self.message = message }
    var errorDescription: String? { message }
}
