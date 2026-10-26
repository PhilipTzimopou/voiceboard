import AppKit
import CoreGraphics

/// Κρατάμε το πρόχειρο,
/// βάζουμε το κείμενο, πατάμε Cmd+V με CGEventPost και επαναφέρουμε το πρόχειρο σε 500 ms.
enum Paster {
    private static let transientType = NSPasteboard.PasteboardType("org.nspasteboard.TransientType")

    static func paste(_ text: String) {
        let pasteboard = NSPasteboard.general
        let saved: [NSPasteboardItem] = (pasteboard.pasteboardItems ?? []).map { item in
            let copy = NSPasteboardItem()
            for type in item.types {
                if let data = item.data(forType: type) { copy.setData(data, forType: type) }
            }
            return copy
        }

        pasteboard.clearContents()
        pasteboard.setString(text, forType: .string)
        // Οι διαχειριστές προχείρου αγνοούν ό,τι έχει αυτό τον τύπο, ώστε να μη γεμίζει το ιστορικό τους.
        pasteboard.setData(Data(), forType: transientType)
        let ourChange = pasteboard.changeCount

        postCommandV()

        DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) {
            guard pasteboard.changeCount == ourChange else { return }
            pasteboard.clearContents()
            if !saved.isEmpty { pasteboard.writeObjects(saved) }
        }
    }

    private static func postCommandV() {
        let source = CGEventSource(stateID: .combinedSessionState)
        let vKey: CGKeyCode = 9
        let down = CGEvent(keyboardEventSource: source, virtualKey: vKey, keyDown: true)
        let up = CGEvent(keyboardEventSource: source, virtualKey: vKey, keyDown: false)
        down?.flags = .maskCommand
        up?.flags = .maskCommand
        down?.post(tap: .cghidEventTap)
        up?.post(tap: .cghidEventTap)
    }
}
