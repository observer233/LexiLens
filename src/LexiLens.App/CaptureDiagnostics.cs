using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LexiLens.App.Platform;
using LexiLens.App.Views;
using LexiLens.Core;

namespace LexiLens.App;

internal sealed record CaptureDiagnosticsOptions
{
    public CaptureBackend Backend { get; init; } = CaptureBackend.Gdi;
    public long Hwnd { get; init; }
    public TextBounds Roi { get; init; } = new(.1, .7, .8, .25);
    public int DurationSeconds { get; init; } = 30;
    public bool Translate { get; init; }
    public bool RecordFrames { get; init; }
    public bool SaveEvaluationText { get; init; }
    public string? ReplayManifest { get; init; }
    public string OutputDirectory { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LexiLens", "capture-diagnostics", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
    public bool AllowUnsupportedWindowsOcr { get; init; }
    public bool FastMode { get; init; } = true;
    public OcrMode OcrMode { get; init; } = OcrMode.Enhanced;
    public int SampleIntervalMs { get; init; } = 150;
    internal static JsonSerializerOptions Json { get; } = new() { WriteIndented = true, PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
    internal static JsonSerializerOptions JsonCompact { get; } = new(Json) { WriteIndented = false };
}

internal static class CaptureDiagnostics
{
    // Independent client/cache, existing provider implementation and credential
    // vault. Keys and text never enter pipeline.jsonl or the metrics report.
    internal static async Task RunAsync(CaptureDiagnosticsOptions options, AppSettings settings, Func<HttpClient, ITranslator>? translatorFactory,
        Action<PipelineEvent>? status = null, CancellationToken ct = default)
    {
        var directory = Path.GetFullPath(options.OutputDirectory);
        Directory.CreateDirectory(directory);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.DurationSeconds, 1, 300)));
        var events = Channel.CreateUnbounded<PipelineEvent>(new UnboundedChannelOptions { SingleReader = true });
        var writeTask = Task.Run(async () =>
        {
            await using var writer = new StreamWriter(Path.Combine(directory, "pipeline.jsonl"), false);
            await foreach (var item in events.Reader.ReadAllAsync())
                await writer.WriteLineAsync(JsonSerializer.Serialize(item, CaptureDiagnosticsOptions.JsonCompact));
        });
        var start = Stopwatch.GetTimestamp();
        long stoppedAt = 0;
        using var stopRegistration = timeout.Token.Register(() => Interlocked.CompareExchange(ref stoppedAt, Stopwatch.GetTimestamp(), 0));
        var counts = new Dictionary<string, long>();
        void Log(PipelineEvent item)
        {
            lock (counts) counts[item.Code] = counts.GetValueOrDefault(item.Code) + 1;
            events.Writer.TryWrite(item); status?.Invoke(item);
        }
        var frameFiles = new List<ReplayFrame>();
        var frameQueue = Channel.CreateBounded<CapturedRegionFrame>(new BoundedChannelOptions(32) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest },
            _ => Log(new(PipelineStage.Capture, PipelineState.Warning, "RECORD_FRAME_DROPPED", "录帧写入跟不上；回放素材存在缺帧")));
        var frameTask = Task.Run(async () =>
        {
            await foreach (var frame in frameQueue.Reader.ReadAllAsync())
            {
                var name = $"frames/{frame.Sequence:D8}.png";
                var path = Path.Combine(directory, name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, frame.Bytes, frame.Width * 4);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = File.Create(path)) encoder.Save(stream);
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(path)));
                var offset = Math.Max(0, (frame.SampledTimestamp - start) * 1000d / Stopwatch.Frequency);
                if (frameFiles.Count == 0 || frameFiles[^1].OffsetMs < offset) frameFiles.Add(new(name, offset, frame.ScreenRegion, Sha256: hash,
                    SourceOffsetMs: (frame.CapturedTimestamp - start) * 1000d / Stopwatch.Frequency,
                    VerifiedOffsetMs: (frame.VerifiedTimestamp - start) * 1000d / Stopwatch.Frequency, IsRepeatedFrame: frame.IsRepeatedFrame));
            }
        });
        SpatialOverlayWindow? overlay = null;
        ScreenTextSource? source = null;
        IRegionCapture? capture = null;
        ReplayCapture? replay = null;
        string? error = null;
        var observations = new List<(ReplayFrame Frame, string Text)>();
        var spatialObservations = new List<(ReplayFrame Frame, string Text)>();
        var evaluation = new List<object>();
        try
        {
            settings = settings with { FastMode = options.FastMode, OcrMode = options.OcrMode };
            var captureStart = Stopwatch.GetTimestamp();
            capture = options.ReplayManifest is { Length: > 0 } replayPath
                ? new ReplayCapture(replayPath) : RegionCapture.Create(options.Backend, (nint)options.Hwnd, options.Roi);
            Log(new(PipelineStage.Capture, PipelineState.Success, "CAPTURE_SOURCE_STARTUP", "捕获源初始化完成")
            { DurationMilliseconds = FrameTiming.ElapsedMilliseconds(captureStart), ColdStart = true });
            replay = capture as ReplayCapture;
            if (replay != null)
            {
                timeout.CancelAfter(Timeout.InfiniteTimeSpan);
                replay.Started += () => timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Min(replay.DurationMs, 300_000)));
            }
            var region = replay?.Manifest.Frames[0].ScreenRegion ?? RegionCapture.Crop(RegionCapture.WindowRegion((nint)options.Hwnd), options.Roi);
            source = new ScreenTextSource(region, settings.SourceLanguage, mode: options.OcrMode, imageMode: settings.OcrImageMode,
                fastMode: options.FastMode, captureInterval: options.SampleIntervalMs, capture: capture, allowUnsupportedWindowsOcr: options.AllowUnsupportedWindowsOcr) { Progress = Log };
            overlay = new(region, settings) { Progress = Log };
            overlay.Show();
            if (!overlay.CaptureExcluded) throw new InvalidOperationException("译文层截图排除失败，停止验证，避免回捕自身。");
            source.FrameCaptured += frame =>
            {
                if (options.RecordFrames) frameQueue.Writer.TryWrite(frame);
            };
            source.BlocksRead += blocks =>
            {
                if (replay?.FrameFor(source.CurrentFrame) is { } frame)
                {
                    var raw = source.LastRecognition?.Text ?? "";
                    var spatial = string.Join("\n", blocks.Select(x => x.Text));
                    observations.Add((frame, raw)); spatialObservations.Add((frame, spatial));
                    if (options.SaveEvaluationText) evaluation.Add(new { frame.OffsetMs, reference = frame.ReferenceText, raw, spatial });
                }
            };
            Log(new(PipelineStage.Session, PipelineState.Running, "CAPTURE_DIAGNOSTIC_START", options.ReplayManifest != null ? "按原时间回放素材" : options.Backend + " 按 HWND 捕获字幕 ROI")
            { MonotonicTimestamp = start });
            if (options.Translate)
            {
                using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(45) };
                var translator = translatorFactory?.Invoke(http) ?? ExistingTranslator(http, settings, Log);
                var session = new SpatialSession(source, translator, options.SampleIntervalMs, options.FastMode, settings.AutoBlockLimit);
                session.Progress += Log;
                overlay.IsCurrentBlock = session.IsCurrent;
                session.Updated += blocks => overlay.Dispatcher.BeginInvoke(() =>
                {
                    if (timeout.IsCancellationRequested) return;
                    overlay.SetRegion(source.CurrentRegion);
                    overlay.SetBlocks(blocks.Where(session.IsCurrent).ToArray());
                });
                await session.RunAsync(timeout.Token);
            }
            else
            {
                while (!timeout.IsCancellationRequested)
                {
                    var tick = Stopwatch.GetTimestamp();
                    var blocks = await source.ReadBlocksAsync(timeout.Token);
                    overlay.SetRegion(source.CurrentRegion);
                    // Position preview only; these are explicitly not translations
                    // and cannot produce OVERLAY_VISIBLE end-to-end samples.
                    overlay.SetBlocks(blocks.Select((b, i) => new SpatialBlock(i + 1, b.Text, b.Bounds, b.Text)).ToArray());
                    Log(new(PipelineStage.Overlay, PipelineState.Success, "OCR_PREVIEW_VISIBLE", "仅检查识别位置，翻译未开启", Frame: source.CurrentFrame?.Frame ?? 0));
                    await Task.Delay(Math.Max(1, options.SampleIntervalMs - (int)FrameTiming.ElapsedMilliseconds(tick)), timeout.Token);
                }
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + LiveSession.FriendlyError(ex);
            Log(new(PipelineStage.Session, PipelineState.Error, "CAPTURE_DIAGNOSTIC_FAILED", error));
        }
        finally
        {
            if (source != null) await source.DisposeAsync();
            else capture?.Dispose();
            Interlocked.CompareExchange(ref stoppedAt, Stopwatch.GetTimestamp(), 0);
            var recordingDuration = FrameTiming.ElapsedMilliseconds(start, stoppedAt);
            overlay?.Close();
            frameQueue.Writer.TryComplete();
            try { await frameTask; }
            catch (Exception ex) { error ??= "录帧失败：" + ex.GetType().Name; Log(new(PipelineStage.Capture, PipelineState.Error, "RECORD_FAILED", "录帧文件写入失败")); }
            frameFiles.RemoveAll(f => f.OffsetMs >= recordingDuration);
            if (options.RecordFrames) await File.WriteAllTextAsync(Path.Combine(directory, "recording.json"), JsonSerializer.Serialize(new ReplayManifest(frameFiles, recordingDuration), CaptureDiagnosticsOptions.Json));
            if (options.SaveEvaluationText) await File.WriteAllTextAsync(Path.Combine(directory, "evaluation-local-text.json"), JsonSerializer.Serialize(evaluation, CaptureDiagnosticsOptions.Json));
            Log(new(PipelineStage.Session, PipelineState.Success, "CAPTURE_DIAGNOSTIC_STOP", "验证结束"));
            events.Writer.TryComplete(); await writeTask;
            await File.WriteAllTextAsync(Path.Combine(directory, "run.json"), JsonSerializer.Serialize(new
            {
                runCompleted = error == null, success = error == null && (!options.Translate || counts.GetValueOrDefault("OVERLAY_VISIBLE") > 0),
                error, backend = options.ReplayManifest != null ? "Replay" : options.Backend.ToString(), options.Roi, options.Translate, options.RecordFrames, options.SaveEvaluationText,
                options.FastMode, options.OcrMode, options.AllowUnsupportedWindowsOcr, options.SampleIntervalMs,
                stopwatchFrequency = Stopwatch.Frequency, elapsedMs = FrameTiming.ElapsedMilliseconds(start), packageIdentity = WindowsOcrSupport.HasPackageIdentity,
                service = options.Translate ? settings.CloudServiceId : null,
                model = options.Translate ? ProviderCatalog.Profile(settings).Model : null,
                events = counts, recordedFrames = frameFiles.Count, annotatedObservations = observations.Count(x => x.Frame.ReferenceText != null),
                ocrQuality = MeasureOcr(observations),
                spatialOutputQuality = MeasureOcr(spatialObservations),
                missedSubtitles = (object?)null, expiredTranslations = (object?)null,
                gameFrameTime = (object?)null, translationAccuracy = (object?)null,
                note = "仅实际日志样本可统计阶段耗时；未标注游戏字幕、未记录渲染帧时间时，对应指标不可判定。WPF 呈现回调不等于实际屏幕发光时间。"
            }, CaptureDiagnosticsOptions.Json));
        }
    }

    // Sampled annotated-frame CER is separate from full-clip subtitle recall.
    private static object? MeasureOcr(List<(ReplayFrame Frame, string Text)> observations)
    {
        var samples = observations.Where(x => !string.IsNullOrWhiteSpace(x.Frame.ReferenceText))
            .GroupBy(x => x.Frame).Select(g => g.Last()).ToArray();
        if (samples.Length == 0) return null;
        long characters = 0, errors = 0; var exact = 0; var skipped = 0;
        foreach (var (frame, text) in samples)
        {
            var reference = TextProcessing.Normalize(frame.ReferenceText!);
            var actual = TextProcessing.Normalize(text);
            if (reference.Length > 8000 || actual.Length > 8000) { skipped++; continue; }
            characters += reference.Length;
            if (reference == actual) exact++;
            var row = Enumerable.Range(0, actual.Length + 1).ToArray();
            for (var i = 1; i <= reference.Length; i++)
            {
                var diagonal = row[0]; row[0] = i;
                for (var j = 1; j <= actual.Length; j++)
                {
                    var previous = row[j];
                    row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (reference[i - 1] == actual[j - 1] ? 0 : 1));
                    diagonal = previous;
                }
            }
            errors += row[^1];
        }
        return new { annotatedFramesRead = samples.Length - skipped, skippedOversizedSamples = skipped, referenceCharacters = characters, characterErrors = errors,
            sampledFrameCer = characters > 0 ? errors / (double)characters : (double?)null, exactFrames = exact,
            normalization = "TextProcessing.Normalize; case-sensitive; UTF-16 code units; nonblank references; repeated reads of a frame counted once" };
    }

    private static ITranslator ExistingTranslator(HttpClient http, AppSettings settings, Action<PipelineEvent> log)
    {
        var key = "";
        if (settings.Provider == TranslationProvider.Cloud)
        {
            var preset = ProviderCatalog.Get(settings.CloudServiceId);
            var vault = new CredentialVault(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LexiLens"));
            key = vault.Read(CredentialVault.Scope(preset.Id, ProviderCatalog.Endpoint(preset, ProviderCatalog.Profile(settings))));
        }
        return new TranslationClient(http, settings, key) { Progress = log };
    }
}
