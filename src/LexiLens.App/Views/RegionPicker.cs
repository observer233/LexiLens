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
    internal static Task<CaptureRegion?> SelectAsync()
    {
        var completion = new TaskCompletionSource<CaptureRegion?>();
        var windows = new List<Window>();
        foreach (var monitor in NativeMethods.Monitors())
        {
            var canvas = new Canvas { Background = new SolidColorBrush(Color.FromArgb(45, 0, 0, 0)) };
            var frame = new Rectangle { Stroke = new SolidColorBrush(Color.FromRgb(233, 189, 130)), StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(32, 233, 189, 130)), Visibility = Visibility.Hidden };
            var label = new TextBlock { Text = "拖动框选任务或字幕区域 · Esc 取消 · 每次选择一块屏幕", Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromRgb(26, 29, 34)), Padding = new Thickness(18), FontSize = 17 };
            Canvas.SetLeft(label, 32); Canvas.SetTop(label, 32);
            canvas.Children.Add(label); canvas.Children.Add(frame);
            var window = new Window
            {
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, AllowsTransparency = true,
                Background = Brushes.Transparent, Topmost = true, ShowInTaskbar = false,
                Content = canvas, Cursor = Cursors.Cross
            };
            windows.Add(window);
            NativeMethods.Point? origin = null;
            System.Windows.Point start = default;
            window.Loaded += (_, _) => NativeMethods.SetWindowPos(new WindowInteropHelper(window).Handle, -1, monitor.Left, monitor.Top, monitor.Right - monitor.Left, monitor.Bottom - monitor.Top, 0x40);
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
                var region = new CaptureRegion(Math.Min(origin.Value.X, end.X), Math.Min(origin.Value.Y, end.Y), Math.Abs(end.X - origin.Value.X), Math.Abs(end.Y - origin.Value.Y));
                canvas.ReleaseMouseCapture(); origin = null;
                if (region.IsValid) completion.TrySetResult(region);
                else frame.Visibility = Visibility.Hidden;
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
