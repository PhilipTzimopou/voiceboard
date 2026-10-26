# Οδηγίες εγκατάστασης του Voiceboard για Windows (για το AI σου)

> Δώσε αυτόν τον σύνδεσμο στο AI σου, π.χ. Claude Code, ChatGPT ή Codex,
> και γράψε: «Εγκατάστησέ μου το Voiceboard για Windows ακολουθώντας αυτές τις οδηγίες».

## Για το AI: τι εγκαθιστάς

Το Voiceboard είναι δωρεάν εφαρμογή υπαγόρευσης για Windows. Ο χρήστης κρατά **Ctrl + Win**, μιλά,
και το κείμενο γράφεται εκεί όπου βρίσκεται ο κέρσορας. Τρέχει στη γραμμή εργασιών (κάτω δεξιά, δίπλα στο ρολόι).
Μεταγράφει με το Whisper large-v3 τοπικά (δωρεάν, χωρίς ίντερνετ) ή με το Scribe v2 της ElevenLabs (με κλειδί του χρήστη).

Η εγκατάσταση γίνεται μόνο για τον τρέχοντα χρήστη, στο `%LOCALAPPDATA%\Programs\Voiceboard`,
**χωρίς δικαιώματα διαχειριστή**. Τα δεδομένα μένουν στο `%APPDATA%\Voiceboard`.

Μίλα στον χρήστη στα ελληνικά, με απλά λόγια. Κάθε βήμα που θέλει δική του ενέργεια, πες του το ακριβώς.

### Πριν ξεκινήσεις, έλεγξε

1. Windows 10 (έκδοση 1809 ή νεότερη) ή Windows 11, 64-bit: `[Environment]::OSVersion.Version` και `[Environment]::Is64BitOperatingSystem`.
2. Ελεύθερος χώρος τουλάχιστον 4 GB στον δίσκο C: (το μοντέλο Whisper είναι 3,1 GB): `Get-PSDrive C`.
3. Μνήμη τουλάχιστον 8 GB για το Whisper: `(Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory`.
   Με λιγότερη μνήμη, πρότεινε στον χρήστη το Scribe v2 της ElevenLabs.

### Βήμα 1: κατέβασε το αρχείο εγκατάστασης

Το `VoiceboardSetup.exe` βρίσκεται στα Releases του https://github.com/PhilipTzimopou/voiceboard,
στο νεότερο release με tag που ξεκινά με `voiceboard-windows-v`.

```powershell
$repo = "PhilipTzimopou/voiceboard"
$release = Invoke-RestMethod "https://api.github.com/repos/$repo/releases" |
  Where-Object { $_.tag_name -like "voiceboard-windows-v*" -and -not $_.draft } | Select-Object -First 1
$asset = $release.assets | Where-Object name -eq "VoiceboardSetup.exe"
$setup = Join-Path $env:TEMP "VoiceboardSetup.exe"
Invoke-WebRequest $asset.browser_download_url -OutFile $setup
Write-Host "Κατέβηκε η έκδοση $($release.tag_name)"
```

### Βήμα 2: αθόρυβη εγκατάσταση

```powershell
# Όχι -Wait: θα περίμενε και το Voiceboard, που ανοίγει μόνο του μετά την εγκατάσταση και μένει ανοιχτό.
$result = Start-Process $setup -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/TASKS=startup" -PassThru
$result.WaitForExit()
if ($result.ExitCode -ne 0) { throw "Η εγκατάσταση απέτυχε (κωδικός $($result.ExitCode))" }
Test-Path "$env:LOCALAPPDATA\Programs\Voiceboard\Voiceboard.exe"
```

Το `/TASKS=startup` ανοίγει το Voiceboard με κάθε εκκίνηση των Windows. Αν ο χρήστης δεν το θέλει, βγάλε το.
Αν ήταν ήδη εγκατεστημένο, η ίδια εντολή το αναβαθμίζει και κρατά λεξιλόγιο, ιστορικό και μοντέλο.

Μετά την εγκατάσταση η εφαρμογή ανοίγει μόνη της: εμφανίζεται το παράθυρο και το εικονίδιο στη γραμμή εργασιών.

### Βήμα 3: μοντέλο ομιλίας

Ρώτα τον χρήστη: «Έχεις κλειδί API από την ElevenLabs;»

- **Όχι** (ή θέλει το δωρεάν): το Voiceboard κατεβάζει μόνο του το Whisper large-v3 (3,1 GB) την πρώτη φορά.
  Η πρόοδος φαίνεται στην Αρχική και στις Ρυθμίσεις. Ο χρήστης περιμένει να τελειώσει (με γρήγορο ίντερνετ, 5-15 λεπτά).
  Αν διακοπεί, πατά «Κατέβασμα» στις Ρυθμίσεις και συνεχίζει από εκεί που σταμάτησε.
- **Ναι**: ο χρήστης ανοίγει **Ρυθμίσεις → Κλειδί ElevenLabs**, επικολλά το κλειδί και πατά «Αποθήκευση».
  Η εφαρμογή περνά στο Scribe v2. Μην ζητήσεις το κλειδί στη συνομιλία· ο χρήστης το βάζει μόνος του στο παράθυρο.

### Βήμα 4: πρώτη δοκιμή

Πες στον χρήστη:

1. Άνοιξε το Σημειωματάριο (Notepad) και κάνε κλικ μέσα στο κείμενο.
2. Κράτα πατημένα **Ctrl + Win**, πες «Γεια σου, αυτή είναι μια δοκιμή», και άφησε τα πλήκτρα.
3. Στο κάτω μέρος της οθόνης εμφανίζεται μια μικρή ένδειξη όσο ηχογραφεί. Μόλις αφήσεις τα πλήκτρα, το κείμενο γράφεται.

Άλλες συντομεύσεις: **μεσαίο κλικ** (ροδέλα ποντικιού) = ηχογράφηση χωρίς χέρια, ξανά μεσαίο κλικ για τέλος. **Esc** = ακύρωση.

### Αν κάτι δεν δουλεύει

| Τι βλέπει ο χρήστης | Τι κάνεις |
|---|---|
| Μπλε παράθυρο του Microsoft Defender SmartScreen πριν από την εγκατάσταση | Συμβαίνει όταν ο χρήστης κατέβασε το αρχείο από browser, γιατί η εφαρμογή δεν έχει ψηφιακή υπογραφή. Ο χρήστης πατά τον σύνδεσμο για περισσότερες πληροφορίες και μετά το κουμπί για εκτέλεση. Με την εγκατάσταση από PowerShell (βήματα 1-2) συνήθως δεν εμφανίζεται. |
| Η ένδειξη γράφει «Δεν ακούω ήχο» ή «Δεν ακούω μικρόφωνο» | `Start-Process ms-settings:privacy-microphone` ανοίγει τις ρυθμίσεις απορρήτου του μικροφώνου. Ο χρήστης ενεργοποιεί την πρόσβαση στο μικρόφωνο, και ειδικά την πρόσβαση για τις εφαρμογές υπολογιστή (desktop apps). Μετά, στις Ρυθμίσεις του Voiceboard, διαλέγει το σωστό μικρόφωνο. |
| Δεν φαίνεται το εικονίδιο κάτω δεξιά | Είναι κρυμμένο στο βελάκι «^». Ο χρήστης το σέρνει δίπλα στο ρολόι. |
| Το κείμενο δεν γράφεται σε κάποια εφαρμογή | Οι εφαρμογές που τρέχουν «ως διαχειριστής» δεν δέχονται πλήκτρα από απλές εφαρμογές. Το κείμενο υπάρχει στο ιστορικό της Αρχικής, με κουμπί «Αντιγραφή». |
| «Δεν φόρτωσε το Whisper» | Δες το αρχείο καταγραφής (παρακάτω). Ο επεξεργαστής πρέπει να έχει AVX2 (σχεδόν όλοι μετά το 2015). Αλλιώς χρησιμοποίησε το Scribe v2. |
| Το Whisper αργεί πολύ | Χωρίς κάρτα γραφικών με Vulkan, το large-v3 τρέχει στον επεξεργαστή. Πρότεινε το Scribe v2. |
| Ηχογραφούν δύο εφαρμογές μαζί | Είναι ανοιχτό το Wispr Flow με την ίδια συντόμευση. Κλείσ' το. |

Αρχείο καταγραφής (χρόνοι και σφάλματα, όχι το κείμενο της υπαγόρευσης):

```powershell
Get-Content "$env:APPDATA\Voiceboard\voiceboard.log" -Tail 40 -Encoding utf8
```

### Απεγκατάσταση

```powershell
(Start-Process "$env:LOCALAPPDATA\Programs\Voiceboard\unins000.exe" -ArgumentList "/VERYSILENT" -PassThru).WaitForExit()
```

Τα δεδομένα (λεξιλόγιο, ιστορικό, κλειδί, μοντέλο Whisper) μένουν στο `%APPDATA%\Voiceboard`. Αν ο χρήστης θέλει να φύγουν κι αυτά,
ρώτα τον πρώτα και μετά διάγραψε τον φάκελο.

### Αν δεν υπάρχει Release: χτίσιμο από τον κώδικα

Χρειάζεται το .NET 10 SDK (`winget install Microsoft.DotNet.SDK.10`), το Git και η Python. Κατέβασε τον κώδικα με
`git clone https://github.com/PhilipTzimopou/voiceboard.git` και από τον φάκελο `voiceboard\windows`:

```powershell
python make-icon.py build/Voiceboard.ico
dotnet publish src/Voiceboard -c Release -r win-x64 --self-contained true -o publish
.\publish\Voiceboard.exe
```

Σε αυτή την περίπτωση το Whisper θέλει και το Visual C++ Redistributable (x64): `winget install Microsoft.VCRedist.2015+.x64`.
