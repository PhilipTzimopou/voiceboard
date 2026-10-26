using System.Text;

namespace Voiceboard.Core;

public static class Audio
{
    public const int SampleRate = 16_000;

    /// WAV 16 kHz, μονοφωνικό, 16-bit: η μορφή που περιμένουν η ElevenLabs και το Whisper.
    public static byte[] Wav(ReadOnlySpan<short> samples, int sampleRate = SampleRate)
    {
        var byteCount = samples.Length * 2;
        using var stream = new MemoryStream(44 + byteCount);
        using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + byteCount);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(sampleRate); writer.Write(sampleRate * 2);
        writer.Write((short)2); writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(byteCount);
        foreach (var sample in samples) writer.Write(sample);
        writer.Flush();
        return stream.ToArray();
    }

    public static float[] ToFloat(ReadOnlySpan<short> samples)
    {
        var result = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++) result[i] = samples[i] / 32768f;
        return result;
    }

    public static float Peak(ReadOnlySpan<short> samples)
    {
        var peak = 0;
        foreach (var sample in samples) peak = Math.Max(peak, sample == short.MinValue ? short.MaxValue : Math.Abs((int)sample));
        return peak / 32768f;
    }
}

/// Στέλνει τον ήχο για μεταγραφή σε κομμάτια όσο μιλάς: σε κάθε παύση ≥ 0,8 δευτ. (και αφού έχουν
/// μαζευτεί ≥ 3 δευτ.) το κομμάτι φεύγει. Όταν αφήνεις τα πλήκτρα μένει μόνο το τελευταίο, σύντομο κομμάτι.
/// Ίδια λογική και ίδια όρια με την έκδοση Mac.
public sealed class DictationPipeline(ITranscriber transcriber, Vocabulary vocabulary)
{
    private const int FrameLength = 480;                     // 30 ms
    private const int MinSegmentSamples = Audio.SampleRate * 3;
    private const int PauseFrames = 27;                      // ≈ 0,8 δευτ. σιωπής

    private readonly object gate = new();
    private readonly List<short> pending = [];               // ήχος που δεν έχει σταλεί ακόμα
    private readonly List<short> frame = new(FrameLength);
    private readonly List<Task<string>> segments = [];
    private readonly CancellationTokenSource cancellation = new();
    private int silentFrames;
    private float loudest;
    private bool cancelled;

    public void Append(ReadOnlySpan<short> samples)
    {
        lock (gate)
        {
            if (cancelled) return;
            foreach (var sample in samples)
            {
                pending.Add(sample);
                frame.Add(sample);
                if (frame.Count == FrameLength) AnalyzeFrame();
            }
        }
    }

    /// Στέλνει το τελευταίο κομμάτι και επιστρέφει όλο το κείμενο, με τη σωστή σειρά.
    public async Task<string> FinishAsync()
    {
        Task<string>[] all;
        lock (gate)
        {
            SendPending(isLast: true);
            all = [.. segments];
        }
        var parts = new List<string>();
        foreach (var segment in all)
        {
            var clean = (await segment.ConfigureAwait(false)).Trim();
            if (clean.Length > 0) parts.Add(clean);
        }
        return Join(parts, vocabulary);
    }

    public void Cancel()
    {
        lock (gate)
        {
            cancelled = true;
            pending.Clear();
        }
        cancellation.Cancel();
    }

    /// Κάθε κομμάτι το μοντέλο το γράφει σαν να ξεκινά νέα πρόταση. Αν το προηγούμενο δεν έκλεισε με σημείο
    /// στίξης, η παύση ήταν μέσα στην πρόταση: συνεχίζουμε με μικρό γράμμα, εκτός αν η λέξη είναι όνομα του λεξιλογίου.
    public static string Join(IReadOnlyList<string> parts, Vocabulary vocabulary)
    {
        var result = new StringBuilder();
        foreach (var part in parts)
        {
            if (result.Length == 0) { result.Append(part); continue; }
            var next = part;
            var endsSentence = ".!?;…:".Contains(result[^1]);
            var firstWord = new string(next.TakeWhile(c => !char.IsWhiteSpace(c) && !char.IsPunctuation(c)).ToArray());
            var isName = vocabulary.Words.Any(w => string.Equals(w, firstWord, StringComparison.OrdinalIgnoreCase));
            if (!endsSentence && !isName && next.Length > 0 && char.IsUpper(next[0]) && (firstWord.Length > 1 || next[0] != 'I'))
            {
                next = char.ToLowerInvariant(next[0]) + next[1..];
            }
            result.Append(' ').Append(next);
        }
        // Όταν κόβουμε σε κομμάτια, το τελευταίο συχνά βγαίνει χωρίς τελεία.
        if (parts.Count > 1 && result.Length > 0 && char.IsLetterOrDigit(result[^1])) result.Append('.');
        return result.ToString();
    }

    private void AnalyzeFrame()
    {
        var sum = 0f;
        foreach (var sample in frame) { var value = sample / 32768f; sum += value * value; }
        var rms = MathF.Sqrt(sum / frame.Count);
        frame.Clear();
        loudest = Math.Max(loudest * 0.999f, rms);
        // Σιωπή: πολύ χαμηλότερα από τη φωνή σου σε αυτή την ηχογράφηση.
        var silent = rms < Math.Max(0.004f, loudest * 0.12f);
        silentFrames = silent ? silentFrames + 1 : 0;
        if (silentFrames >= PauseFrames && pending.Count >= MinSegmentSamples)
        {
            SendPending(isLast: false);
            silentFrames = 0;
        }
    }

    private void SendPending(bool isLast)
    {
        var samples = pending.ToArray();
        pending.Clear();
        // Κομμάτι μόνο με σιωπή (π.χ. η παύση πριν αφήσεις τα πλήκτρα): δεν στέλνεται.
        if (samples.Length < Audio.SampleRate / 4 || Audio.Peak(samples) < 0.02f) return;
        Log.Write($"Κομμάτι {segments.Count + 1}: {samples.Length / (double)Audio.SampleRate:0.0} δευτ.{(isLast ? " (τελευταίο)" : "")}");
        var token = cancellation.Token;
        segments.Add(Task.Run(() => transcriber.TranscribeAsync(samples, vocabulary, token), token));
    }
}
