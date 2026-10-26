using System.Globalization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using static Voiceboard.Native;

namespace Voiceboard;

abstract record OverlayState
{
    public sealed record Recording(bool HandsFree) : OverlayState;
    public sealed record Transcribing : OverlayState;
    public sealed record Notice(string Text, bool Warning) : OverlayState;
}

/// Η μικρή ένδειξη στο κάτω μέρος της οθόνης. Εμφανίζεται σε ΟΛΕΣ τις οθόνες, ώστε να φαίνεται
/// όποια κι αν κοιτάς. Πάντα από πάνω, δεν παίρνει την εστίαση και δεν δέχεται κλικ (περνούν στο παράθυρο από κάτω).
sealed class Overlay
{
    private readonly List<OverlayWindow> windows = [];
    private readonly DispatcherTimer timer;
    private DispatcherTimer? noticeTimer;
    private DateTime startedAt = DateTime.UtcNow;
    private bool noAudio;

    public OverlayState? State { get; private set; }

    public bool NoAudio
    {
        get => noAudio;
        set { noAudio = value; foreach (var window in windows) window.View.NoAudio = value; }
    }

    public Overlay()
    {
        timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromSeconds(1.0 / 30) };
        timer.Tick += (_, _) => { foreach (var window in windows) window.View.Tick(); };
    }

    public void Show(OverlayState state)
    {
        noticeTimer?.Stop();
        if (state is OverlayState.Recording && State is not OverlayState.Recording) startedAt = DateTime.UtcNow;
        State = state;
        RebuildWindowsIfScreensChanged();
        foreach (var window in windows)
        {
            window.View.State = state;
            window.View.StartedAt = startedAt;
            window.View.NoAudio = noAudio;
            window.View.InvalidateVisual();
            if (!window.IsVisible) window.Show();
            window.Place();
        }
        timer.Start();
    }

    /// Σύντομο μήνυμα (π.χ. «Ακυρώθηκε») που σβήνει μόνο του.
    public void Flash(string text, bool warning = false, double seconds = 1.6)
    {
        Show(new OverlayState.Notice(text, warning));
        noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        noticeTimer.Tick += (_, _) => Hide();
        noticeTimer.Start();
    }

    public void Level(float value) { foreach (var window in windows) window.View.Push(value); }

    public void Hide()
    {
        noticeTimer?.Stop();
        State = null;
        noAudio = false;
        timer.Stop();
        foreach (var window in windows) { window.Hide(); window.View.Reset(); }
    }

    private void RebuildWindowsIfScreensChanged()
    {
        var screens = System.Windows.Forms.Screen.AllScreens.Select(s => s.WorkingArea).ToList();
        if (windows.Count == screens.Count && windows.Zip(screens).All(pair => pair.First.Area == pair.Second)) return;
        foreach (var window in windows) window.Close();
        windows.Clear();
        windows.AddRange(screens.Select(area => new OverlayWindow(area)));
    }
}

sealed class OverlayWindow : Window
{
    public const double DipWidth = 160, DipHeight = 42;
    public System.Drawing.Rectangle Area { get; }
    public OverlayView View { get; } = new();

    public OverlayWindow(System.Drawing.Rectangle area)
    {
        Area = area;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        ResizeMode = ResizeMode.NoResize;
        Width = DipWidth;
        Height = DipHeight;
        Left = -10_000;   // η πραγματική θέση μπαίνει στο Place(), σε pixels της οθόνης
        Top = -10_000;
        Content = View;
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var style = (long)GetWindowLongPtr(handle, GWL_EXSTYLE);
            SetWindowLongPtr(handle, GWL_EXSTYLE, (IntPtr)(style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_TOPMOST));
        };
        // Όταν το παράθυρο περνά σε οθόνη με άλλη κλίμακα, τα Windows του αλλάζουν μέγεθος· το ξαναβάζουμε στη θέση του.
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(Place, DispatcherPriority.Background);
    }

    /// Στο κάτω μέρος της οθόνης, στη μέση, 18 pixel πάνω από τη γραμμή εργασιών.
    public void Place()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var scale = ScaleAt(Area.Left + Area.Width / 2, Area.Top + Area.Height / 2);
        var width = (int)Math.Round(DipWidth * scale);
        var height = (int)Math.Round(DipHeight * scale);
        var x = Area.Left + (Area.Width - width) / 2;
        var y = Area.Bottom - height - (int)Math.Round(18 * scale);
        SetWindowPos(handle, HWND_TOPMOST, x, y, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }
}

sealed class OverlayView : FrameworkElement
{
    private static readonly Brush Pill = Frozen(new SolidColorBrush(Color.FromArgb(245, 18, 23, 41)));
    private static readonly Color Sky = Color.FromRgb(87, 181, 232);
    private static readonly Brush Red = Frozen(new SolidColorBrush(Color.FromRgb(237, 69, 56)));
    private static readonly Brush Amber = Frozen(new SolidColorBrush(Color.FromRgb(230, 158, 0)));
    private static readonly Typeface Text = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface Icons = new(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    public OverlayState State { get; set; } = new OverlayState.Recording(false);
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public bool NoAudio { get; set; }

    private readonly double[] levels = new double[12];
    private double latest, phase;

    private static Brush Frozen(Brush brush) { brush.Freeze(); return brush; }

    public void Push(float level) => latest = Math.Max(latest, Math.Min(1, level * 9));

    public void Tick()
    {
        Array.Copy(levels, 1, levels, 0, levels.Length - 1);
        levels[^1] = latest;
        latest *= 0.6;
        phase += 0.15;
        InvalidateVisual();
    }

    public void Reset()
    {
        Array.Clear(levels);
        latest = 0;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth > 0 ? ActualWidth : OverlayWindow.DipWidth, height = ActualHeight > 0 ? ActualHeight : OverlayWindow.DipHeight;
        var bounds = new Rect(2, 2, width - 4, height - 4);
        var midY = height / 2;

        Pen border;
        if (State is OverlayState.Recording)
        {
            // Γαλάζιο περίγραμμα που «αναπνέει»: φαίνεται από μακριά ότι μπορείς να μιλήσεις τώρα.
            var glow = 0.55 + 0.45 * (Math.Sin(phase * 0.9) + 1) / 2;
            border = new Pen(new SolidColorBrush(Color.FromArgb((byte)(glow * 255), Sky.R, Sky.G, Sky.B)), 2.5);
        }
        else border = new Pen(new SolidColorBrush(Color.FromArgb(41, 255, 255, 255)), 1);
        dc.DrawRoundedRectangle(Pill, border, bounds, 19, 19);

        switch (State)
        {
            case OverlayState.Recording recording:
            {
                // Κόκκινη κουκκίδα που πάλλεται.
                var pulse = 0.7 + 0.3 * (Math.Sin(phase * 0.9) + 1) / 2;
                var dot = 9 + 2 * pulse;
                dc.DrawEllipse(Red, null, new Point(19, midY), dot / 2, dot / 2);

                if (NoAudio)
                {
                    DrawText(dc, "Δεν ακούω ήχο", 32, midY, Amber, 11.5);
                }
                else
                {
                    // Οι μπάρες κινούνται πάντα: όταν σωπαίνεις κάνουν ένα ήρεμο κύμα, όταν μιλάς ακολουθούν τη φωνή σου.
                    const double barWidth = 4, gap = 3;
                    var barsWidth = levels.Length * (barWidth + gap) - gap;
                    var areaEnd = width - (recording.HandsFree ? 30 : 16);
                    var x = 32 + (areaEnd - 32 - barsWidth) / 2;
                    for (var i = 0; i < levels.Length; i++)
                    {
                        var idle = 5 + 9 * (Math.Sin(phase * 1.2 - i * 0.6) + 1) / 2;
                        var barHeight = Math.Max(idle, levels[i] * (height - 14));
                        dc.DrawRoundedRectangle(Brushes.White, null, new Rect(x, midY - barHeight / 2, barWidth, barHeight), 2, 2);
                        x += barWidth + gap;
                    }
                    if (recording.HandsFree) DrawText(dc, "", width - 26, midY, Brushes.White, 11, Icons);   // λουκέτο: χωρίς χέρια
                }
                break;
            }
            case OverlayState.Transcribing:
                for (var i = 0; i < 3; i++)
                {
                    var offset = (Math.Sin(phase - i * 0.8) + 1) / 2;
                    var size = 6 + offset * 3;
                    var brush = new SolidColorBrush(Color.FromArgb((byte)((0.5 + offset * 0.5) * 255), Sky.R, Sky.G, Sky.B));
                    dc.DrawEllipse(brush, null, new Point(width / 2 - 18 + i * 18, midY), size / 2, size / 2);
                }
                break;
            case OverlayState.Notice notice:
            {
                var text = Format(notice.Text, notice.Warning ? Amber : Brushes.White, 12, Text);
                dc.DrawText(text, new Point((width - text.Width) / 2, midY - text.Height / 2));
                break;
            }
        }
    }

    private FormattedText Format(string text, Brush brush, double size, Typeface typeface) =>
        new(text, CultureInfo.GetCultureInfo("el-GR"), FlowDirection.LeftToRight, typeface, size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private void DrawText(DrawingContext dc, string text, double x, double midY, Brush brush, double size, Typeface? typeface = null)
    {
        var formatted = Format(text, brush, size, typeface ?? Text);
        dc.DrawText(formatted, new Point(x, midY - formatted.Height / 2));
    }
}
