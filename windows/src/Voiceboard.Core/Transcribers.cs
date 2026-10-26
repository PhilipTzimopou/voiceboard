using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace Voiceboard.Core;

public interface ITranscriber
{
    /// samples: 16 kHz, μονοφωνικό, 16-bit.
    Task<string> TranscribeAsync(short[] samples, Vocabulary vocabulary, CancellationToken cancellation);
}

/// Scribe v2 της ElevenLabs: POST /v1/speech-to-text, με το λεξιλόγιο στο keyterms.
public sealed class ScribeTranscriber : ITranscriber
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public async Task<string> TranscribeAsync(short[] samples, Vocabulary vocabulary, CancellationToken cancellation)
    {
        var key = Settings.Shared.ElevenLabsApiKey;
        if (key.Length == 0) throw new VoiceException("Λείπει το κλειδί της ElevenLabs (Ρυθμίσεις → Μοντέλο ομιλίας)");

        // Το σώμα το φτιάχνουμε όπως η έκδοση Mac, byte προς byte, ώστε η ElevenLabs να βλέπει ακριβώς το ίδιο αίτημα.
        var boundary = $"Voiceboard-{Guid.NewGuid()}";
        using var body = new MemoryStream();
        void Text(string value) { var bytes = Encoding.UTF8.GetBytes(value); body.Write(bytes); }
        void Field(string name, string value) => Text($"--{boundary}\r\nContent-Disposition: form-data; name=\"{name}\"\r\n\r\n{value}\r\n");
        Field("model_id", "scribe_v2");
        Field("language_code", Settings.Shared.Language);
        Field("tag_audio_events", "false");
        Field("timestamps_granularity", "none");
        Field("no_verbatim", "true");
        foreach (var term in vocabulary.ScribeKeyterms) Field("keyterms", term);
        Text($"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"audio.wav\"\r\nContent-Type: audio/wav\r\n\r\n");
        body.Write(Audio.Wav(samples));
        Text($"\r\n--{boundary}--\r\n");

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.elevenlabs.io/v1/speech-to-text");
        request.Headers.Add("xi-api-key", key);
        request.Content = new ByteArrayContent(body.ToArray());
        request.Content.Headers.TryAddWithoutValidation("Content-Type", $"multipart/form-data; boundary={boundary}");

        using var response = await Http.SendAsync(request, cancellation).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        var raw = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
        JsonElement json;
        try { json = JsonDocument.Parse(raw).RootElement; }
        catch (JsonException) { throw new VoiceException($"Άκυρη απάντηση από την ElevenLabs (HTTP {status})"); }

        if (status == 200 && json.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            return text.GetString() ?? "";

        var detail = "";
        if (json.TryGetProperty("detail", out var detailElement))
        {
            detail = detailElement.ValueKind == JsonValueKind.Object && detailElement.TryGetProperty("message", out var message)
                ? message.ToString() : detailElement.ToString();
        }
        throw new VoiceException($"ElevenLabs HTTP {status}: {detail}");
    }
}

/// Whisper large-v3 τοπικά, μέσα στην εφαρμογή (whisper.cpp μέσω Whisper.net). Χωρίς δίκτυο, χωρίς χρέωση.
/// Το μοντέλο μένει φορτωμένο στη μνήμη μόνο όσο είναι επιλεγμένο το Whisper (το large-v3 πιάνει ~3 GB).
/// Με κάρτα γραφικών που υποστηρίζει Vulkan τρέχει εκεί· αλλιώς στον επεξεργαστή, πιο αργά.
public sealed class WhisperEngine
{
    public static WhisperEngine Shared { get; } = new();

    private readonly SemaphoreSlim gate = new(1, 1);
    private WhisperFactory? factory;
    private string? loadedPath;

    public bool IsLoaded => factory != null;
    public string? Runtime { get; private set; }

    /// Φορτώνει το μοντέλο, αν δεν είναι ήδη φορτωμένο. Επιστρέφει false αν λείπει το αρχείο.
    public async Task<bool> LoadAsync(string modelPath)
    {
        if (!File.Exists(modelPath)) return false;
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (factory != null && loadedPath == modelPath) return true;
            factory?.Dispose();
            var started = DateTime.UtcNow;
            factory = await Task.Run(() => WhisperFactory.FromPath(modelPath)).ConfigureAwait(false);
            loadedPath = modelPath;
            Runtime = RuntimeOptions.LoadedLibrary?.ToString();
            Log.Write($"Whisper: φορτώθηκε στη μνήμη σε {(DateTime.UtcNow - started).TotalSeconds:0.0} δευτ. ({Runtime ?? "άγνωστο"})");
            return true;
        }
        finally { gate.Release(); }
    }

    public void Unload()
    {
        gate.Wait();
        try
        {
            factory?.Dispose();
            factory = null;
            loadedPath = null;
        }
        finally { gate.Release(); }
    }

    /// Μία μεταγραφή κάθε φορά: το whisper.cpp χρησιμοποιεί ήδη όλους τους πυρήνες.
    public async Task<string> TranscribeAsync(float[] samples, string language, string prompt, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (factory == null) throw new VoiceException("Το μοντέλο Whisper δεν έχει φορτωθεί");
            var builder = factory.CreateBuilder().WithLanguage(language);
            if (prompt.Length > 0) builder = builder.WithPrompt(prompt);
            await using var processor = builder.Build();
            var text = new StringBuilder();
            await foreach (var segment in processor.ProcessAsync(samples, cancellation).ConfigureAwait(false))
                text.Append(segment.Text);
            return text.ToString();
        }
        finally { gate.Release(); }
    }
}

public sealed class WhisperTranscriber : ITranscriber
{
    public async Task<string> TranscribeAsync(short[] samples, Vocabulary vocabulary, CancellationToken cancellation)
    {
        var settings = Settings.Shared;
        if (!WhisperEngine.Shared.IsLoaded && !await WhisperEngine.Shared.LoadAsync(settings.WhisperModel).ConfigureAwait(false))
            throw new VoiceException("Δεν έχει κατέβει ακόμα το μοντέλο Whisper (Ρυθμίσεις → Μοντέλο ομιλίας)");
        var raw = await WhisperEngine.Shared.TranscribeAsync(Audio.ToFloat(samples), settings.Language, vocabulary.WhisperPrompt, cancellation)
            .ConfigureAwait(false);
        return Clean(raw);
    }

    private static readonly string[] Hallucinations = ["Amara.org", "Υπότιτλοι", "υπότιτλους", "Ευχαριστώ για την παρακολούθηση"];

    /// Το Whisper «ακούει» φράσεις σε κομμάτια σιωπής. Τις πετάμε.
    public static string Clean(string raw)
    {
        var text = Regex.Replace(raw, @"\[[^\]]*\]", " ");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        if (text.Length < 120 && Hallucinations.Any(h => text.Contains(h, StringComparison.CurrentCultureIgnoreCase))) text = "";
        return text;
    }
}

/// Κατεβάζει το μοντέλο Whisper large-v3 (~3,1 GB) από το Hugging Face, την ίδια πηγή με την έκδοση Mac.
/// Αν διακοπεί, συνεχίζει από εκεί που σταμάτησε (αρχείο .part).
public sealed class ModelDownloader
{
    public const string Url = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3.bin";
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public static ModelDownloader Shared { get; } = new();

    private CancellationTokenSource? running;
    public bool IsRunning => running != null;

    /// progress: 0…1, και τα MB που έχουν κατέβει.
    public async Task DownloadAsync(string destination, IProgress<(double Fraction, long Bytes)> progress, string url = Url)
    {
        if (running != null) return;
        running = new CancellationTokenSource();
        var token = running.Token;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var part = destination + ".part";
            var existing = File.Exists(part) ? new FileInfo(part).Length : 0;

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (existing > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable) existing = 0;
            response.EnsureSuccessStatusCode();
            var resumed = response.StatusCode == System.Net.HttpStatusCode.PartialContent;
            if (!resumed) existing = 0;
            var total = existing + (response.Content.Headers.ContentLength ?? 0);

            await using (var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (var output = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                var buffer = new byte[1 << 20];
                var done = existing;
                var lastReport = DateTime.MinValue;
                int read;
                while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    done += read;
                    if (DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(250))
                    {
                        lastReport = DateTime.UtcNow;
                        progress.Report((total > 0 ? (double)done / total : 0, done));
                    }
                }
                if (total > 0 && done != total) throw new VoiceException($"Το κατέβασμα σταμάτησε στα {done / 1_048_576} MB από {total / 1_048_576} MB");
                progress.Report((1, done));
            }
            File.Move(part, destination, overwrite: true);
            Log.Write($"Κατέβηκε το μοντέλο Whisper: {destination}");
        }
        finally
        {
            running.Dispose();
            running = null;
        }
    }

    public void Cancel() => running?.Cancel();
}
