import CoreGraphics
import Foundation

/// Μια συντόμευση που διάλεξε ο χρήστης: πλήκτρα ελέγχου (Control, Option, Command, Shift, Fn) με προαιρετικά ένα ακόμα
/// πλήκτρο, ή ένα κουμπί του ποντικιού. Αποθηκεύεται ως κείμενο, π.χ. "control+option", "roption",
/// "control+key:49=Space", "mouse:3". Ένα πλήκτρο ελέγχου μόνο του κρατά την πλευρά του (π.χ. δεξί Option)·
/// σε συνδυασμό μετράει όποια πλευρά κι αν πατήσεις.
struct Combo: Equatable {
    enum Side: Equatable { case none, any, left, right }

    var control = Side.none, option = Side.none, command = Side.none, shift = Side.none
    var fn = false
    var key: Int64 = -1          // keycode του macOS· -1 = κανένα
    var keyLabel = ""
    var mouse: Int64 = 0         // αριθμός κουμπιού (2 = μεσαίο, 3 = πίσω, 4 = μπροστά, …)· 0 = κανένα

    static let none = Combo()
    static let defaultPushToTalk = "control+option"
    static let defaultHandsFree = "mouse:2"

    var isEmpty: Bool { self == .none }
    var isMouse: Bool { mouse != 0 }
    var hasModifiers: Bool { control != .none || option != .none || command != .none || shift != .none || fn }

    static let functionKeys: [Int64: String] = [
        122: "F1", 120: "F2", 99: "F3", 118: "F4", 96: "F5", 97: "F6", 98: "F7", 100: "F8", 101: "F9", 109: "F10",
        103: "F11", 111: "F12", 105: "F13", 107: "F14", 113: "F15", 106: "F16", 64: "F17", 79: "F18", 80: "F19", 90: "F20",
    ]
    static let namedKeys: [Int64: String] = [
        49: "Space", 36: "Return", 48: "Tab", 51: "Delete", 117: "Forward Delete", 53: "Esc",
        123: "←", 124: "→", 125: "↓", 126: "↑", 115: "Home", 119: "End", 116: "Page Up", 121: "Page Down",
    ]

    /// Κάτι που δεν πατιέται κατά λάθος όταν γράφεις: με Control, Option, Command ή Fn, ή πλήκτρο F, ή κουμπί ποντικιού.
    /// Σκέτο Shift ή σκέτο γράμμα θα ξεκινούσαν ηχογράφηση σε κάθε κεφαλαίο ή λέξη.
    var isSafe: Bool {
        isMouse || Combo.functionKeys[key] != nil || control != .none || option != .none || command != .none || fn
    }

    static func parse(_ text: String?) -> Combo {
        guard var text, !text.isEmpty else { return .none }
        // Οι έτοιμες επιλογές της έκδοσης 1.1
        let legacy = ["control-option": "control+option", "control-command": "control+command", "option-command": "option+command",
                      "right-option": "roption", "right-command": "rcommand",
                      "middle": "mouse:2", "back": "mouse:3", "forward": "mouse:4", "none": ""]
        text = legacy[text] ?? text
        var combo = Combo()
        for part in text.split(separator: "+").map(String.init) {
            func side(_ name: String) -> Side? {
                if part == name { return .any }
                if part == "l" + name { return .left }
                if part == "r" + name { return .right }
                return nil
            }
            if let s = side("control") { combo.control = s }
            else if let s = side("option") { combo.option = s }
            else if let s = side("command") { combo.command = s }
            else if let s = side("shift") { combo.shift = s }
            else if part == "fn" { combo.fn = true }
            else if part.hasPrefix("mouse:"), let button = Int64(part.dropFirst(6)) { return Combo(mouse: button) }
            else if part.hasPrefix("key:") {
                let pieces = part.dropFirst(4).split(separator: "=", maxSplits: 1).map(String.init)
                if let code = Int64(pieces[0]) {
                    combo.key = code
                    combo.keyLabel = pieces.count > 1 ? (pieces[1].removingPercentEncoding ?? pieces[1]) : ""
                }
            }
        }
        return combo
    }

    var string: String {
        if isMouse { return "mouse:\(mouse)" }
        var parts: [String] = []
        func add(_ side: Side, _ name: String) {
            switch side {
            case .none: break
            case .any: parts.append(name)
            case .left: parts.append("l" + name)
            case .right: parts.append("r" + name)
            }
        }
        add(control, "control"); add(option, "option"); add(command, "command"); add(shift, "shift")
        if fn { parts.append("fn") }
        if key >= 0 {
            let label = keyLabel.addingPercentEncoding(withAllowedCharacters: .alphanumerics) ?? ""
            parts.append(label.isEmpty ? "key:\(key)" : "key:\(key)=\(label)")
        }
        return parts.joined(separator: "+")
    }

    var keys: [String] {
        if isEmpty { return ["Καμία"] }
        if isMouse {
            switch mouse {
            case 2: return ["Μεσαίο κλικ"]
            case 3: return ["Πλαϊνό κουμπί ποντικιού (πίσω)"]
            case 4: return ["Πλαϊνό κουμπί ποντικιού (μπροστά)"]
            default: return ["Κουμπί ποντικιού \(mouse + 1)"]
            }
        }
        var names: [String] = []
        func add(_ side: Side, _ name: String) {
            switch side {
            case .none: break
            case .any: names.append(name)
            case .left: names.append("Αριστερό " + name)
            case .right: names.append("Δεξί " + name)
            }
        }
        add(control, "⌃ Control"); add(option, "⌥ Option"); add(command, "⌘ Command"); add(shift, "⇧ Shift")
        if fn { names.append("Fn") }
        if key >= 0 { names.append(Combo.functionKeys[key] ?? Combo.namedKeys[key] ?? (keyLabel.isEmpty ? "Πλήκτρο \(key)" : keyLabel)) }
        return names
    }

    var label: String { keys.joined(separator: " + ") }

    // Τα bits δεξιού/αριστερού πλήκτρου που βάζει το macOS στα flags (NX_DEVICE…KEYMASK).
    private static let bits: [(left: UInt64, right: UInt64)] = [(0x01, 0x2000), (0x20, 0x40), (0x08, 0x10), (0x02, 0x04)]

    /// Ακριβώς αυτά τα πλήκτρα ελέγχου, χωρίς άλλα (π.χ. Control + Option, όχι Control + Option + Shift).
    /// Το Fn μετράει μόνο σε συντομεύσεις χωρίς πλήκτρο: τα βελάκια και τα F του laptop βάζουν μόνα τους Fn.
    func modifiersMatch(_ flags: CGEventFlags) -> Bool {
        let raw = flags.rawValue
        let general: [CGEventFlags] = [.maskControl, .maskAlternate, .maskCommand, .maskShift]
        let wanted = [control, option, command, shift]
        for i in 0..<4 {
            let pressed = flags.contains(general[i])
            var left = raw & Combo.bits[i].left != 0, right = raw & Combo.bits[i].right != 0
            if pressed && !left && !right { left = true }   // κάποια πληκτρολόγια δεν στέλνουν την πλευρά
            switch wanted[i] {
            case .none: if pressed { return false }
            case .any: if !pressed { return false }
            case .left: if !(left && !right) { return false }
            case .right: if !(right && !left) { return false }
            }
        }
        if key < 0 && flags.contains(.maskSecondaryFn) != fn { return false }
        return true
    }

    /// Πιο χαλαρός έλεγχος για τον φύλακα της ηχογράφησης: αρκεί να κρατάς ακόμα τα πλήκτρα ελέγχου της συντόμευσης.
    func stillHeld(_ flags: CGEventFlags) -> Bool {
        if control != .none && !flags.contains(.maskControl) { return false }
        if option != .none && !flags.contains(.maskAlternate) { return false }
        if command != .none && !flags.contains(.maskCommand) { return false }
        if shift != .none && !flags.contains(.maskShift) { return false }
        if fn && key < 0 && !flags.contains(.maskSecondaryFn) { return false }
        return true
    }

    /// Τα πλήκτρα ελέγχου που κρατάς τώρα, με πλευρά (για την καταγραφή νέας συντόμευσης).
    static func pressedSides(_ flags: CGEventFlags) -> [(left: Bool, right: Bool)] {
        let raw = flags.rawValue
        let general: [CGEventFlags] = [.maskControl, .maskAlternate, .maskCommand, .maskShift]
        return (0..<4).map { i in
            var left = raw & bits[i].left != 0, right = raw & bits[i].right != 0
            if flags.contains(general[i]) && !left && !right { left = true }
            if !flags.contains(general[i]) { left = false; right = false }
            return (left, right)
        }
    }
}
