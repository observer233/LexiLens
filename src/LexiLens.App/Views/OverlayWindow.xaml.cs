using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using LexiLens.App.Platform;
using LexiLens.Core;

namespace LexiLens.App.Views;

public partial class OverlayWindow : Window
{
    internal bool IsLocked { get; private set; }
    internal bool CaptureExcluded { get; private set; }
    internal event Action<string>? WordClicked;

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
        StatusLabel.Text = "LEXILENS  /  " + status;
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
        if (e.OriginalSource is not System.Windows.Controls.Button && e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
