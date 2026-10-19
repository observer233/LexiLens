using System.Threading.Channels;

namespace LexiLens.Core;

// OCR keeps running while translation is in flight. Accuracy mode prefers the
// newest text; fast mode retains a bounded dialogue queue and emits replay pairs.
public sealed class LiveSession(ITextSource source, ITranslator translator, int intervalMs, bool fastMode = false)
{
    private readonly object _sync = new();
    private CancellationTokenSource? _request;
    private long _version;
    public event Action<LiveUpdate>? Updated;
    public event Action<string>? Observed;
    public event Action<PipelineEvent>? Progress;
    // In-memory replay receives completed pairs, including a brief subtitle that
    // has left the screen. It never goes into the diagnostic journal.
    public event Action<LiveUpdate>? Completed;

    public async Task RunOnceAsync(CancellationToken ct)
    {
        var text = TextProcessing.Normalize(await source.ReadAsync(ct));
        Observed?.Invoke(text);
        if (text.Length == 0)
        {
            Progress?.Invoke(new(PipelineStage.Stability, PipelineState.Waiting, "OCR_EMPTY", "未识别到文字，未发送 API 请求"));
            Updated?.Invoke(new LiveUpdate("", "未识别到文字")); return;
        }
        Progress?.Invoke(new(PipelineStage.Stability, PipelineState.Success, "TEXT_MANUAL", "单次翻译，直接提交识别文字", Characters: text.Length));
        Updated?.Invoke(new LiveUpdate(text, IsBusy: true));
        var timer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            Progress?.Invoke(new(PipelineStage.Translation, PipelineState.Running, "TRANSLATION_QUEUED", "正在请求翻译", Characters: text.Length));
            var result = await translator.TranslateAsync(text, ct);
            ct.ThrowIfCancellationRequested();
            Progress?.Invoke(new(PipelineStage.Translation, PipelineState.Success, "TRANSLATION_OK", "译文已返回", timer.ElapsedMilliseconds, text.Length));
            var update = new LiveUpdate(text, result);
            Completed?.Invoke(update);
            Updated?.Invoke(update);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Progress?.Invoke(new(PipelineStage.Translation, PipelineState.Error, ex is CloudApiException cloud ? cloud.Code : ex is OperationCanceledException ? "API_TIMEOUT" : "TRANSLATION_ERROR", FriendlyError(ex), timer.ElapsedMilliseconds));
            Updated?.Invoke(new LiveUpdate(text, Error: FriendlyError(ex)));
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (fastMode) { await RunFastAsync(cancellationToken); return; }
        var channel = Channel.CreateBounded<(long Version, string Text)>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var produce = ProduceAsync(channel.Writer, linked.Token);
        var consume = ConsumeAsync(channel.Reader, linked.Token);
        try
        {
            var first = await Task.WhenAny(produce, consume);
            await first;
        }
        finally
        {
            await linked.CancelAsync();
            channel.Writer.TryComplete();
            try { await Task.WhenAll(produce, consume); }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        }
    }

    private async Task ProduceAsync(ChannelWriter<(long, string)> writer, CancellationToken ct)
    {
        var stabilizer = new TextStabilizer();
        long frame = 0;
        while (!ct.IsCancellationRequested)
        {
            var scan = System.Diagnostics.Stopwatch.StartNew();
            var text = TextProcessing.Normalize(await source.ReadAsync(ct));
            frame++;
            Observed?.Invoke(text);
            var decision = stabilizer.Observe(text);
            var message = decision.Code switch
            {
                "OCR_EMPTY" => "未识别到文字；请检查选区或切换 OCR 引擎",
                "TEXT_UNCHANGED" => "文字未变化，跳过重复翻译",
                "TEXT_PENDING" => "已识别文字，等待下一次确认",
                "TEXT_DEBOUNCE_LIMIT" => "文字持续变化，提交当前结果",
                "TEXT_CLEARED" => "字幕已消失",
                _ => "文字已确认，准备翻译"
            };
            Progress?.Invoke(new(PipelineStage.Stability, decision.Submit ? PipelineState.Success : decision.Code == "TEXT_UNCHANGED" ? PipelineState.Skipped : PipelineState.Waiting, decision.Code, message, Characters: text.Length, Frame: frame));
            if (decision.Submit)
            {
                long version;
                lock (_sync)
                {
                    version = ++_version;
                    _request?.Cancel();
                }
                Updated?.Invoke(new LiveUpdate(text, IsBusy: text.Length > 0));
                writer.TryWrite((version, text));
            }
            await DelayScanAsync(scan, ct);
        }
    }

    private async Task ConsumeAsync(ChannelReader<(long Version, string Text)> reader, CancellationToken ct)
    {
        await foreach (var item in reader.ReadAllAsync(ct))
        {
            using var request = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lock (_sync)
            {
                if (item.Version != _version) continue;
                _request = request;
            }
            try
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                if (item.Text.Length > 0) Progress?.Invoke(new(PipelineStage.Translation, PipelineState.Running, "TRANSLATION_QUEUED", "正在请求翻译", Characters: item.Text.Length));
                var result = item.Text.Length == 0 ? "" : await translator.TranslateAsync(item.Text, request.Token);
                lock (_sync)
                {
                    if (!request.IsCancellationRequested && item.Version == _version)
                    {
                        Progress?.Invoke(new(PipelineStage.Translation, PipelineState.Success, "TRANSLATION_OK", "译文已返回", watch.ElapsedMilliseconds, item.Text.Length));
                        var update = new LiveUpdate(item.Text, result);
                        if (item.Text.Length > 0) Completed?.Invoke(update);
                        Updated?.Invoke(update);
                    }
                }
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested) { }
            catch (Exception ex)
            {
                lock (_sync)
                {
                    if (item.Version == _version && !ct.IsCancellationRequested)
                    {
                        Progress?.Invoke(new(PipelineStage.Translation, PipelineState.Error, ex is CloudApiException cloud ? cloud.Code : ex is OperationCanceledException ? "API_TIMEOUT" : "TRANSLATION_ERROR", FriendlyError(ex)));
                        Updated?.Invoke(new LiveUpdate(item.Text, Error: FriendlyError(ex)));
                    }
                }
            }
            finally
            {
                lock (_sync) { if (ReferenceEquals(_request, request)) _request = null; }
            }
        }
    }

    private async Task DelayScanAsync(System.Diagnostics.Stopwatch scan, CancellationToken ct)
    {
        var remaining = Math.Clamp(intervalMs, 50, 5000) - (int)scan.ElapsedMilliseconds;
        if (remaining > 0) await Task.Delay(remaining, ct);
        else await Task.Yield();
        ct.ThrowIfCancellationRequested();
    }

    private async Task RunFastAsync(CancellationToken ct)
    {
        var queue = Channel.CreateBounded<(long Version, string Text, DateTimeOffset CapturedAt)>(new BoundedChannelOptions(8)
        { SingleWriter = true, SingleReader = false, FullMode = BoundedChannelFullMode.DropOldest },
            _ => Progress?.Invoke(new(PipelineStage.Translation, PipelineState.Warning, "SUBTITLE_QUEUE_FULL", "翻译速度落后，已跳过最早的待译字幕；可调低扫描频率或更换更快的服务")));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        long newest = 0, displayed = 0;
        async Task Produce()
        {
            var stabilizer = new TextStabilizer();
            while (true)
            {
                var scan = System.Diagnostics.Stopwatch.StartNew();
                var text = TextProcessing.Normalize(await source.ReadAsync(linked.Token));
                Observed?.Invoke(text);
                var decision = stabilizer.Observe(text, immediate: true);
                Progress?.Invoke(new(PipelineStage.Stability, decision.Submit ? PipelineState.Success : PipelineState.Skipped,
                    decision.Code, decision.Code == "TEXT_IMMEDIATE" ? "快速字幕：首次识别立即提交" : text.Length == 0 ? "暂无文字，保留最近译文" : "文字未变化，跳过重复翻译", Characters: text.Length));
                if (decision.Submit && text.Length > 0)
                {
                    var version = Interlocked.Increment(ref newest);
                    Updated?.Invoke(new(text, IsBusy: true));
                    queue.Writer.TryWrite((version, text, DateTimeOffset.Now));
                }
                await DelayScanAsync(scan, linked.Token);
            }
        }
        async Task Consume()
        {
            await foreach (var item in queue.Reader.ReadAllAsync(linked.Token))
            {
                var timer = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    Progress?.Invoke(new(PipelineStage.Translation, PipelineState.Running, "TRANSLATION_QUEUED", "正在翻译已捕获的字幕", Characters: item.Text.Length));
                    var result = await translator.TranslateAsync(item.Text, linked.Token);
                    linked.Token.ThrowIfCancellationRequested();
                    lock (_sync)
                    {
                        var update = new LiveUpdate(item.Text, result, IsPrevious: item.Version < Interlocked.Read(ref newest), CapturedAt: item.CapturedAt);
                        Completed?.Invoke(update);
                        Progress?.Invoke(new(PipelineStage.Translation, PipelineState.Success, "TRANSLATION_OK", "字幕译文已返回", timer.ElapsedMilliseconds, item.Text.Length));
                        // A late result stays in replay, but never overwrites a newer result.
                        if (item.Version > displayed) { displayed = item.Version; Updated?.Invoke(update); }
                    }
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    Progress?.Invoke(new(PipelineStage.Translation, PipelineState.Error, ex is CloudApiException cloud ? cloud.Code : ex is OperationCanceledException ? "API_TIMEOUT" : "TRANSLATION_ERROR", FriendlyError(ex), timer.ElapsedMilliseconds));
                    lock (_sync) { if (item.Version >= displayed) Updated?.Invoke(new(item.Text, Error: FriendlyError(ex))); }
                }
            }
        }
        var producer = Produce(); var consumers = new[] { Consume(), Consume() };
        try { await await Task.WhenAny(producer, Task.WhenAll(consumers)); }
        finally
        {
            await linked.CancelAsync(); queue.Writer.TryComplete();
            try { await Task.WhenAll(consumers.Prepend(producer)); } catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        }
    }

    public static string FriendlyError(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: { } status } => $"翻译服务返回 HTTP {(int)status}。请检查服务配置和账号额度。",
        HttpRequestException => "无法连接翻译服务，请检查网络和服务地址。",
        OperationCanceledException => "翻译请求超时。请检查服务，暂停后重新开始。",
        _ => ex.Message
    };
}
