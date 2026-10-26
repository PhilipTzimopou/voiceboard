using System.Runtime.InteropServices;
using System.Windows.Threading;
using Voiceboard.Core;
using static Voiceboard.Native;

namespace Voiceboard;

/// Οι συντομεύσεις, όπως στο Wispr Flow για Windows και στο Control + Option της έκδοσης Mac:
/// - κράτα Ctrl + Win: ηχογράφηση όσο τα κρατάς
/// - μεσαίο κλικ: ηχογράφηση χωρίς χέρια, ξανά κλικ για τέλος
/// - Esc: ακύρωση (μόνο όσο ηχογραφεί)
///
/// Η παρακολούθηση (low-level hooks) τρέχει σε δικό της νήμα, ώστε τίποτα στο παράθυρο ή στο μικρόφωνο
/// να μην την καθυστερεί: τα Windows αφαιρούν σιωπηλά ένα hook που αργεί. Όλες οι ειδοποιήσεις φτάνουν στο νήμα του παραθύρου.
sealed class HotkeyMonitor(Dispatcher ui)
{
    /// Κάθε αλλαγή: true όταν κρατάς Ctrl και Win (χωρίς Shift/Alt).
    public Action<bool> OnPushToTalkKeys = _ => { };
    public Action OnToggleHandsFree = () => { };
    /// Esc, ή συντόμευση των Windows (Ctrl + Win + πλήκτρο) αμέσως μετά το πάτημα.
    public Action OnCancel = () => { };

    private readonly object gate = new();
    private bool recording;
    private DateTime? pushToTalkStartedAt;

    // Μόνο από το νήμα των hooks
    private bool lctrl, rctrl, lwin, rwin, lshift, rshift, lalt, ralt;
    private bool held, winUsed;

    private HookProc? keyboardProc, mouseProc;   // κρατάμε αναφορά, αλλιώς ο garbage collector τα σβήνει
    private IntPtr keyboardHook, mouseHook;
    private uint threadId;

    public bool IsRunning => keyboardHook != IntPtr.Zero && mouseHook != IntPtr.Zero;

    /// Ενημερώνεται από τον Controller, ώστε το Esc να «πιάνεται» μόνο όταν ηχογραφεί.
    public void SetRecording(bool value, bool pushToTalk)
    {
        lock (gate)
        {
            recording = value;
            pushToTalkStartedAt = value && pushToTalk ? DateTime.UtcNow : null;
        }
    }

    public bool Start()
    {
        if (IsRunning) return true;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            threadId = GetCurrentThreadId();
            keyboardProc = KeyboardHook;
            mouseProc = MouseHook;
            var module = GetModuleHandle(null);
            keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, keyboardProc, module, 0);
            mouseHook = SetWindowsHookEx(WH_MOUSE_LL, mouseProc, module, 0);
            if (!IsRunning)
            {
                Log.Write($"Δεν ξεκίνησε η παρακολούθηση πλήκτρων (σφάλμα {Marshal.GetLastWin32Error()})");
                if (keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(keyboardHook);
                if (mouseHook != IntPtr.Zero) UnhookWindowsHookEx(mouseHook);
                keyboardHook = mouseHook = IntPtr.Zero;
                ready.Set();
                return;
            }
            ready.Set();
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0) { }
            UnhookWindowsHookEx(keyboardHook);
            UnhookWindowsHookEx(mouseHook);
            keyboardHook = mouseHook = IntPtr.Zero;
        })
        { Name = "voiceboard.hotkeys", IsBackground = true, Priority = ThreadPriority.Highest };
        thread.Start();
        ready.Wait();
        return IsRunning;
    }

    public void Stop()
    {
        if (threadId != 0) PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }

    private void Post(Action action) => ui.BeginInvoke(action);

    private IntPtr KeyboardHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0) return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        if (info.dwExtraInfo == OurInput) return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);   // δικά μας πλήκτρα

        var message = (int)wParam;
        var down = message is WM_KEYDOWN or WM_SYSKEYDOWN;
        var vk = (int)info.vkCode;
        ResyncModifiers(vk);

        var isModifier = true;
        switch (vk)
        {
            case VK_LCONTROL: lctrl = down; break;
            case VK_RCONTROL: rctrl = down; break;
            case VK_CONTROL: lctrl = down; break;
            case VK_LWIN: lwin = down; break;
            case VK_RWIN: rwin = down; break;
            case VK_LSHIFT: lshift = down; break;
            case VK_RSHIFT: rshift = down; break;
            case VK_SHIFT: lshift = down; break;
            case VK_LMENU: lalt = down; break;
            case VK_RMENU: ralt = down; break;
            case VK_MENU: lalt = down; break;
            default: isModifier = false; break;
        }

        if (isModifier)
        {
            var now = (lctrl || rctrl) && (lwin || rwin) && !(lshift || rshift) && !(lalt || ralt);
            if (now != held)
            {
                held = now;
                if (now) winUsed = true;
                Post(() => OnPushToTalkKeys(now));
            }
            // Αφήνεις το Win μετά την υπαγόρευση: τα Windows θα άνοιγαν το μενού Έναρξη, γιατί δεν είδαν άλλο πλήκτρο
            // ανάμεσα. Κρατάμε το άφημα, στέλνουμε ένα «κενό» πλήκτρο και μετά το άφημα του Win.
            if (!down && vk is VK_LWIN or VK_RWIN && winUsed && !lwin && !rwin)
            {
                winUsed = false;
                Send(Key(VK_MASK, false), Key(VK_MASK, true), Key(vk, true, extended: true));
                return 1;
            }
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        if (down)
        {
            bool isRecording;
            DateTime? startedAt;
            lock (gate) { isRecording = recording; startedAt = pushToTalkStartedAt; }
            if (vk == VK_ESCAPE && isRecording)
            {
                Post(() => OnCancel());
                return 1;
            }
            // Ctrl + Win + πλήκτρο αμέσως μετά το πάτημα = συντόμευση των Windows (π.χ. νέα επιφάνεια εργασίας).
            // Αργότερα (ενώ ήδη μιλάς) δεν ακυρώνουμε, για να μη χάνεται υπαγόρευση από κάποιο τυχαίο πλήκτρο.
            if (startedAt is { } started && DateTime.UtcNow - started < TimeSpan.FromSeconds(0.6)) Post(() => OnCancel());
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    /// Αν χάθηκε κάποιο άφημα πλήκτρου (π.χ. όσο ήταν ανοιχτό το Ctrl + Alt + Del), διορθώνουμε την κατάσταση
    /// από το πραγματικό πληκτρολόγιο. Το τρέχον πλήκτρο το αφήνουμε, γιατί η κατάστασή του δεν έχει ενημερωθεί ακόμα.
    private void ResyncModifiers(int current)
    {
        static void Check(ref bool state, int vk, int current) { if (state && vk != current && !IsDown(vk)) state = false; }
        Check(ref lctrl, VK_LCONTROL, current); Check(ref rctrl, VK_RCONTROL, current);
        Check(ref lwin, VK_LWIN, current); Check(ref rwin, VK_RWIN, current);
        Check(ref lshift, VK_LSHIFT, current); Check(ref rshift, VK_RSHIFT, current);
        Check(ref lalt, VK_LMENU, current); Check(ref ralt, VK_RMENU, current);
    }

    private IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if ((info.flags & LLMHF_INJECTED) == 0)
            {
                switch ((int)wParam)
                {
                    case WM_MBUTTONDOWN:
                        Post(() => OnToggleHandsFree());
                        return 1;
                    case WM_MBUTTONUP:
                        return 1;
                }
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }
}
