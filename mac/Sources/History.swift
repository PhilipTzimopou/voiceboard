import Foundation

/// Το ιστορικό υπαγορεύσεων, μόνο τοπικά στο history.json.
struct HistoryEntry: Codable, Identifiable, Hashable {
    var id = UUID()
    var date: Date
    var text: String
    var words: Int
    var seconds: Double
    var model: String
}

enum HistoryStore {
    private static var url: URL { Paths.support.appendingPathComponent("history.json") }
    private static let limit = 2000

    static func load() -> [HistoryEntry] {
        guard let data = try? Data(contentsOf: url) else { return [] }
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return (try? decoder.decode([HistoryEntry].self, from: data)) ?? []
    }

    static func save(_ entries: [HistoryEntry]) {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.prettyPrinted]
        let trimmed = Array(entries.prefix(limit))
        try? encoder.encode(trimmed).write(to: url, options: .atomic)
    }

    static func wordCount(_ text: String) -> Int {
        text.split { $0.isWhitespace || $0.isNewline }.count
    }
}

struct DictationStats {
    let totalWords: Int
    let wordsPerMinute: Int
    let streakDays: Int
    let longestStreak: Int
    let wordsToday: Int
    let dictationsToday: Int
    /// Λέξεις ανά μέρα (αρχή της μέρας → λέξεις), για το ημερολόγιο συνήθειας.
    let wordsByDay: [Date: Int]
    /// Λεπτά που γλίτωσες: η ίδια ποσότητα στο πληκτρολόγιο με 40 λέξεις το λεπτό, μείον τον χρόνο που μίλησες.
    let minutesSaved: Int

    init(_ entries: [HistoryEntry]) {
        let calendar = Calendar.current
        totalWords = entries.reduce(0) { $0 + $1.words }
        let spokenMinutes = entries.reduce(0) { $0 + $1.seconds } / 60
        wordsPerMinute = spokenMinutes > 0.1 ? Int((Double(totalWords) / spokenMinutes).rounded()) : 0
        minutesSaved = max(0, Int((Double(totalWords) / 40 - spokenMinutes).rounded()))

        var byDay: [Date: Int] = [:]
        var countToday = 0
        let today = calendar.startOfDay(for: Date())
        for entry in entries {
            let day = calendar.startOfDay(for: entry.date)
            byDay[day, default: 0] += entry.words
            if day == today { countToday += 1 }
        }
        wordsByDay = byDay
        wordsToday = byDay[today] ?? 0
        dictationsToday = countToday

        var day = today
        if byDay[day] == nil { day = calendar.date(byAdding: .day, value: -1, to: day)! }
        var streak = 0
        while byDay[day] != nil {
            streak += 1
            day = calendar.date(byAdding: .day, value: -1, to: day)!
        }
        streakDays = streak

        var longest = 0, run = 0
        var previous: Date?
        for day in byDay.keys.sorted() {
            if let previous, calendar.dateComponents([.day], from: previous, to: day).day == 1 { run += 1 } else { run = 1 }
            longest = max(longest, run)
            previous = day
        }
        longestStreak = longest
    }
}
