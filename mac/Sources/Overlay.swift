import AppKit

/// Η μικρή ένδειξη στο κάτω μέρος της οθόνης. Εμφανίζεται σε ΟΛΕΣ τις οθόνες, ώστε να φαίνεται
/// όποια κι αν κοιτάς. Πάντα από πάνω, σε όλα τα Spaces, δεν παίρνει την εστίαση και δεν δέχεται κλικ.
final class Overlay {
    enum State: Equatable {
        case recording(handsFree: Bool)
        case transcribing
        case notice(String, warning: Bool)
    }

    private var panels: [NSPanel] = []
    private var views: [OverlayView] = []
    private var timer: Timer?
    private var startedAt = Date()
    private var noticeWork: DispatchWorkItem?
    private(set) var state: State?
    var noAudio = false { didSet { views.forEach { $0.noAudio = noAudio } } }

    func show(_ state: State) {
        noticeWork?.cancel()
        if case .recording = state, !isRecordingState { startedAt = Date() }
        self.state = state
        rebuildPanelsIfScreensChanged()
        for view in views {
            view.state = state
            view.startedAt = startedAt
            view.noAudio = noAudio
            view.needsDisplay = true
        }
        for panel in panels where !panel.isVisible { panel.orderFrontRegardless() }
        if timer == nil {
            timer = Timer.scheduledTimer(withTimeInterval: 1.0 / 30.0, repeats: true) { [weak self] _ in
                self?.views.forEach { $0.tick() }
            }
            RunLoop.main.add(timer!, forMode: .common)
        }
    }

    /// Σύντομο μήνυμα (π.χ. «Ακυρώθηκε») που σβήνει μόνο του.
    func flash(_ text: String, warning: Bool = false, seconds: Double = 1.6) {
        show(.notice(text, warning: warning))
        let work = DispatchWorkItem { [weak self] in self?.hide() }
        noticeWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + seconds, execute: work)
    }

    func level(_ value: Float) { views.forEach { $0.push(level: value) } }

    func hide() {
        noticeWork?.cancel()
        state = nil
        noAudio = false
        timer?.invalidate()
        timer = nil
        panels.forEach { $0.orderOut(nil) }
        views.forEach { $0.reset() }
    }

    private var isRecordingState: Bool {
        if case .recording = state { return true }
        return false
    }

    private func rebuildPanelsIfScreensChanged() {
        let screens = NSScreen.screens
        if panels.count == screens.count,
           zip(panels, screens).allSatisfy({ $0.0.frame.origin == Self.origin(on: $0.1) }) { return }
        panels.forEach { $0.orderOut(nil) }
        panels = []
        views = []
        for screen in screens {
            let view = OverlayView(frame: NSRect(origin: .zero, size: OverlayView.size))
            let panel = NSPanel(contentRect: NSRect(origin: Self.origin(on: screen), size: OverlayView.size),
                                styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
            panel.isOpaque = false
            panel.backgroundColor = .clear
            panel.hasShadow = true
            panel.level = .screenSaver
            panel.ignoresMouseEvents = true
            panel.hidesOnDeactivate = false
            panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
            panel.contentView = view
            panels.append(panel)
            views.append(view)
        }
    }

    private static func origin(on screen: NSScreen) -> NSPoint {
        let frame = screen.visibleFrame
        return NSPoint(x: (frame.midX - OverlayView.size.width / 2).rounded(), y: (frame.minY + 18).rounded())
    }
}

final class OverlayView: NSView {
    static let size = NSSize(width: 160, height: 42)

    var state: Overlay.State = .recording(handsFree: false)
    var startedAt = Date()
    var noAudio = false
    private var levels = [CGFloat](repeating: 0, count: 12)
    private var latest: CGFloat = 0
    private var phase: CGFloat = 0

    func push(level: Float) { latest = max(latest, min(1, CGFloat(level) * 9)) }

    func tick() {
        levels.removeFirst()
        levels.append(latest)
        latest *= 0.6
        phase += 0.15
        needsDisplay = true
    }

    func reset() {
        levels = [CGFloat](repeating: 0, count: levels.count)
        latest = 0
    }

    override func draw(_ dirtyRect: NSRect) {
        let pill = NSBezierPath(roundedRect: bounds.insetBy(dx: 2, dy: 2), xRadius: 19, yRadius: 19)
        NSColor(srgbRed: 0.07, green: 0.09, blue: 0.16, alpha: 0.96).setFill()
        pill.fill()

        if case .recording = state {
            // Γαλάζιο περίγραμμα που «αναπνέει»: φαίνεται από μακριά ότι μπορείς να μιλήσεις τώρα.
            let glow = 0.55 + 0.45 * (sin(phase * 0.9) + 1) / 2
            NSColor(srgbRed: 0.34, green: 0.71, blue: 0.91, alpha: glow).setStroke()
            pill.lineWidth = 2.5
        } else {
            NSColor(white: 1, alpha: 0.16).setStroke()
            pill.lineWidth = 1
        }
        pill.stroke()

        switch state {
        case .recording(let handsFree):
            // Κόκκινη κουκκίδα που πάλλεται, όπως στην πρώτη έκδοση.
            let pulse = 0.7 + 0.3 * (sin(phase * 0.9) + 1) / 2
            let dotSize: CGFloat = 9 + 2 * pulse
            NSColor(srgbRed: 0.93, green: 0.27, blue: 0.22, alpha: 1).setFill()
            NSBezierPath(ovalIn: NSRect(x: 19 - dotSize / 2, y: bounds.midY - dotSize / 2, width: dotSize, height: dotSize)).fill()

            if noAudio {
                text("Δεν ακούω ήχο", x: 32, color: NSColor(srgbRed: 0.90, green: 0.62, blue: 0, alpha: 1), size: 11.5)
            } else {
                // Οι μπάρες κινούνται πάντα: όταν σωπαίνεις κάνουν ένα ήρεμο κύμα, όταν μιλάς ακολουθούν τη φωνή σου.
                let barWidth: CGFloat = 4, gap: CGFloat = 3
                let barsWidth = CGFloat(levels.count) * (barWidth + gap) - gap
                let areaEnd = bounds.maxX - (handsFree ? 30 : 16)
                var x = 32 + (areaEnd - 32 - barsWidth) / 2
                NSColor.white.setFill()
                for (index, level) in levels.enumerated() {
                    let idle = 5 + 9 * (sin(phase * 1.2 - CGFloat(index) * 0.6) + 1) / 2
                    let height = max(idle, level * (bounds.height - 14))
                    NSBezierPath(roundedRect: NSRect(x: x, y: bounds.midY - height / 2, width: barWidth, height: height),
                                 xRadius: 2, yRadius: 2).fill()
                    x += barWidth + gap
                }
            }
            if handsFree, !noAudio, let lock = NSImage(systemSymbolName: "lock.fill", accessibilityDescription: "Χωρίς χέρια") {
                let config = NSImage.SymbolConfiguration(pointSize: 11, weight: .semibold)
                    .applying(NSImage.SymbolConfiguration(paletteColors: [.white]))
                (lock.withSymbolConfiguration(config) ?? lock)
                    .draw(in: NSRect(x: bounds.maxX - 26, y: bounds.midY - 7, width: 11, height: 13))
            }

        case .transcribing:
            for i in 0..<3 {
                let offset = (sin(phase - CGFloat(i) * 0.8) + 1) / 2
                let size: CGFloat = 6 + offset * 3
                let x = bounds.midX - 18 + CGFloat(i) * 18 - size / 2
                NSColor(srgbRed: 0.34, green: 0.71, blue: 0.91, alpha: 0.5 + offset * 0.5).setFill()
                NSBezierPath(ovalIn: NSRect(x: x, y: bounds.midY - size / 2, width: size, height: size)).fill()
            }

        case .notice(let message, let warning):
            let color = warning ? NSColor(srgbRed: 0.90, green: 0.62, blue: 0, alpha: 1) : NSColor.white
            let attributes: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: 12, weight: .semibold)]
            let width = (message as NSString).size(withAttributes: attributes).width
            text(message, x: (bounds.width - width) / 2, color: color, size: 12)
        }
    }

    private func text(_ string: String, x: CGFloat, color: NSColor, size: CGFloat, mono: Bool = false) {
        let font = mono ? NSFont.monospacedDigitSystemFont(ofSize: size, weight: .semibold)
                        : NSFont.systemFont(ofSize: size, weight: .semibold)
        let attributes: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: color]
        let height = (string as NSString).size(withAttributes: attributes).height
        (string as NSString).draw(at: NSPoint(x: x, y: bounds.midY - height / 2), withAttributes: attributes)
    }
}
