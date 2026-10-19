using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using LexiLens.App.Platform;
using LexiLens.Core;

namespace LexiLens.App.Views;

internal static class WindowPicker
{
    internal static Task<CaptureRegion?> SelectAsync(nint? diagnosticTarget = null, Action<Window>? diagnosticRender = null)
    {
        var targets = NativeMethods.WindowTargets();
        var completion = new TaskCompletionSource<CaptureRegion?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var overlays = new List<Window>();
        NativeMethods.WindowTarget? selected = null;
        var redraw = new List<Action<NativeMethods.WindowTarget?>>();
        foreach (var monitor in NativeMethods.Monitors())
        {
            var canvas = new Canvas { Background = new SolidColorBrush(Color.FromArgb(35, 0, 0, 0)) };
            var frame = new Rectangle { Stroke = new SolidColorBrush(Color.FromRgb(40, 140, 240)), StrokeThickness = 3, Fill = new SolidColorBrush(Color.FromArgb(20, 0, 120, 215)), IsHitTestVisible = false, Visibility = Visibility.Hidden };
            var label = new TextBlock { Text = "移动鼠标选择窗口 · 单击确认 · Esc 取消", Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromRgb(30, 40, 55)), Padding = new Thickness(14), FontSize = 15, MaxWidth = 620, IsHitTestVisible = false };
            Canvas.SetLeft(label, 24); Canvas.SetTop(label, 24); canvas.Children.Add(frame); canvas.Children.Add(label);
            var overlay = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, AllowsTransparency = true, Background = Brushes.Transparent, Topmost = true, ShowInTaskbar = false, Content = canvas, Cursor = Cursors.Cross };
            overlays.Add(overlay);
            overlay.Loaded += (_, _) => NativeMethods.SetWindowPos(new WindowInteropHelper(overlay).Handle, -1, monitor.Left, monitor.Top, monitor.Right - monitor.Left, monitor.Bottom - monitor.Top, 0x40);
            overlay.KeyDown += (_, e) => { if (e.Key == Key.Escape) completion.TrySetResult(null); };
            overlay.MouseLeftButtonDown += (_, _) => { if (selected != null) completion.TrySetResult(selected.Region); };
            overlay.Closed += (_, _) => completion.TrySetResult(null);
            redraw.Add(target =>
            {
                if (target == null) { frame.Visibility = Visibility.Hidden; return; }
                var r = target.Region;
                var topLeft = overlay.PointFromScreen(new System.Windows.Point(r.X, r.Y));
                var bottomRight = overlay.PointFromScreen(new System.Windows.Point(r.X + r.Width, r.Y + r.Height));
                Canvas.SetLeft(frame, topLeft.X); Canvas.SetTop(frame, topLeft.Y);
                frame.Width = bottomRight.X - topLeft.X; frame.Height = bottomRight.Y - topLeft.Y; frame.Visibility = Visibility.Visible;
                label.Text = target.Title + $"  ·  {r.Width} × {r.Height}\n单击选择整个可见窗口 · Esc 取消";
            });
        }
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        timer.Tick += (_, _) =>
        {
            NativeMethods.GetCursorPos(out var p);
            var target = targets.FirstOrDefault(t => p.X >= t.Region.X && p.X < t.Region.X + t.Region.Width && p.Y >= t.Region.Y && p.Y < t.Region.Y + t.Region.Height);
            if (target == selected) return;
            selected = target; foreach (var draw in redraw) draw(target);
        };
        foreach (var overlay in overlays) overlay.Show();
        timer.Start(); if (overlays.Count == 0) completion.TrySetResult(null);
        if (diagnosticTarget != null) _ = Application.Current.Dispatcher.BeginInvoke(async () =>
        {
            timer.Stop(); await Task.Delay(100);
            selected = targets.FirstOrDefault(x => x.Handle == diagnosticTarget);
            foreach (var draw in redraw) draw(selected);
            foreach (var overlay in overlays) diagnosticRender?.Invoke(overlay);
            if (selected == null) completion.TrySetResult(null);
            else overlays[0].RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
        });
        return Finish();
        async Task<CaptureRegion?> Finish()
        {
            var result = await completion.Task; timer.Stop(); foreach (var overlay in overlays) overlay.Close(); return result;
        }
    }
}
