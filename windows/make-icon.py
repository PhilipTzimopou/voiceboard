#!/usr/bin/env python3
"""Φτιάχνει το build/Voiceboard.ico, ίδιο σχέδιο με το make-icon.swift της έκδοσης Mac: στρογγυλεμένο τετράγωνο
από σκούρο μπλε σε γαλάζιο, με ένα κύμα φωνής που καταλήγει σε κεχριμπαρένια κουκκίδα.
Μόνο με την τυπική βιβλιοθήκη της Python (τρέχει ίδια σε Mac, Windows και GitHub Actions).
Τα .ico δεν ανεβαίνουν στο GitHub (.gitignore), γι' αυτό το εικονίδιο φτιάχνεται σε κάθε χτίσιμο.
Χρήση: python3 make-icon.py [build/Voiceboard.ico] [προεπισκόπηση.png]"""
import math
import os
import struct
import sys
import zlib

DARK, MID, LIGHT = (15, 26, 48), (0, 92, 158), (87, 181, 232)
AMBER = (230, 158, 0)
SAMPLES = 4  # 4×4 δείγματα ανά pixel για απαλές άκρες


def mix(a, b, t):
    return tuple(a[i] + (b[i] - a[i]) * t for i in range(3))


def render(size):
    inset = size * 0.1
    x0, y0, w = inset, inset, size - 2 * inset
    radius = w * 0.225
    left, right, mid = x0 + w * 0.17, x0 + w * 0.70, y0 + w / 2
    stroke = max(1.2, w * 0.075) / 2
    dot_x, dot_r = x0 + w * 0.80, w * 0.075

    def wave(t):  # ίδια καμπύλη με το Mac: φουσκώνει και σβήνει, πέντε μισά κύματα
        amplitude = w * 0.26 * (1 - t * 0.85) * math.sin(t * math.pi)
        return mid - amplitude * math.sin(t * math.pi * 5)

    curve = [(left + (right - left) * i / 240, wave(i / 240)) for i in range(241)]

    def near_wave(px, py):
        if px < left - stroke or px > right + stroke:
            return False
        for (ax, ay), (bx, by) in zip(curve, curve[1:]):
            if px < min(ax, bx) - stroke or px > max(ax, bx) + stroke:
                continue
            dx, dy = bx - ax, by - ay
            t = max(0.0, min(1.0, ((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy)))
            if (px - ax - t * dx) ** 2 + (py - ay - t * dy) ** 2 <= stroke * stroke:
                return True
        return False

    def inside_shape(px, py):
        cx = min(max(px, x0 + radius), x0 + w - radius)
        cy = min(max(py, y0 + radius), y0 + w - radius)
        return (px - cx) ** 2 + (py - cy) ** 2 <= radius * radius and x0 <= px <= x0 + w and y0 <= py <= y0 + w

    pixels = bytearray()
    for y in range(size):
        pixels.append(0)  # φίλτρο PNG «κανένα» στην αρχή κάθε γραμμής
        for x in range(size):
            r = g = b = a = 0.0
            for sy in range(SAMPLES):
                for sx in range(SAMPLES):
                    px, py = x + (sx + 0.5) / SAMPLES, y + (sy + 0.5) / SAMPLES
                    if not inside_shape(px, py):
                        continue
                    # διαβάθμιση στις 45°: κάτω αριστερά σκούρο, πάνω δεξιά γαλάζιο
                    t = ((px - x0) + (w - (py - y0))) / (2 * w)
                    color = mix(DARK, MID, t * 2) if t < 0.5 else mix(MID, LIGHT, (t - 0.5) * 2)
                    if (px - dot_x) ** 2 + (py - mid) ** 2 <= dot_r * dot_r:
                        color = AMBER
                    elif near_wave(px, py):
                        color = (255, 255, 255)
                    r, g, b, a = r + color[0], g + color[1], b + color[2], a + 1
            n = SAMPLES * SAMPLES
            if a:
                pixels += bytes((round(r / a), round(g / a), round(b / a), round(255 * a / n)))
            else:
                pixels += b"\0\0\0\0"
    return png(size, bytes(pixels))


def png(size, raw):
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)
    header = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b"")


def main():
    output = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "build", "Voiceboard.ico")
    os.makedirs(os.path.dirname(output), exist_ok=True)
    sizes = [16, 24, 32, 48, 64, 128, 256]
    images = [render(size) for size in sizes]
    offset = 6 + 16 * len(sizes)
    ico = struct.pack("<HHH", 0, 1, len(sizes))
    for size, image in zip(sizes, images):
        ico += struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(image), offset)
        offset += len(image)
    with open(output, "wb") as file:
        file.write(ico + b"".join(images))
    if len(sys.argv) > 2:
        with open(sys.argv[2], "wb") as file:
            file.write(images[-1])
    print(f"Έτοιμο: {output}")


if __name__ == "__main__":
    main()
