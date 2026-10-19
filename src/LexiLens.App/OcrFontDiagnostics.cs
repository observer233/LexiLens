using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LexiLens.App.Platform;
using LexiLens.Core;
using Windows.Media.Ocr;

namespace LexiLens.App;

internal static class OcrFontDiagnostics
{
    internal static async Task<bool> RunAsync(string directory, OcrEngine? windows)
    {
        const int width = 1000, height = 120;
        const string expected = "Retrieve the ancient relic before dawn.";
        var cases = new[]
        {
            new Sample("sans", "Segoe UI", 30), new Sample("serif-italic", "Georgia", 29, Italic: true),
            new Sample("condensed", "Impact", 29), new Sample("script", "Segoe Script", 27),
            new Sample("brush", "Brush Script MT", 32), new Sample("outlined", "Georgia", 28, Light: true, Outline: true),
            new Sample("small", "Segoe UI", 13, Light: true), new Sample("low-contrast", "Georgia", 24, LowContrast: true)
        };
        var reports = new List<object>();
        var baselineErrors = 0d; var enhancedErrors = 0d; var standardPassed = false;
        using var local = new LocalOcr();
        foreach (var sample in cases)
        {
            var drawing = new DrawingVisual();
            using (var dc = drawing.RenderOpen())
            {
                dc.DrawRectangle(sample.Light ? Brushes.DarkSlateGray : sample.LowContrast ? Brushes.Gray : Brushes.White, null, new Rect(0, 0, width, height));
                var formatted = new FormattedText(expected, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(new FontFamily(sample.Font), sample.Italic ? FontStyles.Italic : FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), sample.Size, Brushes.Black, 1);
                var geometry = formatted.BuildGeometry(new Point(20, 30));
                dc.DrawGeometry(sample.Light ? Brushes.White : sample.LowContrast ? new SolidColorBrush(Color.FromRgb(163, 163, 163)) : Brushes.Black,
                    sample.Outline ? new Pen(Brushes.Black, 2) : null, geometry);
            }
            var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(drawing);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using (var output = File.Create(Path.Combine(directory, "font-" + sample.Name + ".png"))) encoder.Save(output);
            var bytes = new byte[width * height * 4]; image.CopyPixels(bytes, width * 4, 0);
            var baseline = windows == null ? "" : await ScreenTextSource.RecognizeBytesAsync(windows, bytes, width, height, CancellationToken.None);
            var reading = await Task.Run(() => local.Recognize(bytes, width, height, OcrImageMode.Auto, CancellationToken.None));
            baselineErrors += ErrorRate(expected, baseline); enhancedErrors += ErrorRate(expected, reading.Text);
            if (sample.Name == "sans") standardPassed = ErrorRate(expected, reading.Text) == 0;
            reports.Add(new { sample.Name, sample.Font, expected, windows = baseline, enhanced = reading.Text,
                windowsCer = ErrorRate(expected, baseline), enhancedCer = ErrorRate(expected, reading.Text), reading.Confidence, reading.Milliseconds });
        }
        // Blank frames must not become invented words.
        var blank = Enumerable.Repeat((byte)255, width * height * 4).ToArray();
        var empty = await Task.Run(() => local.Recognize(blank, width, height, OcrImageMode.Auto, CancellationToken.None));
        File.WriteAllText(Path.Combine(directory, "font-report.json"), JsonSerializer.Serialize(new { samples = reports, blankResult = empty.Text }, new JsonSerializerOptions { WriteIndented = true }));
        return empty.Text.Length == 0 && standardPassed && (windows == null || enhancedErrors < baselineErrors);
    }

    private static double ErrorRate(string expected, string actual)
    {
        static string Clean(string text) => new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        var a = Clean(expected); var b = Clean(actual); var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var current = new int[b.Length + 1]; current[0] = i;
            for (var j = 1; j <= b.Length; j++) current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            previous = current;
        }
        return previous[b.Length] / (double)Math.Max(1, a.Length);
    }
    private sealed record Sample(string Name, string Font, int Size, bool Italic = false, bool Light = false, bool Outline = false, bool LowContrast = false);
}
