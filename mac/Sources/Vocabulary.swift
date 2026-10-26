import Foundation
import SQLite3

/// Το λεξιλόγιο, σε απλό JSON που ανοίγει και διορθώνεται με οποιονδήποτε επεξεργαστή κειμένου.
/// - words: λέξεις και ονόματα που πρέπει να γράφονται σωστά (τα στέλνουμε στο μοντέλο ως υπόδειξη)
/// - replacements: «άκουσε Χ, γράψε Υ», εφαρμόζονται μετά τη μεταγραφή
/// - snippets: λες τη φράση και μπαίνει ολόκληρο το κείμενο
struct Vocabulary: Codable {
    struct Replacement: Codable {
        var from: String
        var to: String
    }

    var words: [String] = []
    var replacements: [Replacement] = []
    var snippets: [Replacement] = []
    var importedFromWisprAt: String?

    static func load() -> Vocabulary {
        guard let data = try? Data(contentsOf: Paths.vocabulary),
              let vocabulary = try? JSONDecoder().decode(Vocabulary.self, from: data) else { return Vocabulary() }
        return vocabulary
    }

    func save() throws {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .withoutEscapingSlashes]
        try encoder.encode(self).write(to: Paths.vocabulary, options: .atomic)
    }

    /// Οι όροι για το keyterms του Scribe v2: κάτω από 50 χαρακτήρες, έως 5 λέξεις,
    /// χωρίς < > { } [ ] \, και έως 100 για να μην ισχύσει η ελάχιστη χρέωση των 20 δευτερολέπτων.
    var scribeKeyterms: [String] {
        let forbidden = CharacterSet(charactersIn: "<>{}[]\\")
        var seen = Set<String>()
        var terms: [String] = []
        for term in words + replacements.map(\.to) {
            let clean = term.trimmingCharacters(in: .whitespacesAndNewlines)
            guard clean.count >= 2, clean.count < 50,
                  clean.split(separator: " ").count <= 5,
                  clean.rangeOfCharacter(from: forbidden) == nil,
                  !clean.contains("\n"),
                  seen.insert(clean.lowercased()).inserted else { continue }
            terms.append(clean)
            if terms.count == 100 { break }
        }
        return terms
    }

    /// Το αρχικό prompt του Whisper: λίστα όρων, κομμένη ώστε να χωράει στο όριο του μοντέλου.
    var whisperPrompt: String {
        var prompt = ""
        for term in scribeKeyterms {
            let next = prompt.isEmpty ? term : prompt + ", " + term
            if next.count > 600 { break }
            prompt = next
        }
        return prompt
    }

    func apply(to text: String) -> String {
        var result = text
        let rules = (snippets + replacements).sorted { $0.from.count > $1.from.count }
        for rule in rules where !rule.from.trimmingCharacters(in: .whitespaces).isEmpty {
            let pattern = "(?<![\\p{L}\\p{N}])" + NSRegularExpression.escapedPattern(for: rule.from.trimmingCharacters(in: .whitespaces)) + "(?![\\p{L}\\p{N}])"
            guard let regex = try? NSRegularExpression(pattern: pattern, options: [.caseInsensitive]) else { continue }
            let range = NSRange(result.startIndex..., in: result)
            result = regex.stringByReplacingMatches(in: result, range: range,
                                                    withTemplate: NSRegularExpression.escapedTemplate(for: rule.to))
        }
        return result
    }
}

enum WisprImporter {
    /// Διαβάζει (μόνο ανάγνωση) τον πίνακα Dictionary του Wispr Flow και τον ενώνει με το λεξιλόγιο.
    static func importInto(_ vocabulary: inout Vocabulary) throws -> (words: Int, replacements: Int, snippets: Int) {
        let path = Paths.wisprDatabase.path
        guard FileManager.default.fileExists(atPath: path) else {
            throw VoiceError("Δεν βρέθηκε η βάση του Wispr Flow στο \(path)")
        }
        var db: OpaquePointer?
        let uri = "file:" + path + "?mode=ro"
        guard sqlite3_open_v2(uri, &db, SQLITE_OPEN_READONLY | SQLITE_OPEN_URI, nil) == SQLITE_OK else {
            let message = db.map { String(cString: sqlite3_errmsg($0)) } ?? "άγνωστο σφάλμα"
            sqlite3_close(db)
            throw VoiceError("Δεν άνοιξε η βάση του Wispr Flow: \(message)")
        }
        defer { sqlite3_close(db) }
        sqlite3_busy_timeout(db, 3000)

        let sql = """
            SELECT phrase, replacement, isSnippet FROM Dictionary
            WHERE isDeleted = 0
            ORDER BY isStarred DESC, frequencyUsed DESC, createdAt DESC
            """
        var statement: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &statement, nil) == SQLITE_OK else {
            throw VoiceError("Δεν διαβάστηκε ο πίνακας Dictionary: \(String(cString: sqlite3_errmsg(db)))")
        }
        defer { sqlite3_finalize(statement) }

        var words: [String] = []
        var replacements: [Vocabulary.Replacement] = []
        var snippets: [Vocabulary.Replacement] = []
        while sqlite3_step(statement) == SQLITE_ROW {
            guard let phraseC = sqlite3_column_text(statement, 0) else { continue }
            let phrase = String(cString: phraseC).trimmingCharacters(in: .whitespaces)
            let replacement = sqlite3_column_text(statement, 1).map { String(cString: $0) } ?? ""
            let isSnippet = sqlite3_column_int(statement, 2) == 1
            if phrase.isEmpty { continue }
            if replacement.isEmpty {
                words.append(phrase)
            } else if isSnippet {
                snippets.append(.init(from: phrase, to: replacement))
            } else {
                replacements.append(.init(from: phrase, to: replacement))
            }
        }

        func merge(_ existing: [Vocabulary.Replacement], _ incoming: [Vocabulary.Replacement]) -> [Vocabulary.Replacement] {
            var result = existing.filter { old in !incoming.contains { $0.from.lowercased() == old.from.lowercased() } }
            result.append(contentsOf: incoming)
            return result
        }
        var seen = Set<String>()
        vocabulary.words = (words + vocabulary.words).filter { seen.insert($0.lowercased()).inserted }
        vocabulary.replacements = merge(vocabulary.replacements, replacements)
        vocabulary.snippets = merge(vocabulary.snippets, snippets)
        vocabulary.importedFromWisprAt = ISO8601DateFormatter().string(from: Date())
        return (words.count, replacements.count, snippets.count)
    }
}
