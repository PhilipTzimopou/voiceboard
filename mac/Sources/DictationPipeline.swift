import Foundation

/// Στέλνει τον ήχο για μεταγραφή σε κομμάτια όσο μιλάς: σε κάθε παύση ≥ 0,8 δευτ. (και αφού έχουν
/// μαζευτεί ≥ 3 δευτ.) το κομμάτι φεύγει. Όταν αφήνεις τα πλήκτρα μένει μόνο το τελευταίο, σύντομο κομμάτι.
/// Η κανονική μεταγραφή του Scribe v2 θέλει χρόνο ανάλογο της διάρκειας (≈1,5 δευτ. για 3 δευτ. ήχου, ≈4 για 10),
/// οπότε έτσι η αναμονή μένει μικρή όσο μεγάλη κι αν είναι η υπαγόρευση.
final class DictationPipeline {
    private let transcriber: Transcriber
    private let vocabulary: Vocabulary
    private let queue = DispatchQueue(label: "philip-voice.pipeline")
    private let group = DispatchGroup()

    private let sampleRate = 16_000
    private let frameLength = 480                    // 30 ms
    private let minSegmentSamples = 16_000 * 3       // 3 δευτ.
    private let pauseFrames = 27                     // ≈ 0,8 δευτ. σιωπής

    private var pending: [Int16] = []                // ήχος που δεν έχει σταλεί ακόμα
    private var frame: [Int16] = []
    private var silentFrames = 0
    private var loudest: Float = 0
    private var results: [Int: Result<String, Error>] = [:]
    private var nextIndex = 0
    private var cancelled = false

    init(transcriber: Transcriber, vocabulary: Vocabulary) {
        self.transcriber = transcriber
        self.vocabulary = vocabulary
    }

    func append(_ samples: [Int16]) {
        queue.async {
            guard !self.cancelled else { return }
            for sample in samples {
                self.pending.append(sample)
                self.frame.append(sample)
                if self.frame.count == self.frameLength { self.analyzeFrame() }
            }
        }
    }

    /// Στέλνει το τελευταίο κομμάτι και επιστρέφει όλο το κείμενο, με τη σωστή σειρά.
    func finish(completion: @escaping (Result<String, Error>) -> Void) {
        queue.async {
            self.sendPending(isLast: true)
            self.group.notify(queue: self.queue) {
                var parts: [String] = []
                for index in 0..<self.nextIndex {
                    switch self.results[index] {
                    case .success(let text)?:
                        let clean = text.trimmingCharacters(in: .whitespacesAndNewlines)
                        if !clean.isEmpty { parts.append(clean) }
                    case .failure(let error)?:
                        completion(.failure(error)); return
                    case nil:
                        break
                    }
                }
                completion(.success(self.join(parts)))
            }
        }
    }

    /// Κάθε κομμάτι το μοντέλο το γράφει σαν να ξεκινά νέα πρόταση. Αν το προηγούμενο δεν έκλεισε με σημείο
    /// στίξης, η παύση ήταν μέσα στην πρόταση: συνεχίζουμε με μικρό γράμμα, εκτός αν η λέξη είναι όνομα του λεξιλογίου.
    private func join(_ parts: [String]) -> String {
        var result = ""
        for part in parts {
            guard !result.isEmpty else { result = part; continue }
            var next = part
            let endsSentence = result.last.map { ".!?;…:".contains($0) } ?? true
            let firstWord = String(next.prefix { !$0.isWhitespace && !$0.isPunctuation })
            let isName = vocabulary.words.contains { $0.caseInsensitiveCompare(firstWord) == .orderedSame }
            if !endsSentence, !isName, let first = next.first, first.isUppercase,
               firstWord.count > 1 || first != "I" {
                next = first.lowercased() + next.dropFirst()
            }
            result += " " + next
        }
        // Όταν κόβουμε σε κομμάτια, το τελευταίο συχνά βγαίνει χωρίς τελεία.
        if parts.count > 1, let last = result.last, last.isLetter || last.isNumber { result += "." }
        return result
    }

    func cancel() { queue.async { self.cancelled = true; self.pending.removeAll() } }

    private func analyzeFrame() {
        var sum: Float = 0
        for sample in frame { let value = Float(sample) / 32768; sum += value * value }
        let rms = (sum / Float(frame.count)).squareRoot()
        frame.removeAll(keepingCapacity: true)
        loudest = max(loudest * 0.999, rms)
        // Σιωπή: πολύ χαμηλότερα από τη φωνή σου σε αυτή την ηχογράφηση.
        let silent = rms < max(0.004, loudest * 0.12)
        silentFrames = silent ? silentFrames + 1 : 0
        if silentFrames >= pauseFrames && pending.count >= minSegmentSamples {
            sendPending(isLast: false)
            silentFrames = 0
        }
    }

    private func sendPending(isLast: Bool) {
        let samples = pending
        pending.removeAll(keepingCapacity: true)
        // Κομμάτι μόνο με σιωπή (π.χ. η παύση πριν αφήσεις τα πλήκτρα): δεν στέλνεται.
        let peak = samples.reduce(Int16(0)) { max($0, $1 == Int16.min ? Int16.max : abs($1)) }
        guard samples.count >= sampleRate / 4, Float(peak) / 32768 >= 0.02 else { return }
        let index = nextIndex
        nextIndex += 1
        group.enter()
        Log.write(String(format: "Κομμάτι %d: %.1f δευτ.%@", index + 1, Double(samples.count) / Double(sampleRate), isLast ? " (τελευταίο)" : ""))
        transcriber.transcribe(wav: Recorder.wav(from: samples), vocabulary: vocabulary) { [weak self] result in
            guard let self else { return }
            self.queue.async {
                self.results[index] = result
                self.group.leave()
            }
        }
    }
}
