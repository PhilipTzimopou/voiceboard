using System.Windows;
using Voiceboard.Core;

namespace Voiceboard;

static class Program
{
    /// Παράμετροι:
    ///   --startup          ξεκινά κρυφά στη γραμμή εργασιών (από την εκκίνηση των Windows)
    ///   --preview-overlay  δείχνει την ένδειξη ηχογράφησης για λίγα δευτερόλεπτα, χωρίς μικρόφωνο ή πλήκτρα
    ///   --preview-window   μόνο το παράθυρο, χωρίς πλήκτρα ή μικρόφωνο
    ///   --self-test <φάκελος> [...]   ο αυτόματος έλεγχος του GitHub Actions (SelfTest.cs)
    [STAThread]
    static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"Μη αναμενόμενο σφάλμα: {e.ExceptionObject}");
        if (args.Contains("--self-test")) return SelfTest.Run(args);

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Write($"Σφάλμα στο παράθυρο: {e.Exception}");
            e.Handled = true;   // ένα σφάλμα στην οθόνη δεν κλείνει την υπαγόρευση
        };

        if (args.Contains("--preview-overlay"))
        {
            var preview = new Controller();
            app.Startup += (_, _) => preview.PreviewOverlay();
            return app.Run();
        }
        if (args.Contains("--preview-window"))
        {
            app.ShutdownMode = ShutdownMode.OnLastWindowClose;
            app.Startup += (_, _) => new MainWindow(new AppModel()).Show();
            return app.Run();
        }

        // Μία εκτέλεση μόνο: αν τρέχει ήδη, της λέμε να δείξει το παράθυρο και κλείνουμε.
        using var mutex = new Mutex(true, "Voiceboard.SingleInstance", out var first);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Voiceboard.ShowWindow");
        if (!first)
        {
            showSignal.Set();
            return 0;
        }

        var controller = new Controller();
        new Thread(() =>
        {
            while (showSignal.WaitOne()) app.Dispatcher.BeginInvoke(controller.ShowWindow);
        }) { IsBackground = true, Name = "voiceboard.single-instance" }.Start();

        app.Startup += (_, _) => controller.Start(showWindow: !args.Contains("--startup"));
        var code = app.Run();
        GC.KeepAlive(mutex);
        return code;
    }
}
