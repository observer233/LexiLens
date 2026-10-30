using System.IO;
using System.Windows;
using System.Text.Json;
using LexiLens.Core;

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
        if (e.Args.Length == 2 && e.Args[0] == "--diagnostics-capture-smoke")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Dispatcher.InvokeAsync(async () => { await CaptureSmokeDiagnostics.RunAsync(Path.GetFullPath(e.Args[1])); Shutdown(); });
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--diagnostics-capture")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    var options = JsonSerializer.Deserialize<CaptureDiagnosticsOptions>(File.ReadAllText(e.Args[1]), CaptureDiagnosticsOptions.Json)
                        ?? throw new InvalidDataException("诊断选项为空。");
                    var store = new LocalStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LexiLens"));
                    await CaptureDiagnostics.RunAsync(options, store.Load("settings.json", new AppSettings()).Sanitize(), null);
                }
                catch (Exception ex) { File.WriteAllText(Path.GetFullPath(e.Args[1]) + ".error.txt", ex.GetType().Name + ": " + ex.Message); }
                finally { Shutdown(); }
            });
            return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "LexiLens · 遇到问题", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        var diagnostic = e.Args.Length == 2 && e.Args[0] is "--diagnostics" or "--diagnostics-ui" or "--diagnostics-spatial";
        var window = new MainWindow(diagnostic ? Path.Combine(Path.GetFullPath(e.Args[1]), "profile") : null);
        MainWindow = window;
        if (diagnostic)
            window.Loaded += async (_, _) => { if (e.Args[0] == "--diagnostics-ui") await Diagnostics.RunUiAsync(window, Path.GetFullPath(e.Args[1])); else if (e.Args[0] == "--diagnostics-spatial") await Diagnostics.RunSpatialAsync(window, Path.GetFullPath(e.Args[1])); else await Diagnostics.RunAsync(window, Path.GetFullPath(e.Args[1])); };
        window.Show();
    }
}
