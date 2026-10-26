using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Voiceboard;

/// Το εικονίδιο της εφαρμογής, ίδιο με της έκδοσης Mac (make-icon.swift): στρογγυλεμένο τετράγωνο από σκούρο μπλε
/// σε γαλάζιο, με ένα κύμα φωνής που καταλήγει σε κεχριμπαρένια κουκκίδα. Σχεδιάζεται με κώδικα σε κάθε μέγεθος.
static class IconArt
{
    public static Bitmap Render(int pixels, bool recording = false, double insetRatio = 0.1)
    {
        var bitmap = new Bitmap(pixels, pixels, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        var inset = (float)(pixels * insetRatio);
        var rect = new RectangleF(inset, inset, pixels - 2 * inset, pixels - 2 * inset);
        var radius = rect.Width * 0.225f;
        using var shape = new GraphicsPath();
        shape.AddArc(rect.Left, rect.Top, radius * 2, radius * 2, 180, 90);
        shape.AddArc(rect.Right - radius * 2, rect.Top, radius * 2, radius * 2, 270, 90);
        shape.AddArc(rect.Right - radius * 2, rect.Bottom - radius * 2, radius * 2, radius * 2, 0, 90);
        shape.AddArc(rect.Left, rect.Bottom - radius * 2, radius * 2, radius * 2, 90, 90);
        shape.CloseFigure();
        using (var gradient = new LinearGradientBrush(new PointF(rect.Left, rect.Bottom), new PointF(rect.Right, rect.Top), Color.Black, Color.White))
        {
            gradient.InterpolationColors = new ColorBlend
            {
                Colors = [Color.FromArgb(15, 26, 48), Color.FromArgb(0, 92, 158), Color.FromArgb(87, 181, 232)],
                Positions = [0f, 0.5f, 1f],
            };
            g.FillPath(gradient, shape);
        }

        // Ένα συνεχές κύμα φωνής που μικραίνει από αριστερά προς τα δεξιά: η φωνή που «γίνεται» κείμενο.
        float left = rect.Left + rect.Width * 0.17f, right = rect.Left + rect.Width * 0.70f, mid = rect.Top + rect.Height / 2;
        var points = new PointF[121];
        for (var i = 0; i < points.Length; i++)
        {
            var t = i / (double)(points.Length - 1);
            var amplitude = rect.Height * 0.26 * (1 - t * 0.85) * Math.Sin(t * Math.PI);
            points[i] = new PointF(left + (right - left) * (float)t, mid - (float)(amplitude * Math.Sin(t * Math.PI * 5)));
        }
        using (var pen = new Pen(Color.White, Math.Max(1.2f, rect.Width * 0.075f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            g.DrawLines(pen, points);
        var dot = rect.Width * 0.075f;
        using (var amber = new SolidBrush(Color.FromArgb(230, 158, 0)))
            g.FillEllipse(amber, rect.Left + rect.Width * 0.80f - dot, mid - dot, dot * 2, dot * 2);

        if (recording)
        {
            // Κόκκινη κουκκίδα με λευκό περίγραμμα κάτω δεξιά: ηχογραφεί.
            var size = pixels * 0.5f;
            using var white = new SolidBrush(Color.White);
            using var red = new SolidBrush(Color.FromArgb(237, 69, 56));
            g.FillEllipse(white, pixels - size, pixels - size, size, size);
            var border = Math.Max(1f, pixels * 0.07f);
            g.FillEllipse(red, pixels - size + border, pixels - size + border, size - 2 * border, size - 2 * border);
        }
        return bitmap;
    }

    /// Εικονίδιο για τη γραμμή εργασιών. Το Icon.FromHandle δεν κρατά δικό του αντίγραφο, γι' αυτό το κλωνοποιούμε.
    public static Icon TrayIcon(bool recording)
    {
        var size = System.Windows.Forms.SystemInformation.SmallIconSize.Width;
        using var bitmap = Render(Math.Max(16, size), recording, insetRatio: 0);
        var handle = bitmap.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { Native.DestroyIcon(handle); }
    }

    public static BitmapSource WindowImage(int pixels = 64)
    {
        using var bitmap = Render(pixels);
        var handle = bitmap.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(handle, IntPtr.Zero, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally { DeleteObject(handle); }
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
}
