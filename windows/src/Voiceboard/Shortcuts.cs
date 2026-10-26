using static Voiceboard.Native;

namespace Voiceboard;

/// Οι επιλογές συντομεύσεων που δίνουμε στις Ρυθμίσεις. Μόνο συνδυασμοί που δεν γράφουν χαρακτήρες
/// και δεν πέφτουν πάνω σε συντομεύσεις των Windows όταν τους κρατάς μόνους τους.
/// (Το Ctrl + Alt λείπει επίτηδες: στα Windows λειτουργεί σαν AltGr και γράφει σύμβολα όπως το €.)
static class Shortcuts
{
    public sealed record PushToTalkOption(string Id, string[] Keys);
    public sealed record HandsFreeOption(string Id, string Label);

    public static readonly PushToTalkOption[] PushToTalk =
    [
        new("ctrl-win", ["Ctrl", "Win"]),
        new("ctrl-shift", ["Ctrl", "Shift"]),
        new("alt-win", ["Alt", "Win"]),
        new("right-ctrl", ["Δεξί Ctrl"]),
    ];

    public static readonly HandsFreeOption[] HandsFree =
    [
        new("middle", "Μεσαίο κλικ"),
        new("back", "Πλαϊνό κουμπί ποντικιού (πίσω)"),
        new("forward", "Πλαϊνό κουμπί ποντικιού (μπροστά)"),
        new("none", "Κανένα"),
    ];

    public static PushToTalkOption PushToTalkFor(string id) => PushToTalk.FirstOrDefault(o => o.Id == id) ?? PushToTalk[0];
    public static HandsFreeOption HandsFreeFor(string id) => HandsFree.FirstOrDefault(o => o.Id == id) ?? HandsFree[0];

    public static string Label(PushToTalkOption option) => string.Join(" + ", option.Keys);

    /// Ακριβώς αυτά τα πλήκτρα, χωρίς άλλα (π.χ. Ctrl + Win, όχι Ctrl + Win + Shift).
    public static bool Matches(string id, bool ctrl, bool rightCtrlOnly, bool win, bool shift, bool alt) => id switch
    {
        "ctrl-shift" => ctrl && shift && !win && !alt,
        "alt-win" => alt && win && !ctrl && !shift,
        "right-ctrl" => rightCtrlOnly && !win && !shift && !alt,
        _ => ctrl && win && !shift && !alt,
    };

    /// Το πραγματικό πληκτρολόγιο τώρα: για τον έλεγχο που τελειώνει μια ηχογράφηση αν χάθηκε το άφημα των πλήκτρων.
    public static bool PhysicallyHeld(string id)
    {
        var win = IsDown(VK_LWIN) || IsDown(VK_RWIN);
        return id switch
        {
            "ctrl-shift" => IsDown(VK_CONTROL) && IsDown(VK_SHIFT),
            "alt-win" => IsDown(VK_MENU) && win,
            "right-ctrl" => IsDown(VK_RCONTROL),
            _ => IsDown(VK_CONTROL) && win,
        };
    }
}
