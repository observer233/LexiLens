using LexiLens.Core;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace LexiLens.App.Platform;

internal sealed class ScreenTextSource : ITextSource
{
    private readonly CaptureRegion _region;
    private readonly OcrEngine _engine;

    internal ScreenTextSource(CaptureRegion region, string language)
    {
        _region = region;
        _engine = OcrEngine.TryCreateFromLanguage(new Language(language))
            ?? throw new InvalidOperationException($"未安装 {language} 的 Windows OCR 语言包。请在 Windows 设置 → 时间和语言 → 语言和区域中添加对应语言及其光学字符识别功能，然后重启本应用。");
    }

    internal static IReadOnlyList<(string Tag, string Name)> Languages() => OcrEngine.AvailableRecognizerLanguages
        .Select(x => (x.LanguageTag, $"{x.NativeName} · {x.LanguageTag}")).ToList();

    public Task<string> ReadAsync(CancellationToken cancellationToken) => Task.Run(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!NativeMethods.IsOnDesktop(_region)) throw new InvalidOperationException("屏幕布局发生变化，请重新框选识别区域。");
        var bytes = NativeMethods.Capture(_region, (int)Math.Min(OcrEngine.MaxImageDimension, 2560), out var w, out var h);
        return await RecognizeBytesAsync(_engine, bytes, w, h, cancellationToken);
    }, cancellationToken);

    internal static async Task<string> RecognizeBytesAsync(OcrEngine engine, byte[] bytes, int width, int height, CancellationToken ct)
    {
        using var writer = new DataWriter();
        writer.WriteBytes(bytes);
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(writer.DetachBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Ignore);
        var result = await engine.RecognizeAsync(bitmap).AsTask(ct);
        return string.Join("\n", result.Lines.Select(line => line.Text));
    }
}
