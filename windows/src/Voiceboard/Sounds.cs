using System.Media;
using Voiceboard.Core;

namespace Voiceboard;

/// Σύντομοι, χαμηλοί ήχοι στην αρχή, στο τέλος και στην ακύρωση, όπως οι ήχοι Tink / Pop / Funk του Mac.
/// Φτιάχνονται με κώδικα, ώστε να μην εξαρτώνται από το θέμα ήχων των Windows.
static class Sounds
{
    private static readonly byte[] StartSound = Tone([(1320, 0.05)], 0.22);
    private static readonly byte[] StopSound = Tone([(880, 0.04), (660, 0.05)], 0.22);
    private static readonly byte[] CancelSound = Tone([(392, 0.07), (294, 0.09)], 0.22);
    private static SoundPlayer? player;   // κρατάμε αναφορά, αλλιώς ο ήχος κόβεται

    public static void Start() => Play(StartSound);
    public static void Stop() => Play(StopSound);
    public static void Cancel() => Play(CancelSound);

    private static void Play(byte[] wav)
    {
        if (!Settings.Shared.SoundsEnabled) return;
        try
        {
            player = new SoundPlayer(new MemoryStream(wav));
            player.Play();
        }
        catch (Exception error) { Log.Write($"Ήχος: {error.Message}"); }
    }

    /// Διαδοχικοί τόνοι με απαλό ξεκίνημα και σβήσιμο, ώστε να μην «κροταλίζουν».
    private static byte[] Tone((double Frequency, double Seconds)[] notes, double volume)
    {
        const int rate = 44_100;
        var samples = new List<short>();
        foreach (var (frequency, seconds) in notes)
        {
            var count = (int)(rate * seconds);
            for (var i = 0; i < count; i++)
            {
                var envelope = Math.Min(1, Math.Min(i / (rate * 0.004), (count - i) / (rate * 0.02)));
                samples.Add((short)(Math.Sin(2 * Math.PI * frequency * i / rate) * envelope * volume * 32767));
            }
        }
        return Audio.Wav(samples.ToArray(), rate);
    }
}
