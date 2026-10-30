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
    private CaptureRegion _region;
    private NativeMethods.Rect _monitor;
    private AppSettings _settings;
    private IReadOnlyList<SpatialBlock> _blocks = [];
    internal bool IsLocked { get; private set; } = true;
    internal bool CaptureExcluded { get; private set; }
    internal int BlockCount => _blocks.Count;
    internal int RenderedCount => _canvas.Children.Count;
    internal Action<PipelineEvent>? Progress { get; set; }
    internal Func<SpatialBlock, bool>? IsCurrentBlock { get; set; }
    internal void ResetTelemetry() => _displayed.Clear();
    private readonly HashSet<(int Id, int Revision)> _displayed = [];
    private bool _renderPending;
    private IReadOnlyList<SpatialBlock> _rendered = [];
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
            UpdateMonitor();
            SetLocked(true); Draw();
        };
        SizeChanged += (_, _) => Draw();
        IsVisibleChanged += (_, _) => { if (IsVisible) Draw(); };
        DpiChanged += (_, _) => { UpdateMonitor(); Draw(); };
        Closed += (_, _) => CompositionTarget.Rendering -= OnRendering;
    }

    internal void ApplySettings(AppSettings settings) { _settings = settings; Draw(); }
    internal void SetLocked(bool locked) { IsLocked = locked; NativeMethods.SetClickThrough(this, locked); Draw(); }
    internal void SetBlocks(IReadOnlyList<SpatialBlock> blocks) { _blocks = blocks; Draw(); }
    internal void SetRegion(CaptureRegion region)
    {
        if (region == _region) return;
        _region = region; UpdateMonitor(); Draw();
    }

    private void UpdateMonitor()
    {
        var monitor = NativeMethods.Monitors().OrderByDescending(m => Math.Max(0, Math.Min(m.Right, _region.X + _region.Width) - Math.Max(m.Left, _region.X))
            * (long)Math.Max(0, Math.Min(m.Bottom, _region.Y + _region.Height) - Math.Max(m.Top, _region.Y))).FirstOrDefault();
        if (monitor.Right <= monitor.Left) return;
        _monitor = monitor;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != 0) NativeMethods.SetWindowPos(hwnd, -1, monitor.Left, monitor.Top, monitor.Right - monitor.Left, monitor.Bottom - monitor.Top, 0x10);
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering; _renderPending = false;
        if (!IsVisible || !IsLocked || WindowState == WindowState.Minimized) return;
        if (IsCurrentBlock != null && _blocks.Any(b => !IsCurrentBlock(b)))
        {
            _blocks = _blocks.Where(IsCurrentBlock).ToArray(); Draw(); return;
        }
        foreach (var block in _rendered.Where(x => !string.IsNullOrWhiteSpace(x.Translation) && x.Timing != null))
        {
            if (!_blocks.Contains(block) || IsCurrentBlock?.Invoke(block) == false || !_displayed.Add((block.Id, block.Revision))) continue;
            if (_displayed.Count > 1000) { _displayed.Clear(); _displayed.Add((block.Id, block.Revision)); }
            var timing = block.Timing!;
            Progress?.Invoke(new(PipelineStage.Overlay, PipelineState.Success, "OVERLAY_VISIBLE", "当前修订已提交 WPF 呈现（非光子延迟）", Frame: timing.Frame)
            { CaptureTimestamp = timing.CapturedTimestamp, FrameAgeMilliseconds = timing.AgeMilliseconds, ColdStart = timing.ColdStart,
                DurationMilliseconds = block.ReadyTimestamp > 0 ? FrameTiming.ElapsedMilliseconds(block.ReadyTimestamp) : null,
                VerifiedTimestamp = timing.VerifiedTimestamp, FrameVerificationAgeMilliseconds = timing.VerificationAgeMilliseconds,
                OcrEngine = timing.Engine, FallbackReason = timing.FallbackReason, BlockId = block.Id, Revision = block.Revision });
        }
    }

    private void Draw()
    {
        _canvas.Children.Clear();
        var rendered = new List<SpatialBlock>();
        if (ActualWidth <= 0 || ActualHeight <= 0 || _monitor.Right <= _monitor.Left) return;
        var sx = ActualWidth / (_monitor.Right - _monitor.Left); var sy = ActualHeight / (_monitor.Bottom - _monitor.Top);
        var placed = new List<Rect>();
        foreach (var block in _blocks)
        {
            if (IsCurrentBlock?.Invoke(block) == false) continue;
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
            var clipped = text.DesiredSize.Height > availableHeight;
            if (!string.IsNullOrWhiteSpace(block.Translation) && (clipped || text.FontSize < Math.Clamp(original.Height * .65, 11, _settings.FontSize)))
                Progress?.Invoke(new(PipelineStage.Overlay, PipelineState.Warning, "OVERLAY_LAYOUT_LIMIT", clipped ? "译文超出原位区域，存在裁剪" : "译文已缩小字号以适配原位区域", Frame: block.Timing?.Frame ?? 0)
                { BlockId = block.Id, Revision = block.Revision, CaptureTimestamp = block.Timing?.CapturedTimestamp, FrameAgeMilliseconds = block.Timing?.AgeMilliseconds });
            if (!clipped) rendered.Add(block);
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
        _rendered = rendered;
        if (!_renderPending) { _renderPending = true; CompositionTarget.Rendering += OnRendering; }
    }
}
