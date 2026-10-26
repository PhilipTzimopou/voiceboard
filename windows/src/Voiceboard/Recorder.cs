using NAudio.CoreAudioApi;
using NAudio.Wave;
using Voiceboard.Core;

namespace Voiceboard;

sealed record AudioInputDevice(string Id, string Name);

/// Τα μικρόφωνα των Windows (WASAPI).
static class AudioDevices
{
    public static List<AudioInputDevice> Inputs()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                .Select(device => new AudioInputDevice(device.ID, device.FriendlyName)).ToList();
        }
        catch (Exception error)
        {
            Log.Write($"Δεν διαβάστηκαν τα μικρόφωνα: {error.Message}");
            return [];
        }
    }
}

/// Ηχογραφεί από το μικρόφωνο σε 16 kHz, μονοφωνικό, 16-bit. Νέα σύνδεση σε κάθε ηχογράφηση,
/// ώστε να ακολουθεί όποιο μικρόφωνο είναι ενεργό εκείνη τη στιγμή.
sealed class Recorder
{
    public sealed record Recording(short[] Samples, double Duration, float Peak);

    /// Η ένταση κάθε κομματιού ήχου (RMS), στο νήμα του ήχου.
    public Action<float> OnLevel = _ => { };
    /// Κάθε νέο κομμάτι ήχου (16 kHz, 16-bit), στο νήμα του ήχου. Το χρησιμοποιεί η αποστολή σε κομμάτια.
    public Action<short[]> OnSamples = _ => { };

    private readonly object gate = new();
    private readonly List<short> samples = [];
    private float peak;
    private WasapiCapture? capture;

    /// deviceId: το μικρόφωνο που διάλεξες στις Ρυθμίσεις· null = η προεπιλογή των Windows.
    public void Start(string? deviceId)
    {
        lock (gate) { samples.Clear(); peak = 0; }

        using var enumerator = new MMDeviceEnumerator();
        MMDevice? device = null;
        if (!string.IsNullOrEmpty(deviceId))
        {
            try { device = enumerator.GetDevice(deviceId); }
            catch { Log.Write("Δεν βρέθηκε το μικρόφωνο που διάλεξες, χρησιμοποιώ την προεπιλογή"); }
            if (device != null && device.State != DeviceState.Active) device = null;
        }
        if (device == null)
        {
            if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console)) throw new VoiceException("Δεν βρέθηκε μικρόφωνο");
            device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
        }

        var capture = new WasapiCapture(device, true, 50);
        var converter = new AudioConverter(capture.WaveFormat);
        capture.DataAvailable += (_, e) =>
        {
            var chunk = converter.Convert(e.Buffer, e.BytesRecorded);
            if (chunk.Length == 0) return;
            var sum = 0f;
            var chunkPeak = 0f;
            foreach (var sample in chunk)
            {
                var value = sample / 32768f;
                sum += value * value;
                chunkPeak = Math.Max(chunkPeak, Math.Abs(value));
            }
            var level = MathF.Sqrt(sum / chunk.Length);
            lock (gate)
            {
                samples.AddRange(chunk);
                peak = Math.Max(peak, level);
            }
            OnSamples(chunk);
            OnLevel(level);
        };
        capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception != null) Log.Write($"Η ηχογράφηση σταμάτησε: {e.Exception.Message}");
        };
        try
        {
            capture.StartRecording();
        }
        catch (UnauthorizedAccessException)
        {
            capture.Dispose();
            throw new VoiceException("Τα Windows δεν επιτρέπουν πρόσβαση στο μικρόφωνο (ρυθμίσεις απορρήτου του μικροφώνου)");
        }
        catch (System.Runtime.InteropServices.COMException error) when ((uint)error.HResult == 0x80070005)
        {
            capture.Dispose();
            throw new VoiceException("Τα Windows δεν επιτρέπουν πρόσβαση στο μικρόφωνο (ρυθμίσεις απορρήτου του μικροφώνου)");
        }
        this.capture = capture;
        Log.Write($"Μικρόφωνο: {device.FriendlyName} ({capture.WaveFormat.SampleRate} Hz, {capture.WaveFormat.Channels} κανάλια)");
    }

    public Recording Stop()
    {
        var current = capture;
        capture = null;
        if (current != null)
        {
            try { current.StopRecording(); } catch { }
            current.Dispose();
        }
        lock (gate)
        {
            var captured = samples.ToArray();
            samples.Clear();
            return new Recording(captured, captured.Length / (double)Audio.SampleRate, peak);
        }
    }

    public void Cancel() => Stop();

    /// Πόσα δείγματα έχουν έρθει από το μικρόφωνο σε αυτή την ηχογράφηση (0 = το μικρόφωνο δεν δίνει ήχο).
    public int CapturedSamples { get { lock (gate) return samples.Count; } }
}
