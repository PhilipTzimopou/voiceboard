import CoreGraphics

/// Οι επιλογές συντομεύσεων που δίνουμε στις Ρυθμίσεις. Μόνο συνδυασμοί που δεν γράφουν χαρακτήρες
/// όταν τους κρατάς μόνους τους.
enum Shortcuts {
    struct PushToTalk: Identifiable, Hashable {
        let id: String
        let keys: [String]
        var label: String { keys.joined(separator: " + ") }
    }

    struct HandsFree: Identifiable, Hashable {
        let id: String
        let label: String
        /// Ο αριθμός του κουμπιού στο otherMouseDown (2 = μεσαίο, 3 = πίσω, 4 = μπροστά)· nil = κανένα.
        let button: Int64?
    }

    static let pushToTalk = [
        PushToTalk(id: "control-option", keys: ["⌃ Control", "⌥ Option"]),
        PushToTalk(id: "control-command", keys: ["⌃ Control", "⌘ Command"]),
        PushToTalk(id: "option-command", keys: ["⌥ Option", "⌘ Command"]),
        PushToTalk(id: "right-option", keys: ["Δεξί ⌥ Option"]),
        PushToTalk(id: "right-command", keys: ["Δεξί ⌘ Command"]),
    ]

    static let handsFree = [
        HandsFree(id: "middle", label: "Μεσαίο κλικ", button: 2),
        HandsFree(id: "back", label: "Πλαϊνό κουμπί ποντικιού (πίσω)", button: 3),
        HandsFree(id: "forward", label: "Πλαϊνό κουμπί ποντικιού (μπροστά)", button: 4),
        HandsFree(id: "none", label: "Κανένα", button: nil),
    ]

    static func pushToTalk(_ id: String) -> PushToTalk { pushToTalk.first { $0.id == id } ?? pushToTalk[0] }
    static func handsFree(_ id: String) -> HandsFree { handsFree.first { $0.id == id } ?? handsFree[0] }

    // Τα bits δεξιού/αριστερού πλήκτρου που βάζει το macOS στα flags (NX_DEVICE…KEYMASK).
    private static let leftCommand: UInt64 = 0x08, rightCommand: UInt64 = 0x10
    private static let leftOption: UInt64 = 0x20, rightOption: UInt64 = 0x40

    /// Ακριβώς αυτά τα πλήκτρα, χωρίς άλλα (π.χ. Control + Option, όχι Control + Option + Shift).
    static func matches(_ id: String, _ flags: CGEventFlags) -> Bool {
        let control = flags.contains(.maskControl), option = flags.contains(.maskAlternate)
        let command = flags.contains(.maskCommand), shift = flags.contains(.maskShift)
        let raw = flags.rawValue
        switch id {
        case "control-command": return control && command && !option && !shift
        case "option-command": return option && command && !control && !shift
        case "right-option": return raw & rightOption != 0 && raw & leftOption == 0 && !control && !command && !shift
        case "right-command": return raw & rightCommand != 0 && raw & leftCommand == 0 && !control && !option && !shift
        default: return control && option && !command && !shift
        }
    }

    /// Πιο χαλαρός έλεγχος για τον φύλακα της ηχογράφησης: αρκεί να κρατάς ακόμα τα βασικά πλήκτρα.
    static func stillHeld(_ id: String, _ flags: CGEventFlags) -> Bool {
        switch id {
        case "control-command": return flags.contains(.maskControl) && flags.contains(.maskCommand)
        case "option-command": return flags.contains(.maskAlternate) && flags.contains(.maskCommand)
        case "right-option": return flags.contains(.maskAlternate)
        case "right-command": return flags.contains(.maskCommand)
        default: return flags.contains(.maskControl) && flags.contains(.maskAlternate)
        }
    }
}
