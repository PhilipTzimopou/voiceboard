import AppKit
import CoreGraphics

/// Οι συντομεύσεις (αλλάζουν από τις Ρυθμίσεις, Shortcuts.swift):
/// - κράτα τα πλήκτρα (προεπιλογή Control + Option): ηχογράφηση όσο τα κρατάς
/// - κουμπί ποντικιού (προεπιλογή μεσαίο κλικ): ηχογράφηση χωρίς χέρια, ξανά κλικ για τέλος
/// - Esc: ακύρωση
///
/// Η παρακολούθηση τρέχει σε δικό της νήμα, ώστε τίποτα στο κύριο νήμα (παράθυρο, μικρόφωνο)
/// να μην την καθυστερεί. Όλες οι ειδοποιήσεις φτάνουν στο κύριο νήμα.
final class HotkeyMonitor {
    /// Κάθε αλλαγή στα πλήκτρα: true όταν κρατάς ακριβώς τα πλήκτρα της συντόμευσης.
    var onPushToTalkKeys: (Bool) -> Void = { _ in }
    var onToggleHandsFree: () -> Void = {}
    /// Esc ή συντόμευση άλλης εφαρμογής (τα πλήκτρα μας + άλλο πλήκτρο) αμέσως μετά το πάτημα.
    var onCancel: () -> Void = {}

    private let lock = NSLock()
    private var recording = false
    private var pushToTalkStartedAt: Date?
    private var pushToTalk = "control-option"
    private var handsFreeButton: Int64? = 2
    private var tap: CFMachPort?
    private var thread: Thread?

    var isRunning: Bool { lock.lock(); defer { lock.unlock() }; return tap != nil }

    /// Ενημερώνεται από τον AppDelegate, ώστε το Esc να «πιάνεται» μόνο όταν ηχογραφεί.
    func setRecording(_ value: Bool, pushToTalk: Bool) {
        lock.lock()
        recording = value
        pushToTalkStartedAt = value && pushToTalk ? Date() : nil
        lock.unlock()
    }

    /// Οι συντομεύσεις από τις Ρυθμίσεις. Αλλάζουν αμέσως, χωρίς επανεκκίνηση.
    func configure(pushToTalk: String, handsFree: String) {
        lock.lock()
        self.pushToTalk = Shortcuts.pushToTalk(pushToTalk).id
        handsFreeButton = Shortcuts.handsFree(handsFree).button
        lock.unlock()
    }

    func start() -> Bool {
        if isRunning { return true }
        let ready = DispatchSemaphore(value: 0)
        var created = false
        let thread = Thread { [weak self] in
            guard let self else { ready.signal(); return }
            let types: [CGEventType] = [.flagsChanged, .keyDown, .otherMouseDown, .otherMouseUp]
            let mask = types.reduce(CGEventMask(0)) { $0 | (CGEventMask(1) << $1.rawValue) }
            let callback: CGEventTapCallBack = { _, type, event, userInfo in
                guard let userInfo else { return Unmanaged.passUnretained(event) }
                return Unmanaged<HotkeyMonitor>.fromOpaque(userInfo).takeUnretainedValue().handle(type: type, event: event)
            }
            guard let tap = CGEvent.tapCreate(tap: .cgSessionEventTap, place: .headInsertEventTap, options: .defaultTap,
                                              eventsOfInterest: mask, callback: callback,
                                              userInfo: Unmanaged.passUnretained(self).toOpaque()) else {
                ready.signal()
                return
            }
            self.lock.lock(); self.tap = tap; self.lock.unlock()
            let source = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, tap, 0)
            CFRunLoopAddSource(CFRunLoopGetCurrent(), source, .commonModes)
            CGEvent.tapEnable(tap: tap, enable: true)
            created = true
            ready.signal()
            CFRunLoopRun()
        }
        thread.name = "philip-voice.hotkeys"
        thread.qualityOfService = .userInteractive
        self.thread = thread
        thread.start()
        ready.wait()
        return created
    }

    private func handle(type: CGEventType, event: CGEvent) -> Unmanaged<CGEvent>? {
        switch type {
        case .tapDisabledByTimeout, .tapDisabledByUserInput:
            lock.lock(); let tap = self.tap; lock.unlock()
            if let tap { CGEvent.tapEnable(tap: tap, enable: true) }
            Log.write("Η παρακολούθηση πλήκτρων απενεργοποιήθηκε από το σύστημα, ξαναξεκίνησε")

        case .flagsChanged:
            lock.lock(); let preset = pushToTalk; lock.unlock()
            let held = Shortcuts.matches(preset, event.flags)
            DispatchQueue.main.async { self.onPushToTalkKeys(held) }

        case .keyDown:
            let keyCode = event.getIntegerValueField(.keyboardEventKeycode)
            lock.lock()
            let isRecording = recording
            let startedAt = pushToTalkStartedAt
            lock.unlock()
            if keyCode == 53, isRecording {
                DispatchQueue.main.async { self.onCancel() }
                return nil
            }
            // Τα πλήκτρα μας + άλλο πλήκτρο αμέσως μετά το πάτημα = συντόμευση άλλης εφαρμογής.
            // Αργότερα (ενώ ήδη μιλάς) δεν ακυρώνουμε, για να μη χάνεται υπαγόρευση από κάποιο τυχαίο πλήκτρο.
            if let startedAt, Date().timeIntervalSince(startedAt) < 0.6 {
                DispatchQueue.main.async { self.onCancel() }
            }

        case .otherMouseDown:
            lock.lock(); let button = handsFreeButton; lock.unlock()
            if let button, event.getIntegerValueField(.mouseEventButtonNumber) == button {
                DispatchQueue.main.async { self.onToggleHandsFree() }
                return nil
            }

        case .otherMouseUp:
            lock.lock(); let button = handsFreeButton; lock.unlock()
            if let button, event.getIntegerValueField(.mouseEventButtonNumber) == button { return nil }

        default:
            break
        }
        return Unmanaged.passUnretained(event)
    }
}
