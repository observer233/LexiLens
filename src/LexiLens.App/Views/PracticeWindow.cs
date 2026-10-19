using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LexiLens.App.Views;

internal sealed class PracticeWindow : Window
{
    internal static readonly (string Text, string Translation)[] Samples =
    [
        ("Find the ancient relic before the enemy arrives.", "在敌人到达前找到古代遗物。"),
        ("Gather three healing herbs near the abandoned tower.", "在废弃的高塔附近收集三株治疗草药。"),
        ("Your inventory is full. Return to the merchant.", "你的物品栏已满。返回商人处。"),
        ("We must reach the outpost before dawn.", "我们必须在黎明前到达前哨站。")
    ];
    private int _index;

    internal PracticeWindow()
    {
        Style = (Style)Application.Current.FindResource(typeof(Window));
        Title = "LexiLens 练习场 · 可用于真实屏幕 OCR";
        Width = 900; Height = 420; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(22, 30, 34));
        var panel = new StackPanel { Margin = new Thickness(44) };
        panel.Children.Add(new TextBlock { Text = "THE FORGOTTEN VALLEY", FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(233, 189, 130)), Margin = new Thickness(0, 0, 0, 22) });
        panel.Children.Add(new TextBlock { Text = "QUEST OBJECTIVE", FontSize = 13, Foreground = Brushes.LightGray, Margin = new Thickness(0, 0, 0, 12) });
        var subtitle = new TextBlock { Text = Samples[0].Text, FontFamily = new FontFamily("Segoe UI"), FontSize = 30, Foreground = Brushes.White, MinHeight = 100 };
        panel.Children.Add(subtitle);
        var next = new Button { Content = "下一句  →", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 22, 0, 15) };
        next.Click += (_, _) => subtitle.Text = Samples[++_index % Samples.Length].Text;
        panel.Children.Add(next);
        panel.Children.Add(new TextBlock { Text = "按 Ctrl+Alt+R 框选英文区域，再按 Ctrl+Alt+T 开始翻译。", Foreground = Brushes.LightGray, FontSize = 12 });
        Content = panel;
    }
}
