using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using LexiLens.App.Platform;
using LexiLens.Core;

namespace LexiLens.App.Views;

// A transparent, capture-excluded layer. Paragraphs retain their screen bounds;
// no minimum 300px floating card or aggregate text box is involved.
internal sealed class SpatialOverlayWindow : Window
{
    private readonly Canvas _canvas = new() { ClipToBounds = true };
    private readonly CaptureRegion _region;
    private NativeMethods.Rect _monitor;
    private AppSettings _settings;
    private IReadOnlyList<SpatialBlock> _blocks = [];
    internal bool IsLocked { get; private set; } = true;
    internal bool CaptureExcluded { get; private set; }
    internal int BlockCount => _blocks.Count;
    internal int RenderedCount => _canvas.Children.Count;
    internal event Action? ToggleRequested;
    internal event Action? PickRequested;
    internal event Action? ClearRequested;

    internal SpatialOverlayWindow(CaptureRegion region, AppSettings settings)
    {
        _region = region; _settings = settings;
        Style = (Style)Application.Current.FindResource(typeof(Window));
        Title = "LexiLens 原位翻译"; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent; Topmost = true; ShowInTaskbar = false; ShowActivated = false;
        Content = _canvas;
        SourceInitialized += (_, _) => CaptureExcluded = NativeMethods.ExcludeFromCapture(this);
        Loaded += (_, _) =>
        {
            _monitor = NativeMethods.Monitors().First(m => region.X >= m.Left && region.Y >= m.Top && region.X + region.Width <= m.Right && region.Y + region.Height <= m.Bottom);
            NativeMethods.SetWindowPos(new WindowInteropHelper(this).Handle, -1, _monitor.Left, _monitor.Top, _monitor.Right - _monitor.Left, _monitor.Bottom - _monitor.Top, 0x10);
            SetLocked(true); Draw();
        };
        SizeChanged += (_, _) => Draw();
    }

    internal void ApplySettings(AppSettings settings) { _settings = settings; Draw(); }
    internal void SetLocked(bool locked) { IsLocked = locked; NativeMethods.SetClickThrough(this, locked); Draw(); }
    internal void SetBlocks(IReadOnlyList<SpatialBlock> blocks) { _blocks = blocks; Draw(); }

    private void Draw()
    {
        _canvas.Children.Clear();
        if (ActualWidth <= 0 || ActualHeight <= 0 || _monitor.Right <= _monitor.Left) return;
        var sx = ActualWidth / (_monitor.Right - _monitor.Left); var sy = ActualHeight / (_monitor.Bottom - _monitor.Top);
        var placed = new List<Rect>();
        foreach (var block in _blocks)
        {
            var b = block.Bounds;
            if (!b.IsValid) continue;
            var original = new Rect((_region.X - _monitor.Left + b.X * _region.Width) * sx, (_region.Y - _monitor.Top + b.Y * _region.Height) * sy,
                Math.Max(18, b.Width * _region.Width * sx), Math.Max(18, b.Height * _region.Height * sy));
            var box = original;
            box.Inflate(3, 3);
            if (_settings.OverlayMode == OverlayMode.Nearby)
            {
                var width = Math.Clamp(box.Width, 120, 500); var height = Math.Max(38, box.Height);
                var candidates = new[] { new Rect(original.Right + 8, original.Top, width, height), new Rect(original.Left, original.Bottom + 6, width, height), new Rect(original.Left, original.Top - height - 6, width, height) };
                box = candidates.FirstOrDefault(x => x.Left >= 0 && x.Top >= 0 && x.Right <= ActualWidth && x.Bottom <= ActualHeight && !placed.Any(p => p.IntersectsWith(x)), box);
            }
            box.X = Math.Clamp(box.X, 0, Math.Max(0, ActualWidth - box.Width)); box.Y = Math.Clamp(box.Y, 0, Math.Max(0, ActualHeight - box.Height));
            box.Width = Math.Min(box.Width, ActualWidth - box.X); box.Height = Math.Min(box.Height, ActualHeight - box.Y);
            placed.Add(box);
            var content = block.Error != null ? "翻译失败" : block.Translation ?? "…";
            var text = new TextBlock
            {
                Text = content,
                Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = Math.Clamp(original.Height * .65, 11, _settings.FontSize),
                VerticalAlignment = VerticalAlignment.Center
            };
            var availableWidth = Math.Max(4, box.Width - 8); var availableHeight = Math.Max(4, box.Height - 4);
            text.Measure(new Size(availableWidth, double.PositiveInfinity));
            while (text.DesiredSize.Height > availableHeight && text.FontSize > 10)
            { text.FontSize--; text.Measure(new Size(availableWidth, double.PositiveInfinity)); }
            text.MaxHeight = availableHeight;
            var border = new Border
            {
                Width = box.Width,
                Height = box.Height,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 2, 4, 2),
                ClipToBounds = true,
                Background = new SolidColorBrush(Color.FromArgb((byte)(Math.Clamp(_settings.OverlayOpacity, .35, 1) * 255), 17, 24, 34)),
                BorderBrush = new SolidColorBrush(block.Error != null ? Colors.IndianRed : Color.FromArgb(80, 125, 178, 240)),
                BorderThickness = new Thickness(1),
                Child = text,
                ToolTip = $"文本块 {block.Id}\n{block.Source}\n\n{block.Error ?? block.Translation ?? "翻译中"}"
            };
            if (!IsLocked)
            {
                var menu = new ContextMenu();
                var pick = new MenuItem { Header = "再次划选 · Ctrl+Alt+Q" }; pick.Click += (_, _) => PickRequested?.Invoke(); menu.Items.Add(pick);
                var toggle = new MenuItem { Header = "开始 / 停止固定区域" }; toggle.Click += (_, _) => ToggleRequested?.Invoke(); menu.Items.Add(toggle);
                var clear = new MenuItem { Header = "清除临时译文 · Ctrl+Alt+X" }; clear.Click += (_, _) => ClearRequested?.Invoke(); menu.Items.Add(clear);
                border.ContextMenu = menu;
            }
            Canvas.SetLeft(border, box.Left); Canvas.SetTop(border, box.Top); _canvas.Children.Add(border);
        }
    }
}
