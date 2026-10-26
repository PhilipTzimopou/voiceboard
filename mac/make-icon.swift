// Φτιάχνει το AppIcon.icns: στρογγυλεμένο τετράγωνο από σκούρο μπλε σε γαλάζιο,
// με ένα κύμα φωνής που καταλήγει σε κεχριμπαρένια κουκκίδα.
// Χρήση: swift make-icon.swift <φάκελος εξόδου> [png για προεπισκόπηση]
import AppKit

let output = URL(fileURLWithPath: CommandLine.arguments[1])
let iconset = output.appendingPathComponent("AppIcon.iconset")
try? FileManager.default.removeItem(at: iconset)
try FileManager.default.createDirectory(at: iconset, withIntermediateDirectories: true)

func render(_ pixels: Int) -> Data {
    let size = CGFloat(pixels)
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: pixels, pixelsHigh: pixels, bitsPerSample: 8,
                               samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
                               bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    let inset = size * 0.1
    let rect = NSRect(x: inset, y: inset, width: size - 2 * inset, height: size - 2 * inset)
    let shape = NSBezierPath(roundedRect: rect, xRadius: rect.width * 0.225, yRadius: rect.width * 0.225)
    NSGradient(colors: [NSColor(srgbRed: 0.06, green: 0.10, blue: 0.19, alpha: 1),
                        NSColor(srgbRed: 0.0, green: 0.36, blue: 0.62, alpha: 1),
                        NSColor(srgbRed: 0.34, green: 0.71, blue: 0.91, alpha: 1)])!.draw(in: shape, angle: 45)

    // Ένα συνεχές κύμα φωνής που μικραίνει από αριστερά προς τα δεξιά και καταλήγει σε κεχριμπαρένια
    // κουκκίδα: η φωνή που «γίνεται» κείμενο.
    let wave = NSBezierPath()
    let left = rect.minX + rect.width * 0.17, right = rect.minX + rect.width * 0.70
    let mid = rect.midY
    let steps = 240
    for i in 0...steps {
        let t = CGFloat(i) / CGFloat(steps)
        let x = left + (right - left) * t
        let amplitude = rect.height * 0.26 * (1 - t * 0.85) * sin(t * .pi)  // ξεκινά από το κέντρο, φουσκώνει, σβήνει
        let y = mid + amplitude * sin(t * .pi * 5)
        if i == 0 { wave.move(to: NSPoint(x: x, y: y)) } else { wave.line(to: NSPoint(x: x, y: y)) }
    }
    wave.lineWidth = rect.width * 0.075
    wave.lineCapStyle = .round
    wave.lineJoinStyle = .round
    NSColor.white.setStroke()
    wave.stroke()
    let dot = rect.width * 0.075
    NSColor(srgbRed: 0.90, green: 0.62, blue: 0.0, alpha: 1).setFill()
    NSBezierPath(ovalIn: NSRect(x: rect.minX + rect.width * 0.80 - dot, y: mid - dot, width: dot * 2, height: dot * 2)).fill()
    NSGraphicsContext.restoreGraphicsState()
    return rep.representation(using: .png, properties: [:])!
}

for base in [16, 32, 128, 256, 512] {
    try render(base).write(to: iconset.appendingPathComponent("icon_\(base)x\(base).png"))
    try render(base * 2).write(to: iconset.appendingPathComponent("icon_\(base)x\(base)@2x.png"))
}
if CommandLine.arguments.count > 2 {
    try render(512).write(to: URL(fileURLWithPath: CommandLine.arguments[2]))
}
