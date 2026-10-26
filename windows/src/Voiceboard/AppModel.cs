using Microsoft.Win32;
using Voiceboard.Core;

namespace Voiceboard;

enum Phase { Idle, Recording, Transcribing }

/// Ό,τι δείχνει και αλλάζει το παράθυρο. Το λεξιλόγιο και το ιστορικό αποθηκεύονται μόλις αλλάξουν.
/// Κάθε αλλαγή ειδοποιεί το παράθυρο με το Changed και ένα όνομα (π.χ. "history"), ώστε να ξαναχτίσει μόνο ό,τι χρειάζεται.
sealed class AppModel
{
    public event Action<string>? Changed;
    private void Notify(string what) => Changed?.Invoke(what);

    private Phase phase;
    public Phase Phase { get => phase; set { if (phase != value) { phase = value; Notify("status"); } } }

    private bool shortcutsActive;
    public bool ShortcutsActive { get => shortcutsActive; set { if (shortcutsActive != value) { shortcutsActive = value; Notify("status"); } } }

    private bool microphoneGranted = true;
    public bool MicrophoneGranted { get => microphoneGranted; set { if (microphoneGranted != value) { microphoneGranted = value; Notify("status"); } } }

    private string lastError = "";
    public string LastError { get => lastError; set { if (lastError != value) { lastError = value; Notify("status"); } } }

    public List<AudioInputDevice> InputDevices { get; private set; } = [];
    public void RefreshDevices() { InputDevices = AudioDevices.Inputs(); Notify("devices"); }

    public bool HasElevenLabsKey => Settings.Shared.HasElevenLabsKey;
    public void SetElevenLabsKey(string key)
    {
        Settings.Shared.ElevenLabsApiKey = key;
        if (!HasElevenLabsKey && SpeechModel == SpeechModel.Scribe) SpeechModel = SpeechModel.Whisper;
        Notify("model");
    }

    public SpeechModel SpeechModel
    {
        get => Settings.Shared.Model;
        set { if (Settings.Shared.Model != value) { Settings.Shared.Model = value; Notify("model"); } }
    }

    public string MicrophoneId { get => Settings.Shared.MicrophoneId; set => Settings.Shared.MicrophoneId = value; }
    public bool SoundsEnabled { get => Settings.Shared.SoundsEnabled; set => Settings.Shared.SoundsEnabled = value; }
    /// Η συντόμευση «κράτα και μίλα» δεν μένει ποτέ κενή: χωρίς αυτήν δεν υπαγορεύεις.
    public Combo PushToTalk
    {
        get => Combo.Parse(Settings.Shared.PushToTalk) is { IsEmpty: false } combo ? combo : Combo.Parse(Shortcuts.DefaultPushToTalk);
        set { Settings.Shared.PushToTalk = value.ToString(); Notify("shortcuts"); }
    }

    public Combo HandsFree
    {
        get => Combo.Parse(Settings.Shared.HandsFree);
        set { Settings.Shared.HandsFree = value.ToString(); Notify("shortcuts"); }
    }

    /// Καταγραφή νέας συντόμευσης από τα πλήκτρα (το συνδέει ο Controller με την παρακολούθηση πλήκτρων).
    public Action<Action<Combo?>> CaptureShortcut = done => done(null);
    public Action CancelCapture = () => { };

    public int DailyGoal { get => Settings.Shared.DailyGoal; set { Settings.Shared.DailyGoal = value; Notify("history"); } }

    public Vocabulary Vocabulary { get; private set; } = Vocabulary.Load();

    /// Κάθε αλλαγή στο λεξιλόγιο περνά από εδώ: αλλάζει, αποθηκεύεται, ειδοποιεί.
    public void EditVocabulary(Action<Vocabulary> change)
    {
        change(Vocabulary);
        try { Vocabulary.Save(); }
        catch (Exception error) { LastError = $"Δεν αποθηκεύτηκε το λεξιλόγιο: {error.Message}"; }
        Notify("vocabulary");
    }

    public void ReplaceVocabulary(Vocabulary vocabulary) => EditVocabulary(v =>
    {
        v.Words = vocabulary.Words; v.Replacements = vocabulary.Replacements;
        v.Snippets = vocabulary.Snippets; v.ImportedFromWisprAt = vocabulary.ImportedFromWisprAt;
    });

    public List<HistoryEntry> History { get; private set; } = HistoryStore.Load();

    public void AddHistory(HistoryEntry entry)
    {
        History.Insert(0, entry);
        HistoryStore.Save(History);
        Notify("history");
    }

    public void ClearHistory()
    {
        History = [];
        HistoryStore.Save(History);
        Notify("history");
    }

    public DictationStats Stats => new(History);

    // Μοντέλο Whisper: υπάρχει στον δίσκο; κατεβαίνει; σε ποιο υλικό τρέχει;
    public bool WhisperModelExists => File.Exists(Settings.Shared.WhisperModel);
    public double? DownloadProgress { get; private set; }
    public long DownloadedBytes { get; private set; }
    public void SetDownload(double? progress, long bytes) { DownloadProgress = progress; DownloadedBytes = bytes; Notify("download"); }
    public string? WhisperRuntime => WhisperEngine.Shared.Runtime;
    /// Ξεχωριστό όνομα από το "model": η αλλαγή μοντέλου φορτώνει το Whisper, η φόρτωση δεν πρέπει να ξαναφορτώνει.
    public void WhisperStateChanged() => Notify("whisper");

    // Άνοιγμα με την εκκίνηση των Windows: τιμή στο HKCU\...\Run (χωρίς δικαιώματα διαχειριστή).
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public bool LaunchAtLogin
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue("Voiceboard") is string;
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey);
                if (value) key.SetValue("Voiceboard", $"\"{Environment.ProcessPath}\" --startup");
                else key.DeleteValue("Voiceboard", throwOnMissingValue: false);
            }
            catch (Exception error) { LastError = $"Άνοιγμα με την εκκίνηση: {error.Message}"; }
            Notify("settings");
        }
    }

    // Ενέργειες που συνδέει ο Controller
    public Action ImportFromWispr = () => { };
    public Action OpenMicrophoneSettings = () => { };
    public Action OpenLog = () => { };
    public Action DownloadWhisperModel = () => { };
    public Action CancelDownload = () => { };
    public Action<string> Copy = _ => { };
}
