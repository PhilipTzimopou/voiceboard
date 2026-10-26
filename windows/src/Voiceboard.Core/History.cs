using System.Text.Json.Serialization;

namespace Voiceboard.Core;

/// Μία υπαγόρευση στο ιστορικό, μόνο τοπικά στο history.json.
public sealed class HistoryEntry
{
    [JsonPropertyName("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [JsonPropertyName("date")] public DateTime Date { get; set; }
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("words")] public int Words { get; set; }
    [JsonPropertyName("seconds")] public double Seconds { get; set; }
    [JsonPropertyName("model")] public string Model { get; set; } = "";
}

public static class HistoryStore
{
    private const int Limit = 2000;

    public static List<HistoryEntry> Load() => Json.Read<List<HistoryEntry>>(Paths.History) ?? [];

    public static void Save(IEnumerable<HistoryEntry> entries)
    {
        try { Json.Write(Paths.History, entries.Take(Limit).ToList()); }
        catch (Exception error) { Log.Write($"Δεν γράφτηκε το ιστορικό: {error.Message}"); }
    }

    public static int WordCount(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}

/// Τα νούμερα της Αρχικής: σύνολο, σερί, στόχος της ημέρας, χρόνος που κέρδισες.
public sealed class DictationStats
{
    public int TotalWords { get; }
    public int WordsPerMinute { get; }
    public int StreakDays { get; }
    public int LongestStreak { get; }
    public int WordsToday { get; }
    public int DictationsToday { get; }
    /// Λέξεις ανά μέρα (αρχή της μέρας, τοπική ώρα → λέξεις), για το ημερολόγιο συνήθειας.
    public IReadOnlyDictionary<DateTime, int> WordsByDay { get; }
    /// Λεπτά που γλίτωσες: η ίδια ποσότητα στο πληκτρολόγιο με 40 λέξεις το λεπτό, μείον τον χρόνο που μίλησες.
    public int MinutesSaved { get; }

    public DictationStats(IReadOnlyCollection<HistoryEntry> entries, DateTime? now = null)
    {
        var today = (now ?? DateTime.Now).Date;
        TotalWords = entries.Sum(e => e.Words);
        var spokenMinutes = entries.Sum(e => e.Seconds) / 60;
        WordsPerMinute = spokenMinutes > 0.1 ? (int)Math.Round(TotalWords / spokenMinutes) : 0;
        MinutesSaved = Math.Max(0, (int)Math.Round(TotalWords / 40.0 - spokenMinutes));

        var byDay = new Dictionary<DateTime, int>();
        foreach (var entry in entries)
        {
            var day = entry.Date.ToLocalTime().Date;
            byDay[day] = byDay.GetValueOrDefault(day) + entry.Words;
            if (day == today) DictationsToday++;
        }
        WordsByDay = byDay;
        WordsToday = byDay.GetValueOrDefault(today);

        var cursor = byDay.ContainsKey(today) ? today : today.AddDays(-1);
        while (byDay.ContainsKey(cursor)) { StreakDays++; cursor = cursor.AddDays(-1); }

        int longest = 0, run = 0;
        DateTime? previous = null;
        foreach (var day in byDay.Keys.Order())
        {
            run = previous is { } p && (day - p).TotalDays == 1 ? run + 1 : 1;
            longest = Math.Max(longest, run);
            previous = day;
        }
        LongestStreak = longest;
    }
}
