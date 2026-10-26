using System.Runtime.InteropServices;
using Voiceboard.Core;
using static Voiceboard.Native;

namespace Voiceboard;

/// Ίδια μέθοδος με το Wispr Flow και την έκδοση Mac: κρατάμε το πρόχειρο, βάζουμε το κείμενο,
/// πατάμε Ctrl+V και επαναφέρουμε το πρόχειρο σε 500 ms, αν δεν το άλλαξε κάτι άλλο στο μεταξύ.
sealed class Paster(IntPtr owner)
{
    private static readonly uint[] GdiFormats =
        [CF_BITMAP, CF_METAFILEPICT, CF_PALETTE, CF_ENHMETAFILE, CF_OWNERDISPLAY, CF_DSPBITMAP, CF_DSPMETAFILEPICT, CF_DSPENHMETAFILE];

    // Το ιστορικό προχείρου των Windows (Win + V) και οι διαχειριστές προχείρου αγνοούν ό,τι έχει αυτά τα σημάδια.
    private readonly uint excludeFromMonitors = RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    private readonly uint canIncludeInHistory = RegisterClipboardFormat("CanIncludeInClipboardHistory");
    private readonly uint canUploadToCloud = RegisterClipboardFormat("CanUploadToCloudClipboard");

    public async Task PasteAsync(string text)
    {
        // Αν κρατάς ακόμα Win/Alt/Shift, το Ctrl+V θα γινόταν άλλη συντόμευση (π.χ. Win + Ctrl + V). Περιμένουμε έως 3 δευτ.
        for (var i = 0; i < 60 && (IsDown(VK_LWIN) || IsDown(VK_RWIN) || IsDown(VK_MENU) || IsDown(VK_SHIFT)); i++)
            await Task.Delay(50);

        var saved = Save();
        if (!SetText(text))
        {
            Log.Write("Δεν άνοιξε το πρόχειρο, η επικόλληση δεν έγινε");
            throw new VoiceException("Το πρόχειρο είναι κλειδωμένο από άλλη εφαρμογή");
        }
        var ours = GetClipboardSequenceNumber();
        Send(Key(VK_CONTROL, false), Key(VK_V, false), Key(VK_V, true), Key(VK_CONTROL, true));

        await Task.Delay(500);
        if (GetClipboardSequenceNumber() != ours) return;   // κάτι άλλο έβαλε στο πρόχειρο στο μεταξύ: το αφήνουμε
        Restore(saved);
    }

    /// Βάζει κείμενο στο πρόχειρο χωρίς επικόλληση (κουμπί «Αντιγραφή» στο ιστορικό).
    public bool Copy(string text) => SetText(text, transient: false);

    private bool OpenWithRetry()
    {
        // Άλλη εφαρμογή μπορεί να κρατά το πρόχειρο για λίγα ms.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (OpenClipboard(owner)) return true;
            Thread.Sleep(20);
        }
        return false;
    }

    private bool SetText(string text, bool transient = true)
    {
        if (!OpenWithRetry()) return false;
        try
        {
            EmptyClipboard();
            var bytes = new byte[(text.Length + 1) * 2];
            System.Text.Encoding.Unicode.GetBytes(text, 0, text.Length, bytes, 0);
            var ok = Put(CF_UNICODETEXT, bytes);
            if (transient)
            {
                Put(excludeFromMonitors, [0, 0, 0, 0]);
                Put(canIncludeInHistory, [0, 0, 0, 0]);
                Put(canUploadToCloud, [0, 0, 0, 0]);
            }
            return ok;
        }
        finally { CloseClipboard(); }
    }

    private List<(uint Format, byte[] Data)> Save()
    {
        var saved = new List<(uint, byte[])>();
        if (!OpenWithRetry()) return saved;
        try
        {
            for (var format = EnumClipboardFormats(0); format != 0; format = EnumClipboardFormats(format))
            {
                if (GdiFormats.Contains(format)) continue;   // λαβές γραφικών, όχι μνήμη· η εικόνα σώζεται ως CF_DIB
                var handle = GetClipboardData(format);
                if (handle == IntPtr.Zero) continue;
                var size = (long)GlobalSize(handle);
                if (size <= 0 || size > 64L * 1024 * 1024) continue;
                var pointer = GlobalLock(handle);
                if (pointer == IntPtr.Zero) continue;
                try
                {
                    var data = new byte[size];
                    Marshal.Copy(pointer, data, 0, (int)size);
                    saved.Add((format, data));
                }
                finally { GlobalUnlock(handle); }
            }
        }
        catch (Exception error) { Log.Write($"Το πρόχειρο δεν σώθηκε όλο: {error.Message}"); }
        finally { CloseClipboard(); }
        return saved;
    }

    private void Restore(List<(uint Format, byte[] Data)> saved)
    {
        if (!OpenWithRetry()) { Log.Write("Δεν άνοιξε το πρόχειρο για επαναφορά"); return; }
        try
        {
            EmptyClipboard();
            foreach (var (format, data) in saved) Put(format, data);
        }
        finally { CloseClipboard(); }
    }

    /// Το πρόχειρο κρατά δικό του αντίγραφο της μνήμης· αν το SetClipboardData αποτύχει, την ελευθερώνουμε εμείς.
    private static bool Put(uint format, byte[] data)
    {
        var memory = GlobalAlloc(GMEM_MOVEABLE, (nuint)Math.Max(data.Length, 1));
        if (memory == IntPtr.Zero) return false;
        var pointer = GlobalLock(memory);
        if (pointer == IntPtr.Zero) { GlobalFree(memory); return false; }
        Marshal.Copy(data, 0, pointer, data.Length);
        GlobalUnlock(memory);
        if (SetClipboardData(format, memory) != IntPtr.Zero) return true;
        GlobalFree(memory);
        return false;
    }

    /// Για τον έλεγχο της εφαρμογής: διαβάζει το κείμενο του προχείρου.
    public string? ReadText()
    {
        if (!OpenWithRetry()) return null;
        try
        {
            var handle = GetClipboardData(CF_UNICODETEXT);
            if (handle == IntPtr.Zero) return null;
            var pointer = GlobalLock(handle);
            try { return Marshal.PtrToStringUni(pointer); }
            finally { GlobalUnlock(handle); }
        }
        finally { CloseClipboard(); }
    }
}
