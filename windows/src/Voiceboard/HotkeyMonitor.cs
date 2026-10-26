using System.Runtime.InteropServices;
using System.Windows.Threading;
using Voiceboard.Core;
using static Voiceboard.Native;

namespace Voiceboard;

/// Οι συντομεύσεις, όποιες διάλεξε ο χρήστης στις Ρυθμίσεις (Combo στο Shortcuts.cs):
/// - κράτα (προεπιλογή Ctrl + Win, όπως στο Wispr Flow για Windows): ηχογράφηση όσο τα κρατάς
/// - χωρίς χέρια (προεπιλογή μεσαίο κλικ): ηχογράφηση με ένα πάτημα, ξανά πάτημα για τέλος
/// - Esc: ακύρωση (μόνο όσο ηχογραφεί)
///
/// Η παρακολούθηση (low-level hooks) τρέχει σε δικό της νήμα, ώστε τίποτα στο παράθυρο ή στο μικρόφωνο
/// να μην την καθυστερεί: τα Windows αφαιρούν σιωπηλά ένα hook που αργεί. Όλες οι ειδοποιήσεις φτάνουν στο νήμα του παραθύρου.
sealed class HotkeyMonitor(Dispatcher ui)
{
    /// Κάθε αλλαγή: true όταν κρατάς τη συντόμευση «κράτα και μίλα».
    public Action<bool> OnPushToTalkKeys = _ => { };
    public Action OnToggleHandsFree = () => { };
    /// Esc, ή συντόμευση των Windows (τα πλήκτρα μας + άλλο πλήκτρο) αμέσως μετά το πάτημα.
    public Action OnCancel = () => { };

    private const int WM_XBUTTONDOWN = 0x020B, WM_XBUTTONUP = 0x020C;

    private readonly object gate = new();
    private bool recording;
    private DateTime? pushToTalkStartedAt;
    private volatile Combo pushToTalk = Combo.Parse(Shortcuts.DefaultPushToTalk);
    private volatile Combo handsFree = Combo.Parse(Shortcuts.DefaultHandsFree);
    /// Όσο ο χρήστης γράφει νέα συντόμευση στις Ρυθμίσεις: τα πλήκτρα πάνε εδώ και όχι στις εφαρμογές.
    private volatile Action<Combo?>? capture;

    // Μόνο από το νήμα των hooks
    private bool lctrl, rctrl, lwin, rwin, lshift, rshift, lalt, ralt;
    private bool held, handsFreeHeld;
    private readonly HashSet<int> swallowed = [];   // πλήκτρα της συντόμευσης που κρατάμε μακριά από τις εφαρμογές
    private bool captureLctrl, captureRctrl, captureLalt, captureRalt, captureLshift, captureRshift, captureLwin, captureRwin;
    private int captureKey;

    private HookProc? keyboardProc, mouseProc;   // κρατάμε αναφορά, αλλιώς ο garbage collector τα σβήνει
    private IntPtr keyboardHook, mouseHook;
    private uint threadId;

    public bool IsRunning => keyboardHook != IntPtr.Zero && mouseHook != IntPtr.Zero;

    /// Οι συντομεύσεις από τις Ρυθμίσεις. Αλλάζουν αμέσως, χωρίς επανεκκίνηση.
    public void Configure(Combo pushToTalkCombo, Combo handsFreeCombo)
    {
        pushToTalk = pushToTalkCombo;
        handsFree = handsFreeCombo;
    }

    /// Ξεκινά την καταγραφή νέας συντόμευσης: το επόμενο πάτημα (πλήκτρα ως το άφημα, ή κουμπί ποντικιού) επιστρέφει
    /// στο done. Esc = ακύρωση (null). Το αριστερό και το δεξί κλικ περνούν κανονικά, για να πατηθεί το «Άκυρο».
    public void Capture(Action<Combo?> done)
    {
        ui.VerifyAccess();
        capture = done;
    }

    public void CancelCapture() => capture = null;

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

    private bool Mods(Combo combo) => combo.ModifiersMatch(lctrl, rctrl, lalt, ralt, lshift, rshift, lwin, rwin);

    /// Ένα «κενό» πλήκτρο ενώ κρατάς τα πλήκτρα: όταν τα αφήσεις, τα Windows δεν ανοίγουν το μενού Έναρξη (Win),
    /// η εφαρμογή δεν πάει στο μενού της (Alt) και δεν αλλάζει η γλώσσα πληκτρολογίου (Ctrl + Shift),
    /// γιατί είδαν άλλο πλήκτρο ανάμεσα. Το πλήκτρο δεν έχει αντιστοίχιση και καμία εφαρμογή δεν το χρησιμοποιεί.
    private static void Mask() => Send(Key(VK_MASK, false), Key(VK_MASK, true));

    private void SetHeld(bool value)
    {
        if (value == held) return;
        held = value;
        if (value && pushToTalk.HasModifiers) Mask();
        Post(() => OnPushToTalkKeys(value));
    }

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

        if (capture is { } done) return CaptureKey(done, vk, down, isModifier);

        var ptt = pushToTalk;
        var hands = handsFree;

        if (isModifier)
        {
            // Συντόμευση μόνο με πλήκτρα ελέγχου: κρατάς = ηχογραφεί.
            if (ptt.Key == 0 && !ptt.IsMouse && ptt.HasModifiers) SetHeld(Mods(ptt));
            // Με πλήκτρο: αν αφήσεις ένα από τα πλήκτρα ελέγχου, τελειώνει.
            else if (held && ptt.Key != 0 && !Mods(ptt)) SetHeld(false);

            // Χωρίς χέρια μόνο με πλήκτρα ελέγχου: κάθε φορά που πατιούνται όλα μαζί.
            if (hands.Key == 0 && !hands.IsMouse && hands.HasModifiers)
            {
                var now = Mods(hands);
                if (now && !handsFreeHeld) { Mask(); Post(() => OnToggleHandsFree()); }
                handsFreeHeld = now;
            }
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        // Πλήκτρο που ανήκει σε συντόμευση: δεν φτάνει στην εφαρμογή (ούτε οι επαναλήψεις όσο το κρατάς, ούτε το άφημα).
        if (down)
        {
            if (ptt.Key == vk && Mods(ptt))
            {
                swallowed.Add(vk);
                SetHeld(true);
                return 1;
            }
            if (hands.Key == vk && Mods(hands))
            {
                if (swallowed.Add(vk)) { if (hands.HasModifiers) Mask(); Post(() => OnToggleHandsFree()); }
                return 1;
            }
            if (swallowed.Contains(vk)) return 1;
        }
        else if (swallowed.Remove(vk))
        {
            if (ptt.Key == vk) SetHeld(false);
            return 1;
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
            // Τα πλήκτρα μας + άλλο πλήκτρο αμέσως μετά το πάτημα = συντόμευση των Windows (π.χ. Ctrl + Win + D).
            // Αργότερα (ενώ ήδη μιλάς) δεν ακυρώνουμε, για να μη χάνεται υπαγόρευση από κάποιο τυχαίο πλήκτρο.
            if (startedAt is { } started && DateTime.UtcNow - started < TimeSpan.FromSeconds(0.6)) Post(() => OnCancel());
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    /// Καταγραφή νέας συντόμευσης: μαζεύουμε ό,τι πατήθηκε μέχρι να αφεθούν όλα. Τίποτα δεν φτάνει στις εφαρμογές.
    private IntPtr CaptureKey(Action<Combo?> done, int vk, bool down, bool isModifier)
    {
        if (down)
        {
            if (vk == VK_ESCAPE && !lctrl && !rctrl && !lalt && !ralt && !lshift && !rshift && !lwin && !rwin)
            {
                Finish(done, null);
                return 1;
            }
            captureLctrl |= lctrl; captureRctrl |= rctrl; captureLalt |= lalt; captureRalt |= ralt;
            captureLshift |= lshift; captureRshift |= rshift; captureLwin |= lwin; captureRwin |= rwin;
            if (!isModifier) captureKey = vk;
            if (lwin || rwin || lalt || ralt) Mask();
            return 1;
        }
        // Όταν αφεθούν όλα, η συντόμευση είναι έτοιμη.
        var anyDown = lctrl || rctrl || lalt || ralt || lshift || rshift || lwin || rwin || (captureKey != 0 && captureKey != vk && IsDown(captureKey));
        if (!anyDown && (captureKey != 0 || captureLctrl || captureRctrl || captureLalt || captureRalt || captureLshift || captureRshift || captureLwin || captureRwin))
        {
            int groups = (captureLctrl || captureRctrl ? 1 : 0) + (captureLalt || captureRalt ? 1 : 0)
                + (captureLshift || captureRshift ? 1 : 0) + (captureLwin || captureRwin ? 1 : 0);
            // Ένα πλήκτρο ελέγχου μόνο του (π.χ. δεξί Ctrl): κρατάμε την πλευρά. Σε συνδυασμό: όποια πλευρά.
            var sided = groups == 1 && captureKey == 0;
            Side Pick(bool left, bool right) => !left && !right ? Side.None : !sided ? Side.Any : left && right ? Side.Any : left ? Side.Left : Side.Right;
            var combo = new Combo(Pick(captureLctrl, captureRctrl), Pick(captureLalt, captureRalt), Pick(captureLshift, captureRshift),
                Pick(captureLwin, captureRwin), captureKey, MouseButton.None);
            Finish(done, combo);
        }
        return 1;
    }

    private void Finish(Action<Combo?> done, Combo? combo)
    {
        capture = null;
        captureLctrl = captureRctrl = captureLalt = captureRalt = captureLshift = captureRshift = captureLwin = captureRwin = false;
        captureKey = 0;
        Post(() => done(combo));
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
                var message = (int)wParam;
                // Πλαϊνά κουμπιά: το ποιο από τα δύο λέει η πάνω λέξη του mouseData (1 = πίσω, 2 = μπροστά).
                var button = message switch
                {
                    WM_MBUTTONDOWN or WM_MBUTTONUP => MouseButton.Middle,
                    WM_XBUTTONDOWN or WM_XBUTTONUP => (info.mouseData >> 16) == 1 ? MouseButton.Back : MouseButton.Forward,
                    _ => MouseButton.None,
                };
                var down = message is WM_MBUTTONDOWN or WM_XBUTTONDOWN;
                if (button != MouseButton.None)
                {
                    if (capture is { } done)
                    {
                        if (down) Finish(done, Combo.None with { Mouse = button });
                        return 1;
                    }
                    if (pushToTalk.Mouse == button)
                    {
                        SetHeld(down);
                        return 1;   // το κλικ δεν φτάνει στην εφαρμογή από κάτω
                    }
                    if (handsFree.Mouse == button)
                    {
                        if (down) Post(() => OnToggleHandsFree());
                        return 1;
                    }
                }
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }
}
