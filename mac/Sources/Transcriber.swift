import Foundation

protocol Transcriber {
    func transcribe(wav: Data, vocabulary: Vocabulary, completion: @escaping (Result<String, Error>) -> Void)
}

/// Scribe v2 της ElevenLabs: POST /v1/speech-to-text, με το λεξιλόγιο στο keyterms.
final class ScribeTranscriber: Transcriber {
    func transcribe(wav: Data, vocabulary: Vocabulary, completion: @escaping (Result<String, Error>) -> Void) {
        guard let key = Settings.shared.config.elevenLabsAPIKey, !key.isEmpty else {
            completion(.failure(VoiceError("Λείπει το κλειδί της ElevenLabs από το config.json")))
            return
        }
        let boundary = "Voiceboard-\(UUID().uuidString)"
        var body = Data()
        func field(_ name: String, _ value: String) {
            body.append("--\(boundary)\r\nContent-Disposition: form-data; name=\"\(name)\"\r\n\r\n\(value)\r\n".data(using: .utf8)!)
        }
        field("model_id", "scribe_v2")
        field("language_code", Settings.shared.language)
        field("tag_audio_events", "false")
        field("timestamps_granularity", "none")
        field("no_verbatim", "true")
        for term in vocabulary.scribeKeyterms { field("keyterms", term) }
        body.append("--\(boundary)\r\nContent-Disposition: form-data; name=\"file\"; filename=\"audio.wav\"\r\nContent-Type: audio/wav\r\n\r\n".data(using: .utf8)!)
        body.append(wav)
        body.append("\r\n--\(boundary)--\r\n".data(using: .utf8)!)

        var request = URLRequest(url: URL(string: "https://api.elevenlabs.io/v1/speech-to-text")!)
        request.httpMethod = "POST"
        request.timeoutInterval = 30
        request.setValue(key, forHTTPHeaderField: "xi-api-key")
        request.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")
        request.httpBody = body

        URLSession.shared.dataTask(with: request) { data, response, error in
            if let error { completion(.failure(error)); return }
            let status = (response as? HTTPURLResponse)?.statusCode ?? 0
            guard let data,
                  let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
                completion(.failure(VoiceError("Άκυρη απάντηση από την ElevenLabs (HTTP \(status))")))
                return
            }
            guard status == 200, let text = json["text"] as? String else {
                let detail = (json["detail"] as? [String: Any])?["message"] as? String ?? "\(json["detail"] ?? "")"
                completion(.failure(VoiceError("ElevenLabs HTTP \(status): \(detail)")))
                return
            }
            completion(.success(text))
        }.resume()
    }
}

/// Whisper large-v3 τοπικά, μέσω του whisper-cli (whisper.cpp από το Homebrew). Χωρίς δίκτυο, χωρίς χρέωση.
final class WhisperTranscriber: Transcriber {
    private let queue = DispatchQueue(label: "philip-voice.whisper")

    func transcribe(wav: Data, vocabulary: Vocabulary, completion: @escaping (Result<String, Error>) -> Void) {
        if WhisperServer.shared.isReady {
            transcribeWithServer(wav: wav, vocabulary: vocabulary) { [weak self] result in
                switch result {
                case .success: completion(result)
                case .failure(let error):
                    Log.write("Whisper server: \(error.localizedDescription), δοκιμάζω whisper-cli")
                    self?.transcribeWithCLI(wav: wav, vocabulary: vocabulary, completion: completion)
                }
            }
        } else {
            transcribeWithCLI(wav: wav, vocabulary: vocabulary, completion: completion)
        }
    }

    /// Μέσω του whisper-server: το μοντέλο είναι ήδη φορτωμένο, οπότε γλιτώνουμε ~1 δευτ. ανά υπαγόρευση.
    private func transcribeWithServer(wav: Data, vocabulary: Vocabulary, completion: @escaping (Result<String, Error>) -> Void) {
        let boundary = "Voiceboard-\(UUID().uuidString)"
        var body = Data()
        func field(_ name: String, _ value: String) {
            body.append("--\(boundary)\r\nContent-Disposition: form-data; name=\"\(name)\"\r\n\r\n\(value)\r\n".data(using: .utf8)!)
        }
        field("language", Settings.shared.language)
        field("response_format", "text")
        let prompt = vocabulary.whisperPrompt
        if !prompt.isEmpty { field("prompt", prompt) }
        body.append("--\(boundary)\r\nContent-Disposition: form-data; name=\"file\"; filename=\"audio.wav\"\r\nContent-Type: audio/wav\r\n\r\n".data(using: .utf8)!)
        body.append(wav)
        body.append("\r\n--\(boundary)--\r\n".data(using: .utf8)!)

        var request = URLRequest(url: WhisperServer.shared.inferenceURL)
        request.httpMethod = "POST"
        request.timeoutInterval = 60
        request.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")
        request.httpBody = body
        URLSession.shared.dataTask(with: request) { data, response, error in
            if let error { completion(.failure(error)); return }
            guard (response as? HTTPURLResponse)?.statusCode == 200, let data else {
                completion(.failure(VoiceError("HTTP \((response as? HTTPURLResponse)?.statusCode ?? 0)")))
                return
            }
            completion(.success(Self.clean(String(decoding: data, as: UTF8.self))))
        }.resume()
    }

    private func transcribeWithCLI(wav: Data, vocabulary: Vocabulary, completion: @escaping (Result<String, Error>) -> Void) {
        queue.async {
            let settings = Settings.shared
            guard FileManager.default.isExecutableFile(atPath: settings.whisperCLI) else {
                completion(.failure(VoiceError("Δεν βρέθηκε το whisper-cli στο \(settings.whisperCLI)")))
                return
            }
            guard FileManager.default.fileExists(atPath: settings.whisperModel) else {
                completion(.failure(VoiceError("Δεν βρέθηκε το μοντέλο Whisper στο \(settings.whisperModel)")))
                return
            }
            let file = FileManager.default.temporaryDirectory.appendingPathComponent("philip-voice-\(UUID().uuidString).wav")
            defer { try? FileManager.default.removeItem(at: file) }
            do { try wav.write(to: file) } catch { completion(.failure(error)); return }

            let process = Process()
            process.executableURL = URL(fileURLWithPath: settings.whisperCLI)
            var arguments = ["-m", settings.whisperModel, "-f", file.path, "-l", settings.language, "-nt", "-np"]
            let prompt = vocabulary.whisperPrompt
            if !prompt.isEmpty { arguments += ["--prompt", prompt] }
            process.arguments = arguments
            let output = Pipe()
            process.standardOutput = output
            process.standardError = FileHandle.nullDevice
            do { try process.run() } catch { completion(.failure(error)); return }
            let data = output.fileHandleForReading.readDataToEndOfFile()
            process.waitUntilExit()
            guard process.terminationStatus == 0 else {
                completion(.failure(VoiceError("Το whisper-cli τερμάτισε με κωδικό \(process.terminationStatus)")))
                return
            }
            completion(.success(Self.clean(String(decoding: data, as: UTF8.self))))
        }
    }

    /// Το Whisper «ακούει» φράσεις σε κομμάτια σιωπής. Τις πετάμε.
    private static func clean(_ raw: String) -> String {
        var text = raw.replacingOccurrences(of: "\\[[^\\]]*\\]", with: " ", options: .regularExpression)
            .replacingOccurrences(of: "\\s+", with: " ", options: .regularExpression)
            .trimmingCharacters(in: .whitespacesAndNewlines)
        let hallucinations = ["Amara.org", "Υπότιτλοι", "υπότιτλους", "Ευχαριστώ για την παρακολούθηση"]
        if hallucinations.contains(where: { text.localizedCaseInsensitiveContains($0) }) && text.count < 120 {
            text = ""
        }
        return text
    }
}
