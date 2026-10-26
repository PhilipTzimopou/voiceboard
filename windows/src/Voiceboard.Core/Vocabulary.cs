using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Voiceboard.Core;

/// Το λεξιλόγιο, στην ίδια μορφή vocabulary.json με την έκδοση Mac:
/// - words: λέξεις και ονόματα που πρέπει να γράφονται σωστά (τα στέλνουμε στο μοντέλο ως υπόδειξη)
/// - replacements: «άκουσε Χ, γράψε Υ», εφαρμόζονται μετά τη μεταγραφή
/// - snippets: λες τη φράση και μπαίνει ολόκληρο το κείμενο
public sealed class Vocabulary
{
    public sealed class Replacement
    {
        [JsonPropertyName("from")] public string From { get; set; } = "";
        [JsonPropertyName("to")] public string To { get; set; } = "";

        public Replacement() { }
        public Replacement(string from, string to) { From = from; To = to; }
    }

    [JsonPropertyName("words")] public List<string> Words { get; set; } = [];
    [JsonPropertyName("replacements")] public List<Replacement> Replacements { get; set; } = [];
    [JsonPropertyName("snippets")] public List<Replacement> Snippets { get; set; } = [];
    [JsonPropertyName("importedFromWisprAt")] public string? ImportedFromWisprAt { get; set; }

    public static Vocabulary Load() => Json.Read<Vocabulary>(Paths.Vocabulary) ?? new Vocabulary();

    public void Save() => Json.Write(Paths.Vocabulary, this);

    /// Αντίγραφο, ώστε μια μεταγραφή που τρέχει να μη βλέπει αλλαγές που κάνεις στο παράθυρο εκείνη τη στιγμή.
    public Vocabulary Copy() => new()
    {
        Words = [.. Words],
        Replacements = Replacements.Select(r => new Replacement(r.From, r.To)).ToList(),
        Snippets = Snippets.Select(r => new Replacement(r.From, r.To)).ToList(),
        ImportedFromWisprAt = ImportedFromWisprAt,
    };

    private static int Length(string text) => new StringInfo(text).LengthInTextElements;

    /// Οι όροι για το keyterms του Scribe v2: κάτω από 50 χαρακτήρες, έως 5 λέξεις,
    /// χωρίς &lt; &gt; { } [ ] \, και έως 100 για να μην ισχύσει η ελάχιστη χρέωση των 20 δευτερολέπτων.
    public List<string> ScribeKeyterms
    {
        get
        {
            var seen = new HashSet<string>();
            var terms = new List<string>();
            foreach (var term in Words.Concat(Replacements.Select(r => r.To)))
            {
                var clean = term.Trim();
                if (Length(clean) < 2 || Length(clean) >= 50) continue;
                if (clean.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 5) continue;
                if (clean.IndexOfAny(['<', '>', '{', '}', '[', ']', '\\', '\n']) >= 0) continue;
                if (!seen.Add(clean.ToLowerInvariant())) continue;
                terms.Add(clean);
                if (terms.Count == 100) break;
            }
            return terms;
        }
    }

    /// Το αρχικό prompt του Whisper: λίστα όρων, κομμένη ώστε να χωράει στο όριο του μοντέλου.
    public string WhisperPrompt
    {
        get
        {
            var prompt = "";
            foreach (var term in ScribeKeyterms)
            {
                var next = prompt.Length == 0 ? term : prompt + ", " + term;
                if (Length(next) > 600) break;
                prompt = next;
            }
            return prompt;
        }
    }

    /// Έτοιμα κείμενα και διορθώσεις, πρώτα οι μεγαλύτερες φράσεις. Ταιριάζουν μόνο ολόκληρες λέξεις, χωρίς πεζά/κεφαλαία.
    public string Apply(string text)
    {
        var result = text;
        var rules = Snippets.Concat(Replacements).OrderByDescending(rule => Length(rule.From)).ToList();
        foreach (var rule in rules)
        {
            var from = rule.From.Trim();
            if (from.Length == 0) continue;
            var pattern = @"(?<![\p{L}\p{N}])" + Regex.Escape(from) + @"(?![\p{L}\p{N}])";
            var to = rule.To;
            result = Regex.Replace(result, pattern, _ => to, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        return result;
    }
}

public static class WisprImporter
{
    /// Διαβάζει (μόνο ανάγνωση) τον πίνακα Dictionary του Wispr Flow και τον ενώνει με το λεξιλόγιο.
    public static (int Words, int Replacements, int Snippets) ImportInto(Vocabulary vocabulary, string? databasePath = null)
    {
        var path = databasePath ?? Paths.WisprDatabaseCandidates.FirstOrDefault(File.Exists)
            ?? throw new VoiceException("Δεν βρέθηκε η βάση του Wispr Flow (flow.sqlite) στον φάκελο AppData.");

        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, DefaultTimeout = 3 }.ToString();
        var words = new List<string>();
        var replacements = new List<Vocabulary.Replacement>();
        var snippets = new List<Vocabulary.Replacement>();
        try
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT phrase, replacement, isSnippet FROM Dictionary
                WHERE isDeleted = 0
                ORDER BY isStarred DESC, frequencyUsed DESC, createdAt DESC
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(0)) continue;
                var phrase = reader.GetString(0).Trim();
                var replacement = reader.IsDBNull(1) ? "" : reader.GetString(1);
                var isSnippet = !reader.IsDBNull(2) && reader.GetInt64(2) == 1;
                if (phrase.Length == 0) continue;
                if (replacement.Length == 0) words.Add(phrase);
                else if (isSnippet) snippets.Add(new(phrase, replacement));
                else replacements.Add(new(phrase, replacement));
            }
        }
        catch (SqliteException error)
        {
            throw new VoiceException($"Δεν διαβάστηκε η βάση του Wispr Flow: {error.Message}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();   // αλλιώς το αρχείο μένει «πιασμένο» και το Wispr Flow δεν μπορεί να το αλλάξει
        }

        static List<Vocabulary.Replacement> Merge(List<Vocabulary.Replacement> existing, List<Vocabulary.Replacement> incoming)
        {
            var result = existing.Where(old => !incoming.Any(n => string.Equals(n.From, old.From, StringComparison.OrdinalIgnoreCase))).ToList();
            result.AddRange(incoming);
            return result;
        }
        var seen = new HashSet<string>();
        vocabulary.Words = words.Concat(vocabulary.Words).Where(w => seen.Add(w.ToLowerInvariant())).ToList();
        vocabulary.Replacements = Merge(vocabulary.Replacements, replacements);
        vocabulary.Snippets = Merge(vocabulary.Snippets, snippets);
        vocabulary.ImportedFromWisprAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        return (words.Count, replacements.Count, snippets.Count);
    }
}
