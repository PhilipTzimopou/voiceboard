using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using Voiceboard.Core;

namespace Voiceboard;

/// Η καρδιά της εφαρμογής, ίδια λογική με τον AppDelegate της έκδοσης Mac:
/// συντομεύσεις → ηχογράφηση → μεταγραφή σε κομμάτια → λεξιλόγιο → επικόλληση.
sealed class Controller
{
    private sealed class ActiveRecording(bool handsFree, DictationPipeline pipeline)
    {
        public bool HandsFree { get; } = handsFree;
        public DateTime Started { get; } = DateTime.UtcNow;
        public DictationPipeline Pipeline { get; } = pipeline;
        public bool RestartedMicrophone { get; set; }
    }

    /// Μία μεταγραφή σε εξέλιξη. Τελειώνει μία φορά, είτε με κείμενο είτε με σφάλμα είτε με λήξη χρόνου.
    private sealed class Job
    {
        public bool Done;
        public bool FellBack;
    }

    private static readonly TimeSpan MaxRecording = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan TranscriptionTimeout = TimeSpan.FromSeconds(45);

    private readonly Dispatcher ui = Dispatcher.CurrentDispatcher;
    private readonly AppModel model = new();
    private readonly HotkeyMonitor hotkeys;
    private readonly Recorder recorder = new();
    private readonly Overlay overlay = new();
    private readonly HwndSource messageWindow;
    private readonly Paster paster;
    private MainWindow? mainWindow;
    private TrayIcon? tray;
    private DispatcherTimer? watchdog;
    private ActiveRecording? active;
    private int pendingJobs;

    public Controller()
    {
        hotkeys = new HotkeyMonitor(ui);
        // Αόρατο παράθυρο, κάτοχος του προχείρου όταν βάζουμε κείμενο.
        messageWindow = new HwndSource(new HwndSourceParameters("Voiceboard") { Width = 0, Height = 0, WindowStyle = 0, ParentWindow = new IntPtr(-3) });
        paster = new Paster(messageWindow.Handle);
    }

    // MARK: Εκκίνηση

    public void Start(bool showWindow)
    {
        Log.Write($"Εκκίνηση Voiceboard {typeof(Controller).Assembly.GetName().Version} για Windows");
        tray = new TrayIcon(ShowWindow, Quit);
        WireModel();

        if (!File.Exists(Paths.Vocabulary) && Paths.WisprDatabaseCandidates.Any(File.Exists)) ImportFromWispr(showResult: false);

        recorder.OnLevel = level => ui.BeginInvoke(() => overlay.Level(level));
        hotkeys.OnPushToTalkKeys = PushToTalkKeys;
        hotkeys.OnToggleHandsFree = ToggleHandsFree;
        hotkeys.OnCancel = Cancel;

        if (hotkeys.Start()) Log.Write("Οι συντομεύσεις είναι ενεργές");
        RefreshPermissions();
        // Η άδεια μικροφώνου αλλάζει από τις Ρυθμίσεις των Windows· την ελέγχουμε συνέχεια, ώστε το παράθυρο να δείχνει την αλήθεια.
        var permissions = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        permissions.Tick += (_, _) => RefreshPermissions();
        permissions.Start();

        model.Changed += what => { if (what == "model") ApplySpeechModel(); };
        ApplySpeechModel();
        model.RefreshDevices();

        if (showWindow) ShowWindow();
        WarnIfWisprFlowRunning();
    }

    public void ShowWindow()
    {
        RefreshPermissions();
        mainWindow ??= new MainWindow(model);
        mainWindow.ShowAndActivate();
    }

    public void Quit()
    {
        Log.Write("Έξοδος");
        hotkeys.Stop();
        recorder.Cancel();
        tray?.Dispose();
        WhisperEngine.Shared.Unload();
        Application.Current.Shutdown();
    }

    private void WireModel()
    {
        model.ImportFromWispr = () => ImportFromWispr(showResult: true);
        model.OpenMicrophoneSettings = () => Open("ms-settings:privacy-microphone");
        model.OpenLog = () =>
        {
            if (!File.Exists(Paths.Log)) Log.Write("Αρχείο καταγραφής");
            Open(Paths.Log);
        };
        model.DownloadWhisperModel = () => _ = DownloadWhisperModelAsync();
        model.CancelDownload = () => ModelDownloader.Shared.Cancel();
        model.Copy = text => paster.Copy(text);
    }

    private static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception error) { Log.Write($"Δεν άνοιξε το {target}: {error.Message}"); }
    }

    private void RefreshPermissions()
    {
        model.ShortcutsActive = hotkeys.IsRunning || hotkeys.Start();
        model.MicrophoneGranted = MicrophoneAllowed();
    }

    /// Οι ρυθμίσεις απορρήτου των Windows για το μικρόφωνο: για όλο τον υπολογιστή, για τον χρήστη,
    /// και για τις «κλασικές» εφαρμογές (όπως αυτή). Αν κάποια λέει Deny, το μικρόφωνο δίνει μόνο σιωπή.
    private static bool MicrophoneAllowed()
    {
        const string store = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
        static bool Denied(RegistryKey root, string path)
        {
            try
            {
                using var key = root.OpenSubKey(path);
                return string.Equals(key?.GetValue("Value") as string, "Deny", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
        return !Denied(Registry.LocalMachine, store) && !Denied(Registry.CurrentUser, store) && !Denied(Registry.CurrentUser, store + @"\NonPackaged");
    }

    private void WarnIfWisprFlowRunning()
    {
        if (!Process.GetProcesses().Any(p => p.ProcessName.Contains("Wispr Flow", StringComparison.OrdinalIgnoreCase)
                                             || p.ProcessName.Equals("WisprFlow", StringComparison.OrdinalIgnoreCase))) return;
        MessageBox.Show("Χρησιμοποιεί την ίδια συντόμευση (Ctrl + Win), οπότε θα ηχογραφούν και οι δύο εφαρμογές μαζί. Κλείσε το Wispr Flow από το εικονίδιό του στη γραμμή εργασιών.",
            "Το Wispr Flow είναι ανοιχτό", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // MARK: Μοντέλο ομιλίας

    /// Το Whisper μένει φορτωμένο στη μνήμη μόνο όσο είναι επιλεγμένο (το large-v3 πιάνει ~3 GB).
    /// Αν λείπει το μοντέλο, το κατεβάζουμε μόνοι μας: χωρίς αυτό η δωρεάν υπαγόρευση δεν δουλεύει.
    private void ApplySpeechModel()
    {
        if (model.SpeechModel == SpeechModel.Whisper)
        {
            if (model.WhisperModelExists) _ = LoadWhisperAsync();
            else if (!ModelDownloader.Shared.IsRunning) _ = DownloadWhisperModelAsync();
        }
        else
        {
            WhisperEngine.Shared.Unload();
        }
    }

    private async Task LoadWhisperAsync()
    {
        try
        {
            await WhisperEngine.Shared.LoadAsync(Settings.Shared.WhisperModel);
        }
        catch (Exception error)
        {
            // Συνήθως: λείπει το Visual C++ Redistributable, ή ο επεξεργαστής δεν έχει AVX2.
            Fail($"Δεν φόρτωσε το Whisper: {error.Message}");
        }
        model.WhisperStateChanged();
    }

    private async Task DownloadWhisperModelAsync()
    {
        if (ModelDownloader.Shared.IsRunning) return;
        Log.Write("Κατέβασμα του μοντέλου Whisper large-v3");
        model.SetDownload(0, 0);
        var progress = new Progress<(double Fraction, long Bytes)>(p => model.SetDownload(p.Fraction, p.Bytes));
        try
        {
            await ModelDownloader.Shared.DownloadAsync(Settings.Shared.WhisperModel, progress);
            model.SetDownload(null, 0);
            if (model.SpeechModel == SpeechModel.Whisper) await LoadWhisperAsync();
        }
        catch (OperationCanceledException)
        {
            model.SetDownload(null, 0);
            Log.Write("Το κατέβασμα του μοντέλου σταμάτησε");
        }
        catch (Exception error)
        {
            model.SetDownload(null, 0);
            Fail($"Το κατέβασμα του μοντέλου Whisper απέτυχε: {error.Message}. Πάτα ξανά «Κατέβασμα», συνεχίζει από εκεί που σταμάτησε.");
        }
    }

    // MARK: Ηχογράφηση

    private void PushToTalkKeys(bool held)
    {
        if (held && active == null) StartRecording(handsFree: false);
        else if (!held && active is { HandsFree: false }) FinishRecording();
    }

    private void ToggleHandsFree()
    {
        if (active == null) StartRecording(handsFree: true); else FinishRecording();
    }

    private void Cancel()
    {
        if (active is not { } current) return;
        recorder.Cancel();
        current.Pipeline.Cancel();
        StopWatchdog();
        active = null;
        hotkeys.SetRecording(false, pushToTalk: false);
        Sounds.Cancel();
        overlay.Flash("Ακυρώθηκε");
        UpdateIndicators();
        Log.Write("Ακύρωση ηχογράφησης");
    }

    private void StartRecording(bool handsFree)
    {
        if (model.SpeechModel == SpeechModel.Whisper && !model.WhisperModelExists)
        {
            overlay.Flash(model.DownloadProgress is { } p ? $"Κατεβαίνει το Whisper {p:P0}" : "Λείπει το μοντέλο Whisper", warning: true, seconds: 2.5);
            ShowWindow();
            return;
        }
        var pipeline = new DictationPipeline(Transcriber(model.SpeechModel), model.Vocabulary.Copy());
        recorder.OnSamples = samples => pipeline.Append(samples);
        try
        {
            recorder.Start(string.IsNullOrEmpty(model.MicrophoneId) ? null : model.MicrophoneId);
        }
        catch (Exception error)
        {
            Fail($"Δεν ξεκίνησε η ηχογράφηση: {error.Message}");
            if (!model.MicrophoneGranted) ShowWindow();
            return;
        }
        hotkeys.SetRecording(true, pushToTalk: !handsFree);
        active = new ActiveRecording(handsFree, pipeline);
        overlay.Show(new OverlayState.Recording(handsFree));
        Sounds.Start();
        StartWatchdog();
        UpdateIndicators();
    }

    /// Ελέγχει την ηχογράφηση πέντε φορές το δευτερόλεπτο:
    /// - αν άφησες Ctrl/Win αλλά το μήνυμα χάθηκε, σταματά (αλλιώς η ηχογράφηση «κολλάει»)
    /// - αν το μικρόφωνο δεν δίνει ήχο, ξαναξεκινά με την προεπιλογή των Windows και το δείχνει
    /// - στα 15 λεπτά τελειώνει μόνη της
    private void StartWatchdog()
    {
        StopWatchdog();
        watchdog = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        watchdog.Tick += (_, _) => CheckRecording();
        watchdog.Start();
    }

    private void StopWatchdog()
    {
        watchdog?.Stop();
        watchdog = null;
    }

    private void CheckRecording()
    {
        if (active is not { } current) { StopWatchdog(); return; }
        var elapsed = DateTime.UtcNow - current.Started;
        if (!current.HandsFree && elapsed > TimeSpan.FromSeconds(0.3))
        {
            var ctrl = Native.IsDown(Native.VK_CONTROL);
            var win = Native.IsDown(Native.VK_LWIN) || Native.IsDown(Native.VK_RWIN);
            if (!(ctrl && win))
            {
                Log.Write("Τα πλήκτρα έχουν αφεθεί χωρίς να έρθει μήνυμα, τελειώνω την ηχογράφηση");
                FinishRecording();
                return;
            }
        }
        var silentMicrophone = recorder.CapturedSamples == 0;
        if (silentMicrophone && elapsed > TimeSpan.FromSeconds(1.5) && !current.RestartedMicrophone)
        {
            Log.Write("Το μικρόφωνο δεν δίνει ήχο, ξαναξεκινώ με την προεπιλογή των Windows");
            current.RestartedMicrophone = true;
            recorder.Cancel();
            try { recorder.Start(null); } catch (Exception error) { Log.Write($"Ούτε η προεπιλογή ξεκίνησε: {error.Message}"); }
        }
        overlay.NoAudio = silentMicrophone && elapsed > TimeSpan.FromSeconds(3);
        if (elapsed > MaxRecording)
        {
            Log.Write("Η ηχογράφηση έφτασε τα 15 λεπτά, τελειώνει");
            FinishRecording();
        }
    }

    private void FinishRecording()
    {
        if (active is not { } current) return;
        StopWatchdog();
        hotkeys.SetRecording(false, pushToTalk: false);
        var recording = recorder.Stop();
        var elapsed = DateTime.UtcNow - current.Started;
        active = null;
        Sounds.Stop();

        if (recording.Duration < 0.35 || recording.Peak < 0.004f)
        {
            Log.Write($"Παράλειψη: {recording.Duration:0.00} δευτ., μέγιστη ένταση {recording.Peak:0.0000}");
            current.Pipeline.Cancel();
            if (recording.Duration == 0 && elapsed > TimeSpan.FromSeconds(1)) overlay.Flash("Δεν ακούω μικρόφωνο", warning: true, seconds: 2.5);
            else if (recording.Duration >= 0.35) overlay.Flash("Δεν ακούστηκε φωνή", warning: true);
            else UpdateIndicators();
            return;
        }

        var speechModel = model.SpeechModel;
        var vocabulary = model.Vocabulary.Copy();
        var started = DateTime.UtcNow;
        var job = new Job();
        pendingJobs++;
        UpdateIndicators();

        void Complete(string? text, Exception? error, SpeechModel used)
        {
            ui.BeginInvoke(() =>
            {
                if (job.Done) return;
                job.Done = true;
                pendingJobs--;
                if (text != null) _ = Deliver(text, used, (DateTime.UtcNow - started).TotalSeconds, recording.Duration);
                else Fail($"Η μεταγραφή απέτυχε: {error?.Message}");
                UpdateIndicators();
            });
        }
        void FallBack(Exception error)
        {
            ui.BeginInvoke(() =>
            {
                if (job.Done || job.FellBack || speechModel != SpeechModel.Scribe) { Complete(null, error, speechModel); return; }
                if (!model.WhisperModelExists) { Complete(null, error, speechModel); return; }
                job.FellBack = true;
                Log.Write($"Scribe v2: {error.Message}, δοκιμάζω Whisper");
                Task.Run(async () =>
                {
                    try { Complete(await new WhisperTranscriber().TranscribeAsync(recording.Samples, vocabulary, CancellationToken.None), null, SpeechModel.Whisper); }
                    catch (Exception whisperError) { Complete(null, whisperError, SpeechModel.Whisper); }
                });
            });
        }

        Task.Run(async () =>
        {
            try { Complete(await current.Pipeline.FinishAsync(), null, speechModel); }
            catch (Exception error) { FallBack(error); }
        });

        // Το Scribe απαντά σε λίγα δευτερόλεπτα· το Whisper στον επεξεργαστή μπορεί να θέλει πολύ περισσότερο.
        var timeout = speechModel == SpeechModel.Scribe
            ? TranscriptionTimeout
            : TimeSpan.FromSeconds(Math.Max(TranscriptionTimeout.TotalSeconds, 30 + recording.Duration * 2));
        var timer = new DispatcherTimer { Interval = timeout };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!job.Done) FallBack(new VoiceException($"δεν απάντησε σε {(int)timeout.TotalSeconds} δευτ."));
        };
        timer.Start();
    }

    private static ITranscriber Transcriber(SpeechModel speechModel) =>
        speechModel == SpeechModel.Scribe ? new ScribeTranscriber() : new WhisperTranscriber();

    private async Task Deliver(string raw, SpeechModel speechModel, double seconds, double audio)
    {
        var text = model.Vocabulary.Apply(raw.Trim());
        Log.Write($"{speechModel.Id()}: {audio:0.0} δευτ. ήχου, {seconds:0.0} δευτ. αναμονή μετά το άφημα, {text.Length} χαρακτήρες");
        if (text.Length == 0)
        {
            if (active == null) overlay.Flash("Δεν ακούστηκε φωνή", warning: true);
            return;
        }
        model.LastError = "";
        model.AddHistory(new HistoryEntry
        {
            Date = DateTime.UtcNow, Text = text, Words = HistoryStore.WordCount(text), Seconds = audio, Model = speechModel.Id(),
        });
        try { await paster.PasteAsync(text); }
        catch (Exception error) { Fail($"Το κείμενο δεν μπήκε: {error.Message}. Το βρίσκεις στο ιστορικό της Αρχικής."); }
    }

    private void Fail(string message)
    {
        model.LastError = message;
        Log.Write($"Σφάλμα: {message}");
        System.Media.SystemSounds.Beep.Play();
        if (active == null) overlay.Flash("Σφάλμα μεταγραφής", warning: true, seconds: 3);
    }

    /// Η ένδειξη και το εικονίδιο ακολουθούν πάντα την πραγματική κατάσταση:
    /// ηχογράφηση > μεταγραφή σε εξέλιξη > τίποτα.
    private void UpdateIndicators()
    {
        if (active is { } current)
        {
            model.Phase = Phase.Recording;
            tray?.SetRecording(true);
            if (overlay.State is not OverlayState.Recording r || r.HandsFree != current.HandsFree) overlay.Show(new OverlayState.Recording(current.HandsFree));
        }
        else if (pendingJobs > 0)
        {
            model.Phase = Phase.Transcribing;
            tray?.SetRecording(false);
            if (overlay.State is not OverlayState.Notice) overlay.Show(new OverlayState.Transcribing());
        }
        else
        {
            model.Phase = Phase.Idle;
            tray?.SetRecording(false);
            if (overlay.State is not OverlayState.Notice) overlay.Hide();
        }
    }

    /// Δείχνει την ένδειξη χωρίς ηχογράφηση, για έλεγχο της εμφάνισης (--preview-overlay).
    public void PreviewOverlay(Action? onStep = null)
    {
        overlay.Show(new OverlayState.Recording(false));
        var step = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) =>
        {
            step++;
            if (step > 25) overlay.Level((float)Math.Abs(Math.Sin(step / 3.0)) * 0.1f);
            if (step == 50) overlay.Show(new OverlayState.Recording(true));
            if (step == 100) overlay.NoAudio = true;
            if (step == 150) overlay.Show(new OverlayState.Transcribing());
            if (step == 200) overlay.Flash("Ακυρώθηκε", seconds: 3);
            if (step % 50 == 40) onStep?.Invoke();
            if (step == 250) { timer.Stop(); Application.Current.Shutdown(); }
        };
        timer.Start();
    }

    private void ImportFromWispr(bool showResult)
    {
        var updated = model.Vocabulary.Copy();
        try
        {
            var (words, replacements, snippets) = WisprImporter.ImportInto(updated);
            model.ReplaceVocabulary(updated);
            var message = $"Ήρθαν από το Wispr Flow {words} λέξεις, {replacements} διορθώσεις και {snippets} έτοιμα κείμενα.";
            Log.Write(message);
            if (showResult) MessageBox.Show(message, "Έτοιμο");
        }
        catch (Exception error)
        {
            Log.Write($"Η εισαγωγή από Wispr Flow απέτυχε: {error.Message}");
            if (showResult) MessageBox.Show(error.Message, "Η εισαγωγή απέτυχε");
        }
    }
}
