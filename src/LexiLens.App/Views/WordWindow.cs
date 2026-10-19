using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LexiLens.App.Platform;
using LexiLens.Core;

namespace LexiLens.App.Views;

internal sealed class WordWindow : Window
{
    internal WordWindow(string word, string context, ITranslator translator, Action<VocabularyEntry> save)
    {
        Style = (Style)Application.Current.FindResource(typeof(Window));
        Title = "LexiLens · " + word; Width = 450; Height = 370; Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; ResizeMode = ResizeMode.CanMinimize;
        var panel = new StackPanel { Margin = new Thickness(26) };
        panel.Children.Add(new TextBlock { Text = word, FontSize = 30, Foreground = new SolidColorBrush(Color.FromRgb(233, 189, 130)), Margin = new Thickness(0, 0, 0, 12) });
        var meaning = new TextBox { Text = GameGlossary.Lookup(word) ?? "", TextWrapping = TextWrapping.Wrap, Height = 64, AcceptsReturn = true };
        panel.Children.Add(meaning);
        var hint = new TextBlock { Text = meaning.Text.Length > 0 ? "内置游戏词汇释义 · 可编辑" : "正在通过当前翻译服务查词…", Foreground = Brushes.DarkGray, FontSize = 11, Margin = new Thickness(0, 7, 0, 14) };
        panel.Children.Add(hint);
        panel.Children.Add(new TextBlock { Text = context, FontSize = 13, MaxHeight = 60, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 16) });
        var add = new Button { Content = "＋ 收藏到生词本", Style = (Style)Application.Current.FindResource("Primary") };
        add.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(meaning.Text)) { hint.Text = "请填写释义后收藏。"; return; }
            save(new VocabularyEntry(word.ToLowerInvariant(), meaning.Text.Trim(), context, DateTimeOffset.Now));
            Close();
        };
        panel.Children.Add(add); Content = panel;
        SourceInitialized += (_, _) => NativeMethods.ExcludeFromCapture(this);
        var cancellation = new CancellationTokenSource();
        Closed += (_, _) => cancellation.Cancel();
        if (meaning.Text.Length == 0) Loaded += async (_, _) =>
        {
            try
            {
                var result = await translator.TranslateAsync(word, cancellation.Token);
                if (!cancellation.IsCancellationRequested && meaning.Text.Length == 0) meaning.Text = result;
                hint.Text = "当前服务返回的单词译文 · 可补充释义";
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception ex) { hint.Text = LiveSession.FriendlyError(ex) + " 也可手动填写。"; }
        };
    }
}
