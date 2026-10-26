import AppKit
import CoreGraphics

/// Οι συντομεύσεις, όποιες διάλεξε ο χρήστης στις Ρυθμίσεις (Combo στο Shortcuts.swift):
/// - κράτα (προεπιλογή Control + Option): ηχογράφηση όσο τα κρατάς
/// - χωρίς χέρια (προεπιλογή μεσαίο κλικ): ηχογράφηση με ένα πάτημα, ξανά πάτημα για τέλος
/// - Esc: ακύρωση
///
/// Η παρακολούθηση τρέχει σε δικό της νήμα, ώστε τίποτα στο κύριο νήμα (παράθυρο, μικρόφωνο)
/// να μην την καθυστερεί. Όλες οι ειδοποιήσεις φτάνουν στο κύριο νήμα.
final class HotkeyMonitor {
    /// Κάθε αλλαγή: true όταν κρατάς τη συντόμευση «κράτα και μίλα».
    var onPushToTalkKeys: (Bool) -> Void = { _ in }
    var onToggleHandsFree: () -> Void = {}
    /// Esc ή συντόμευση άλλης εφαρμογής (τα πλήκτρα μας + άλλο πλήκτρο) αμέσως μετά το πάτημα.
    var onCancel: () -> Void = {}

    private let lock = NSLock()
    private var recording = false
    private var pushToTalkStartedAt: Date?
    private var pushToTalk = Combo.parse(Combo.defaultPushToTalk)
    private var handsFree = Combo.parse(Combo.defaultHandsFree)
    /// Όσο ο χρήστης γράφει νέα συντόμευση στις Ρυθμίσεις: τα πλήκτρα πάνε εδώ και όχι στις εφαρμογές.
    private var capture: ((Combo?) -> Void)?
    private var tap: CFMachPort?
    private var thread: Thread?

    // Μόνο από το νήμα της παρακολούθησης
    private var held = false
    private var handsFreeHeld = false
    private var swallowed = Set<Int64>()   // πλήκτρα της συντόμευσης που κρατάμε μακριά από τις εφαρμογές
    private var captureSides = [(left: Bool, right: Bool)](repeating: (false, false), count: 4)
    private var captureFn = false
    private var captureKey: Int64 = -1
    private var captureLabel = ""
    private var captureKeyDown = false

    var isRunning: Bool { lock.lock(); defer { lock.unlock() }; return tap != nil }

    /// Ενημερώνεται από τον AppDelegate, ώστε το Esc να «πιάνεται» μόνο όταν ηχογραφεί.
    func setRecording(_ value: Bool, pushToTalk: Bool) {
        lock.lock()
        recording = value
        pushToTalkStartedAt = value && pushToTalk ? Date() : nil
        lock.unlock()
    }

    /// Οι συντομεύσεις από τις Ρυθμίσεις. Αλλάζουν αμέσως, χωρίς επανεκκίνηση.
    func configure(pushToTalk: Combo, handsFree: Combo) {
        lock.lock()
        self.pushToTalk = pushToTalk
        self.handsFree = handsFree
        lock.unlock()
    }

    /// Ξεκινά την καταγραφή νέας συντόμευσης: το επόμενο πάτημα (πλήκτρα ως το άφημα, ή κουμπί ποντικιού) επιστρέφει
    /// στο done, στο κύριο νήμα. Esc = ακύρωση (nil). Το αριστερό και το δεξί κλικ περνούν κανονικά, για το «Άκυρο».
    func capture(_ done: @escaping (Combo?) -> Void) {
        lock.lock(); capture = done; lock.unlock()
    }

    func cancelCapture() {
        lock.lock(); capture = nil; lock.unlock()
    }

    func start() -> Bool {
        if isRunning { return true }
        let ready = DispatchSemaphore(value: 0)
        var created = false
        let thread = Thread { [weak self] in
            guard let self else { ready.signal(); return }
            let types: [CGEventType] = [.flagsChanged, .keyDown, .keyUp, .otherMouseDown, .otherMouseUp]
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

    private func setHeld(_ value: Bool) {
        guard value != held else { return }
        held = value
        DispatchQueue.main.async { self.onPushToTalkKeys(value) }
    }

    private func toggleHandsFree() {
        DispatchQueue.main.async { self.onToggleHandsFree() }
    }

    private func handle(type: CGEventType, event: CGEvent) -> Unmanaged<CGEvent>? {
        if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
            lock.lock(); let tap = self.tap; lock.unlock()
            if let tap { CGEvent.tapEnable(tap: tap, enable: true) }
            Log.write("Η παρακολούθηση πλήκτρων απενεργοποιήθηκε από το σύστημα, ξαναξεκίνησε")
            return Unmanaged.passUnretained(event)
        }

        lock.lock()
        let ptt = pushToTalk, hands = handsFree, done = capture
        let isRecording = recording, startedAt = pushToTalkStartedAt
        lock.unlock()

        if let done { return captureEvent(type: type, event: event, done: done) }

        switch type {
        case .flagsChanged:
            let flags = event.flags
            // Συντόμευση μόνο με πλήκτρα ελέγχου: κρατάς = ηχογραφεί.
            if ptt.key < 0 && !ptt.isMouse && ptt.hasModifiers { setHeld(ptt.modifiersMatch(flags)) }
            // Με πλήκτρο: αν αφήσεις ένα από τα πλήκτρα ελέγχου, τελειώνει.
            else if held && ptt.key >= 0 && !ptt.modifiersMatch(flags) { setHeld(false) }
            // Χωρίς χέρια μόνο με πλήκτρα ελέγχου: κάθε φορά που πατιούνται όλα μαζί.
            if hands.key < 0 && !hands.isMouse && hands.hasModifiers {
                let now = hands.modifiersMatch(flags)
                if now && !handsFreeHeld { toggleHandsFree() }
                handsFreeHeld = now
            }

        case .keyDown:
            let keyCode = event.getIntegerValueField(.keyboardEventKeycode)
            // Πλήκτρο που ανήκει σε συντόμευση: δεν φτάνει στην εφαρμογή (ούτε οι επαναλήψεις όσο το κρατάς, ούτε το άφημα).
            if ptt.key == keyCode && ptt.modifiersMatch(event.flags) {
                swallowed.insert(keyCode)
                setHeld(true)
                return nil
            }
            if hands.key == keyCode && hands.modifiersMatch(event.flags) {
                if swallowed.insert(keyCode).inserted { toggleHandsFree() }
                return nil
            }
            if swallowed.contains(keyCode) { return nil }
            if keyCode == 53, isRecording {
                DispatchQueue.main.async { self.onCancel() }
                return nil
            }
            // Τα πλήκτρα μας + άλλο πλήκτρο αμέσως μετά το πάτημα = συντόμευση άλλης εφαρμογής.
            // Αργότερα (ενώ ήδη μιλάς) δεν ακυρώνουμε, για να μη χάνεται υπαγόρευση από κάποιο τυχαίο πλήκτρο.
            if let startedAt, Date().timeIntervalSince(startedAt) < 0.6 {
                DispatchQueue.main.async { self.onCancel() }
            }

        case .keyUp:
            let keyCode = event.getIntegerValueField(.keyboardEventKeycode)
            if swallowed.remove(keyCode) != nil {
                if ptt.key == keyCode { setHeld(false) }
                return nil
            }

        case .otherMouseDown, .otherMouseUp:
            let button = event.getIntegerValueField(.mouseEventButtonNumber)
            let down = type == .otherMouseDown
            if ptt.mouse == button {
                setHeld(down)
                return nil   // το κλικ δεν φτάνει στην εφαρμογή από κάτω
            }
            if hands.mouse == button {
                if down { toggleHandsFree() }
                return nil
            }

        default:
            break
        }
        return Unmanaged.passUnretained(event)
    }

    /// Καταγραφή νέας συντόμευσης: μαζεύουμε ό,τι πατήθηκε μέχρι να αφεθούν όλα. Τίποτα δεν φτάνει στις εφαρμογές.
    private func captureEvent(type: CGEventType, event: CGEvent, done: @escaping (Combo?) -> Void) -> Unmanaged<CGEvent>? {
        let flags = event.flags
        let sides = Combo.pressedSides(flags)
        switch type {
        case .otherMouseDown:
            finishCapture(done, Combo(mouse: event.getIntegerValueField(.mouseEventButtonNumber)))
            return nil
        case .otherMouseUp:
            return nil
        case .keyDown:
            let keyCode = event.getIntegerValueField(.keyboardEventKeycode)
            if keyCode == 53 && !sides.contains(where: { $0.left || $0.right }) { finishCapture(done, nil); return nil }
            captureKey = keyCode
            captureKeyDown = true
            let characters = NSEvent(cgEvent: event)?.charactersIgnoringModifiers?.uppercased() ?? ""
            captureLabel = characters.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? "" : characters
        case .keyUp:
            captureKeyDown = false
        case .flagsChanged:
            break
        default:
            return Unmanaged.passUnretained(event)
        }
        for i in 0..<4 {
            captureSides[i].left = captureSides[i].left || sides[i].left
            captureSides[i].right = captureSides[i].right || sides[i].right
        }
        captureFn = captureFn || (flags.contains(.maskSecondaryFn) && type == .flagsChanged)
        // Όταν αφεθούν όλα, η συντόμευση είναι έτοιμη.
        let anyDown = sides.contains(where: { $0.left || $0.right }) || captureKeyDown
            || (type == .flagsChanged && flags.contains(.maskSecondaryFn))
        let anything = captureKey >= 0 || captureFn || captureSides.contains(where: { $0.left || $0.right })
        if !anyDown && anything {
            let groups = captureSides.filter { $0.left || $0.right }.count + (captureFn ? 1 : 0)
            // Ένα πλήκτρο ελέγχου μόνο του (π.χ. δεξί Option): κρατάμε την πλευρά. Σε συνδυασμό: όποια πλευρά.
            let sided = groups == 1 && captureKey < 0
            func pick(_ s: (left: Bool, right: Bool)) -> Combo.Side {
                if !s.left && !s.right { return .none }
                if !sided || (s.left && s.right) { return .any }
                return s.left ? .left : .right
            }
            var combo = Combo(control: pick(captureSides[0]), option: pick(captureSides[1]),
                              command: pick(captureSides[2]), shift: pick(captureSides[3]))
            combo.fn = captureFn && captureKey < 0
            combo.key = captureKey
            combo.keyLabel = captureLabel
            finishCapture(done, combo)
        }
        return nil
    }

    private func finishCapture(_ done: @escaping (Combo?) -> Void, _ combo: Combo?) {
        lock.lock(); capture = nil; lock.unlock()
        captureSides = [(left: Bool, right: Bool)](repeating: (false, false), count: 4)
        captureFn = false
        captureKey = -1
        captureLabel = ""
        captureKeyDown = false
        DispatchQueue.main.async { done(combo) }
    }
}
