#!/bin/bash
# Χτίζει το Voiceboard.app και το εγκαθιστά στο ~/Applications.
# Μετά από κάθε νέο χτίσιμο το macOS ζητά ξανά την άδεια Προσβασιμότητας (η υπογραφή είναι τοπική).
# BUILD_ONLY=1: μόνο χτίσιμο σε προσωρινό φάκελο, χωρίς εγκατάσταση (για τον έλεγχο στο GitHub Actions).
set -euo pipefail
DIR="$(cd "$(dirname "$0")" && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
BUILD="$WORK/Voiceboard.app"
DEST="$HOME/Applications/Voiceboard.app"

mkdir -p "$BUILD/Contents/MacOS" "$BUILD/Contents/Resources"
# Apple Silicon (arm64) ή Intel (x86_64): χτίζουμε για τον επεξεργαστή αυτού του Mac.
swiftc -O -swift-version 5 -target "$(uname -m)-apple-macos14.0" \
  -o "$BUILD/Contents/MacOS/Voiceboard" "$DIR"/Sources/*.swift \
  -framework AppKit -framework SwiftUI -framework AVFoundation -framework ApplicationServices -lsqlite3
cp "$DIR/Info.plist" "$BUILD/Contents/Info.plist"
swift "$DIR/make-icon.swift" "$WORK"
iconutil -c icns "$WORK/AppIcon.iconset" -o "$BUILD/Contents/Resources/AppIcon.icns"
codesign --force --sign - --identifier com.philiptzimopou.voiceboard "$BUILD"

if [ "${BUILD_ONLY:-}" = "1" ]; then
  echo "Χτίστηκε (χωρίς εγκατάσταση): $(du -sh "$BUILD" | cut -f1)"
  exit 0
fi

pkill -x Voiceboard 2>/dev/null || true
mkdir -p "$HOME/Applications"
rm -rf "$DEST"
cp -R "$BUILD" "$DEST"
echo "Εγκαταστάθηκε: $DEST"
