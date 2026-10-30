using System.Diagnostics;
using System.Runtime.InteropServices;
using LexiLens.Core;
using OpenCvSharp;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models.Local;

namespace LexiLens.App.Platform;

internal sealed record Recognition(string Text, string Engine, double? Confidence, long Milliseconds, IReadOnlyList<RecognizedBlock>? Blocks = null)
{
    public int InputWidth { get; init; }
    public int InputHeight { get; init; }
    public int PreparedWidth { get; init; }
    public int PreparedHeight { get; init; }
    public bool ColdStart { get; init; }
    internal string Summary => $"{Engine} · {Milliseconds} ms" + (Confidence is { } score ? $" · 模型置信度 {score:P0}（非正确率）" : "");
}

// CPU only: the bundled model needs no account, Python, GPU or model download.
// Owned by one session; Run and Dispose must never overlap.
internal sealed class LocalOcr : IDisposable
{
    private PaddleOcrAll? _engine;

    internal Recognition Recognize(byte[] bytes, int width, int height, OcrImageMode mode, CancellationToken ct, bool fast = false)
    {
        var clock = Stopwatch.StartNew();
        ct.ThrowIfCancellationRequested();
        var coldStart = _engine == null;
        if (_engine == null)
        {
            _engine = new PaddleOcrAll(LocalFullModels.ChineseV5, PaddleDevice.OneDnn(cacheCapacity: 2, cpuMathThreadCount: 2))
            { AllowRotateDetection = false, Enable180Classification = false };
            _engine.Detector.MaxSize = 2048;
        }
        using var bgra = new Mat(height, width, MatType.CV_8UC4);
        Marshal.Copy(bytes, 0, bgra.Data, bytes.Length);
        using var color = new Mat();
        Cv2.CvtColor(bgra, color, ColorConversionCodes.BGRA2BGR);
        using var prepared = Prepare(color, mode, fast);
        var result = Read(prepared);
        ct.ThrowIfCancellationRequested();
        // A weak reading gets one second view. Never "correct" text with a language
        // model: proper names and unfamiliar quest words must not be invented.
        if (!fast && mode == OcrImageMode.Auto && result.Confidence < .86)
        {
            using var original = Prepare(color, OcrImageMode.Original);
            var retry = Read(original);
            if (Quality(retry) > Quality(result)) result = retry;
        }
        ct.ThrowIfCancellationRequested();
        return result with { Milliseconds = clock.ElapsedMilliseconds, InputWidth = width, InputHeight = height, ColdStart = coldStart };
    }

    private Recognition Read(Mat image)
    {
        var result = _engine!.Run(image);
        var regions = result.Regions.Where(x => x.Score >= .45 && !string.IsNullOrWhiteSpace(x.Text)).ToArray();
        var total = regions.Sum(x => x.Text.Length);
        var score = total == 0 ? 0 : regions.Sum(x => x.Score * x.Text.Length) / total;
        var boxes = regions.Select(x => AxisAlignedBox(x.Text, x.Rect)).ToArray();
        var text = OcrLineLayout.Join(boxes);
        return new Recognition(text, "PaddleOCR v5 · 本机 CPU", score, 0, ParagraphLayout.Group(boxes, image.Width, image.Height))
        { PreparedWidth = image.Width, PreparedHeight = image.Height };
    }

    internal static OcrTextBox AxisAlignedBox(string text, RotatedRect rect)
    {
        // Size is measured along the rectangle's rotated axes. Paddle commonly
        // returns horizontal text with an angle near 90 degrees, so Size.Height
        // can actually be its screen width. Layout needs the four-corner bounds.
        var points = rect.Points();
        var left = points.Min(p => p.X); var right = points.Max(p => p.X);
        var top = points.Min(p => p.Y); var bottom = points.Max(p => p.Y);
        return new(text, (left + right) / 2, (top + bottom) / 2, bottom - top, right - left);
    }

    private static double Quality(Recognition r) => (r.Confidence ?? 0) * Math.Min(1, r.Text.Count(char.IsLetterOrDigit) / 8d);

    private static Mat Prepare(Mat color, OcrImageMode mode, bool fast = false)
    {
        using var scaled = new Mat();
        // Keep ROI glyph pixels at native size up to the detector's 2048px cap.
        // The old fast cap of 1280 shrank even a 1920px subtitle crop a second time.
        var scale = Math.Min(fast ? 1.5 : 2, 2048d / Math.Max(color.Width, color.Height));
        Cv2.Resize(color, scaled, new Size(), scale, scale, InterpolationFlags.Cubic);
        var result = new Mat();
        if (mode == OcrImageMode.Original) scaled.CopyTo(result);
        else
        {
            using var gray = new Mat();
            Cv2.CvtColor(scaled, gray, ColorConversionCodes.BGR2GRAY);
            using var contrast = Cv2.CreateCLAHE(2, new Size(8, 8));
            contrast.Apply(gray, result);
            if (mode == OcrImageMode.LightOnDark || (mode == OcrImageMode.Auto && Cv2.Mean(gray).Val0 < 110))
                Cv2.BitwiseNot(result, result);
            if (mode == OcrImageMode.LowContrast) Cv2.Normalize(result, result, 0, 255, NormTypes.MinMax);
        }
        return result;
    }

    public void Dispose() { _engine?.Dispose(); _engine = null; }
}
