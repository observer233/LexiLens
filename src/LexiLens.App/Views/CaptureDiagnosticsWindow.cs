using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using LexiLens.App.Platform;
using LexiLens.Core;
using Microsoft.Win32;

namespace LexiLens.App.Views;

internal sealed class CaptureDiagnosticsWindow : Window
{
    private readonly ComboBox _windows = new() { DisplayMemberPath = "Title", MinWidth = 340 };
    private readonly ComboBox _backend = new() { ItemsSource = new[] { "Gdi", "Wgc" }, SelectedIndex = 0, Width = 100 };
    private readonly TextBox _roi = new() { Text = "0.1, 0.7, 0.8, 0.25", MinWidth = 260 };
    private readonly CheckBox _translate = new() { Content = "使用当前服务翻译", Margin = new Thickness(0, 8, 16, 8) };
    private readonly CheckBox _record = new() { Content = "保存 ROI 图片供本机回放", Margin = new Thickness(0, 8, 16, 8) };
    private readonly CheckBox _windowsOcr = new() { Content = "实验：尝试 Windows OCR（便携版不在官方支持范围）", Margin = new Thickness(0, 8, 16, 8) };
    private readonly TextBlock _status = new() { Text = "选择窗口和字幕区域，再开始。每轮 30 秒。", TextWrapping = TextWrapping.Wrap };
    private readonly AppSettings _settings;
    private CancellationTokenSource? _running;
    private string? _manifest;
    private bool _closing;

    internal CaptureDiagnosticsWindow(AppSettings settings)
    {
        _settings = settings;
        Title = "LexiLens · 捕获与延迟验证"; Width = 720; Height = 510;
        Style = (Style)Application.Current.FindResource(typeof(Window));
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "捕获与延迟验证", FontSize = 23, Margin = new Thickness(0, 0, 0, 16) });
        var row = new WrapPanel(); row.Children.Add(_windows); row.Children.Add(_backend);
        AddButton(row, "刷新窗口", (_, _) => RefreshWindows()); panel.Children.Add(row);
        panel.Children.Add(new TextBlock { Text = "字幕 ROI：左、上、宽、高（相对目标窗口，0–1）", Margin = new Thickness(0, 16, 0, 6) });
        var roiRow = new WrapPanel(); roiRow.Children.Add(_roi); AddButton(roiRow, "在屏幕上框选", PickRoi); panel.Children.Add(roiRow);
        var flags = new WrapPanel(); flags.Children.Add(_translate); flags.Children.Add(_record); panel.Children.Add(flags);
        panel.Children.Add(_windowsOcr);
        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 18) };
        AddButton(actions, "开始窗口验证", async (_, _) => { _manifest = null; await Run(); });
        AddButton(actions, "回放素材清单", async (_, _) =>
        {
            if (_running != null) return;
            var dialog = new OpenFileDialog { Filter = "回放清单|*.json" };
            if (dialog.ShowDialog(this) == true) { _manifest = dialog.FileName; await Run(); }
        });
        AddButton(actions, "停止", (_, _) => _running?.Cancel()); panel.Children.Add(actions); panel.Children.Add(_status);
        Content = panel;
        SourceInitialized += (_, _) => NativeMethods.ExcludeFromCapture(this);
        Closing += (_, e) => { if (_running != null) { e.Cancel = true; _closing = true; _running.Cancel(); } };
        RefreshWindows();
    }

    private static void AddButton(Panel panel, string text, RoutedEventHandler action)
    {
        var button = new Button { Content = text, Margin = new Thickness(6, 0, 0, 0) }; button.Click += action; panel.Children.Add(button);
    }
    private void RefreshWindows() { _windows.ItemsSource = NativeMethods.WindowTargets(); _windows.SelectedIndex = 0; }

    private async void PickRoi(object sender, RoutedEventArgs e)
    {
        if (_running != null || _windows.SelectedItem is not NativeMethods.WindowTarget target) return;
        var previous = NativeMethods.GetForegroundWindow();
        Hide();
        try
        {
            var selection = await RegionPicker.SelectAsync();
            if (selection == null) return;
            var bounds = RegionCapture.WindowRegion(target.Handle);
            var roi = new TextBounds((selection.X - bounds.X) / (double)bounds.Width, (selection.Y - bounds.Y) / (double)bounds.Height,
                selection.Width / (double)bounds.Width, selection.Height / (double)bounds.Height);
            if (!roi.IsValid || roi.Right > 1 || roi.Bottom > 1) throw new InvalidOperationException("字幕区域必须位于所选窗口内。");
            _roi.Text = string.Join(", ", new[] { roi.X, roi.Y, roi.Width, roi.Height }.Select(n => n.ToString("0.######", CultureInfo.InvariantCulture)));
        }
        catch (Exception ex) { _status.Text = ex.Message; }
        finally { Show(); NativeMethods.SetForegroundWindow(previous); }
    }

    private async Task Run()
    {
        if (_running != null) return;
        try
        {
            var parts = _roi.Text.Split(',').Select(s => double.Parse(s.Trim(), CultureInfo.InvariantCulture)).ToArray();
            if (parts.Length != 4) throw new InvalidOperationException("请填写四个 ROI 数值。");
            var target = _windows.SelectedItem as NativeMethods.WindowTarget;
            if (_manifest == null && target == null) throw new InvalidOperationException("请选择一个窗口。");
            var options = new CaptureDiagnosticsOptions
            {
                Backend = Enum.Parse<CaptureBackend>((string)_backend.SelectedItem), Hwnd = (long)(target?.Handle ?? 0),
                Roi = new(parts[0], parts[1], parts[2], parts[3]), ReplayManifest = _manifest,
                Translate = _translate.IsChecked == true, RecordFrames = _record.IsChecked == true,
                AllowUnsupportedWindowsOcr = _windowsOcr.IsChecked == true,
                OutputDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LexiLens", "capture-diagnostics", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"))
            };
            using var cancellation = new CancellationTokenSource(); _running = cancellation;
            await CaptureDiagnostics.RunAsync(options, _settings, null,
                item => { if (item.State is PipelineState.Error or PipelineState.Warning) Dispatcher.BeginInvoke(() => _status.Text = item.Code + " · " + item.Message); }, cancellation.Token);
            _status.Text = "验证结束，记录位置：\n" + options.OutputDirectory;
        }
        catch (Exception ex) { _status.Text = ex.Message; }
        finally { _running = null; if (_closing) Close(); }
    }
}
