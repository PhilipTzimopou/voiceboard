using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Voiceboard.Core;

namespace Voiceboard;

/// Αυτόματος έλεγχος σε πραγματικά Windows (τον τρέχει το GitHub Actions μετά το χτίσιμο):
///   Voiceboard.exe --self-test <φάκελος αποτελεσμάτων> [--model ggml.bin --wav φωνή.wav --language en --expect λέξη]
/// Ελέγχει ό,τι γίνεται χωρίς άνθρωπο μπροστά στην οθόνη: φόρτωση και μεταγραφή Whisper, πρόχειρο, επικόλληση με Ctrl+V,
/// παρακολούθηση πλήκτρων, μικρόφωνα, και βγάζει εικόνες από το παράθυρο και την ένδειξη για έλεγχο με το μάτι.
/// Ό,τι θέλει άνθρωπο (μικρόφωνο, πραγματικό πάτημα πλήκτρων, μενού Έναρξη) ΔΕΝ ελέγχεται εδώ.
static class SelfTest
{
    private sealed record Check(string Name, bool Passed, bool Required, string Detail);

    public static int Run(string[] args)
    {
        string Arg(string name, string fallback = "") { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
        var output = Path.GetFullPath(Arg("--self-test", "self-test"));
        Directory.CreateDirectory(output);
        Paths.UseFolder(Path.Combine(output, "data"));
        File.WriteAllText(Paths.Config, JsonSerializer.Serialize(new { whisperModel = Arg("--model"), language = Arg("--language", "el") }));
        Settings.Shared.Reload();
        Settings.Shared.Model = SpeechModel.Whisper;
        SeedSampleData();

        var checks = new List<Check>();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            async Task Step(string name, bool required, Func<Task<string>> body)
            {
                var watch = Stopwatch.StartNew();
                try { checks.Add(new Check(name, true, required, $"{await body()} ({watch.Elapsed.TotalSeconds:0.0} δευτ.)")); }
                catch (Exception error) { checks.Add(new Check(name, false, required, error.ToString())); }
                Console.WriteLine($"{(checks[^1].Passed ? "OK  " : "FAIL")} {name}: {checks[^1].Detail.Split('\n')[0]}");
            }

            await Step("Μικρόφωνα (WASAPI)", true, () => Task.FromResult($"{AudioDevices.Inputs().Count} μικρόφωνα"));
            await Step("Εικονίδια", true, () => { using var icon = IconArt.TrayIcon(true); IconArt.WindowImage(); return Task.FromResult($"{icon.Width}px"); });
            await Step("Εικόνες παραθύρου", true, () => RenderWindows(output));
            await Step("Εικόνες ένδειξης", true, () => RenderOverlay(output));
            await Step("Πρόχειρο", true, ClipboardRoundTrip);
            await Step("Επικόλληση με Ctrl+V και επαναφορά προχείρου", false, PasteIntoTextBox);
            await Step("Παρακολούθηση πλήκτρων (όλες οι επιλογές)", false, HotkeysSeeInjectedKeys);
            var model = Arg("--model");
            if (model.Length > 0)
                await Step("Whisper: φόρτωση και μεταγραφή", true, () => Transcribe(model, Arg("--wav"), Arg("--expect")));

            File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions
            {
                WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
            WhisperEngine.Shared.Unload();
            app.Shutdown(checks.Any(c => c.Required && !c.Passed) ? 1 : 0);
        };
        return app.Run();
    }

    private static void SeedSampleData()
    {
        var vocabulary = new Vocabulary
        {
            Words = ["Higgsfield", "Claude Code", "ElevenLabs", "Voiceboard", "Skool"],
            Replacements = [new("key.ai", "kie.ai"), new("κλοντ κοντ", "Claude Code")],
            Snippets = [new("ξέρω γεια", "Γεια σου! Τα λέμε σύντομα.\nΦίλιππος")],
        };
        vocabulary.Save();
        var now = DateTime.UtcNow;
        var history = Enumerable.Range(0, 40).Select(i => new HistoryEntry
        {
            Date = now.AddHours(-i * 9), Text = i % 2 == 0 ? "Γράψε μου ένα σύντομο email για τη συνάντηση της Τρίτης με το Claude Code." : "Δοκιμή υπαγόρευσης στα Windows.",
            Words = 14 + i * 3, Seconds = 6 + i, Model = i % 3 == 0 ? "whisper" : "scribe_v2",
        }).ToList();
        HistoryStore.Save(history);
    }

    private static async Task<string> RenderWindows(string output)
    {
        var files = new List<string>();
        foreach (var page in Enum.GetValues<Page>())
        {
            Environment.SetEnvironmentVariable("VOICEBOARD_PAGE", page.ToString());
            var window = new MainWindow(new AppModel()) { AllowClose = true, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
            window.Show();
            await Task.Delay(800);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var file = Path.Combine(output, $"window-{page.ToString().ToLowerInvariant()}.png");
            Save((FrameworkElement)window.Content, file);
            files.Add(Path.GetFileName(file));
            window.Close();
        }
        Environment.SetEnvironmentVariable("VOICEBOARD_PAGE", null);
        return string.Join(", ", files);
    }

    private static Task<string> RenderOverlay(string output)
    {
        OverlayState[] states =
        [
            new OverlayState.Recording(false), new OverlayState.Recording(true), new OverlayState.Transcribing(),
            new OverlayState.Notice("Ακυρώθηκε", false), new OverlayState.Notice("Δεν ακούστηκε φωνή", true),
        ];
        var strip = new StackPanel { Background = Brushes.LightGray };
        foreach (var state in states)
        {
            var view = new OverlayView { State = state, Width = OverlayWindow.DipWidth, Height = OverlayWindow.DipHeight, Margin = new Thickness(8) };
            for (var i = 0; i < 20; i++) { view.Push(i % 3 == 0 ? 0.06f : 0.02f); view.Tick(); }
            strip.Children.Add(view);
        }
        var noAudio = new OverlayView { State = new OverlayState.Recording(false), NoAudio = true, Width = OverlayWindow.DipWidth, Height = OverlayWindow.DipHeight, Margin = new Thickness(8) };
        strip.Children.Add(noAudio);
        strip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        strip.Arrange(new Rect(strip.DesiredSize));
        strip.UpdateLayout();
        var file = Path.Combine(output, "overlay.png");
        Save(strip, file);

        // Και ένα πραγματικό παράθυρο ένδειξης: ανοίγει χωρίς να πάρει την εστίαση;
        var overlay = new Overlay();
        var before = GetForegroundWindow();
        overlay.Show(new OverlayState.Recording(false));
        var after = GetForegroundWindow();
        overlay.Hide();
        return Task.FromResult($"overlay.png· η εστίαση {(before == after ? "έμεινε" : "ΑΛΛΑΞΕ")} στο ίδιο παράθυρο");
    }

    private static void Save(FrameworkElement element, string file)
    {
        var width = (int)Math.Ceiling(element.ActualWidth);
        var height = (int)Math.Ceiling(element.ActualHeight);
        var bitmap = new RenderTargetBitmap(Math.Max(width, 1), Math.Max(height, 1), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(file);
        encoder.Save(stream);
    }

    private static HwndSource MessageWindow() =>
        new(new HwndSourceParameters("Voiceboard self-test") { Width = 0, Height = 0, WindowStyle = 0, ParentWindow = new IntPtr(-3) });

    private static Task<string> ClipboardRoundTrip()
    {
        using var window = MessageWindow();
        var paster = new Paster(window.Handle);
        const string text = "Ελληνικά και English ✓ «εισαγωγικά»";
        if (!paster.Copy(text)) throw new Exception("Δεν γράφτηκε το πρόχειρο");
        var read = paster.ReadText();
        if (read != text) throw new Exception($"Διαβάστηκε «{read}»");
        return Task.FromResult("γράφει και διαβάζει ελληνικά");
    }

    private static async Task<string> PasteIntoTextBox()
    {
        using var owner = MessageWindow();
        var paster = new Paster(owner.Handle);
        paster.Copy("προηγούμενο περιεχόμενο");
        var box = new TextBox { FontSize = 20 };
        var window = new Window { Title = "Voiceboard paste test", Width = 500, Height = 120, Content = box, Topmost = true };
        window.Show();
        window.Activate();
        Native.SetForegroundWindow(new WindowInteropHelper(window).Handle);
        box.Focus();
        await Task.Delay(400);
        await paster.PasteAsync("Το κείμενο της υπαγόρευσης");
        await Task.Delay(300);
        var pasted = box.Text;
        var restored = paster.ReadText();
        window.Close();
        if (pasted != "Το κείμενο της υπαγόρευσης") throw new Exception($"Στο πεδίο μπήκε «{pasted}»");
        if (restored != "προηγούμενο περιεχόμενο") throw new Exception($"Το πρόχειρο έμεινε «{restored}»");
        return "μπήκε στο πεδίο, το πρόχειρο επανήλθε";
    }

    /// Για κάθε επιλογή συντόμευσης: πατά και αφήνει τα πλήκτρα σαν πραγματικό πληκτρολόγιο και ελέγχει ότι
    /// η παρακολούθηση είδε «πατημένα → αφέθηκαν». Επίσης ότι Ctrl + Win + Shift ΔΕΝ μετράει ως Ctrl + Win.
    private static async Task<string> HotkeysSeeInjectedKeys()
    {
        var events = new List<bool>();
        var monitor = new HotkeyMonitor(Dispatcher.CurrentDispatcher) { OnPushToTalkKeys = held => events.Add(held) };
        if (!monitor.Start()) throw new Exception("Δεν μπήκαν τα hooks");
        static Native.INPUT Key(int vk, bool up)
        {
            var input = Native.Key(vk, up, extended: vk is Native.VK_LWIN or Native.VK_RWIN or Native.VK_RCONTROL or Native.VK_RMENU);
            input.u.ki.dwExtraInfo = UIntPtr.Zero;   // σαν πραγματικό πληκτρολόγιο, όχι σαν δικό μας πλήκτρο
            return input;
        }
        async Task<string> Press(string preset, params int[] keys)
        {
            monitor.Configure(preset, "middle");
            events.Clear();
            Native.Send(keys.Select(k => Key(k, false)).ToArray());
            await Task.Delay(300);
            Native.Send(keys.Reverse().Select(k => Key(k, true)).ToArray());
            await Task.Delay(300);
            return string.Join(" → ", events.Select(e => e ? "πατημένα" : "αφέθηκαν"));
        }
        var results = new List<string>();
        foreach (var (preset, keys) in new (string, int[])[]
        {
            ("ctrl-win", [Native.VK_LCONTROL, Native.VK_LWIN]),
            ("ctrl-shift", [Native.VK_LCONTROL, Native.VK_LSHIFT]),
            ("alt-win", [Native.VK_LMENU, Native.VK_LWIN]),
            ("right-ctrl", [Native.VK_RCONTROL]),
        })
        {
            var seen = await Press(preset, keys);
            results.Add($"{preset}: {seen}");
            if (seen != "πατημένα → αφέθηκαν") { monitor.Stop(); throw new Exception(string.Join("; ", results)); }
        }
        var extra = await Press("ctrl-win", Native.VK_LCONTROL, Native.VK_LSHIFT, Native.VK_LWIN);
        monitor.Stop();
        results.Add($"ctrl-win με Shift: {(extra.Length == 0 ? "σωστά τίποτα" : extra)}");
        if (extra.Contains("πατημένα")) throw new Exception(string.Join("; ", results));
        return string.Join("; ", results);
    }

    private static async Task<string> Transcribe(string model, string wav, string expect)
    {
        if (!await WhisperEngine.Shared.LoadAsync(model)) throw new Exception($"Δεν βρέθηκε το μοντέλο {model}");
        var bytes = File.ReadAllBytes(wav);
        var position = 12;
        while (System.Text.Encoding.ASCII.GetString(bytes, position, 4) != "data") position += 8 + BitConverter.ToInt32(bytes, position + 4);
        var samples = new short[BitConverter.ToInt32(bytes, position + 4) / 2];
        Buffer.BlockCopy(bytes, position + 8, samples, 0, samples.Length * 2);

        var pipeline = new DictationPipeline(new WhisperTranscriber(), new Vocabulary());
        for (var i = 0; i < samples.Length; i += 1600) pipeline.Append(samples.AsSpan(i, Math.Min(1600, samples.Length - i)));
        var text = await pipeline.FinishAsync();
        if (expect.Length > 0 && !text.Contains(expect, StringComparison.OrdinalIgnoreCase)) throw new Exception($"Έγραψε «{text}»");
        return $"{WhisperEngine.Shared.Runtime}: «{text}»";
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
