using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using LexiLens.App.Platform;
using LexiLens.Core;

namespace LexiLens.App.Views;

public partial class OverlayWindow : Window
{
    internal bool IsLocked { get; private set; }
    internal bool CaptureExcluded { get; private set; }
    internal event Action<string>? WordClicked;
    internal string AreaName { get; set; } = "";
    internal OverlayPlacement Placement => new(Left, Top, Width);
    private bool _userPositioned;

    public OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => CaptureExcluded = NativeMethods.ExcludeFromCapture(this);
        Left = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - Width) / 2;
        Top = SystemParameters.WorkArea.Bottom - 290;
    }

    internal void ApplySettings(AppSettings settings)
    {
        Opacity = settings.OverlayOpacity;
        OriginalText.FontSize = settings.FontSize;
        TranslationText.FontSize = settings.FontSize;
        OriginalText.LineHeight = settings.FontSize * 1.55;
        TranslationText.LineHeight = settings.FontSize * 1.55;
        OriginalText.Visibility = settings.ShowOriginal ? Visibility.Visible : Visibility.Collapsed;
        MaxHeight = settings.CaptureAreas().Count > 1 ? 380 : 600;
        ContentScroll.MaxHeight = MaxHeight - 105;
    }

    internal void SetContent(string original, string translation, string status)
    {
        OriginalText.Inlines.Clear();
        foreach (Match match in Regex.Matches(original, @"[A-Za-z]+(?:['’-][A-Za-z]+)*|[^A-Za-z]+"))
        {
            var run = new Run(match.Value);
            if (char.IsAsciiLetter(match.Value[0]))
            {
                var word = match.Value;
                run.Cursor = Cursors.Hand;
                run.ToolTip = "点击查看并收藏 " + word;
                run.MouseLeftButtonDown += (_, e) => { WordClicked?.Invoke(word); e.Handled = true; };
            }
            OriginalText.Inlines.Add(run);
        }
        TranslationText.Text = translation;
        StatusLabel.Text = (AreaName.Length > 0 ? AreaName + " · " : "") + status;
    }

    internal void PlaceNear(CaptureRegion region, OverlayPlacement? saved)
    {
        Width = Math.Clamp(region.Width * .8, 320, 620);
        if (saved?.IsValid == true)
        {
            Left = saved.Left; Top = saved.Top; Width = saved.Width;
            UpdateLayout();
            NativeMethods.GetWindowRect(new WindowInteropHelper(this).Handle, out var bounds);
            if (NativeMethods.Monitors().Any(m => bounds.Left >= m.Left && bounds.Top >= m.Top && bounds.Right <= m.Right && bounds.Bottom <= m.Bottom)) { _userPositioned = true; return; }
        }
        var monitor = NativeMethods.Monitors().FirstOrDefault(m => region.X >= m.Left && region.Y >= m.Top && region.X < m.Right && region.Y < m.Bottom);
        UpdateLayout();
        NativeMethods.GetWindowRect(new WindowInteropHelper(this).Handle, out var rect);
        var width = rect.Right - rect.Left; var height = Math.Max(180, rect.Bottom - rect.Top);
        var x = region.X + region.Width + 12; var y = region.Y;
        if (x + width > monitor.Right)
        {
            x = region.X;
            y = region.Y + region.Height + 12;
            if (y + height > monitor.Bottom) y = region.Y - height - 12;
        }
        x = Math.Clamp(x, monitor.Left, Math.Max(monitor.Left, monitor.Right - width));
        y = Math.Clamp(y, monitor.Top, Math.Max(monitor.Top, monitor.Bottom - height));
        NativeMethods.SetWindowPos(new WindowInteropHelper(this).Handle, 0, x, y, 0, 0, 0x15);
    }

    internal void AvoidOverlaps(IEnumerable<OverlayWindow> others)
    {
        if (_userPositioned) return;
        UpdateLayout();
        var handle = new WindowInteropHelper(this).Handle;
        NativeMethods.GetWindowRect(handle, out var rect);
        var occupied = others.Where(x => x != this && x.IsVisible).Select(x =>
        { x.UpdateLayout(); NativeMethods.GetWindowRect(new WindowInteropHelper(x).Handle, out var r); return r; }).ToArray();
        static bool Overlaps(NativeMethods.Rect a, NativeMethods.Rect b) => a.Left < b.Right + 8 && a.Right + 8 > b.Left && a.Top < b.Bottom + 8 && a.Bottom + 8 > b.Top;
        if (!occupied.Any(x => Overlaps(rect, x))) return;
        var monitor = NativeMethods.Monitors().FirstOrDefault(m => rect.Left >= m.Left && rect.Left < m.Right && rect.Top >= m.Top && rect.Top < m.Bottom);
        var width = rect.Right - rect.Left; var height = rect.Bottom - rect.Top;
        var xs = occupied.SelectMany(x => new[] { x.Right + 12, x.Left - width - 12 }).Append(monitor.Left + 12).Append(monitor.Right - width - 12).Append(rect.Left);
        var ys = occupied.SelectMany(x => new[] { x.Bottom + 12, x.Top - height - 12 }).Append(monitor.Top + 12).Append(monitor.Bottom - height - 12).Append(rect.Top);
        var candidate = (from x in xs from y in ys
            let box = new NativeMethods.Rect { Left = x, Top = y, Right = x + width, Bottom = y + height }
            where box.Left >= monitor.Left && box.Top >= monitor.Top && box.Right <= monitor.Right && box.Bottom <= monitor.Bottom && !occupied.Any(r => Overlaps(box, r))
            orderby Math.Abs((long)x - rect.Left) + Math.Abs((long)y - rect.Top)
            select (x, y)).FirstOrDefault((rect.Left, rect.Top));
        NativeMethods.SetWindowPos(handle, 0, candidate.Item1, candidate.Item2, 0, 0, 0x15);
    }

    internal void SetLocked(bool locked)
    {
        IsLocked = locked;
        NativeMethods.SetClickThrough(this, locked);
        LockButton.Content = locked ? "已穿透 · Ctrl+Alt+L 解锁" : "锁定穿透  Ctrl+Alt+L";
        HintText.Text = locked ? "鼠标操作直接传给游戏 · Ctrl+Alt+L 解锁查词" : "拖动顶部移动 · 点击英文查词 · Ctrl+Alt+H 隐藏";
    }

    private void ToggleLock(object sender, RoutedEventArgs e) => SetLocked(!IsLocked);
    private void DragHeader(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not System.Windows.Controls.Button && e.LeftButton == MouseButtonState.Pressed) { DragMove(); _userPositioned = true; }
    }
}
