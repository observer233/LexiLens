using LexiLens.Core;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace LexiLens.App.Platform;

internal sealed class ScreenTextSource : ITextSource, IDisposable
{
    private readonly CaptureRegion _region;
    private readonly OcrEngine? _engine;
    private readonly OcrMode _mode;
    private readonly OcrImageMode _imageMode;
    private LocalOcr? _local;
    private byte[]? _lastBytes;
    private string _lastText = "";
    private bool _localFailed;
    internal Recognition? LastRecognition { get; private set; }
    internal (byte[] Bytes, int Width, int Height)? LastFrame { get; private set; }
    internal Action<string>? Status { get; set; }
    private readonly string? _expectedDiagnosticText;

    internal ScreenTextSource(CaptureRegion region, string language, string? expectedDiagnosticText = null,
        OcrMode mode = OcrMode.Enhanced, OcrImageMode imageMode = OcrImageMode.Auto)
    {
        _region = region;
        _expectedDiagnosticText = expectedDiagnosticText;
        _mode = mode; _imageMode = imageMode;
        _engine = OcrEngine.TryCreateFromLanguage(new Language(language));
        if (mode == OcrMode.Enhanced && (language.StartsWith("en", StringComparison.OrdinalIgnoreCase) || language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)))
            _local = new LocalOcr();
        else if (_engine == null)
            throw new InvalidOperationException($"未安装 {language} 的 Windows OCR 语言包。请在 Windows 设置 → 时间和语言 → 语言和区域中添加对应语言及其光学字符识别功能，或为英文/中文选择本地增强识别。");
    }

    internal static IReadOnlyList<(string Tag, string Name)> Languages() => OcrEngine.AvailableRecognizerLanguages
        .Select(x => (x.LanguageTag, $"{x.NativeName} · {x.LanguageTag}")).ToList();

    public Task<string> ReadAsync(CancellationToken cancellationToken) => Task.Run(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!NativeMethods.IsOnDesktop(_region)) throw new InvalidOperationException("屏幕布局发生变化，请重新框选识别区域。");
        var bytes = NativeMethods.Capture(_region, (int)Math.Min(OcrEngine.MaxImageDimension, 2560), out var w, out var h);
        LastFrame = (bytes, w, h);
        if (_lastBytes != null && bytes.AsSpan().SequenceEqual(_lastBytes)) return _lastText;
        string text;
        if (_local != null && !_localFailed)
        {
            try { LastRecognition = _local.Recognize(bytes, w, h, _imageMode, cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (_engine == null) throw new InvalidOperationException("本地增强引擎启动失败，且没有可用的 Windows OCR。请完整解压发布包；在偏好设置打开微软 VC++ x64 运行库页面安装后重启，或安装对应 Windows OCR 语言功能。", ex);
                _localFailed = true;
                Status?.Invoke("本地增强引擎不可用，已切换 Windows OCR。可重新开始重试；请检查解压完整性和 VC++ x64 运行库。");
            }
        }
        if (_local == null || _localFailed)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            text = await RecognizeBytesAsync(_engine!, bytes, w, h, cancellationToken);
            LastRecognition = new Recognition(text, "Windows OCR" + (_localFailed ? " · 增强引擎加载失败" : _mode == OcrMode.Enhanced ? " · 当前语言" : ""), null, clock.ElapsedMilliseconds);
        }
        text = LastRecognition!.Text;
        Status?.Invoke(LastRecognition.Summary + (_localFailed ? " · 请检查解压完整性或安装 VC++ x64 运行库" : ""));
        if (_expectedDiagnosticText != null && TextProcessing.Normalize(text) != TextProcessing.Normalize(_expectedDiagnosticText))
            throw new InvalidOperationException("诊断练习文字被遮挡或识别不符，已停止，未将该内容发送翻译服务。");
        _lastBytes = bytes; _lastText = text;
        return text;
    }, cancellationToken);

    internal static async Task<string> RecognizeBytesAsync(OcrEngine engine, byte[] bytes, int width, int height, CancellationToken ct)
    {
        using var writer = new DataWriter();
        writer.WriteBytes(bytes);
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(writer.DetachBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Ignore);
        var result = await engine.RecognizeAsync(bitmap).AsTask(ct);
        return string.Join("\n", result.Lines.Select(line => line.Text));
    }
    public void Dispose() { _local?.Dispose(); _local = null; _lastBytes = null; LastFrame = null; }
}
