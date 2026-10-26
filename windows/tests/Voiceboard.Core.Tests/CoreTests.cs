using Microsoft.Data.Sqlite;
using Voiceboard.Core;

namespace Voiceboard.Core.Tests;

/// Κάθε test γράφει σε δικό του προσωρινό φάκελο, όχι στο %APPDATA% του υπολογιστή.
public sealed class TempHome : IDisposable
{
    public string Folder { get; } = Path.Combine(Path.GetTempPath(), "voiceboard-tests-" + Guid.NewGuid());
    public TempHome() { Paths.UseFolder(Folder); Settings.Shared.Reload(); }
    public void Dispose() { try { Directory.Delete(Folder, true); } catch { } }
}

[CollectionDefinition("home", DisableParallelization = true)]
public sealed class HomeCollection;

[Collection("home")]
public class VocabularyTests
{
    [Fact]
    public void ReadsTheMacFormat()
    {
        using var home = new TempHome();
        File.WriteAllText(Paths.Vocabulary, """
            {
              "words" : ["Higgsfield", "Claude Code"],
              "replacements" : [ { "from" : "key.ai", "to" : "kie.ai" } ],
              "snippets" : [ { "from" : "ξέρω γεια", "to" : "Γεια σου!\nΦίλιππος" } ],
              "importedFromWisprAt" : "2026-10-20T10:00:00Z"
            }
            """);
        var vocabulary = Vocabulary.Load();
        Assert.Equal(["Higgsfield", "Claude Code"], vocabulary.Words);
        Assert.Equal("kie.ai", vocabulary.Replacements[0].To);
        Assert.Equal("Γεια σου!\nΦίλιππος", vocabulary.Snippets[0].To);

        vocabulary.Save();
        var saved = File.ReadAllText(Paths.Vocabulary);
        Assert.Contains("ξέρω γεια", saved);          // ελληνικά όπως είναι, όχι ξ…
        Assert.Contains("\"replacements\"", saved);
    }

    [Fact]
    public void AppliesWholeWordsIgnoringCaseLongestFirst()
    {
        var vocabulary = new Vocabulary
        {
            Replacements = [new("key.ai", "kie.ai"), new("κλοντ", "Claude"), new("κλοντ κοντ", "Claude Code")],
            Snippets = [new("ξέρω γεια", "Γεια σου, $1 τα λέμε!")],
        };
        Assert.Equal("Δες το kie.ai και το Claude Code.", vocabulary.Apply("Δες το Key.ai και το κλοντ κοντ."));
        Assert.Equal("κλοντάκι", vocabulary.Apply("κλοντάκι"));                 // όχι μέσα σε λέξη
        Assert.Equal("Γεια σου, $1 τα λέμε!", vocabulary.Apply("ξέρω γεια"));   // το $1 μένει όπως είναι
    }

    [Fact]
    public void KeytermsFollowTheScribeLimits()
    {
        var vocabulary = new Vocabulary
        {
            Words = ["a", "Higgsfield", "higgsfield", "one two three four five six", "bad[term]", new string('x', 50), "Claude Code"],
            Replacements = [new("key.ai", "kie.ai")],
        };
        Assert.Equal(["Higgsfield", "Claude Code", "kie.ai"], vocabulary.ScribeKeyterms);
        Assert.Equal("Higgsfield, Claude Code, kie.ai", vocabulary.WhisperPrompt);

        var many = new Vocabulary { Words = Enumerable.Range(0, 150).Select(i => $"όρος{i}").ToList() };
        Assert.Equal(100, many.ScribeKeyterms.Count);
        Assert.True(many.WhisperPrompt.Length <= 600);
    }

    [Fact]
    public void ImportsFromWisprFlowDatabase()
    {
        using var home = new TempHome();
        var database = Path.Combine(home.Folder, "flow.sqlite");
        using (var connection = new SqliteConnection($"Data Source={database}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE Dictionary (phrase TEXT, replacement TEXT, isSnippet INTEGER, isDeleted INTEGER,
                                         isStarred INTEGER, frequencyUsed INTEGER, createdAt TEXT);
                INSERT INTO Dictionary VALUES ('Higgsfield', NULL, 0, 0, 1, 5, '2026-01-01');
                INSERT INTO Dictionary VALUES ('key.ai', 'kie.ai', 0, 0, 0, 1, '2026-01-01');
                INSERT INTO Dictionary VALUES ('my email', 'philip@example.com', 1, 0, 0, 1, '2026-01-01');
                INSERT INTO Dictionary VALUES ('deleted', NULL, 0, 1, 0, 1, '2026-01-01');
                """;
            command.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var vocabulary = new Vocabulary { Words = ["higgsfield", "Δική μου"] };
        var counts = WisprImporter.ImportInto(vocabulary, database);
        Assert.Equal((1, 1, 1), counts);
        Assert.Equal(["Higgsfield", "Δική μου"], vocabulary.Words);
        Assert.Equal("kie.ai", vocabulary.Replacements.Single().To);
        Assert.Equal("philip@example.com", vocabulary.Snippets.Single().To);
        Assert.NotNull(vocabulary.ImportedFromWisprAt);
    }
}

public class PipelineTests
{
    private sealed class FakeTranscriber(Func<short[], string> answer) : ITranscriber
    {
        public int Calls;
        public Task<string> TranscribeAsync(short[] samples, Vocabulary vocabulary, CancellationToken cancellation)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(answer(samples));
        }
    }

    private static short[] Tone(double seconds, double amplitude = 0.3) =>
        Enumerable.Range(0, (int)(seconds * Audio.SampleRate))
            .Select(i => (short)(Math.Sin(i * 2 * Math.PI * 220 / Audio.SampleRate) * amplitude * 32767)).ToArray();

    private static short[] Silence(double seconds) => new short[(int)(seconds * Audio.SampleRate)];

    [Fact]
    public void JoinContinuesSentencesAcrossPauses()
    {
        var vocabulary = new Vocabulary { Words = ["Claude"] };
        Assert.Equal("Γεια σου και καλημέρα.", DictationPipeline.Join(["Γεια σου", "Και καλημέρα"], vocabulary));
        Assert.Equal("Πρώτη. Δεύτερη.", DictationPipeline.Join(["Πρώτη.", "Δεύτερη"], vocabulary));
        Assert.Equal("Άνοιξε το Claude Code.", DictationPipeline.Join(["Άνοιξε το", "Claude Code"], vocabulary));
        Assert.Equal("so I think I know", DictationPipeline.Join(["so I think", "I know"], vocabulary).TrimEnd('.'));
        Assert.Equal("Μόνο ένα", DictationPipeline.Join(["Μόνο ένα"], vocabulary));
    }

    [Fact]
    public async Task SendsSegmentsAtPausesAndKeepsOrder()
    {
        // Τα κομμάτια μεταγράφονται παράλληλα· το κείμενο εξαρτάται από το μήκος, ώστε να φαίνεται η σειρά.
        var transcriber = new FakeTranscriber(samples => samples.Length > 3 * Audio.SampleRate ? "Πρώτο κομμάτι" : "Δεύτερο κομμάτι");
        var pipeline = new DictationPipeline(transcriber, new Vocabulary());
        pipeline.Append(Tone(3.5));
        pipeline.Append(Silence(1.0));   // παύση ≥ 0,8 δευτ. μετά από ≥ 3 δευτ.: φεύγει το πρώτο κομμάτι
        pipeline.Append(Tone(1.0));
        var text = await pipeline.FinishAsync();
        Assert.Equal(2, transcriber.Calls);
        Assert.Equal("Πρώτο κομμάτι δεύτερο κομμάτι.", text);
    }

    [Fact]
    public async Task SilenceIsNotSent()
    {
        var transcriber = new FakeTranscriber(_ => "δεν πρέπει");
        var pipeline = new DictationPipeline(transcriber, new Vocabulary());
        pipeline.Append(Silence(2));
        Assert.Equal("", await pipeline.FinishAsync());
        Assert.Equal(0, transcriber.Calls);
    }

    [Fact]
    public void WavHeaderIsCorrect()
    {
        var wav = Audio.Wav(new short[] { 1, -1, 300 });
        Assert.Equal(44 + 6, wav.Length);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal(16000, BitConverter.ToInt32(wav, 24));
        Assert.Equal(6, BitConverter.ToInt32(wav, 40));
    }

    [Fact]
    public void CleansWhisperHallucinations()
    {
        Assert.Equal("", WhisperTranscriber.Clean("Υπότιτλοι από την κοινότητα Amara.org"));
        Assert.Equal("Γεια σου κόσμε", WhisperTranscriber.Clean(" [Μουσική]  Γεια   σου κόσμε "));
    }
}

[Collection("home")]
public class SettingsAndHistoryTests
{
    [Fact]
    public void ConfigUsesTheMacKeys()
    {
        using var home = new TempHome();
        File.WriteAllText(Paths.Config, """{ "elevenLabsAPIKey": "sk_test", "language": "el" }""");
        Settings.Shared.Reload();
        Assert.True(Settings.Shared.HasElevenLabsKey);
        Assert.Equal(SpeechModel.Scribe, Settings.Shared.Model);
        Settings.Shared.Model = SpeechModel.Whisper;
        Settings.Shared.ElevenLabsApiKey = " sk_new ";
        Settings.Shared.Reload();
        Assert.Equal(SpeechModel.Whisper, Settings.Shared.Model);
        Assert.Equal("sk_new", Settings.Shared.ElevenLabsApiKey);
        Assert.Contains("\"elevenLabsAPIKey\": \"sk_new\"", File.ReadAllText(Paths.Config));
        Assert.Equal(Path.Combine(home.Folder, "models", "ggml-large-v3.bin"), Settings.Shared.WhisperModel);
    }

    [Fact]
    public void StatsCountStreaksAndToday()
    {
        var now = new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Local);
        HistoryEntry Entry(int daysAgo, int words) =>
            new() { Date = now.AddDays(-daysAgo).ToUniversalTime(), Words = words, Seconds = words / 2.0, Text = "x" };
        var entries = new List<HistoryEntry> { Entry(0, 100), Entry(0, 50), Entry(1, 10), Entry(2, 10), Entry(5, 10), Entry(6, 10), Entry(7, 10), Entry(8, 10) };
        var stats = new DictationStats(entries, now);
        Assert.Equal(150, stats.WordsToday);
        Assert.Equal(2, stats.DictationsToday);
        Assert.Equal(3, stats.StreakDays);
        Assert.Equal(4, stats.LongestStreak);
        Assert.Equal(210, stats.TotalWords);
        Assert.Equal(120, stats.WordsPerMinute);
    }

    [Fact]
    public void HistoryRoundTrips()
    {
        using var home = new TempHome();
        HistoryStore.Save([new HistoryEntry { Date = DateTime.UtcNow, Text = "Γεια", Words = 1, Seconds = 1, Model = "whisper" }]);
        Assert.Equal("Γεια", HistoryStore.Load().Single().Text);
        Assert.Equal(3, HistoryStore.WordCount(" ένα  δύο\nτρία "));
    }
}

public class AudioConverterTests
{
    /// 48 kHz στερεοφωνικό float (η συνηθισμένη μορφή των Windows) → 16 kHz μονοφωνικό, σε κομμάτια των 10 ms.
    [Theory]
    [InlineData(48000, 2, 32, true)]
    [InlineData(44100, 1, 16, false)]
    [InlineData(16000, 2, 24, false)]
    public void ConvertsToSixteenKilohertzMono(int rate, int channels, int bits, bool isFloat)
    {
        var format = isFloat ? NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(rate, channels) : new NAudio.Wave.WaveFormat(rate, bits, channels);
        var converter = new AudioConverter(format);
        var bytesPerFrame = bits / 8 * channels;
        var output = new List<short>();
        var seconds = 2;
        var chunkFrames = rate / 100;
        for (var start = 0; start < rate * seconds; start += chunkFrames)
        {
            var buffer = new byte[chunkFrames * bytesPerFrame];
            for (var i = 0; i < chunkFrames; i++)
            {
                var value = 0.5 * Math.Sin(2 * Math.PI * 440 * (start + i) / rate);
                for (var c = 0; c < channels; c++)
                {
                    var offset = (i * channels + c) * (bits / 8);
                    if (isFloat) BitConverter.GetBytes((float)value).CopyTo(buffer, offset);
                    else if (bits == 16) BitConverter.GetBytes((short)(value * 32767)).CopyTo(buffer, offset);
                    else { var v = (int)(value * 8388607); buffer[offset] = (byte)v; buffer[offset + 1] = (byte)(v >> 8); buffer[offset + 2] = (byte)(v >> 16); }
                }
            }
            output.AddRange(converter.Convert(buffer, buffer.Length));
        }
        Assert.InRange(output.Count, 16000 * seconds - 200, 16000 * seconds + 10);
        // Μετά τη σύντομη καθυστέρηση του φίλτρου: πλάτος ~0,5 και 440 κύκλοι το δευτερόλεπτο.
        var steady = output.Skip(1600).Take(16000).Select(s => s / 32768.0).ToArray();
        Assert.InRange(steady.Max(), 0.45, 0.55);
        var crossings = Enumerable.Range(1, steady.Length - 1).Count(i => steady[i - 1] < 0 && steady[i] >= 0);
        Assert.InRange(crossings, 435, 445);
    }
}
