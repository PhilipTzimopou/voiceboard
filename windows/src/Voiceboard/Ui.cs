using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Voiceboard;

/// Η παλέτα της έκδοσης Mac: σκούρο μπλε πλαϊνό, ψυχρό ανοιχτό γκρι περιεχόμενο. Χρώματα Okabe-Ito
/// (μπλε, γαλάζιο, πορτοκαλί), που ξεχωρίζουν και με χρωματοψία· κανένα σήμα δεν βασίζεται μόνο στο χρώμα.
static class Theme
{
    public static readonly SolidColorBrush Sidebar = Brush(15, 26, 48);
    public static readonly SolidColorBrush SidebarText = Brushes.White;
    public static readonly SolidColorBrush SidebarMuted = Brush(255, 255, 255, 158);
    public static readonly SolidColorBrush SidebarSelection = Brush(255, 255, 255, 31);
    public static readonly SolidColorBrush Background = Brush(243, 245, 249);
    public static readonly SolidColorBrush Card = Brushes.White;
    public static readonly SolidColorBrush Border = Brush(15, 26, 48, 26);
    public static readonly SolidColorBrush Text = Brush(20, 28, 46);
    public static readonly SolidColorBrush Secondary = Brush(92, 102, 122);
    public static readonly SolidColorBrush Blue = Brush(0, 114, 178);
    public static readonly SolidColorBrush Sky = Brush(86, 180, 233);
    public static readonly SolidColorBrush Orange = Brush(213, 94, 0);
    public static readonly SolidColorBrush Amber = Brush(230, 159, 0);
    public static readonly SolidColorBrush Chip = Brush(0, 114, 178, 20);

    /// Η γραμματοσειρά των Windows (Segoe UI), όπως η έκδοση Mac χρησιμοποιεί τη γραμματοσειρά του Mac.
    public static readonly FontFamily Font = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily Display = new("Segoe UI Variable Display, Segoe UI");
    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public static SolidColorBrush Brush(byte r, byte g, byte b, byte a = 255)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    public static SolidColorBrush WithOpacity(SolidColorBrush brush, double opacity) =>
        Brush(brush.Color.R, brush.Color.G, brush.Color.B, (byte)(opacity * 255));
}

/// Μικρά κομμάτια για το παράθυρο, χτισμένα με κώδικα (όχι XAML), ώστε κάθε σελίδα να διαβάζεται σαν την έκδοση Mac.
static class Ui
{
    public static readonly CultureInfo Greek = CultureInfo.GetCultureInfo("el-GR");

    public static TextBlock Text(string text, double size = 13, bool bold = false, Brush? color = null, bool wrap = true) => new()
    {
        Text = text,
        FontSize = size,
        FontFamily = Theme.Font,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = color ?? Theme.Text,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static TextBlock Icon(string glyph, double size = 14, Brush? color = null) => new()
    {
        Text = glyph,
        FontFamily = Theme.Icons,
        FontSize = size,
        Foreground = color ?? Theme.Text,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static Border Card(UIElement content, double padding = 18) => new()
    {
        Background = Theme.Card,
        BorderBrush = Theme.Border,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(14),
        Padding = new Thickness(padding),
        Child = content,
    };

    public static StackPanel Stack(double spacing, params UIElement[] children) => Stack(spacing, Orientation.Vertical, children);

    public static StackPanel Row(double spacing, params UIElement[] children) => Stack(spacing, Orientation.Horizontal, children);

    private static StackPanel Stack(double spacing, Orientation orientation, UIElement[] children)
    {
        var panel = new StackPanel { Orientation = orientation };
        for (var i = 0; i < children.Length; i++)
        {
            if (i > 0 && children[i] is FrameworkElement element)
            {
                // Αντικαθιστά (όχι προσθέτει) το κενό, ώστε ένα στοιχείο που ξαναμπαίνει σε σελίδα να μη «φεύγει» κάθε φορά πιο κάτω.
                var margin = element.Margin;
                element.Margin = orientation == Orientation.Vertical
                    ? new Thickness(margin.Left, spacing, margin.Right, margin.Bottom)
                    : new Thickness(spacing, margin.Top, margin.Right, margin.Bottom);
            }
            panel.Children.Add(children[i]);
        }
        return panel;
    }

    /// Οριζόντια γραμμή με ένα στοιχείο τέρμα δεξιά (σαν το Spacer() του SwiftUI).
    public static DockPanel Spread(UIElement left, UIElement right)
    {
        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(right);
        dock.Children.Add(left);
        return dock;
    }

    public static Border KeyCap(string label) => new()
    {
        Background = Theme.Background,
        BorderBrush = Theme.WithOpacity(Theme.Text, 0.18),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(8, 3, 8, 4),
        VerticalAlignment = VerticalAlignment.Center,
        Child = Text(label, 12, bold: true, wrap: false),
    };

    public static StackPanel SectionTitle(string title, string? subtitle = null)
    {
        var heading = Text(title, 28, bold: true);
        heading.FontFamily = Theme.Display;
        heading.FontWeight = FontWeights.Bold;
        return subtitle == null ? Stack(6, heading) : Stack(6, heading, Text(subtitle, 13, color: Theme.Secondary));
    }

    public static TextBlock FieldLabel(string text)
    {
        var label = Text(text.ToUpper(Greek), 10.5, bold: true, color: Theme.Secondary, wrap: false);
        return label;
    }

    public static Separator Divider() => new() { Background = Theme.Border, Margin = new Thickness(0, 2, 0, 2) };

    /// Κουμπί με στρογγυλεμένες γωνίες: primary = μπλε γέμισμα, αλλιώς λευκό με περίγραμμα, plain = μόνο κείμενο/εικονίδιο.
    public static Button Button(object content, Action action, bool primary = false, bool plain = false, string? tooltip = null)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "chrome";
        border.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(System.Windows.Controls.Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(System.Windows.Controls.Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(System.Windows.Controls.Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(System.Windows.Controls.Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.85, "chrome"));
        template.Triggers.Add(hover);
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45, "chrome"));
        template.Triggers.Add(disabled);

        var button = new Button
        {
            Template = template,
            Content = content is string s ? Text(s, 13, bold: primary, color: primary ? Brushes.White : Theme.Text, wrap: false) : content,
            Background = plain ? Brushes.Transparent : primary ? Theme.Blue : Theme.Card,
            BorderBrush = plain || primary ? Brushes.Transparent : Theme.WithOpacity(Theme.Text, 0.18),
            BorderThickness = new Thickness(1),
            Padding = plain ? new Thickness(6, 4, 6, 4) : new Thickness(14, 6, 14, 7),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = tooltip,
            Focusable = true,
        };
        button.Click += (_, _) => action();
        return button;
    }

    public static TextBox TextBox(string placeholder, double width = double.NaN, bool multiline = false)
    {
        var box = new TextBox
        {
            FontFamily = Theme.Font,
            FontSize = 13,
            Padding = new Thickness(6, 5, 6, 5),
            BorderBrush = Theme.WithOpacity(Theme.Text, 0.2),
            Background = Theme.Card,
            Foreground = Theme.Text,
            VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden,
            Width = width,
        };
        if (multiline) box.Height = 96;
        return WithPlaceholder(box, placeholder);
    }

    /// Γκρι κείμενο υπόδειξης μέσα στο πεδίο όσο είναι άδειο.
    public static TextBox WithPlaceholder(TextBox box, string placeholder)
    {
        var brush = new VisualBrush
        {
            Stretch = Stretch.None,
            AlignmentX = AlignmentX.Left,
            AlignmentY = box.AcceptsReturn ? AlignmentY.Top : AlignmentY.Center,
            Transform = new TranslateTransform(8, box.AcceptsReturn ? 6 : 0),
            Visual = Text(placeholder, 13, color: Theme.WithOpacity(Theme.Secondary, 0.75), wrap: false),
        };
        void Update() => box.Background = box.Text.Length == 0 ? brush : Theme.Card;
        box.TextChanged += (_, _) => Update();
        Update();
        return box;
    }

    /// Τα λεπτά σε «45 λεπτά» ή «2 ώρ. 5 λ.».
    public static string Duration(int minutes)
    {
        if (minutes < 60) return $"{minutes} λεπτά";
        int hours = minutes / 60, rest = minutes % 60;
        return rest == 0 ? $"{hours} ώρ." : $"{hours} ώρ. {rest} λ.";
    }

    public static string Number(int value) => value.ToString("N0", Greek);
}

/// Στοίχιση σε γραμμές που αναδιπλώνονται, για τις λέξεις του λεξιλογίου.
sealed class WrapPanelWithGap : Panel
{
    public double Gap { get; set; } = 8;

    protected override Size MeasureOverride(Size available)
    {
        double x = 0, y = 0, rowHeight = 0;
        var width = double.IsInfinity(available.Width) ? 600 : available.Width;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(width, double.PositiveInfinity));
            var size = child.DesiredSize;
            if (x + size.Width > width && x > 0) { x = 0; y += rowHeight + Gap; rowHeight = 0; }
            x += size.Width + Gap;
            rowHeight = Math.Max(rowHeight, size.Height);
        }
        return new Size(width, y + rowHeight);
    }

    protected override Size ArrangeOverride(Size final)
    {
        double x = 0, y = 0, rowHeight = 0;
        foreach (UIElement child in InternalChildren)
        {
            var size = child.DesiredSize;
            if (x + size.Width > final.Width && x > 0) { x = 0; y += rowHeight + Gap; rowHeight = 0; }
            child.Arrange(new Rect(new Point(x, y), size));
            x += size.Width + Gap;
            rowHeight = Math.Max(rowHeight, size.Height);
        }
        return final;
    }
}
