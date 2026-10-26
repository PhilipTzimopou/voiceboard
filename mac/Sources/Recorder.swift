import AVFoundation
import AudioToolbox

/// Ηχογραφεί από το τρέχον μικρόφωνο σε 16 kHz, μονοφωνικό, 16-bit, και φτιάχνει WAV.
/// Νέο AVAudioEngine σε κάθε ηχογράφηση, ώστε να ακολουθεί όποιο μικρόφωνο είναι ενεργό εκείνη τη στιγμή.
final class Recorder {
    struct Recording {
        let wav: Data
        let duration: Double
        let peak: Float
    }

    var onLevel: (Float) -> Void = { _ in }
    /// Κάθε νέο κομμάτι ήχου (16 kHz, 16-bit), στο νήμα του ήχου. Το χρησιμοποιεί η αποστολή σε κομμάτια.
    var onSamples: ([Int16]) -> Void = { _ in }

    private var engine: AVAudioEngine?
    private var samples: [Int16] = []
    private var peak: Float = 0
    private let lock = NSLock()
    private let sampleRate = 16_000.0

    /// deviceUID: το μικρόφωνο που διάλεξες στις Ρυθμίσεις· nil = η προεπιλογή του συστήματος.
    func start(deviceUID: String?) throws {
        lock.lock(); samples.removeAll(keepingCapacity: true); peak = 0; lock.unlock()

        let engine = AVAudioEngine()
        let input = engine.inputNode
        if let deviceUID, var deviceID = AudioDevices.deviceID(forUID: deviceUID), let unit = input.audioUnit {
            let status = AudioUnitSetProperty(unit, kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 0,
                                              &deviceID, UInt32(MemoryLayout<AudioDeviceID>.size))
            if status != noErr { Log.write("Δεν επιλέχθηκε το μικρόφωνο (\(status)), χρησιμοποιώ την προεπιλογή") }
        }
        let inputFormat = input.outputFormat(forBus: 0)
        guard inputFormat.sampleRate > 0, inputFormat.channelCount > 0 else {
            throw VoiceError("Δεν βρέθηκε μικρόφωνο")
        }
        guard let outputFormat = AVAudioFormat(commonFormat: .pcmFormatInt16, sampleRate: sampleRate,
                                               channels: 1, interleaved: true),
              let converter = AVAudioConverter(from: inputFormat, to: outputFormat) else {
            throw VoiceError("Δεν στήθηκε η μετατροπή ήχου")
        }

        input.installTap(onBus: 0, bufferSize: 1024, format: inputFormat) { [weak self] buffer, _ in
            guard let self else { return }
            var level: Float = 0
            if let channel = buffer.floatChannelData?[0], buffer.frameLength > 0 {
                var sum: Float = 0
                for i in 0..<Int(buffer.frameLength) { sum += channel[i] * channel[i] }
                level = (sum / Float(buffer.frameLength)).squareRoot()
            }

            let capacity = AVAudioFrameCount(Double(buffer.frameLength) * self.sampleRate / inputFormat.sampleRate + 64)
            guard let converted = AVAudioPCMBuffer(pcmFormat: outputFormat, frameCapacity: capacity) else { return }
            var supplied = false
            var error: NSError?
            converter.convert(to: converted, error: &error) { _, status in
                if supplied { status.pointee = .noDataNow; return nil }
                supplied = true
                status.pointee = .haveData
                return buffer
            }
            if let data = converted.int16ChannelData?[0], converted.frameLength > 0 {
                let chunk = UnsafeBufferPointer(start: data, count: Int(converted.frameLength))
                self.lock.lock()
                self.samples.append(contentsOf: chunk)
                self.onSamples(Array(chunk))
                self.peak = max(self.peak, level)
                self.lock.unlock()
            }
            DispatchQueue.main.async { self.onLevel(level) }
        }

        engine.prepare()
        try engine.start()
        self.engine = engine
    }

    func stop() -> Recording {
        if let engine {
            engine.inputNode.removeTap(onBus: 0)
            engine.stop()
        }
        engine = nil
        lock.lock()
        let captured = samples
        let capturedPeak = peak
        samples.removeAll()
        lock.unlock()
        return Recording(wav: Self.wav(from: captured, sampleRate: Int(sampleRate)),
                         duration: Double(captured.count) / sampleRate,
                         peak: capturedPeak)
    }

    func cancel() { _ = stop() }

    /// Πόσα δείγματα έχουν έρθει από το μικρόφωνο σε αυτή την ηχογράφηση (0 = το μικρόφωνο δεν δίνει ήχο).
    var capturedSamples: Int { lock.lock(); defer { lock.unlock() }; return samples.count }

    static func wav(from samples: [Int16], sampleRate: Int = 16_000) -> Data {
        var data = Data()
        func append<T: FixedWidthInteger>(_ value: T) {
            var little = value.littleEndian
            data.append(Data(bytes: &little, count: MemoryLayout<T>.size))
        }
        let byteCount = samples.count * 2
        data.append("RIFF".data(using: .ascii)!)
        append(UInt32(36 + byteCount))
        data.append("WAVEfmt ".data(using: .ascii)!)
        append(UInt32(16)); append(UInt16(1)); append(UInt16(1))
        append(UInt32(sampleRate)); append(UInt32(sampleRate * 2))
        append(UInt16(2)); append(UInt16(16))
        data.append("data".data(using: .ascii)!)
        append(UInt32(byteCount))
        samples.withUnsafeBufferPointer { data.append(Data(buffer: $0)) }
        return data
    }
}
