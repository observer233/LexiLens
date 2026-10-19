using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LexiLens.App.Views;

internal sealed class RecognitionPreviewWindow : Window
{
    internal RecognitionPreviewWindow(byte[] bytes, int width, int height, string text, string summary)
    {
        Title = "LexiLens · 检查识别"; Width = 800; Height = 600; MinWidth = 500; MinHeight = 400;
        Style = (Style)FindResource(typeof(Window));
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var grid = new Grid { Margin = new Thickness(22) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var label = new TextBlock { Text = summary + "\n仅显示框选截图和原始识别结果；本次检查不会上传文字或截图。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        grid.Children.Add(label);
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, bytes, width * 4); bitmap.Freeze();
        var image = new Image { Source = bitmap, Stretch = Stretch.Uniform };
        var scroll = new ScrollViewer { Content = image, Margin = new Thickness(0, 0, 0, 16) };
        Grid.SetRow(scroll, 1); grid.Children.Add(scroll);
        var result = new TextBox { Text = text.Length > 0 ? text : "（未识别到文字，可尝试更紧的选区或其他图像方式）", IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 18 };
        Grid.SetRow(result, 2); grid.Children.Add(result);
        var root = new Grid { Background = (Brush)FindResource("Bg") }; root.Children.Add(grid); Content = root;
        SourceInitialized += (_, _) => Platform.NativeMethods.ExcludeFromCapture(this);
    }
}
