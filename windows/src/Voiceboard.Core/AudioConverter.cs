using NAudio.Dsp;
using NAudio.Wave;

namespace Voiceboard.Core;

/// Μετατρέπει ό,τι δίνει το μικρόφωνο (συνήθως 48 kHz, στερεοφωνικό, float) σε 16 kHz, μονοφωνικό, 16-bit,
/// κομμάτι-κομμάτι όσο ηχογραφεί. Για την αλλαγή συχνότητας χρησιμοποιεί τον WDL resampler του NAudio, με φίλτρο,
/// ώστε οι υψηλές συχνότητες να μη «διπλώνουν» μέσα στη φωνή.
public sealed class AudioConverter
{
    private static readonly Guid IeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    private readonly WaveFormat format;
    private readonly bool isFloat;
    private readonly int bytesPerSample;
    private readonly WdlResampler resampler = new();
    private float[] output = new float[4096];

    public AudioConverter(WaveFormat format)
    {
        this.format = format;
        bytesPerSample = format.BitsPerSample / 8;
        isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
                  || (format is WaveFormatExtensible extensible && extensible.SubFormat == IeeeFloat);
        if (!isFloat && bytesPerSample is not (2 or 3 or 4))
            throw new VoiceException($"Άγνωστη μορφή ήχου από το μικρόφωνο ({format})");
        resampler.SetMode(true, 2, false);
        resampler.SetFilterParms();
        resampler.SetFeedMode(true);
        resampler.SetRates(format.SampleRate, Audio.SampleRate);
    }

    public short[] Convert(byte[] buffer, int count)
    {
        var channels = format.Channels;
        var frames = count / (bytesPerSample * channels);
        if (frames == 0) return [];

        var needed = resampler.ResamplePrepare(frames, 1, out var input, out var inputOffset);
        for (var frame = 0; frame < needed; frame++)
        {
            var sum = 0f;
            for (var channel = 0; channel < channels; channel++)
                sum += Sample(buffer, (frame * channels + channel) * bytesPerSample);
            input[inputOffset + frame] = sum / channels;
        }

        var maxOut = (int)((long)frames * Audio.SampleRate / format.SampleRate) + 64;
        if (output.Length < maxOut) output = new float[maxOut];
        var produced = resampler.ResampleOut(output, 0, needed, maxOut, 1);

        var result = new short[produced];
        for (var i = 0; i < produced; i++) result[i] = (short)Math.Clamp(output[i] * 32767f, -32768f, 32767f);
        return result;
    }

    private float Sample(byte[] buffer, int offset)
    {
        if (isFloat) return BitConverter.ToSingle(buffer, offset);
        return bytesPerSample switch
        {
            2 => BitConverter.ToInt16(buffer, offset) / 32768f,
            3 => ((buffer[offset] << 8 | buffer[offset + 1] << 16 | buffer[offset + 2] << 24) >> 8) / 8388608f,
            _ => BitConverter.ToInt32(buffer, offset) / 2147483648f,
        };
    }
}
