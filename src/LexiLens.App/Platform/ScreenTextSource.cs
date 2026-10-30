using LexiLens.Core;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using System.Threading.Channels;
using System.Diagnostics;

namespace LexiLens.App.Platform;

internal sealed class ScreenTextSource : IBlockTextSource, IFrameTimingSource, IDisposable, IAsyncDisposable
{
    private readonly CaptureRegion _region;
    private readonly OcrEngine? _engine;
    private readonly OcrMode _mode;
    private readonly OcrImageMode _imageMode;
    private OcrWorker? _local;
    private long _frame;
    private byte[]? _lastBytes;
    private int _lastWidth, _lastHeight;
    private string _lastText = "";
    private bool _localFailed;
    private string _fallbackReason = "";
    private string _localFailureReason = "", _lastFallbackReason = "";
    private readonly bool _fastMode;
    private readonly int _captureInterval;
    private Channel<CapturedRegionFrame>? _frames;
    private readonly IRegionCapture? _capture;
    private long _captureSequence;
    private readonly string _windowsReason;
    private CancellationTokenSource? _captureCancellation;
    private Task? _captureTask;
    private readonly object _disposeSync = new();
    private Task? _disposeTask;
    private int _disposed;
    private long _droppedFrames;
    internal Recognition? LastRecognition { get; private set; }
    internal (byte[] Bytes, int Width, int Height)? LastFrame { get; private set; }
    internal Action<string>? Status { get; set; }
    internal Action<PipelineEvent>? Progress { get; set; }
    internal Func<bool>? Paused { get; set; }
    internal Func<RecognizedBlock, bool>? IncludeBlock { get; set; }
    public FrameTiming? CurrentFrame { get; private set; }
    internal CaptureRegion CurrentRegion { get; private set; }
    internal event Action<CapturedRegionFrame>? FrameCaptured;
    internal event Action<IReadOnlyList<RecognizedBlock>>? BlocksRead;
    private readonly string? _expectedDiagnosticText;

    internal ScreenTextSource(CaptureRegion region, string language, string? expectedDiagnosticText = null,
        OcrMode mode = OcrMode.Enhanced, OcrImageMode imageMode = OcrImageMode.Auto, Func<OcrWorker>? workerFactory = null,
        bool fastMode = false, int captureInterval = 150, IRegionCapture? capture = null, bool allowUnsupportedWindowsOcr = false)
    {
        _region = region; CurrentRegion = region; _capture = capture;
        _expectedDiagnosticText = expectedDiagnosticText;
        _mode = mode; _imageMode = imageMode;
        _fastMode = fastMode; _captureInterval = Math.Clamp(captureInterval, 100, 1000);
        _engine = WindowsOcrSupport.TryCreate(language, out _windowsReason, allowUnsupportedWindowsOcr);
        if (mode == OcrMode.Enhanced && (language.StartsWith("en", StringComparison.OrdinalIgnoreCase) || language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)))
            _local = workerFactory?.Invoke() ?? new OcrWorker();
        else if (_engine == null)
            throw new InvalidOperationException($"Windows OCR 不可用（{_windowsReason}）。便携版请使用英文/中文本地增强识别；Windows OCR 官方要求 MSIX 包身份及对应 OCR 语言功能。");
    }

    internal static IReadOnlyList<(string Tag, string Name)> Languages() => WindowsOcrSupport.Languages()
        .Select(x => (x.LanguageTag, $"{x.NativeName} · {x.LanguageTag}")).ToList();

    public Task<string> ReadAsync(CancellationToken cancellationToken) => Task.Run(async () =>
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        while (Paused?.Invoke() == true) await Task.Delay(50, cancellationToken);
        Interlocked.Increment(ref _frame);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var waitStarted = Stopwatch.GetTimestamp();
        var captured = _fastMode ? await ReadBufferedFrameAsync(cancellationToken) : await CaptureFrameAsync(cancellationToken);
        var frame = captured.Sequence;
        var bytes = captured.Bytes; var w = captured.Width; var h = captured.Height;
        CurrentRegion = captured.ScreenRegion;
        CurrentFrame = new(frame, captured.CapturedTimestamp, _frame == 1) { CaptureStartedTimestamp = waitStarted, FallbackReason = _windowsReason,
            VerifiedTimestamp = captured.VerifiedTimestamp > 0 ? captured.VerifiedTimestamp : captured.CapturedTimestamp };
        Emit(PipelineStage.Capture, PipelineState.Success, "FRAME_DEQUEUE", "帧就绪至识别开始", FrameTiming.ElapsedMilliseconds(waitStarted), captured.Backend);
        LastFrame = (bytes, w, h);
        if (IsNearlyBlack(bytes)) Progress?.Invoke(new(PipelineStage.Capture, PipelineState.Warning, "CAPTURE_DARK", "截图接近全黑；检查窗口遮挡或切换无边框模式", Frame: frame));
        if (_lastBytes != null && w == _lastWidth && h == _lastHeight && bytes.AsSpan().SequenceEqual(_lastBytes))
        {
            CurrentFrame = CurrentFrame with { Engine = LastRecognition?.Engine, FallbackReason = _lastFallbackReason, OcrCompletedTimestamp = Stopwatch.GetTimestamp() };
            Emit(PipelineStage.Ocr, PipelineState.Skipped, "OCR_FRAME_CACHE", "ROI 像素未变化，复用识别结果", 0);
            return _lastText;
        }
        var ocrStarted = Stopwatch.GetTimestamp();
        _fallbackReason = _localFailed ? _localFailureReason : "";
        if (_frame == 1 && _windowsReason.Length > 0)
            Emit(PipelineStage.Ocr, PipelineState.Warning, "WINDOWS_OCR_SUPPORT", "Windows OCR：" + _windowsReason, 0);
        Progress?.Invoke(new(PipelineStage.Ocr, PipelineState.Running, "OCR_START", _fastMode && _engine != null ? "Windows OCR 快速识别中" : _local != null && !_localFailed ? "增强识别中，首次加载最多等待 15 秒" : "Windows OCR 识别中", Frame: frame));
        string text;
        // Windows OCR usually returns small subtitle crops much sooner. Enhanced
        // inference remains the fallback; accuracy mode keeps the original order.
        var usedFastWindows = false;
        if (_fastMode && _engine != null)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var quick = await RecognizeDetailedAsync(_engine, bytes, w, h, cancellationToken);
                // An empty quick reading is not evidence that the image has no
                // text. Enhanced mode must still get its fallback attempt.
                if (!string.IsNullOrWhiteSpace(quick.Text) || _local == null || _localFailed)
                { LastRecognition = quick with { Engine = "Windows OCR · 快速字幕", Milliseconds = clock.ElapsedMilliseconds }; usedFastWindows = true; }
                else _fallbackReason = "windows_empty_enhanced_retry";
            }
            catch (Exception ex) when (ex is not OperationCanceledException && _local != null) { _fallbackReason = "windows_failed_enhanced_retry"; }
        }
        if (!usedFastWindows && _local != null && !_localFailed)
        {
            try
            {
                LastRecognition = await _local.ReadAsync(bytes, w, h, _imageMode, cancellationToken, _fastMode);
                if (string.IsNullOrWhiteSpace(LastRecognition.Text) && _engine != null)
                {
                    _fallbackReason = "enhanced_empty_windows_retry";
                    var alternative = await RecognizeDetailedAsync(_engine, bytes, w, h, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(alternative.Text))
                    {
                        LastRecognition = alternative with { Engine = "Windows OCR · 增强结果为空", Milliseconds = watch.ElapsedMilliseconds };
                        Progress?.Invoke(new(PipelineStage.Ocr, PipelineState.Warning, "OCR_EMPTY_FALLBACK", "增强结果为空，Windows OCR 已识别到文字", Characters: alternative.Text.Length, Frame: frame));
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (_engine == null) throw new InvalidOperationException("本地增强引擎启动失败，且没有可用的 Windows OCR。请完整解压发布包；在偏好设置打开微软 VC++ x64 运行库页面安装后重启，或安装对应 Windows OCR 语言功能。", ex);
                _localFailed = true;
                _localFailureReason = _fallbackReason = ex is TimeoutException ? "enhanced_timeout_windows_fallback" : "enhanced_failed_windows_fallback";
                Progress?.Invoke(new(PipelineStage.Ocr, PipelineState.Warning, ex is TimeoutException ? "OCR_TIMEOUT_FALLBACK" : "OCR_ENGINE_FALLBACK", "增强引擎异常，已切换 Windows OCR", watch.ElapsedMilliseconds, Frame: frame));
                Status?.Invoke("本地增强引擎不可用，已切换 Windows OCR。可重新开始重试；请检查解压完整性和 VC++ x64 运行库。");
            }
        }
        if (!usedFastWindows && (_local == null || _localFailed))
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            LastRecognition = await RecognizeDetailedAsync(_engine!, bytes, w, h, cancellationToken);
            LastRecognition = LastRecognition with { Engine = "Windows OCR" + (_localFailed ? " · 增强不可用回退" : _mode == OcrMode.Enhanced ? " · 当前语言" : ""), Milliseconds = clock.ElapsedMilliseconds };
        }
        text = LastRecognition!.Text;
        CurrentFrame = CurrentFrame with { Engine = LastRecognition.Engine, FallbackReason = _fallbackReason.Length > 0 ? _fallbackReason : _windowsReason,
            ColdStart = _frame == 1 || LastRecognition.ColdStart, OcrCompletedTimestamp = Stopwatch.GetTimestamp() };
        Emit(PipelineStage.Ocr, text.Length == 0 ? PipelineState.Warning : PipelineState.Success, text.Length == 0 ? "OCR_EMPTY" : "OCR_OK",
            $"输入 {w}×{h} → 预处理 {LastRecognition.PreparedWidth}×{LastRecognition.PreparedHeight}", FrameTiming.ElapsedMilliseconds(ocrStarted));
        Status?.Invoke(LastRecognition.Summary);
        if (_expectedDiagnosticText != null && !string.IsNullOrWhiteSpace(text) && TextProcessing.Normalize(text) != TextProcessing.Normalize(_expectedDiagnosticText))
            throw new InvalidOperationException("诊断练习文字被遮挡或识别不符，已停止，未将该内容发送翻译服务。");
        _lastBytes = bytes; _lastText = text; _lastWidth = w; _lastHeight = h;
        _lastFallbackReason = CurrentFrame.FallbackReason ?? "";
        return text;
    }, cancellationToken);

    public async Task<IReadOnlyList<RecognizedBlock>> ReadBlocksAsync(CancellationToken ct)
    {
        var text = await ReadAsync(ct);
        var blocks = LastRecognition?.Blocks ?? (string.IsNullOrWhiteSpace(text) ? [] : [new(text, new(0, 0, 1, 1))]);
        var included = IncludeBlock == null ? blocks : blocks.Where(IncludeBlock).ToArray();
        BlocksRead?.Invoke(included);
        return included;
    }

    private void Emit(PipelineStage stage, PipelineState state, string code, string message, double ms, string? backend = null)
    {
        Progress?.Invoke(new(stage, state, code, message, (long)ms, LastRecognition?.Text.Length ?? 0, CurrentFrame?.Frame ?? 0)
        { DurationMilliseconds = ms, CaptureTimestamp = CurrentFrame?.CapturedTimestamp, FrameAgeMilliseconds = CurrentFrame?.AgeMilliseconds,
            VerifiedTimestamp = CurrentFrame?.VerifiedTimestamp, FrameVerificationAgeMilliseconds = CurrentFrame?.VerificationAgeMilliseconds,
            ColdStart = CurrentFrame?.ColdStart, OcrEngine = CurrentFrame?.Engine, FallbackReason = CurrentFrame?.FallbackReason,
            DroppedCount = Interlocked.Read(ref _droppedFrames), QueueLength = _frames?.Reader.CanCount == true ? _frames.Reader.Count : 0 });
    }

    private async ValueTask<CapturedRegionFrame> CaptureFrameAsync(CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        CapturedRegionFrame result;
        if (_capture != null) result = await _capture.CaptureAsync(ct);
        else
        {
            if (!NativeMethods.IsOnDesktop(_region)) throw new InvalidOperationException("屏幕布局发生变化，请重新框选识别区域。");
            // Capture the selected ROI at native resolution. Never reduce the
            // whole game window before cropping subtitle pixels.
            var bytes = NativeMethods.Capture(_region, 16384, out var w, out var h);
            result = new(bytes, w, h, _region, started, 0, FrameTiming.ElapsedMilliseconds(started), "Gdi");
        }
        var sequence = Interlocked.Increment(ref _captureSequence);
        // Verification belongs to producer acquisition, not consumer dequeue.
        // A buffered retained WGC surface must age while waiting for OCR too.
        var sampledAt = Stopwatch.GetTimestamp();
        if (result.CapturedTimestamp > sampledAt)
            Progress?.Invoke(new(PipelineStage.Capture, PipelineState.Warning, "CAPTURE_SOURCE_CLOCK_AHEAD", "来源时间略晚于采样时间；保留原值，年龄显示下限为零", Frame: sequence)
            { DurationMilliseconds = (result.CapturedTimestamp - sampledAt) * 1000d / Stopwatch.Frequency, CaptureTimestamp = result.CapturedTimestamp });
        result = result with { Sequence = sequence, SampledTimestamp = sampledAt,
            VerifiedTimestamp = result.VerifiedTimestamp > 0 ? result.VerifiedTimestamp : result.IsRepeatedFrame ? sampledAt : result.CapturedTimestamp };
        var cold = sequence == 1;
        Progress?.Invoke(new(PipelineStage.Capture, PipelineState.Success, "CAPTURE_OK", $"{result.Backend} ROI {result.Width}×{result.Height}", (long)result.CaptureMs, Frame: sequence)
        { DurationMilliseconds = result.CaptureMs, CaptureTimestamp = result.CapturedTimestamp, FrameAgeMilliseconds = FrameTiming.ElapsedMilliseconds(result.CapturedTimestamp),
            CaptureBackend = result.Backend, IsRepeatedFrame = result.IsRepeatedFrame, SourceCompositor100ns = result.SourceSystemRelativeTime?.Ticks, CaptureRegion = result.ScreenRegion,
            DroppedCount = result.DroppedFrames + Interlocked.Read(ref _droppedFrames), ColdStart = cold,
            VerifiedTimestamp = result.VerifiedTimestamp, FrameVerificationAgeMilliseconds = FrameTiming.ElapsedMilliseconds(result.VerifiedTimestamp) });
        Progress?.Invoke(new(PipelineStage.Capture, PipelineState.Success, "FRAME_WAIT", "等待捕获源新帧", (long)result.FrameWaitMs, Frame: sequence)
        { DurationMilliseconds = result.FrameWaitMs, CaptureTimestamp = result.CapturedTimestamp, ColdStart = cold });
        FrameCaptured?.Invoke(result);
        return result;
    }

    private async Task<CapturedRegionFrame> ReadBufferedFrameAsync(CancellationToken ct)
    {
        if (_frames == null)
        {
            _captureCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _frames = Channel.CreateBounded<CapturedRegionFrame>(new BoundedChannelOptions(1)
            { SingleReader = false, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest }, _ =>
            {
                if (Interlocked.Increment(ref _droppedFrames) % 20 == 1)
                    Progress?.Invoke(new(PipelineStage.Ocr, PipelineState.Warning, "CAPTURE_BACKLOG", "识别落后于采样，仅保留最近一帧；可缩小区域或切换识别模式"));
            });
            var writer = _frames.Writer; var token = _captureCancellation.Token;
            _captureTask = Task.Run(async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        if (Paused?.Invoke() == true)
                        {
                            while (_frames.Reader.TryRead(out _)) Interlocked.Increment(ref _droppedFrames);
                            while (Paused?.Invoke() == true) await Task.Delay(50, token);
                        }
                        var clock = System.Diagnostics.Stopwatch.StartNew();
                        writer.TryWrite(await CaptureFrameAsync(token));
                        await Task.Delay(Math.Max(1, _captureInterval - (int)clock.ElapsedMilliseconds), token);
                    }
                    writer.TryComplete();
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { writer.TryComplete(); }
                catch (Exception ex) { writer.TryComplete(ex); }
            }, CancellationToken.None);
        }
        var latest = await _frames.Reader.ReadAsync(ct);
        while (_frames.Reader.TryRead(out var newer)) { latest = newer; Interlocked.Increment(ref _droppedFrames); }
        return latest;
    }

    internal static async Task<string> RecognizeBytesAsync(OcrEngine engine, byte[] bytes, int width, int height, CancellationToken ct)
        => (await RecognizeDetailedAsync(engine, bytes, width, height, ct)).Text;

    internal static async Task<Recognition> RecognizeDetailedAsync(OcrEngine engine, byte[] bytes, int width, int height, CancellationToken ct, Action<IReadOnlyList<OcrTextBox>>? inspect = null)
    {
        using var writer = new DataWriter();
        writer.WriteBytes(bytes);
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(writer.DetachBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Ignore);
        var result = await engine.RecognizeAsync(bitmap).AsTask(ct);
        var boxes = result.Lines.SelectMany(line => line.Words).Select(word =>
        {
            var rect = word.BoundingRect;
            return new OcrTextBox(word.Text, rect.X + rect.Width / 2, rect.Y + rect.Height / 2, rect.Height, rect.Width);
        }).ToArray();
        inspect?.Invoke(boxes);
        return new(string.Join("\n", result.Lines.Select(line => line.Text)), "Windows OCR", null, 0, ParagraphLayout.Group(boxes, width, height))
        { InputWidth = width, InputHeight = height, PreparedWidth = width, PreparedHeight = height };
    }
    public void Dispose()
    {
        // Normal UI teardown remains nonblocking. Diagnostics await DisposeAsync
        // before closing log/record writers so no capture callback can arrive late.
        var pending = DisposeTask();
        _ = pending.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }
    public ValueTask DisposeAsync() => new(DisposeTask());
    private Task DisposeTask()
    {
        lock (_disposeSync) return _disposeTask ??= DisposeCoreAsync();
    }
    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        _captureCancellation?.Cancel();
        _local?.Dispose(); _local = null;
        if (_captureTask is { } task) await task.ConfigureAwait(false);
        _capture?.Dispose(); _captureCancellation?.Dispose();
        _lastBytes = null; LastFrame = null;
    }
    private static bool IsNearlyBlack(byte[] pixels)
    {
        for (var i = 0; i + 2 < pixels.Length; i += 64)
            if (Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2])) > 12) return false;
        return true;
    }
}
