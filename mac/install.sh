#!/bin/bash
# Εγκαθιστά το Voiceboard σε Mac, από την αρχή ή ως αναβάθμιση:
# 1. ελέγχει τα εργαλεία της Apple (Command Line Tools) και το Homebrew
# 2. εγκαθιστά το whisper-cpp (Whisper τοπικά, δωρεάν)
# 3. κατεβάζει το μοντέλο Whisper large-v3 (3,1 GB), αν δεν υπάρχει ήδη
# 4. γράφει το config.json, κρατώντας το κλειδί ElevenLabs αν υπάρχει ήδη
# 5. χτίζει την εφαρμογή στο ~/Applications/Voiceboard.app και την ανοίγει
set -euo pipefail
DIR="$(cd "$(dirname "$0")" && pwd)"
SUPPORT="$HOME/Library/Application Support/Voiceboard"
MODEL="$SUPPORT/models/ggml-large-v3.bin"
CONFIG="$SUPPORT/config.json"

if ! xcode-select -p >/dev/null 2>&1; then
  echo "Λείπουν τα Command Line Tools της Apple. Τρέξε: xcode-select --install"
  echo "Όταν τελειώσει η εγκατάσταση, τρέξε ξανά αυτό το αρχείο."
  exit 1
fi
if ! command -v brew >/dev/null 2>&1; then
  echo "Λείπει το Homebrew. Οδηγίες εγκατάστασης: https://brew.sh"
  exit 1
fi

brew list whisper-cpp >/dev/null 2>&1 || brew install whisper-cpp
WHISPER_CLI="$(brew --prefix)/bin/whisper-cli"
[ -x "$WHISPER_CLI" ] || { echo "Δεν βρέθηκε το whisper-cli στο $WHISPER_CLI"; exit 1; }

mkdir -p "$SUPPORT/models"
if [ ! -f "$MODEL" ]; then
  echo "Κατεβάζω το μοντέλο Whisper large-v3 (3,1 GB). Αν διακοπεί, τρέξε ξανά: συνεχίζει από εκεί που σταμάτησε."
  curl -L --fail -C - -o "$MODEL.part" https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3.bin
  mv "$MODEL.part" "$MODEL"
fi

umask 077
CONFIG="$CONFIG" WHISPER_CLI="$WHISPER_CLI" MODEL="$MODEL" python3 -c '
import json, os
path = os.environ["CONFIG"]
config = {}
if os.path.exists(path):
    try: config = json.load(open(path))
    except Exception: config = {}
config["whisperCLI"] = os.environ["WHISPER_CLI"]
config["whisperModel"] = os.environ["MODEL"]
config.setdefault("language", "el")
json.dump(config, open(path, "w"), indent=2, ensure_ascii=False)'
chmod 600 "$CONFIG"

"$DIR/build.sh"
# Αμέσως μετά το κλείσιμο της παλιάς εφαρμογής το macOS μπορεί να αρνηθεί το άνοιγμα (σφάλμα -600): περιμένουμε και ξαναδοκιμάζουμε.
APP="$HOME/Applications/Voiceboard.app"
for attempt in 1 2 3; do
  sleep 1
  open "$APP" 2>/dev/null && break
  [ "$attempt" = 3 ] && echo "Άνοιξε το Voiceboard από τον φάκελο Εφαρμογές του λογαριασμού σου (~/Applications)."
done
echo "Έτοιμο. Το Voiceboard τρέχει στη γραμμή μενού, πάνω δεξιά."
