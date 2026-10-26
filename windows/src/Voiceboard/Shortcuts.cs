using System.Runtime.InteropServices;
using System.Text;
using static Voiceboard.Native;

namespace Voiceboard;

public enum Side { None, Any, Left, Right }
public enum MouseButton { None, Middle, Back, Forward }

/// Μια συντόμευση που διάλεξε ο χρήστης: πλήκτρα ελέγχου (Ctrl, Alt, Shift, Win) με προαιρετικά ένα ακόμα πλήκτρο,
/// ή ένα κουμπί του ποντικιού. Αποθηκεύεται ως κείμενο, π.χ. "ctrl+win", "rctrl", "ctrl+shift+key:77", "mouse:middle".
/// Ένα πλήκτρο ελέγχου μόνο του κρατά την πλευρά του (π.χ. δεξί Ctrl)· σε συνδυασμό μετράει όποια πλευρά κι αν πατήσεις.
public sealed record Combo(Side Ctrl, Side Alt, Side Shift, Side Win, int Key, MouseButton Mouse)
{
    public static readonly Combo None = new(Side.None, Side.None, Side.None, Side.None, 0, MouseButton.None);

    public bool IsEmpty => this == None;
    public bool HasModifiers => Ctrl != Side.None || Alt != Side.None || Shift != Side.None || Win != Side.None;
    public bool IsMouse => Mouse != MouseButton.None;
    public static bool IsFunctionKey(int vk) => vk is >= 0x70 and <= 0x87;   // F1-F24

    /// Κάτι που δεν πατιέται κατά λάθος όταν γράφεις: με Ctrl, Alt ή Win, ή πλήκτρο F, ή κουμπί ποντικιού.
    /// Σκέτο Shift ή σκέτο γράμμα θα ξεκινούσαν ηχογράφηση σε κάθε κεφαλαίο ή λέξη.
    public bool IsSafe => IsMouse || IsFunctionKey(Key) || Ctrl != Side.None || Alt != Side.None || Win != Side.None;

    public static Combo Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return None;
        // Οι έτοιμες επιλογές της έκδοσης 1.1
        text = text switch
        {
            "ctrl-win" => "ctrl+win", "ctrl-shift" => "ctrl+shift", "alt-win" => "alt+win", "right-ctrl" => "rctrl",
            "middle" => "mouse:middle", "back" => "mouse:back", "forward" => "mouse:forward", "none" => "",
            _ => text,
        };
        Side ctrl = Side.None, alt = Side.None, shift = Side.None, win = Side.None;
        var key = 0;
        var mouse = MouseButton.None;
        foreach (var part in text.ToLowerInvariant().Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part)
            {
                case "ctrl": ctrl = Side.Any; break;
                case "lctrl": ctrl = Side.Left; break;
                case "rctrl": ctrl = Side.Right; break;
                case "alt": alt = Side.Any; break;
                case "lalt": alt = Side.Left; break;
                case "ralt": alt = Side.Right; break;
                case "shift": shift = Side.Any; break;
                case "lshift": shift = Side.Left; break;
                case "rshift": shift = Side.Right; break;
                case "win": win = Side.Any; break;
                case "lwin": win = Side.Left; break;
                case "rwin": win = Side.Right; break;
                case "mouse:middle": mouse = MouseButton.Middle; break;
                case "mouse:back": mouse = MouseButton.Back; break;
                case "mouse:forward": mouse = MouseButton.Forward; break;
                default:
                    if (part.StartsWith("key:") && int.TryParse(part[4..], System.Globalization.NumberStyles.HexNumber, null, out var vk)) key = vk;
                    break;
            }
        }
        return mouse != MouseButton.None ? None with { Mouse = mouse } : new Combo(ctrl, alt, shift, win, key, MouseButton.None);
    }

    public override string ToString()
    {
        if (IsMouse) return "mouse:" + Mouse.ToString().ToLowerInvariant();
        var parts = new List<string>();
        void Add(Side side, string name) { if (side != Side.None) parts.Add(side switch { Side.Left => "l", Side.Right => "r", _ => "" } + name); }
        Add(Ctrl, "ctrl"); Add(Alt, "alt"); Add(Shift, "shift"); Add(Win, "win");
        if (Key != 0) parts.Add($"key:{Key:x2}");
        return string.Join("+", parts);
    }

    public string Label
    {
        get
        {
            if (IsEmpty) return "Καμία";
            if (IsMouse) return Mouse switch
            {
                MouseButton.Middle => "Μεσαίο κλικ",
                MouseButton.Back => "Πλαϊνό κουμπί ποντικιού (πίσω)",
                _ => "Πλαϊνό κουμπί ποντικιού (μπροστά)",
            };
            var parts = new List<string>();
            void Add(Side side, string name) { if (side != Side.None) parts.Add(side switch { Side.Left => "Αριστερό " + name, Side.Right => "Δεξί " + name, _ => name }); }
            Add(Ctrl, "Ctrl"); Add(Alt, "Alt"); Add(Shift, "Shift"); Add(Win, "Win");
            if (Key != 0) parts.Add(KeyName(Key));
            return string.Join(" + ", parts);
        }
    }

    /// Τα πλήκτρα για τα «κουτάκια» της Αρχικής.
    public IEnumerable<string> Keys => IsMouse || IsEmpty ? [Label] : Label.Split(" + ");

    /// Ακριβώς αυτά τα πλήκτρα ελέγχου, χωρίς άλλα (π.χ. Ctrl + Win, όχι Ctrl + Win + Shift).
    public bool ModifiersMatch(bool lctrl, bool rctrl, bool lalt, bool ralt, bool lshift, bool rshift, bool lwin, bool rwin) =>
        Fits(Ctrl, lctrl, rctrl) && Fits(Alt, lalt, ralt) && Fits(Shift, lshift, rshift) && Fits(Win, lwin, rwin);

    private static bool Fits(Side side, bool left, bool right) => side switch
    {
        Side.None => !left && !right,
        Side.Any => left || right,
        Side.Left => left && !right,
        _ => right && !left,
    };

    /// Το πραγματικό πληκτρολόγιο/ποντίκι τώρα: για τον έλεγχο που τελειώνει μια ηχογράφηση αν χάθηκε το άφημα.
    public bool PhysicallyHeld()
    {
        if (IsMouse) return IsDown(Mouse switch { MouseButton.Middle => 0x04, MouseButton.Back => 0x05, _ => 0x06 });
        static bool Down(Side side, int generic, int left, int right) => side switch
        {
            Side.None => true,
            Side.Left => IsDown(left),
            Side.Right => IsDown(right),
            _ => IsDown(generic),
        };
        var win = Win == Side.None || (Win switch { Side.Left => IsDown(VK_LWIN), Side.Right => IsDown(VK_RWIN), _ => IsDown(VK_LWIN) || IsDown(VK_RWIN) });
        return Down(Ctrl, VK_CONTROL, VK_LCONTROL, VK_RCONTROL) && Down(Alt, VK_MENU, VK_LMENU, VK_RMENU)
            && Down(Shift, VK_SHIFT, VK_LSHIFT, VK_RSHIFT) && win && (Key == 0 || IsDown(Key));
    }

    /// Το όνομα ενός πλήκτρου, όπως το γράφουν τα Windows για το πληκτρολόγιο του χρήστη (π.χ. «Space», «F8», «Q»).
    public static string KeyName(int vk)
    {
        if (IsFunctionKey(vk)) return $"F{vk - 0x6F}";
        var scan = MapVirtualKey((uint)vk, 0 /* MAPVK_VK_TO_VSC */);
        int lParam = (int)(scan << 16);
        if (vk is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E or 0x6F or 0x90) lParam |= 1 << 24;   // πλήκτρα «extended»
        var name = new StringBuilder(64);
        return GetKeyNameText(lParam, name, name.Capacity) > 0 ? name.ToString() : $"Πλήκτρο {vk}";
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetKeyNameText(int lParam, StringBuilder lpString, int cchSize);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);
}

/// Οι προεπιλογές: ίδιες με το Wispr Flow για Windows.
public static class Shortcuts
{
    public const string DefaultPushToTalk = "ctrl+win";
    public const string DefaultHandsFree = "mouse:middle";
}
