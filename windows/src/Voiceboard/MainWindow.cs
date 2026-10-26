using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Voiceboard.Core;
using static Voiceboard.Ui;

namespace Voiceboard;

enum Page { Home, Dictionary, Snippets, Settings }

/// Το κεντρικό παράθυρο: Αρχική (στατιστικά, habit tracker, ιστορικό), Λεξιλόγιο, Έτοιμα κείμενα, Ρυθμίσεις.
/// Όταν το κλείνεις, η εφαρμογή συνεχίζει να τρέχει στη γραμμή εργασιών.
sealed class MainWindow : Window
{
    private readonly AppModel model;
    private readonly StackPanel navigation = new();
    private readonly ContentControl statusHost = new();
    private readonly ScrollViewer scroller = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Theme.Background };
    private Page page;

    // Αρχική: ό,τι ξαναχτίζεται χωρίς να χαθεί αυτό που γράφεις στην αναζήτηση
    private readonly ContentControl bannerHost = new();
    private readonly ContentControl trackerHost = new();
    private readonly ContentControl historyHost = new();
    private readonly TextBox search = Ui.TextBox("Αναζήτηση", 240);

    // Έτοιμα κείμενα: η φόρμα κρατά ό,τι έγραψες ανάμεσα στις αλλαγές
    private readonly TextBox snippetTrigger = Ui.TextBox("π.χ. ξέρω γεια");
    private readonly TextBox snippetText = Ui.TextBox("Το κείμενο που θα μπαίνει, π.χ. ένα email ή έναν σύνδεσμο", multiline: true);
    private string? editingSnippet;
    private string? savedSnippet;

    // Ρυθμίσεις: η πρόοδος του κατεβάσματος αλλάζει χωρίς να ξαναχτιστεί η σελίδα
    private ProgressBar? downloadBar;
    private TextBlock? downloadLabel;

    public MainWindow(AppModel model)
    {
        this.model = model;
        Title = "Voiceboard";
        Icon = IconArt.WindowImage();
        Width = 1080;
        Height = 760;
        MinWidth = 960;
        MinHeight = 660;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Theme.Background;
        FontFamily = Theme.Font;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);

        var sidebar = new DockPanel { Width = 224, Background = Theme.Sidebar };
        var header = Row(10, new Image { Source = IconArt.WindowImage(60), Width = 30, Height = 30 }, Text("Voiceboard", 16, bold: true, color: Theme.SidebarText));
        header.Margin = new Thickness(24, 30, 12, 22);
        DockPanel.SetDock(header, Dock.Top);
        sidebar.Children.Add(header);
        statusHost.Margin = new Thickness(12, 0, 12, 16);
        DockPanel.SetDock(statusHost, Dock.Bottom);
        sidebar.Children.Add(statusHost);
        navigation.Margin = new Thickness(12, 0, 12, 0);
        sidebar.Children.Add(navigation);

        var root = new DockPanel();
        DockPanel.SetDock(sidebar, Dock.Left);
        root.Children.Add(sidebar);
        root.Children.Add(scroller);
        Content = root;

        search.TextChanged += (_, _) => RenderHistory();
        snippetText.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; SaveSnippet(); }
        };
        snippetTrigger.KeyDown += (_, e) => { if (e.Key == Key.Enter) snippetText.Focus(); };

        model.Changed += what =>
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => OnModelChanged(what)); return; }
            OnModelChanged(what);
        };
        if (Enum.TryParse<Page>(Environment.GetEnvironmentVariable("VOICEBOARD_PAGE"), true, out var initial)) page = initial;
        RenderNavigation();
        RenderStatus();
        RenderPage();
    }

    public void ShowAndActivate()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;   // τα Windows δεν φέρνουν πάντα μπροστά παράθυρο άλλης εφαρμογής· ένα σύντομο Topmost το κάνει
        Topmost = false;
        Focus();
    }

    /// Το κλείσιμο κρύβει το παράθυρο· η εφαρμογή συνεχίζει στη γραμμή εργασιών.
    public bool AllowClose { get; set; }

    /// Όλη η σελίδα, και όσα δεν χωρούν στην οθόνη (για τις εικόνες του αυτόματου ελέγχου).
    public FrameworkElement? PageContent => scroller.Content as FrameworkElement;

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (AllowClose) return;
        e.Cancel = true;
        Hide();
    }

    private void OnModelChanged(string what)
    {
        switch (what)
        {
            case "status":
                RenderStatus();
                if (page == Page.Home) RenderBanner();
                if (page == Page.Settings) RenderPage(keepScroll: true);
                break;
            case "history":
                if (page == Page.Home) { RenderTracker(); RenderHistory(); }
                break;
            case "vocabulary":
                if (page is Page.Dictionary or Page.Snippets) RenderPage(keepScroll: true);
                break;
            case "download":
                if (model.DownloadProgress is { } progress && downloadBar != null && downloadLabel != null)
                {
                    downloadBar.Value = progress * 100;
                    downloadLabel.Text = DownloadText();
                }
                else if (page == Page.Settings) RenderPage(keepScroll: true);
                if (page == Page.Home) RenderBanner();
                break;
            case "shortcuts":
                if (page is Page.Home or Page.Settings) RenderPage(keepScroll: true);
                break;
            case "model":
            case "whisper":
            case "devices":
            case "settings":
                RenderStatus();
                if (page == Page.Settings) RenderPage(keepScroll: true);
                if (page == Page.Home) RenderBanner();
                break;
        }
    }

    // MARK: Πλαϊνό

    private static readonly (Page Page, string Title, string Glyph)[] Pages =
    [
        (Page.Home, "Αρχική", ""),
        (Page.Dictionary, "Λεξιλόγιο", ""),
        (Page.Snippets, "Έτοιμα κείμενα", ""),
        (Page.Settings, "Ρυθμίσεις", ""),
    ];

    private void RenderNavigation()
    {
        navigation.Children.Clear();
        foreach (var (item, title, glyph) in Pages)
        {
            var selected = item == page;
            var color = selected ? Theme.SidebarText : Theme.SidebarMuted;
            var label = new Border
            {
                Child = Row(10, Icon(glyph, 15, color), Text(title, 13, bold: selected, color: color, wrap: false)),
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = 176,
            };
            var button = Ui.Button(label, () =>
            {
                if (capturing != null) { model.CancelCapture(); capturing = null; }
                page = item; RenderNavigation(); RenderPage();
            }, plain: true);
            button.Background = selected ? Theme.SidebarSelection : Brushes.Transparent;
            button.Padding = new Thickness(12, 9, 12, 9);
            button.Margin = new Thickness(0, 0, 0, 4);
            navigation.Children.Add(button);
        }
    }

    private void RenderStatus()
    {
        var ready = model.ShortcutsActive && model.MicrophoneGranted && (model.SpeechModel == SpeechModel.Scribe || model.WhisperModelExists);
        var (glyph, title, color) = model.Phase switch
        {
            Phase.Recording => ("", "Ηχογραφεί…", Theme.Amber),
            Phase.Transcribing => ("", "Γράφει…", Theme.Sky),
            _ when ready => ("", "Έτοιμο", Theme.Sky),
            _ when model.DownloadProgress != null => ("", "Κατεβαίνει το Whisper", Theme.Amber),
            _ => ("", "Χρειάζεται ρύθμιση", Theme.Amber),
        };
        var modelName = model.SpeechModel == SpeechModel.Scribe ? "Scribe v2 · ElevenLabs" : "Whisper · τοπικά";
        statusHost.Content = new Border
        {
            Background = Theme.WithOpacity(Theme.SidebarText, 0.07),
            BorderBrush = Theme.WithOpacity(Theme.SidebarText, 0.12),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12),
            Child = Stack(6, Row(8, Icon(glyph, 13, color), Text(title, 13, bold: true, color: color)), Text(modelName, 12, color: Theme.SidebarMuted)),
        };
    }

    // MARK: Σελίδες

    private void RenderPage(bool keepScroll = false)
    {
        var offset = scroller.VerticalOffset;
        downloadBar = null;
        downloadLabel = null;
        var content = page switch
        {
            Page.Home => HomePage(),
            Page.Dictionary => DictionaryPage(),
            Page.Snippets => SnippetsPage(),
            _ => SettingsPage(),
        };
        content.Margin = new Thickness(36, 44, 36, 36);
        content.MaxWidth = 860;
        content.HorizontalAlignment = HorizontalAlignment.Stretch;
        scroller.Content = new Grid { Children = { content } };
        // Κάθε σελίδα ανοίγει από την κορυφή, όχι στο σημείο που είχες κυλήσει την προηγούμενη.
        if (keepScroll) scroller.Dispatcher.BeginInvoke(() => scroller.ScrollToVerticalOffset(offset), System.Windows.Threading.DispatcherPriority.Loaded);
        else scroller.ScrollToTop();
    }

    // MARK: Αρχική

    private FrameworkElement HomePage()
    {
        Detach(bannerHost, trackerHost, historyHost, search);
        RenderBanner();
        RenderTracker();
        RenderHistory();

        var hold = new StackPanel { Orientation = Orientation.Horizontal };
        void Add(StackPanel row, UIElement element) { if (element is FrameworkElement fe) fe.Margin = new Thickness(row.Children.Count == 0 ? 0 : 8, 0, 0, 0); row.Children.Add(element); }
        Add(hold, Text("Κράτα", wrap: false));
        foreach (var (key, index) in model.PushToTalk.Keys.Select((k, i) => (k, i)))
        {
            if (index > 0) Add(hold, Text("+", wrap: false));
            Add(hold, KeyCap(key));
        }
        Add(hold, Text("και μίλα.", wrap: false));
        var others = new StackPanel { Orientation = Orientation.Horizontal };
        if (!model.HandsFree.IsEmpty)
        {
            Add(others, KeyCap(model.HandsFree.Label));
            Add(others, Text("χωρίς χέρια", color: Theme.Secondary, wrap: false));
        }
        Add(others, KeyCap("Esc"));
        Add(others, Text("ακύρωση", color: Theme.Secondary, wrap: false));
        var shortcuts = Card(Spread(hold, others));

        return Stack(24,
            SectionTitle("Καλώς ήρθες"),
            bannerHost,
            trackerHost,
            shortcuts,
            Stack(10, Spread(FieldLabel("Ιστορικό"), search), historyHost));
    }

    /// Ένα στοιχείο που ξαναμπαίνει σε νέα σελίδα πρέπει πρώτα να βγει από την παλιά.
    private static void Detach(params FrameworkElement[] elements)
    {
        foreach (var element in elements)
        {
            switch (element.Parent)
            {
                case Panel panel: panel.Children.Remove(element); break;
                case ContentControl control: control.Content = null; break;
                case Decorator decorator: decorator.Child = null; break;
            }
        }
    }

    private void RenderBanner()
    {
        var problems = new List<UIElement>();
        if (!model.ShortcutsActive)
            problems.Add(Text("Τα Windows δεν επέτρεψαν στην εφαρμογή να ακούει τις συντομεύσεις. Κλείσε και άνοιξε ξανά το Voiceboard. Αν συνεχίζει, κάποιο πρόγραμμα προστασίας (antivirus) μπορεί να το μπλοκάρει.", 13));
        if (!model.MicrophoneGranted)
            problems.Add(Stack(8, Text("Τα Windows δεν επιτρέπουν στο Voiceboard να χρησιμοποιεί το μικρόφωνο. Άνοιξε τις ρυθμίσεις απορρήτου του μικροφώνου και ενεργοποίησε την πρόσβαση, και για τις εφαρμογές υπολογιστή.", 13),
                Ui.Button("Άνοιγμα ρυθμίσεων μικροφώνου", model.OpenMicrophoneSettings, primary: true)));
        if (model.SpeechModel == SpeechModel.Whisper && !model.WhisperModelExists)
        {
            if (model.DownloadProgress is { } progress)
                problems.Add(Text($"Κατεβαίνει το μοντέλο Whisper ({progress:P0}). Η δωρεάν υπαγόρευση ξεκινά μόλις τελειώσει. Μπορείς να κλείσεις το παράθυρο.", 13));
            else
                problems.Add(Stack(8, Text("Λείπει το μοντέλο Whisper (3,1 GB), που κάνει τη δωρεάν υπαγόρευση στον υπολογιστή σου.", 13),
                    Ui.Button("Κατέβασμα", model.DownloadWhisperModel, primary: true)));
        }
        if (model.LastError.Length > 0) problems.Add(Text(model.LastError, 13, bold: true));

        if (problems.Count == 0) { bannerHost.Content = null; return; }
        var ready = model.ShortcutsActive && model.MicrophoneGranted && (model.SpeechModel == SpeechModel.Scribe || model.WhisperModelExists);
        var heading = Row(8, Icon("", 15, Theme.Orange),
            Text(ready ? "Κάτι δεν πήγε καλά" : "Η υπαγόρευση δεν λειτουργεί ακόμα", 15, bold: true, color: Theme.Orange));
        bannerHost.Content = new Border
        {
            Background = Theme.WithOpacity(Theme.Orange, 0.07),
            BorderBrush = Theme.WithOpacity(Theme.Orange, 0.4),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(18),
            Child = Stack(12, [heading, .. problems]),
        };
    }

    private void RenderTracker()
    {
        var stats = model.Stats;
        var goal = Math.Max(1, model.DailyGoal);

        Border Tile(UIElement content) => new()
        {
            Background = Theme.Card, BorderBrush = Theme.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16), MinHeight = 92, Child = content,
        };

        var today = Tile(Row(14,
            new GoalRing(stats.WordsToday / (double)goal) { Width = 58, Height = 58 },
            Stack(3,
                FieldLabel("Σήμερα"),
                Number(22, $"{Ui.Number(stats.WordsToday)} / {Ui.Number(goal)}"),
                Text(stats.WordsToday >= goal ? "Πέτυχες τον στόχο ✓" : $"λέξεις · {stats.DictationsToday} υπαγορεύσεις", 12, color: Theme.Secondary))));
        var streak = Tile(Stack(3,
            FieldLabel("Σερί"),
            Row(6, Icon("", 18, Theme.Amber), Number(28, stats.StreakDays.ToString()), Text(stats.StreakDays == 1 ? "μέρα" : "μέρες", 13, color: Theme.Secondary)),
            Text($"Ρεκόρ: {stats.LongestStreak} {(stats.LongestStreak == 1 ? "μέρα" : "μέρες")}", 12, color: Theme.Secondary)));
        var saved = Tile(Stack(3,
            FieldLabel("Χρόνος που κέρδισες"),
            Number(24, Ui.Duration(stats.MinutesSaved)),
            Text($"{Ui.Number(stats.TotalWords)} λέξεις · {stats.WordsPerMinute} λέξεις/λεπτό", 12, color: Theme.Secondary)));

        var tiles = new Grid();
        for (var i = 0; i < 3; i++) tiles.ColumnDefinitions.Add(new ColumnDefinition());
        foreach (var (tile, column) in new[] { (today, 0), (streak, 1), (saved, 2) })
        {
            tile.Margin = new Thickness(column == 0 ? 0 : 8, 0, column == 2 ? 0 : 8, 0);
            Grid.SetColumn(tile, column);
            tiles.Children.Add(tile);
        }

        var calendar = Card(Stack(14,
            Spread(Text("Η εβδομάδα σου", 15, bold: true), WeekStrip(stats.WordsByDay, goal)),
            Divider(),
            Spread(Text("Τελευταίοι 6 μήνες", 13, bold: true, color: Theme.Secondary), HeatmapLegend()),
            Heatmap(stats.WordsByDay)));

        trackerHost.Content = Stack(16, tiles, calendar);
    }

    private static TextBlock Number(double size, string text)
    {
        var block = Text(text, size, bold: true, wrap: false);
        block.FontFamily = Theme.Display;
        block.Typography.NumeralAlignment = FontNumeralAlignment.Tabular;
        return block;
    }

    private static DateTime Monday(DateTime day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    /// Δευτέρα έως Κυριακή: γεμάτος κύκλος με ✓ = πέτυχες τον στόχο, μισογεμάτος = υπαγόρευσες,
    /// άδειος = καμία υπαγόρευση. Το σχήμα και το σύμβολο λένε το ίδιο με το χρώμα.
    private static StackPanel WeekStrip(IReadOnlyDictionary<DateTime, int> wordsByDay, int goal)
    {
        string[] names = ["Δε", "Τρ", "Τε", "Πε", "Πα", "Σα", "Κυ"];
        var today = DateTime.Today;
        var monday = Monday(today);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        for (var i = 0; i < 7; i++)
        {
            var day = monday.AddDays(i);
            var words = wordsByDay.GetValueOrDefault(day);
            var circle = new Grid { Width = 24, Height = 24 };
            circle.Children.Add(new Ellipse { Stroke = day == today ? Theme.Blue : Theme.Border, StrokeThickness = day == today ? 2 : 1 });
            if (words >= goal)
            {
                circle.Children.Add(new Ellipse { Fill = Theme.Blue, Margin = new Thickness(2) });
                var check = Icon("", 10, Brushes.White);
                check.HorizontalAlignment = HorizontalAlignment.Center;
                circle.Children.Add(check);
            }
            else if (words > 0) circle.Children.Add(new Ellipse { Fill = Theme.WithOpacity(Theme.Sky, 0.45), Margin = new Thickness(2) });
            circle.ToolTip = $"{names[i]}: {words} λέξεις";
            var label = Text(names[i], 11, bold: day == today, color: Theme.Secondary, wrap: false);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            var column = Stack(4, circle, label);
            column.Margin = new Thickness(i == 0 ? 0 : 10, 0, 0, 0);
            row.Children.Add(column);
        }
        return row;
    }

    private static int HeatLevel(int words) => words switch { 0 => 0, < 200 => 1, < 600 => 2, < 1500 => 3, _ => 4 };

    private static Brush HeatColor(int level) => level == 0 ? Theme.WithOpacity(Theme.Blue, 0.06) : Theme.WithOpacity(Theme.Blue, new[] { 0, 0.25, 0.5, 0.75, 1.0 }[level]);

    private static StackPanel Heatmap(IReadOnlyDictionary<DateTime, int> wordsByDay)
    {
        const int weeks = 26;
        const double cell = 18, gap = 4;
        var today = DateTime.Today;
        var firstMonday = Monday(today).AddDays(-7 * (weeks - 1));
        var grid = new StackPanel { Orientation = Orientation.Horizontal };
        var labels = new StackPanel { Margin = new Thickness(0, 0, gap, 0) };
        foreach (var name in new[] { "Δε", "", "Τε", "", "Πα", "", "Κυ" })
        {
            var label = Text(name, 9, color: Theme.Secondary, wrap: false);
            label.Height = cell; label.Width = 22; label.TextAlignment = TextAlignment.Right; label.Margin = new Thickness(0, 0, 0, gap);
            labels.Children.Add(label);
        }
        grid.Children.Add(labels);
        for (var week = 0; week < weeks; week++)
        {
            var column = new StackPanel { Margin = new Thickness(0, 0, gap, 0) };
            for (var row = 0; row < 7; row++)
            {
                var day = firstMonday.AddDays(week * 7 + row);
                var words = wordsByDay.GetValueOrDefault(day);
                column.Children.Add(new Rectangle
                {
                    Width = cell, Height = cell, RadiusX = 3, RadiusY = 3, Margin = new Thickness(0, 0, 0, gap),
                    Fill = day > today ? Brushes.Transparent : HeatColor(HeatLevel(words)),
                    Stroke = day == today ? Theme.Text : null,
                    StrokeThickness = 1.5,
                    ToolTip = $"{day.ToString("dddd d MMMM", Greek)}: {Ui.Number(words)} λέξεις",
                });
            }
            grid.Children.Add(column);
        }
        return grid;
    }

    private static StackPanel HeatmapLegend()
    {
        var row = Row(4, Text("λιγότερο", 11, color: Theme.Secondary, wrap: false));
        for (var level = 0; level < 5; level++)
            row.Children.Add(new Rectangle { Width = 12, Height = 12, RadiusX = 3, RadiusY = 3, Fill = HeatColor(level), Margin = new Thickness(4, 0, 0, 0) });
        var more = Text("περισσότερο", 11, color: Theme.Secondary, wrap: false);
        more.Margin = new Thickness(4, 0, 0, 0);
        row.Children.Add(more);
        return row;
    }

    private void RenderHistory()
    {
        var query = search.Text.Trim();
        var entries = query.Length == 0 ? model.History : model.History.Where(e => e.Text.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();
        if (entries.Count == 0)
        {
            historyHost.Content = Card(Text(query.Length == 0 ? "Ό,τι υπαγορεύεις θα εμφανίζεται εδώ." : "Δεν βρέθηκε κάτι.", color: Theme.Secondary));
            return;
        }
        var list = new StackPanel();
        var groups = entries.Take(300).GroupBy(e => e.Date.ToLocalTime().Date).OrderByDescending(g => g.Key);
        foreach (var group in groups)
        {
            var rows = new StackPanel();
            var items = group.OrderByDescending(e => e.Date).ToList();
            for (var i = 0; i < items.Count; i++)
            {
                rows.Children.Add(HistoryRow(items[i]));
                if (i < items.Count - 1) rows.Children.Add(new Border { Height = 1, Background = Theme.Border });
            }
            var section = Stack(8, FieldLabel(DayLabel(group.Key)), new Border
            {
                Background = Theme.Card, BorderBrush = Theme.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Child = rows,
            });
            section.Margin = new Thickness(0, list.Children.Count == 0 ? 0 : 18, 0, 0);
            list.Children.Add(section);
        }
        historyHost.Content = list;
    }

    private static string DayLabel(DateTime day)
    {
        if (day == DateTime.Today) return "Σήμερα";
        if (day == DateTime.Today.AddDays(-1)) return "Χθες";
        return day.ToString("dddd d MMMM", Greek);
    }

    private UIElement HistoryRow(HistoryEntry entry)
    {
        var time = Text(entry.Date.ToLocalTime().ToString("HH:mm", Greek), 12, color: Theme.Secondary, wrap: false);
        time.Width = 56;
        time.VerticalAlignment = VerticalAlignment.Top;
        // Κείμενο που επιλέγεται και αντιγράφεται, χωρίς να μοιάζει με πεδίο.
        var text = new TextBox
        {
            Text = entry.Text, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0),
            TextWrapping = TextWrapping.Wrap, FontSize = 13, FontFamily = Theme.Font, Foreground = Theme.Text,
        };
        TextBlock copyLabel = Text("Αντιγραφή", 12, color: Theme.Blue, wrap: false);
        var copy = Ui.Button(Row(6, Icon("", 12, Theme.Blue), copyLabel), () => { }, plain: true);
        copy.Click += async (_, _) =>
        {
            model.Copy(entry.Text);
            copyLabel.Text = "Αντιγράφηκε";
            await Task.Delay(1200);
            copyLabel.Text = "Αντιγραφή";
        };
        copy.Opacity = 0;
        copy.VerticalAlignment = VerticalAlignment.Top;

        var row = new DockPanel { Margin = new Thickness(16, 12, 16, 12), Background = Brushes.Transparent };
        DockPanel.SetDock(time, Dock.Left);
        DockPanel.SetDock(copy, Dock.Right);
        row.Children.Add(time);
        row.Children.Add(copy);
        row.Children.Add(text);
        row.MouseEnter += (_, _) => copy.Opacity = 1;
        row.MouseLeave += (_, _) => { if (copyLabel.Text == "Αντιγραφή") copy.Opacity = 0; };
        return row;
    }

    // MARK: Λεξιλόγιο

    private FrameworkElement DictionaryPage()
    {
        var vocabulary = model.Vocabulary;

        var newWord = Ui.TextBox("Νέα λέξη, π.χ. Higgsfield (Enter για προσθήκη)");
        void AddWord()
        {
            var word = newWord.Text.Trim();
            if (word.Length == 0) return;
            if (!vocabulary.Words.Any(w => string.Equals(w, word, StringComparison.CurrentCultureIgnoreCase)))
                model.EditVocabulary(v => v.Words.Insert(0, word));
            FocusLater("newWord");
        }
        newWord.Name = "newWord";
        newWord.KeyDown += (_, e) => { if (e.Key == Key.Enter) AddWord(); };
        var addWord = Ui.Button("Προσθήκη", AddWord, primary: true);
        addWord.Margin = new Thickness(8, 0, 0, 0);

        var chips = new WrapPanelWithGap();
        foreach (var word in vocabulary.Words)
        {
            var remove = Ui.Button(Icon("", 9, Theme.Secondary), () => model.EditVocabulary(v => v.Words.Remove(word)), plain: true, tooltip: "Αφαίρεση");
            remove.Padding = new Thickness(2);
            chips.Children.Add(new Border
            {
                Background = Theme.Chip, BorderBrush = Theme.WithOpacity(Theme.Blue, 0.22), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14), Padding = new Thickness(10, 4, 6, 5), Child = Row(6, Text(word, 13, wrap: false), remove),
            });
        }

        var from = Ui.TextBox("Γράφει λάθος, π.χ. key.ai");
        var to = Ui.TextBox("Σωστό, π.χ. kie.ai");
        void AddReplacement()
        {
            string f = from.Text.Trim(), t = to.Text.Trim();
            if (f.Length == 0 || t.Length == 0) return;
            model.EditVocabulary(v =>
            {
                v.Replacements.RemoveAll(r => string.Equals(r.From, f, StringComparison.CurrentCultureIgnoreCase));
                v.Replacements.Insert(0, new Vocabulary.Replacement(f, t));
            });
            FocusLater("from");
        }
        from.Name = "from";
        from.KeyDown += (_, e) => { if (e.Key == Key.Enter) to.Focus(); };
        to.KeyDown += (_, e) => { if (e.Key == Key.Enter) AddReplacement(); };
        var replacementForm = new Grid();
        replacementForm.ColumnDefinitions.Add(new ColumnDefinition());
        replacementForm.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        replacementForm.ColumnDefinitions.Add(new ColumnDefinition());
        replacementForm.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var arrow = Icon("", 13, Theme.Secondary);
        arrow.Margin = new Thickness(10, 0, 10, 0);
        var addReplacement = Ui.Button("Προσθήκη", AddReplacement, primary: true);
        addReplacement.Margin = new Thickness(8, 0, 0, 0);
        foreach (var (element, column) in new (UIElement, int)[] { (from, 0), (arrow, 1), (to, 2), (addReplacement, 3) })
        {
            Grid.SetColumn(element, column);
            replacementForm.Children.Add(element);
        }

        var rules = new StackPanel();
        for (var i = 0; i < vocabulary.Replacements.Count; i++)
        {
            var rule = vocabulary.Replacements[i];
            var left = Text(rule.From, 13, wrap: false);
            left.Width = 220;
            var trash = Ui.Button(Icon("", 13, Theme.Secondary), () => model.EditVocabulary(v => v.Replacements.Remove(rule)), plain: true, tooltip: "Αφαίρεση");
            var line = Spread(Row(10, left, Icon("", 12, Theme.Secondary), Text(rule.To, 13, bold: true, wrap: false)), trash);
            line.Margin = new Thickness(0, 6, 0, 6);
            rules.Children.Add(line);
            if (i < vocabulary.Replacements.Count - 1) rules.Children.Add(new Border { Height = 1, Background = Theme.Border });
        }

        var wordsForm = new DockPanel();
        DockPanel.SetDock(addWord, Dock.Right);
        wordsForm.Children.Add(addWord);
        wordsForm.Children.Add(newWord);

        return Stack(24,
            Spread(SectionTitle("Λεξιλόγιο", "Ονόματα και όροι που λες συχνά. Τα στέλνουμε στο μοντέλο, ώστε να τα γράφει σωστά."),
                   Ui.Button("Εισαγωγή από Wispr Flow", model.ImportFromWispr)),
            Card(Stack(14, Text($"Λέξεις ({vocabulary.Words.Count})", 15, bold: true), wordsForm, chips)),
            Card(Stack(14,
                Text($"Διορθώσεις ({vocabulary.Replacements.Count})", 15, bold: true),
                Text("Όταν το μοντέλο γράψει την αριστερή λέξη, τη διορθώνουμε στη δεξιά.", 13, color: Theme.Secondary),
                replacementForm,
                rules)));
    }

    /// Μετά από προσθήκη η σελίδα ξαναχτίζεται· βάζουμε τον κέρσορα ξανά στο πεδίο για την επόμενη.
    private void FocusLater(string name) => Dispatcher.BeginInvoke(() =>
    {
        if (scroller.Content is FrameworkElement root && LogicalTreeHelper.FindLogicalNode(root, name) is TextBox box) box.Focus();
    }, System.Windows.Threading.DispatcherPriority.Input);

    // MARK: Έτοιμα κείμενα

    private FrameworkElement SnippetsPage()
    {
        Detach(snippetTrigger, snippetText);
        var save = Ui.Button(editingSnippet == null ? "Αποθήκευση" : "Αποθήκευση αλλαγών", SaveSnippet, primary: true);
        var actions = Row(10, save);
        if (editingSnippet != null) actions.Children.Add(Ui.Button("Άκυρο", () => { editingSnippet = null; snippetTrigger.Text = ""; snippetText.Text = ""; RenderPage(true); }));
        actions.Children.Add(Text("Ctrl + Enter για αποθήκευση", 12, color: Theme.Secondary, wrap: false));
        if (savedSnippet != null) actions.Children.Add(Row(6, Icon("", 12, Theme.Blue), Text($"Αποθηκεύτηκε: «{savedSnippet}»", 12, bold: true, color: Theme.Blue, wrap: false)));
        foreach (FrameworkElement child in actions.Children) child.Margin = new Thickness(child == save ? 0 : 10, 0, 0, 0);

        var form = Card(Stack(14,
            Text(editingSnippet == null ? "Νέο έτοιμο κείμενο" : "Διόρθωση έτοιμου κειμένου", 15, bold: true),
            Stack(6, FieldLabel("Όταν λέω"), snippetTrigger),
            Stack(6, FieldLabel("Να γράφεται"), snippetText),
            actions));

        var list = new StackPanel();
        foreach (var snippet in model.Vocabulary.Snippets)
        {
            var edit = Ui.Button(Icon("", 13, Theme.Secondary), () =>
            {
                editingSnippet = snippet.From; savedSnippet = null;
                snippetTrigger.Text = snippet.From; snippetText.Text = snippet.To;
                RenderPage();
                Dispatcher.BeginInvoke(() => snippetText.Focus(), System.Windows.Threading.DispatcherPriority.Input);
            }, plain: true, tooltip: "Διόρθωση");
            var delete = Ui.Button(Icon("", 13, Theme.Secondary), () => model.EditVocabulary(v => v.Snippets.Remove(snippet)), plain: true, tooltip: "Αφαίρεση");
            var body = new TextBox
            {
                Text = snippet.To, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0),
                TextWrapping = TextWrapping.Wrap, FontSize = 13, FontFamily = Theme.Font, Foreground = Theme.Secondary,
            };
            var card = Card(Spread(Stack(6, Row(6, Icon("", 11, Theme.Blue), Text($"«{snippet.From}»", 13, bold: true)), body), Row(4, edit, delete)));
            card.Margin = new Thickness(0, list.Children.Count == 0 ? 0 : 12, 0, 0);
            list.Children.Add(card);
        }

        return Stack(24,
            SectionTitle("Έτοιμα κείμενα", "Λες μια σύντομη φράση και γράφεται ολόκληρο το κείμενο. Για email, συνδέσμους και απαντήσεις που γράφεις συχνά."),
            form,
            FieldLabel($"Τα έτοιμα κείμενά σου ({model.Vocabulary.Snippets.Count})"),
            list);
    }

    private void SaveSnippet()
    {
        var phrase = snippetTrigger.Text.Trim();
        var body = snippetText.Text.Trim();
        if (phrase.Length == 0 || body.Length == 0) return;
        var previous = editingSnippet;
        editingSnippet = null;
        savedSnippet = phrase;
        snippetTrigger.Text = "";
        snippetText.Text = "";
        model.EditVocabulary(v =>
        {
            v.Snippets.RemoveAll(s => string.Equals(s.From, phrase, StringComparison.CurrentCultureIgnoreCase) || s.From == previous);
            v.Snippets.Insert(0, new Vocabulary.Replacement(phrase, body));
        });
        Dispatcher.BeginInvoke(() => snippetTrigger.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    // MARK: Ρυθμίσεις

    private FrameworkElement SettingsPage()
    {
        var keyBox = new PasswordBox { FontSize = 13, Padding = new Thickness(6, 5, 6, 5), Width = 320, BorderBrush = Theme.WithOpacity(Theme.Text, 0.2) };
        if (model.HasElevenLabsKey) keyBox.Password = Settings.Shared.ElevenLabsApiKey;
        var keyRow = Row(8, keyBox,
            Ui.Button("Αποθήκευση", () => { model.SetElevenLabsKey(keyBox.Password); if (model.HasElevenLabsKey) model.SpeechModel = SpeechModel.Scribe; }, primary: true));
        if (model.HasElevenLabsKey) keyRow.Children.Add(Ui.Button("Αφαίρεση", () => model.SetElevenLabsKey("")));

        var whisperDetail = "Δωρεάν, τρέχει στον υπολογιστή σου χωρίς ίντερνετ. Πιάνει ~3 GB μνήμης όσο είναι επιλεγμένο. Με κάρτα γραφικών γράφει γρήγορα· μόνο με τον επεξεργαστή μπορεί να αργεί μερικά δευτερόλεπτα.";
        var models = Card(Stack(12,
            Text("Μοντέλο ομιλίας", 15, bold: true),
            ModelOption("Scribe v2 της ElevenLabs",
                model.HasElevenLabsKey
                    ? "Το καλύτερο για ελληνικά με αγγλικούς όρους. Χρεώνεται ανά λεπτό ήχου, με κλειδί από την ElevenLabs. Ο ήχος στέλνεται στην ElevenLabs."
                    : "Λείπει το κλειδί της ElevenLabs. Βάλ' το παρακάτω για να το ενεργοποιήσεις.",
                model.SpeechModel == SpeechModel.Scribe, model.HasElevenLabsKey, () => model.SpeechModel = SpeechModel.Scribe),
            ModelOption("Whisper της OpenAI", whisperDetail, model.SpeechModel == SpeechModel.Whisper, true, () => model.SpeechModel = SpeechModel.Whisper),
            Stack(6, FieldLabel("Κλειδί ElevenLabs"), keyRow,
                Text("Το κλειδί μένει μόνο στον υπολογιστή σου, στο αρχείο config.json. Αν το Scribe αποτύχει, η εφαρμογή περνά μόνη της στο Whisper.", 12, color: Theme.Secondary)),
            WhisperModelStatus()));

        var minus = Ui.Button("−", () => { model.DailyGoal = Math.Max(100, model.DailyGoal - 100); RenderPage(true); });
        var plus = Ui.Button("+", () => { model.DailyGoal = Math.Min(20_000, model.DailyGoal + 100); RenderPage(true); });
        var goal = Card(Stack(14,
            Text("Στόχος", 15, bold: true),
            Row(10, Text($"Λέξεις την ημέρα: {Ui.Number(model.DailyGoal)}", wrap: false), minus, plus),
            Text("Μετράει στον δακτύλιο «Σήμερα» και στα ✓ της εβδομάδας στην Αρχική.", 12, color: Theme.Secondary)));

        var microphones = new ComboBox { Width = 360, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Left };
        microphones.Items.Add(new ComboBoxItem { Content = "Προεπιλογή των Windows", Tag = "" });
        foreach (var device in model.InputDevices) microphones.Items.Add(new ComboBoxItem { Content = device.Name, Tag = device.Id });
        microphones.SelectedItem = microphones.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == model.MicrophoneId) ?? microphones.Items[0];
        microphones.SelectionChanged += (_, _) => { if (microphones.SelectedItem is ComboBoxItem item) model.MicrophoneId = (string)item.Tag; };
        var sounds = Check("Ήχος στην αρχή και στο τέλος της ηχογράφησης", model.SoundsEnabled, value => model.SoundsEnabled = value);
        var startup = Check("Άνοιγμα με την εκκίνηση των Windows", model.LaunchAtLogin, value => model.LaunchAtLogin = value);
        var audio = Card(Stack(14, Text("Μικρόφωνο και ήχοι", 15, bold: true), Row(10, Text("Μικρόφωνο", wrap: false), microphones), sounds, startup));

        var shortcuts = Card(Stack(12,
            Text("Συντομεύσεις", 15, bold: true),
            ShortcutRow("Κράτα πατημένα και μίλα", "ptt", model.PushToTalk, Combo.Parse(Shortcuts.DefaultPushToTalk)),
            ShortcutRow("Χωρίς χέρια (ξανά για τέλος)", "hands", model.HandsFree, Combo.Parse(Shortcuts.DefaultHandsFree)),
            Shortcut(["Esc"], "Ακύρωση"),
            shortcutError == null ? new Border() : Row(6, Icon("\uE7BA", 12, Theme.Orange), Text(shortcutError, 12, color: Theme.Orange)),
            Text("Πάτα «Αλλαγή» και μετά τα πλήκτρα που θέλεις (π.χ. Ctrl + Space, δεξί Alt, F8) ή ένα κουμπί του ποντικιού. " +
                 "Τα πλήκτρα της συντόμευσης δεν φτάνουν πια στις άλλες εφαρμογές, ούτε το κουμπί του ποντικιού που διαλέγεις.", 12, color: Theme.Secondary)));

        var permissions = Card(Stack(12,
            Text("Άδειες", 15, bold: true),
            Permission("Συντομεύσεις πληκτρολογίου", model.ShortcutsActive, null),
            Permission("Μικρόφωνο", model.MicrophoneGranted, Ui.Button("Άνοιγμα ρυθμίσεων", model.OpenMicrophoneSettings))));

        var data = Stack(12,
            Text("Δεδομένα", 15, bold: true),
            Text("Το ιστορικό και το λεξιλόγιο μένουν μόνο στον υπολογιστή σου. Ο ήχος δεν αποθηκεύεται.", 13, color: Theme.Secondary),
            Row(8,
                Ui.Button("Αρχείο καταγραφής", model.OpenLog),
                Ui.Button("Φάκελος δεδομένων", () => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Paths.Support) { UseShellExecute = true })),
                Ui.Button("Διαγραφή ιστορικού", () =>
                {
                    if (MessageBox.Show(this, "Να διαγραφεί όλο το ιστορικό;", "Voiceboard", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                        model.ClearHistory();
                })));
        if (model.LastError.Length > 0) data.Children.Add(Row(6, Icon("", 12, Theme.Orange), Text(model.LastError, 12, color: Theme.Orange)));

        return Stack(24, SectionTitle("Ρυθμίσεις"), models, goal, audio, shortcuts, permissions, Card(data));
    }

    private UIElement WhisperModelStatus()
    {
        var label = FieldLabel("Μοντέλο Whisper large-v3");
        if (model.DownloadProgress is { } progress)
        {
            downloadBar = new ProgressBar { Height = 6, Value = progress * 100, Maximum = 100, Foreground = Theme.Blue, Background = Theme.Chip, BorderThickness = new Thickness(0) };
            downloadLabel = Text(DownloadText(), 12, color: Theme.Secondary);
            return Stack(6, label, downloadBar, Spread(downloadLabel, Ui.Button("Διακοπή", model.CancelDownload)));
        }
        if (!model.WhisperModelExists)
            return Stack(6, label, Text("Δεν έχει κατέβει ακόμα (3,1 GB). Χωρίς αυτό η δωρεάν υπαγόρευση δεν λειτουργεί.", 12, color: Theme.Secondary),
                Ui.Button("Κατέβασμα", model.DownloadWhisperModel, primary: true));
        var where = model.WhisperRuntime switch
        {
            null => model.SpeechModel == SpeechModel.Whisper ? "Φορτώνεται…" : "Έτοιμο, φορτώνεται όταν το επιλέξεις.",
            var runtime when runtime.Contains("Vulkan", StringComparison.OrdinalIgnoreCase) => "Έτοιμο, τρέχει στην κάρτα γραφικών (Vulkan).",
            _ => "Έτοιμο, τρέχει στον επεξεργαστή.",
        };
        var ready = model.WhisperRuntime != null || model.SpeechModel != SpeechModel.Whisper;
        return Stack(6, label, Row(6, Icon(ready ? "\uE73E" : "\uE895", 12, Theme.Blue), Text(where, 12, color: Theme.Secondary)));
    }

    private string DownloadText() =>
        $"Κατεβαίνει: {model.DownloadedBytes / 1_048_576.0:N0} MB από ~2.950 MB ({model.DownloadProgress ?? 0:P0}). Αν διακοπεί, συνεχίζει από εκεί που σταμάτησε.";

    private static Border ModelOption(string title, string detail, bool selected, bool enabled, Action select)
    {
        var radio = Icon(selected ? "" : "", 16, selected ? Theme.Blue : Theme.Secondary);
        radio.VerticalAlignment = VerticalAlignment.Top;
        var option = new Border
        {
            Background = selected ? Theme.WithOpacity(Theme.Blue, 0.06) : Brushes.Transparent,
            BorderBrush = selected ? Theme.WithOpacity(Theme.Blue, 0.5) : Theme.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12),
            Opacity = enabled ? 1 : 0.5,
            Cursor = enabled ? Cursors.Hand : Cursors.Arrow,
            Child = Spread(Stack(4, Text(title, 13, bold: true), Text(detail, 12, color: Theme.Secondary)), radio, leftDock: true),
        };
        radio.Margin = new Thickness(0, 0, 12, 0);
        if (enabled) option.MouseLeftButtonUp += (_, _) => select();
        return option;
    }

    private string? capturing;      // "ptt" ή "hands" όσο καταγράφεται νέα συντόμευση
    private string? shortcutError;

    private DockPanel ShortcutRow(string title, string which, Combo combo, Combo fallback)
    {
        var keys = new StackPanel { Orientation = Orientation.Horizontal };
        if (capturing == which)
            keys.Children.Add(Text("Πάτα τα πλήκτρα ή ένα κουμπί του ποντικιού… (Esc για ακύρωση)", 13, bold: true, color: Theme.Blue, wrap: false));
        else
            foreach (var key in combo.Keys)
            {
                if (keys.Children.Count > 0) keys.Children.Add(new TextBlock { Text = "+", Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
                keys.Children.Add(KeyCap(key));
            }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0) };
        void Add(Button button) { button.Margin = new Thickness(buttons.Children.Count == 0 ? 0 : 6, 0, 0, 0); buttons.Children.Add(button); }
        if (capturing == which)
        {
            Add(Ui.Button("Άκυρο", () => { model.CancelCapture(); capturing = null; RenderPage(keepScroll: true); }));
        }
        else
        {
            Add(Ui.Button("Αλλαγή", () => StartCapture(which)));
            if (combo != fallback) Add(Ui.Button("Προεπιλογή", () => SetShortcut(which, fallback)));
            if (which == "hands" && !combo.IsEmpty) Add(Ui.Button("Καμία", () => SetShortcut(which, Combo.None)));
        }
        var row = Labeled(title, keys);
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Insert(1, buttons);
        return row;
    }

    private void StartCapture(string which)
    {
        if (capturing != null) model.CancelCapture();
        capturing = which;
        shortcutError = null;
        RenderPage(keepScroll: true);
        model.CaptureShortcut(combo =>
        {
            if (capturing != which) return;
            capturing = null;
            if (combo != null) SetShortcut(which, combo);
            else RenderPage(keepScroll: true);
        });
    }

    private void SetShortcut(string which, Combo combo)
    {
        var other = which == "ptt" ? model.HandsFree : model.PushToTalk;
        shortcutError = null;
        if (!combo.IsEmpty && !combo.IsSafe)
            shortcutError = $"Το «{combo.Label}» το πατάς συχνά όταν γράφεις. Διάλεξε συνδυασμό με Ctrl, Alt ή Win, ένα πλήκτρο F (F1-F24) ή κουμπί του ποντικιού.";
        else if (!combo.IsEmpty && combo == other)
            shortcutError = $"Το «{combo.Label}» είναι ήδη η άλλη συντόμευση.";
        else if (which == "ptt") model.PushToTalk = combo;
        else model.HandsFree = combo;
        RenderPage(keepScroll: true);
    }

    private static DockPanel Labeled(string label, UIElement control)
    {
        var text = Text(label, wrap: false);
        text.Width = 240;
        var row = new DockPanel();
        DockPanel.SetDock(text, Dock.Left);
        row.Children.Add(text);
        row.Children.Add(control);
        return row;
    }

    private static CheckBox Check(string text, bool value, Action<bool> changed)
    {
        var box = new CheckBox { Content = Text(text, 13), IsChecked = value, VerticalContentAlignment = VerticalAlignment.Center };
        box.Checked += (_, _) => changed(true);
        box.Unchecked += (_, _) => changed(false);
        return box;
    }

    private static StackPanel Shortcut(string[] keys, string meaning)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        for (var i = 0; i < keys.Length; i++)
        {
            if (i > 0) { var plus = Text("+", wrap: false); plus.Margin = new Thickness(6, 0, 6, 0); row.Children.Add(plus); }
            row.Children.Add(KeyCap(keys[i]));
        }
        var text = Text(meaning, color: Theme.Secondary, wrap: false);
        text.Margin = new Thickness(14, 0, 0, 0);
        row.Children.Add(text);
        return row;
    }

    private static DockPanel Permission(string title, bool granted, UIElement? action)
    {
        var left = Row(8, Icon(granted ? "" : "", 13, granted ? Theme.Blue : Theme.Orange), Text(title, 13, bold: true, wrap: false),
            Text(granted ? "ενεργό" : "λείπει", 13, color: Theme.Secondary, wrap: false));
        return Spread(left, !granted && action != null ? action : new Border());
    }
}

/// Ο δακτύλιος «Σήμερα»: πόσο από τον ημερήσιο στόχο έχεις γράψει. Με ✓ όταν τον πετύχεις.
sealed class GoalRing(double progress) : FrameworkElement
{
    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = size / 2 - 3.5;
        dc.DrawEllipse(null, new Pen(Theme.WithOpacity(Theme.Blue, 0.12), 7), center, radius, radius);
        var value = Math.Clamp(progress, 0, 1);
        if (value > 0)
        {
            var pen = new Pen(Theme.Blue, 7) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            if (value >= 0.9999) dc.DrawEllipse(null, pen, center, radius, radius);
            else
            {
                var angle = value * 2 * Math.PI;
                var start = new Point(center.X, center.Y - radius);
                var end = new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle));
                var figure = new PathFigure { StartPoint = start, IsClosed = false };
                figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0, value > 0.5, SweepDirection.Clockwise, true));
                dc.DrawGeometry(null, pen, new PathGeometry([figure]));
            }
        }
        var label = progress >= 1 ? "" : $"{(int)(Math.Clamp(progress, 0, 1) * 100)}%";
        var typeface = progress >= 1
            ? new Typeface(Theme.Icons, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal)
            : new Typeface(Theme.Font, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var text = new FormattedText(label, Ui.Greek, FlowDirection.LeftToRight, typeface, progress >= 1 ? 16 : 12,
            progress >= 1 ? Theme.Blue : Theme.Text, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
    }
}
