using System.Windows.Forms;

namespace Voiceboard;

/// Το εικονίδιο στη γραμμή εργασιών (κάτω δεξιά, δίπλα στο ρολόι). Αλλάζει όσο ηχογραφεί.
/// Τα Windows 11 κρύβουν τα νέα εικονίδια στο βελάκι «^»· ο χρήστης μπορεί να το καρφιτσώσει από εκεί.
sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon icon;
    private readonly System.Drawing.Icon idle = IconArt.TrayIcon(recording: false);
    private readonly System.Drawing.Icon recording = IconArt.TrayIcon(recording: true);
    private bool isRecording;

    public TrayIcon(Action open, Action quit)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Άνοιγμα Voiceboard", null, (_, _) => open());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Έξοδος", null, (_, _) => quit());
        icon = new NotifyIcon { Icon = idle, Text = "Voiceboard", ContextMenuStrip = menu, Visible = true };
        icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) open(); };
    }

    public void SetRecording(bool value)
    {
        if (value == isRecording) return;
        isRecording = value;
        icon.Icon = value ? recording : idle;
        icon.Text = value ? "Voiceboard: ηχογραφεί" : "Voiceboard";
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
        idle.Dispose();
        recording.Dispose();
    }
}
