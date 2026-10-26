#!/bin/bash
# Βάζει το κλειδί της ElevenLabs στο config.json, παίρνοντάς το από το πρόχειρο.
# Αντέγραψε πρώτα το κλειδί (Cmd+C) και μετά τρέξε αυτό το αρχείο. Έτσι το κλειδί δεν γράφεται πουθενά στη συνομιλία.
# Με την παράμετρο --remove το σβήνει (η εφαρμογή γυρίζει στο Whisper).
set -euo pipefail
CONFIG="$HOME/Library/Application Support/Voiceboard/config.json"
mkdir -p "$(dirname "$CONFIG")"
if [ "${1:-}" = "--remove" ]; then KEY=""; else KEY="$(pbpaste | tr -d '[:space:]')"; fi
if [ "${1:-}" != "--remove" ] && [ ${#KEY} -lt 20 ]; then
  echo "Στο πρόχειρο δεν υπάρχει κλειδί. Αντέγραψε το κλειδί από την ElevenLabs και τρέξε ξανά."
  exit 1
fi
umask 077
CONFIG="$CONFIG" KEY="$KEY" python3 -c '
import json, os
path = os.environ["CONFIG"]
config = json.load(open(path)) if os.path.exists(path) else {"language": "el"}
config["elevenLabsAPIKey"] = os.environ["KEY"] or None
json.dump(config, open(path, "w"), indent=2, ensure_ascii=False)'
chmod 600 "$CONFIG"
# Το μοντέλο που διαλέγει η εφαρμογή όταν δεν έχεις επιλέξει: Scribe αν υπάρχει κλειδί, αλλιώς Whisper.
defaults delete com.philiptzimopou.voiceboard model 2>/dev/null || true
pkill -x Voiceboard 2>/dev/null || true
sleep 1
open "$HOME/Applications/Voiceboard.app"
if [ -n "$KEY" ]; then echo "Το κλειδί αποθηκεύτηκε. Το Voiceboard χρησιμοποιεί τώρα το Scribe v2."; else echo "Το κλειδί σβήστηκε. Το Voiceboard χρησιμοποιεί το Whisper."; fi
