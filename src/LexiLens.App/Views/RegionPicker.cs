using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Interop;
using LexiLens.App.Platform;
using LexiLens.Core;

namespace LexiLens.App.Views;

internal static class RegionPicker
{
    internal static Task<CaptureRegion?> SelectAsync(CaptureRegion? diagnosticSelection = null, bool immediate = false, bool diagnosticCancel = false)
    {
        var completion = new TaskCompletionSource<CaptureRegion?>();
        var windows = new List<Window>();
        foreach (var monitor in NativeMethods.Monitors())
        {
            var canvas = new Canvas { Background = new SolidColorBrush(Color.FromArgb(45, 0, 0, 0)) };
            var frame = new Rectangle { Stroke = new SolidColorBrush(Color.FromRgb(40, 140, 240)), StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(24, 0, 120, 215)), Visibility = Visibility.Hidden };
            var label = new TextBlock { Text = immediate ? "拖动划选 · 松手立即翻译 · Esc 取消" : "拖动选择固定区域 · 松手开始翻译 · Esc 取消", Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromRgb(30, 40, 55)), Padding = new Thickness(14), FontSize = 15 };
            Canvas.SetLeft(label, 32); Canvas.SetTop(label, 32);
            canvas.Children.Add(label); canvas.Children.Add(frame);
            var window = new Window
            {
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, AllowsTransparency = true,
                Background = Brushes.Transparent, Topmost = true, ShowInTaskbar = false,
                Content = canvas, Cursor = Cursors.Cross
            };
            windows.Add(window);
            window.SourceInitialized += (_, _) => NativeMethods.ExcludeFromCapture(window);
            NativeMethods.Point? origin = null;
            System.Windows.Point start = default;
            void Submit(NativeMethods.Point a, NativeMethods.Point b)
            {
                b.X = Math.Clamp(b.X, monitor.Left, monitor.Right); b.Y = Math.Clamp(b.Y, monitor.Top, monitor.Bottom);
                var region = new CaptureRegion(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
                if (region.IsValid) completion.TrySetResult(region); else frame.Visibility = Visibility.Hidden;
            }
            window.Loaded += (_, _) =>
            {
                NativeMethods.SetWindowPos(new WindowInteropHelper(window).Handle, -1, monitor.Left, monitor.Top, monitor.Right - monitor.Left, monitor.Bottom - monitor.Top, 0x40);
                if (diagnosticSelection != null && diagnosticSelection.X >= monitor.Left && diagnosticSelection.Y >= monitor.Top && diagnosticSelection.X + diagnosticSelection.Width <= monitor.Right && diagnosticSelection.Y + diagnosticSelection.Height <= monitor.Bottom)
                    _ = window.Dispatcher.BeginInvoke(() => Submit(new() { X = diagnosticSelection.X, Y = diagnosticSelection.Y }, new() { X = diagnosticSelection.X + diagnosticSelection.Width, Y = diagnosticSelection.Y + diagnosticSelection.Height }));
                if (diagnosticCancel) _ = window.Dispatcher.BeginInvoke(() => window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent }));
            };
            window.KeyDown += (_, e) => { if (e.Key == Key.Escape) completion.TrySetResult(null); };
            canvas.MouseLeftButtonDown += (_, e) =>
            {
                NativeMethods.GetCursorPos(out var point); origin = point;
                start = e.GetPosition(canvas); frame.Visibility = Visibility.Visible;
                Canvas.SetLeft(frame, start.X); Canvas.SetTop(frame, start.Y);
                frame.Width = 0; frame.Height = 0;
                canvas.CaptureMouse();
            };
            canvas.MouseMove += (_, e) =>
            {
                if (origin == null) return;
                var end = e.GetPosition(canvas);
                Canvas.SetLeft(frame, Math.Min(start.X, end.X)); Canvas.SetTop(frame, Math.Min(start.Y, end.Y));
                frame.Width = Math.Abs(end.X - start.X); frame.Height = Math.Abs(end.Y - start.Y);
            };
            canvas.MouseLeftButtonUp += (_, _) =>
            {
                if (origin == null) return;
                NativeMethods.GetCursorPos(out var end);
                end.X = Math.Clamp(end.X, monitor.Left, monitor.Right);
                end.Y = Math.Clamp(end.Y, monitor.Top, monitor.Bottom);
                var begin = origin.Value; canvas.ReleaseMouseCapture(); origin = null;
                Submit(begin, end);
            };
            window.Closed += (_, _) => completion.TrySetResult(null);
        }
        foreach (var window in windows) window.Show();
        if (windows.Count == 0) completion.TrySetResult(null);
        return FinishAsync();

        async Task<CaptureRegion?> FinishAsync()
        {
            var result = await completion.Task;
            foreach (var window in windows) window.Close();
            return result;
        }
    }
}
