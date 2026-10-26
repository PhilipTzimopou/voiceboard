using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Voiceboard.Core;

public enum SpeechModel { Scribe, Whisper }

public static class SpeechModels
{
    public static string Id(this SpeechModel model) => model == SpeechModel.Scribe ? "scribe_v2" : "whisper";

    public static SpeechModel? Parse(string? id) => id switch
    {
        "scribe_v2" => SpeechModel.Scribe,
        "whisper" => SpeechModel.Whisper,
        _ => null,
    };
}

/// Όλα τα αρχεία της εφαρμογής, στο %APPDATA%\Voiceboard\ (η μεταβλητή VOICEBOARD_HOME αλλάζει τον φάκελο, για τα tests).
public static class Paths
{
    public static string Support { get; private set; } = Init(null);

    public static void UseFolder(string folder) => Support = Init(folder);

    private static string Init(string? folder)
    {
        var path = folder
            ?? Environment.GetEnvironmentVariable("VOICEBOARD_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Voiceboard");
        Directory.CreateDirectory(path);
        return path;
    }

    public static string Config => Path.Combine(Support, "config.json");
    public static string Preferences => Path.Combine(Support, "settings.json");
    public static string Vocabulary => Path.Combine(Support, "vocabulary.json");
    public static string History => Path.Combine(Support, "history.json");
    public static string Log => Path.Combine(Support, "voiceboard.log");
    public static string Models => Path.Combine(Support, "models");
    public static string DefaultWhisperModel => Path.Combine(Models, "ggml-large-v3.bin");

    /// Η βάση του Wispr Flow στα Windows (εφαρμογή Electron, άρα στο %APPDATA%). Δεν έχει ελεγχθεί σε πραγματικό
    /// υπολογιστή με Wispr Flow για Windows· γι' αυτό δοκιμάζουμε και το %LOCALAPPDATA%.
    public static IEnumerable<string> WisprDatabaseCandidates
    {
        get
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Wispr Flow", "flow.sqlite");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wispr Flow", "flow.sqlite");
        }
    }
}

static class Json
{
    /// Ελληνικά όπως είναι (όχι α…), με εσοχές, ώστε τα αρχεία να ανοίγουν και να διορθώνονται με το Σημειωματάριο.
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static T? Read<T>(string path) where T : class
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) : null; }
        catch (Exception error) { Core.Log.Write($"Δεν διαβάστηκε το {Path.GetFileName(path)}: {error.Message}"); return null; }
    }

    /// Γράφει πρώτα σε προσωρινό αρχείο και μετά το μετονομάζει, ώστε μια διακοπή να μη χαλάει το αρχείο.
    public static void Write<T>(string path, T value)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
        File.Move(temp, path, overwrite: true);
    }
}

/// Το config.json, με τα ίδια κλειδιά με την έκδοση Mac. Το κλειδί της ElevenLabs μπαίνει από τις Ρυθμίσεις.
public sealed class AppConfig
{
    [JsonPropertyName("elevenLabsAPIKey")] public string? ElevenLabsApiKey { get; set; }
    [JsonPropertyName("whisperModel")] public string? WhisperModel { get; set; }
    [JsonPropertyName("language")] public string? Language { get; set; }
}

/// Οι επιλογές του παραθύρου (στο Mac ζουν στα UserDefaults): settings.json.
public sealed class Preferences
{
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("microphoneId")] public string MicrophoneId { get; set; } = "";
    [JsonPropertyName("soundsEnabled")] public bool SoundsEnabled { get; set; } = true;
    [JsonPropertyName("dailyGoal")] public int DailyGoal { get; set; } = 1000;
    /// Τα πλήκτρα που κρατάς για να μιλήσεις: ctrl-win, ctrl-shift, alt-win, right-ctrl.
    [JsonPropertyName("pushToTalk")] public string PushToTalk { get; set; } = "ctrl-win";
    /// Το κουμπί του ποντικιού για ηχογράφηση χωρίς χέρια: middle, back, forward, none.
    [JsonPropertyName("handsFree")] public string HandsFree { get; set; } = "middle";
}

public sealed class Settings
{
    public static Settings Shared { get; } = new();

    private readonly object gate = new();
    private AppConfig config = new();
    private Preferences preferences = new();

    private Settings() => Reload();

    public void Reload()
    {
        lock (gate)
        {
            config = Json.Read<AppConfig>(Paths.Config) ?? new AppConfig();
            preferences = Json.Read<Preferences>(Paths.Preferences) ?? new Preferences();
        }
    }

    public string ElevenLabsApiKey
    {
        get { lock (gate) return (config.ElevenLabsApiKey ?? "").Trim(); }
        set { lock (gate) { config.ElevenLabsApiKey = string.IsNullOrWhiteSpace(value) ? null : value.Trim(); SaveConfig(); } }
    }

    public bool HasElevenLabsKey => ElevenLabsApiKey.Length > 0;

    public SpeechModel Model
    {
        get { lock (gate) return SpeechModels.Parse(preferences.Model) ?? (HasElevenLabsKey ? SpeechModel.Scribe : SpeechModel.Whisper); }
        set { lock (gate) { preferences.Model = value.Id(); SavePreferences(); } }
    }

    /// Το αναγνωριστικό του μικροφώνου· κενό = η προεπιλογή των Windows.
    public string MicrophoneId
    {
        get { lock (gate) return preferences.MicrophoneId; }
        set { lock (gate) { preferences.MicrophoneId = value; SavePreferences(); } }
    }

    public bool SoundsEnabled
    {
        get { lock (gate) return preferences.SoundsEnabled; }
        set { lock (gate) { preferences.SoundsEnabled = value; SavePreferences(); } }
    }

    /// Ημερήσιος στόχος λέξεων για το habit tracker.
    public int DailyGoal
    {
        get { lock (gate) return preferences.DailyGoal; }
        set { lock (gate) { preferences.DailyGoal = value; SavePreferences(); } }
    }

    public string PushToTalk
    {
        get { lock (gate) return preferences.PushToTalk; }
        set { lock (gate) { preferences.PushToTalk = value; SavePreferences(); } }
    }

    public string HandsFree
    {
        get { lock (gate) return preferences.HandsFree; }
        set { lock (gate) { preferences.HandsFree = value; SavePreferences(); } }
    }

    public string Language { get { lock (gate) return string.IsNullOrWhiteSpace(config.Language) ? "el" : config.Language!; } }
    public string WhisperModel { get { lock (gate) return string.IsNullOrWhiteSpace(config.WhisperModel) ? Paths.DefaultWhisperModel : config.WhisperModel!; } }

    private void SaveConfig()
    {
        try { Json.Write(Paths.Config, config); } catch (Exception error) { Log.Write($"Δεν γράφτηκε το config.json: {error.Message}"); }
    }

    private void SavePreferences()
    {
        try { Json.Write(Paths.Preferences, preferences); } catch (Exception error) { Log.Write($"Δεν γράφτηκε το settings.json: {error.Message}"); }
    }
}

public static class Log
{
    private static readonly object gate = new();

    public static void Write(string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-ddTHH:mm:ss.fffzzz} {message}{Environment.NewLine}";
        lock (gate)
        {
            try { File.AppendAllText(Paths.Log, line); } catch { /* ένα αρχείο καταγραφής που δεν γράφεται δεν σταματά την υπαγόρευση */ }
        }
    }
}

public sealed class VoiceException(string message) : Exception(message);
