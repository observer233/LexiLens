using System.IO;
using System.Windows;

namespace LexiLens.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "LexiLens · 遇到问题", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        var diagnostic = e.Args.Length == 2 && e.Args[0] == "--diagnostics";
        var window = new MainWindow(diagnostic ? Path.Combine(Path.GetFullPath(e.Args[1]), "profile") : null);
        MainWindow = window;
        if (diagnostic)
            window.Loaded += async (_, _) => await Diagnostics.RunAsync(window, Path.GetFullPath(e.Args[1]));
        window.Show();
    }
}
