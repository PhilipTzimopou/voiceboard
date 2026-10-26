import AppKit
import AVFoundation
import ApplicationServices
import Combine

final class AppDelegate: NSObject, NSApplicationDelegate {
    private struct ActiveRecording {
        let handsFree: Bool
        let started: Date
        let pipeline: DictationPipeline
        var restartedMicrophone = false
    }

    /// Μία μεταγραφή σε εξέλιξη. Τελειώνει μία φορά, είτε με κείμενο είτε με σφάλμα είτε με λήξη χρόνου.
    private final class Job {
        var done = false
        var fellBack = false
    }

    private static let bundleID = "com.philiptzimopou.voiceboard"
    private static let showWindowNotification = Notification.Name("com.philiptzimopou.voiceboard.show")
    private static let maxRecording: TimeInterval = 15 * 60
    private static let transcriptionTimeout: TimeInterval = 45

    private let hotkeys = HotkeyMonitor()
    private let recorder = Recorder()
    private let overlay = Overlay()
    private let model = AppModel()
    private lazy var mainWindow = MainWindowController(model: model)
    private var statusItem: NSStatusItem!
    private var modelObserver: AnyCancellable?
    private var watchdog: Timer?
    private var active: ActiveRecording? { didSet { updateIndicators() } }
    private var pendingJobs = 0 { didSet { updateIndicators() } }

    // MARK: - Εκκίνηση

    func applicationDidFinishLaunching(_ notification: Notification) {
        if CommandLine.arguments.contains("--preview-overlay") { previewOverlay(); return }
        // Μόνο το παράθυρο, χωρίς πλήκτρα, μικρόφωνο ή άδειες: για στιγμιότυπα ελέγχου από αντίγραφο της εφαρμογής.
        if CommandLine.arguments.contains("--preview-window") { wireModel(); showWindow(); return }

        // Μία εκτέλεση μόνο: αν τρέχει ήδη, του λέμε να δείξει το παράθυρο και κλείνουμε.
        let me = ProcessInfo.processInfo.processIdentifier
        if NSRunningApplication.runningApplications(withBundleIdentifier: Self.bundleID)
            .contains(where: { $0.processIdentifier != me }) {
            DistributedNotificationCenter.default().postNotificationName(Self.showWindowNotification, object: nil,
                                                                         userInfo: nil, deliverImmediately: true)
            NSApp.terminate(nil)
            return
        }
        DistributedNotificationCenter.default().addObserver(forName: Self.showWindowNotification, object: nil,
                                                            queue: .main) { [weak self] _ in self?.showWindow() }
        Log.write("Εκκίνηση \(AppInfo.name)")

        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        setIcon(recording: false)
        buildMenu()
        wireModel()

        if !FileManager.default.fileExists(atPath: Paths.vocabulary.path) { importFromWispr(showResult: false) }

        recorder.onLevel = { [weak self] level in self?.overlay.level(level) }
        hotkeys.onPushToTalkKeys = { [weak self] held in self?.pushToTalkKeys(held) }
        hotkeys.onToggleHandsFree = { [weak self] in self?.toggleHandsFree() }
        hotkeys.onCancel = { [weak self] in self?.cancel() }
        hotkeys.configure(pushToTalk: model.pushToTalkCombo, handsFree: model.handsFreeCombo)

        AVCaptureDevice.requestAccess(for: .audio) { [weak self] granted in
            if !granted { Log.write("Δεν δόθηκε άδεια μικροφώνου") }
            DispatchQueue.main.async { self?.refreshPermissions() }
        }
        startHotkeys(prompt: true)
        // Οι άδειες αλλάζουν από τις Ρυθμίσεις συστήματος· τις ελέγχουμε συνέχεια, ώστε το παράθυρο να δείχνει την αλήθεια.
        Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] _ in self?.refreshPermissions() }

        // Το Whisper μένει φορτωμένο στη μνήμη μόνο όσο είναι επιλεγμένο (το large-v3 πιάνει ~3 GB).
        modelObserver = model.$speechModel.sink { speechModel in
            if speechModel == .whisper { WhisperServer.shared.start() } else { WhisperServer.shared.stop() }
        }

        model.refreshDevices()
        showWindow()
        warnIfWisprFlowRunning()
    }

    func applicationWillTerminate(_ notification: Notification) {
        WhisperServer.shared.stop()
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        showWindow()
        return true
    }

    @objc private func showWindow() {
        refreshPermissions()
        mainWindow.show()
    }

    private func wireModel() {
        model.importFromWispr = { [weak self] in self?.importFromWispr(showResult: true) }
        model.shortcutsChanged = { [weak self] in
            guard let self else { return }
            self.hotkeys.configure(pushToTalk: self.model.pushToTalkCombo, handsFree: self.model.handsFreeCombo)
            Log.write("Συντομεύσεις: \(self.model.pushToTalkCombo.label), χωρίς χέρια \(self.model.handsFreeCombo.label)")
        }
        model.captureShortcut = { [weak self] done in self?.hotkeys.capture(done) }
        model.cancelCapture = { [weak self] in self?.hotkeys.cancelCapture() }
        model.openAccessibility = {
            NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility")!)
        }
        model.resetAccessibility = { [weak self] in self?.resetAccessibility() }
        model.openMicrophone = {
            NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone")!)
        }
        model.openLog = {
            if !FileManager.default.fileExists(atPath: Paths.log.path) { Log.write("Αρχείο καταγραφής") }
            NSWorkspace.shared.open(Paths.log)
        }
    }

    private func refreshPermissions() {
        let trusted = AXIsProcessTrusted()
        if trusted && !hotkeys.isRunning && hotkeys.start() {
            Log.write("Δόθηκε η άδεια Προσβασιμότητας, οι συντομεύσεις είναι ενεργές")
        }
        let running = trusted && hotkeys.isRunning
        if model.accessibilityGranted != trusted { model.accessibilityGranted = trusted }
        if model.shortcutsActive != running { model.shortcutsActive = running }
        let mic = AVCaptureDevice.authorizationStatus(for: .audio) == .authorized
        if model.microphoneGranted != mic { model.microphoneGranted = mic }
    }

    private func startHotkeys(prompt: Bool) {
        let options = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: prompt] as CFDictionary
        if AXIsProcessTrustedWithOptions(options) && hotkeys.start() {
            Log.write("Οι συντομεύσεις είναι ενεργές")
        } else {
            Log.write("Περιμένω την άδεια Προσβασιμότητας")
        }
        refreshPermissions()
    }

    /// Κάθε νέο χτίσιμο έχει άλλη τοπική υπογραφή και η παλιά άδεια δεν ισχύει, αν και ο διακόπτης φαίνεται ανοιχτός.
    /// Σβήνουμε μόνο τη δική μας εγγραφή και ζητάμε καινούργια.
    private func resetAccessibility() {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/tccutil")
        process.arguments = ["reset", "Accessibility", Self.bundleID]
        try? process.run()
        process.waitUntilExit()
        Log.write("Επαναφορά άδειας Προσβασιμότητας (tccutil: \(process.terminationStatus))")
        if process.terminationStatus != 0 {
            model.lastError = "Η αυτόματη επαναφορά δεν έγινε. Στη λίστα Προσβασιμότητας επίλεξε το \(AppInfo.name), πάτα «−», και μετά «+» για να το ξαναπροσθέσεις από τον φάκελο Εφαρμογές του λογαριασμού σου."
        }
        startHotkeys(prompt: true)
        model.openAccessibility()
    }

    private func warnIfWisprFlowRunning() {
        guard NSWorkspace.shared.runningApplications.contains(where: { $0.bundleIdentifier == "com.electron.wispr-flow" }) else { return }
        let alert = NSAlert()
        alert.messageText = "Το Wispr Flow είναι ανοιχτό"
        alert.informativeText = "Αν χρησιμοποιεί την ίδια συντόμευση, θα ηχογραφούν και οι δύο εφαρμογές μαζί. Κλείσε το Wispr Flow από το εικονίδιό του στη γραμμή μενού, ή άλλαξε τα πλήκτρα στις Ρυθμίσεις του Voiceboard."
        alert.addButton(withTitle: "Εντάξει")
        NSApp.activate(ignoringOtherApps: true)
        alert.runModal()
    }

    // MARK: - Ηχογράφηση

    private func pushToTalkKeys(_ held: Bool) {
        if held, active == nil {
            startRecording(handsFree: false)
        } else if !held, let active, !active.handsFree {
            finishRecording()
        }
    }

    private func toggleHandsFree() {
        if active == nil { startRecording(handsFree: true) } else { finishRecording() }
    }

    private func cancel() {
        guard let current = active else { return }
        recorder.cancel()
        current.pipeline.cancel()
        stopWatchdog()
        active = nil
        hotkeys.setRecording(false, pushToTalk: false)
        play("Funk")
        overlay.flash("Ακυρώθηκε")
        Log.write("Ακύρωση ηχογράφησης")
    }

    private func startRecording(handsFree: Bool) {
        guard AVCaptureDevice.authorizationStatus(for: .audio) == .authorized else {
            fail("Δεν υπάρχει άδεια μικροφώνου")
            showWindow()
            return
        }
        let pipeline = DictationPipeline(transcriber: transcriber(for: model.speechModel), vocabulary: model.vocabulary)
        recorder.onSamples = { [weak pipeline] samples in pipeline?.append(samples) }
        do {
            try recorder.start(deviceUID: model.microphoneUID.isEmpty ? nil : model.microphoneUID)
        } catch {
            fail("Δεν ξεκίνησε η ηχογράφηση: \(error.localizedDescription)")
            return
        }
        hotkeys.setRecording(true, pushToTalk: !handsFree)
        active = ActiveRecording(handsFree: handsFree, started: Date(), pipeline: pipeline)
        overlay.show(.recording(handsFree: handsFree))
        play("Tink")
        startWatchdog()
    }

    /// Ελέγχει την ηχογράφηση πέντε φορές το δευτερόλεπτο:
    /// - αν άφησες τα πλήκτρα αλλά το μήνυμα χάθηκε, σταματά (αλλιώς η ηχογράφηση «κολλάει»)
    /// - αν το μικρόφωνο δεν δίνει ήχο, ξαναξεκινά με την προεπιλογή του συστήματος και το δείχνει
    /// - στα 15 λεπτά τελειώνει μόνη της
    private func startWatchdog() {
        stopWatchdog()
        watchdog = Timer.scheduledTimer(withTimeInterval: 0.2, repeats: true) { [weak self] _ in self?.checkRecording() }
        RunLoop.main.add(watchdog!, forMode: .common)
    }

    private func stopWatchdog() {
        watchdog?.invalidate()
        watchdog = nil
    }

    private func checkRecording() {
        guard let current = active else { stopWatchdog(); return }
        let elapsed = Date().timeIntervalSince(current.started)
        if !current.handsFree, elapsed > 0.3 {
            let flags = CGEventSource.flagsState(.combinedSessionState)
            if !model.pushToTalkCombo.stillHeld(flags) {
                Log.write("Τα πλήκτρα έχουν αφεθεί χωρίς να έρθει μήνυμα, τελειώνω την ηχογράφηση")
                finishRecording()
                return
            }
        }
        let silentMicrophone = recorder.capturedSamples == 0
        if silentMicrophone, elapsed > 1.5, !current.restartedMicrophone {
            Log.write("Το μικρόφωνο δεν δίνει ήχο, ξαναξεκινώ με την προεπιλογή του συστήματος")
            active?.restartedMicrophone = true
            recorder.cancel()
            try? recorder.start(deviceUID: nil)
        }
        overlay.noAudio = silentMicrophone && elapsed > 3
        if elapsed > Self.maxRecording {
            Log.write("Η ηχογράφηση έφτασε τα 15 λεπτά, τελειώνει")
            finishRecording()
        }
    }

    private func finishRecording() {
        guard let current = active else { return }
        stopWatchdog()
        hotkeys.setRecording(false, pushToTalk: false)
        let recording = recorder.stop()
        let elapsed = Date().timeIntervalSince(current.started)
        active = nil
        play("Pop")

        guard recording.duration >= 0.35, recording.peak >= 0.004 else {
            Log.write(String(format: "Παράλειψη: %.2f δευτ., μέγιστη ένταση %.4f", recording.duration, recording.peak))
            current.pipeline.cancel()
            if recording.duration == 0 && elapsed > 1 {
                overlay.flash("Δεν ακούω μικρόφωνο", warning: true, seconds: 2.5)
            } else if recording.duration >= 0.35 {
                overlay.flash("Δεν ακούστηκε φωνή", warning: true)
            } else {
                updateIndicators()
            }
            return
        }

        let speechModel = model.speechModel
        let vocabulary = model.vocabulary
        let started = Date()
        let job = Job()
        pendingJobs += 1

        func complete(_ result: Result<String, Error>, _ used: SpeechModel) {
            DispatchQueue.main.async {
                guard !job.done else { return }
                job.done = true
                self.pendingJobs -= 1
                switch result {
                case .success(let text):
                    self.deliver(text, model: used, seconds: Date().timeIntervalSince(started), audio: recording.duration)
                case .failure(let error):
                    self.fail("Η μεταγραφή απέτυχε: \(error.localizedDescription)")
                }
            }
        }
        func fallBack(after error: Error) {
            DispatchQueue.main.async {
                guard !job.done, !job.fellBack, speechModel == .scribe else { complete(.failure(error), speechModel); return }
                job.fellBack = true
                Log.write("Scribe v2: \(error.localizedDescription), δοκιμάζω Whisper")
                WhisperTranscriber().transcribe(wav: recording.wav, vocabulary: vocabulary) { complete($0, .whisper) }
            }
        }

        current.pipeline.finish { result in
            switch result {
            case .success: complete(result, speechModel)
            case .failure(let error): fallBack(after: error)
            }
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + Self.transcriptionTimeout) {
            guard !job.done else { return }
            fallBack(after: VoiceError("δεν απάντησε σε \(Int(Self.transcriptionTimeout)) δευτ."))
        }
    }

    private func transcriber(for speechModel: SpeechModel) -> Transcriber {
        switch speechModel {
        case .scribe: return ScribeTranscriber()
        case .whisper: return WhisperTranscriber()
        }
    }

    private func deliver(_ raw: String, model speechModel: SpeechModel, seconds: Double, audio: Double) {
        let text = model.vocabulary.apply(to: raw.trimmingCharacters(in: .whitespacesAndNewlines))
        Log.write(String(format: "%@: %.1f δευτ. ήχου, %.1f δευτ. αναμονή μετά το άφημα, %d χαρακτήρες", speechModel.rawValue, audio, seconds, text.count))
        guard !text.isEmpty else {
            if active == nil { overlay.flash("Δεν ακούστηκε φωνή", warning: true) }
            return
        }
        model.lastError = ""
        model.history.insert(HistoryEntry(date: Date(), text: text, words: HistoryStore.wordCount(text),
                                          seconds: audio, model: speechModel.rawValue), at: 0)
        Paster.paste(text)
    }

    private func fail(_ message: String) {
        model.lastError = message
        Log.write("Σφάλμα: \(message)")
        NSSound.beep()
        if active == nil { overlay.flash("Σφάλμα μεταγραφής", warning: true, seconds: 3) }
    }

    private func play(_ name: String) {
        guard model.soundsEnabled, let sound = NSSound(named: NSSound.Name(name)) else { return }
        sound.volume = 0.25
        sound.play()
    }

    /// Η ένδειξη και το εικονίδιο ακολουθούν πάντα την πραγματική κατάσταση:
    /// ηχογράφηση > μεταγραφή σε εξέλιξη > τίποτα.
    private func updateIndicators() {
        if let active {
            model.phase = .recording
            setIcon(recording: true)
            if overlay.state != .recording(handsFree: active.handsFree) { overlay.show(.recording(handsFree: active.handsFree)) }
        } else if pendingJobs > 0 {
            model.phase = .transcribing
            setIcon(recording: false)
            if case .notice? = overlay.state {} else { overlay.show(.transcribing) }
        } else {
            model.phase = .idle
            setIcon(recording: false)
            if case .notice? = overlay.state {} else { overlay.hide() }
        }
    }

    private func setIcon(recording: Bool) {
        let image = NSImage(systemSymbolName: recording ? "record.circle.fill" : "waveform.path",
                            accessibilityDescription: AppInfo.name)
        image?.isTemplate = true
        statusItem?.button?.image = image
    }

    /// Δείχνει την ένδειξη χωρίς ηχογράφηση, για έλεγχο της εμφάνισης.
    private func previewOverlay() {
        overlay.show(.recording(handsFree: false))
        var step = 0
        Timer.scheduledTimer(withTimeInterval: 0.05, repeats: true) { [weak self] timer in
            guard let self else { return }
            step += 1
            if step > 25 { self.overlay.level(Float(abs(sin(Double(step) / 3))) * 0.1) }  // πρώτα σιωπή, μετά φωνή
            if step == 50 { self.overlay.show(.recording(handsFree: true)) }
            if step == 100 { self.overlay.noAudio = true }
            if step == 150 { self.overlay.show(.transcribing) }
            if step == 200 { self.overlay.flash("Ακυρώθηκε", seconds: 3) }
            if step == 250 { timer.invalidate(); NSApp.terminate(nil) }
        }
    }

    // MARK: - Μενού και λεξιλόγιο

    private func buildMenu() {
        let menu = NSMenu()
        let open = NSMenuItem(title: "Άνοιγμα \(AppInfo.name)", action: #selector(showWindow), keyEquivalent: "")
        open.target = self
        menu.addItem(open)
        menu.addItem(.separator())
        menu.addItem(NSMenuItem(title: "Έξοδος", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q"))
        statusItem.menu = menu
    }

    private func importFromWispr(showResult: Bool) {
        var updated = model.vocabulary
        do {
            let counts = try WisprImporter.importInto(&updated)
            model.vocabulary = updated
            let message = "Ήρθαν από το Wispr Flow \(counts.words) λέξεις, \(counts.replacements) διορθώσεις και \(counts.snippets) έτοιμα κείμενα."
            Log.write(message)
            if showResult { showAlert("Έτοιμο", message) }
        } catch {
            Log.write("Η εισαγωγή από Wispr Flow απέτυχε: \(error.localizedDescription)")
            if showResult { showAlert("Η εισαγωγή απέτυχε", error.localizedDescription) }
        }
    }

    private func showAlert(_ title: String, _ text: String) {
        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = text
        NSApp.activate(ignoringOtherApps: true)
        alert.runModal()
    }
}
