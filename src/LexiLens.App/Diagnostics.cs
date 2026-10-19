using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LexiLens.App.Platform;
using LexiLens.App.Views;
using LexiLens.Core;
using Windows.Globalization;
using Windows.Media.Ocr;

namespace LexiLens.App;

internal static class Diagnostics
{
    internal static async Task RunAsync(MainWindow window, string directory)
    {
        Directory.CreateDirectory(directory);
        var report = new Dictionary<string, object?>();
        try
        {
            await Task.Delay(250);
            Render(window, Path.Combine(directory, "main.png"));
            report["mainRendered"] = true;
            window.RenderSettingsForDiagnostics();
            Render(window, Path.Combine(directory, "settings.png"));
            window.RenderLiveForDiagnostics();
            var overlay = new OverlayWindow();
            overlay.SetContent(PracticeWindow.Samples[0].Text, PracticeWindow.Samples[0].Translation, "示例预览 · 非实时识别");
            overlay.Show();
            await Task.Delay(150);
            Render(overlay, Path.Combine(directory, "overlay.png"));
            report["overlayRendered"] = true;
            report["captureExclusion"] = overlay.CaptureExcluded;
            overlay.SetLocked(true);
            report["clickThrough"] = (NativeMethods.GetWindowLong(new WindowInteropHelper(overlay).Handle, -20) & 0x20) != 0;
            overlay.SetLocked(false);
            report["clickThroughUnlock"] = (NativeMethods.GetWindowLong(new WindowInteropHelper(overlay).Handle, -20) & 0x20) == 0;
            overlay.Close();

            var languages = ScreenTextSource.Languages();
            report["ocrLanguages"] = languages.Select(x => x.Tag).ToArray();
            var engine = OcrEngine.TryCreateFromLanguage(new Language("en-US"));
            report["englishOcrAvailable"] = engine != null;
            if (engine != null)
            {
                var text = new TextBlock { Text = "Find the ancient relic before dawn.", FontFamily = new FontFamily("Segoe UI"), FontSize = 34, Foreground = Brushes.Black, Background = Brushes.White, Padding = new Thickness(20) };
                text.Measure(new Size(1000, 100)); text.Arrange(new Rect(0, 0, 1000, 100));
                var image = new RenderTargetBitmap(1000, 100, 96, 96, PixelFormats.Pbgra32);
                image.Render(text);
                var pixels = new byte[1000 * 100 * 4]; image.CopyPixels(pixels, 4000, 0);
                var recognized = await ScreenTextSource.RecognizeBytesAsync(engine, pixels, 1000, 100, CancellationToken.None);
                report["generatedImageOcr"] = recognized;
                report["generatedImageOcrPassed"] = recognized.Contains("ancient relic", StringComparison.OrdinalIgnoreCase);

                var practice = new PracticeWindow(); practice.Show(); practice.Activate();
                await Task.Delay(500);
                NativeMethods.GetWindowRect(new WindowInteropHelper(practice).Handle, out var bounds);
                var source = new ScreenTextSource(new CaptureRegion(bounds.Left + 20, bounds.Top + 40, bounds.Right - bounds.Left - 40, bounds.Bottom - bounds.Top - 60), "en-US");
                var captured = await source.ReadAsync(CancellationToken.None);
                report["desktopCaptureOcr"] = captured;
                report["desktopCaptureOcrPassed"] = TextProcessing.Normalize(captured).Contains("ancient relic", StringComparison.OrdinalIgnoreCase);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(55));
                var live = await window.VerifyLiveSessionAsync(new CaptureRegion(bounds.Left + 20, bounds.Top + 40, bounds.Right - bounds.Left - 40, bounds.Bottom - bounds.Top - 60), timeout.Token);
                report["liveSource"] = live.Source;
                report["liveTranslation"] = live.Translation;
                report["liveSessionPassed"] = live.Source.Contains("ancient relic", StringComparison.OrdinalIgnoreCase)
                    && live.Translation.Any(c => c >= '\u4E00' && c <= '\u9FFF')
                    && !live.Translation.Contains("未完成");
                practice.Close();
            }
            report["success"] = report.GetValueOrDefault("desktopCaptureOcrPassed") is true
                && report.GetValueOrDefault("generatedImageOcrPassed") is true
                && report.GetValueOrDefault("clickThrough") is true
                && report.GetValueOrDefault("clickThroughUnlock") is true
                && report.GetValueOrDefault("liveSessionPassed") is true;
        }
        catch (Exception ex) { report["error"] = ex.ToString(); report["success"] = false; }
        finally
        {
            File.WriteAllText(Path.Combine(directory, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            window.Close();
        }
    }

    private static void Render(Window window, string path)
    {
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var image = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
