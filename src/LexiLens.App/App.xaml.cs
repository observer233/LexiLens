using System.IO;
using System.Windows;

namespace LexiLens.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length is 2 or 3 && e.Args[0] == "--ocr-worker")
        {
            try { Platform.OcrWorker.Run(e.Args[1], e.Args.Length == 3 && e.Args[2] == "--diagnostic-stall"); } catch (Exception) { }
            Shutdown(); return;
        }
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "LexiLens · 遇到问题", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        var diagnostic = e.Args.Length == 2 && e.Args[0] is "--diagnostics" or "--diagnostics-ui";
        var window = new MainWindow(diagnostic ? Path.Combine(Path.GetFullPath(e.Args[1]), "profile") : null);
        MainWindow = window;
        if (diagnostic)
            window.Loaded += async (_, _) => { if (e.Args[0] == "--diagnostics-ui") await Diagnostics.RunUiAsync(window, Path.GetFullPath(e.Args[1])); else await Diagnostics.RunAsync(window, Path.GetFullPath(e.Args[1])); };
        window.Show();
    }
}
