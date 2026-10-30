using System.Collections.Concurrent;
using System.Diagnostics;
using LexiLens.Core;
using Xunit;

namespace LexiLens.Core.Tests;

// Deterministic engineering tests. Stub HTTP below tests request suppression,
// never game accuracy, provider latency, quota eligibility or translation quality.
public class LatencyPipelineTests
{
    private const string Reply = """{"choices":[{"finish_reason":"stop","message":{"content":"译文"}}]}""";

    [Fact]
    public async Task RevisionObsoleteDuringRateWaitNeverReachesHttp()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        { Interlocked.Increment(ref calls); return Task.FromResult(TranslationTests.Json(Reply)); }));
        var client = new TranslationClient(http, CloudTests.Settings("zhipu") with { RequestIntervalMs = 300 }, "dummy");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        await client.TranslateAsync("Prime", timeout.Token);
        var queued = Signal(); var current = 1;
        var events = new ConcurrentQueue<PipelineEvent>();
        client.Progress = value => { events.Enqueue(value); if (value.Code == "API_CLIENT_QUEUE_WAIT") queued.TrySetResult(); };
        using var scope = new TranslationRequestScope(() => Volatile.Read(ref current) == 1, new(19, Stopwatch.GetTimestamp()), 3, 8);
        var pending = client.TranslateAsync("Obsolete", timeout.Token);
        await queued.Task.WaitAsync(timeout.Token);
        Volatile.Write(ref current, 0);
        await Assert.ThrowsAsync<StaleTranslationException>(() => pending);
        Assert.Equal(1, calls); Assert.Equal(1, client.Cloud.RequestsSent);
        Assert.Contains(events, x => x.Frame == 19 && x.BlockId == 3 && x.Revision == 8 && x.FrameAgeMilliseconds >= 0);
    }

    [Fact]
    public async Task RevisionObsoleteBehindSharedConcurrencyGateNeverReachesHttp()
    {
        var calls = 0; var bothRunning = Signal(); var release = Signal();
        using var http = new HttpClient(new Handler(async (_, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 2) bothRunning.TrySetResult();
            await release.Task.WaitAsync(ct); return TranslationTests.Json(Reply);
        }));
        var client = new TranslationClient(http, CloudTests.Settings("zhipu") with { RequestIntervalMs = 0 }, "dummy");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var first = client.TranslateAsync("First quest", timeout.Token);
        var second = client.TranslateAsync("Second quest", timeout.Token);
        await bothRunning.Task.WaitAsync(timeout.Token);
        var current = 1;
        using var scope = new TranslationRequestScope(() => Volatile.Read(ref current) == 1);
        var pending = client.TranslateAsync("Old subtitle", timeout.Token);
        Volatile.Write(ref current, 0); release.TrySetResult();
        await Task.WhenAll(first, second);
        await Assert.ThrowsAsync<StaleTranslationException>(() => pending);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task SharedClientKeepsConcurrentAreaCorrelationsSeparate()
    {
        var requests = new ConcurrentQueue<(int? Area, string Body)>();
        var events = new ConcurrentQueue<PipelineEvent>();
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            requests.Enqueue((TranslationRequestScope.Current?.Area, await request.Content!.ReadAsStringAsync(ct)));
            await Task.Yield(); return TranslationTests.Json(Reply);
        }));
        var client = new TranslationClient(http, CloudTests.Settings("zhipu") with { RequestIntervalMs = 0 }, "dummy") { Progress = events.Enqueue };
        var first = new SpatialSession(new Source { Blocks = [Block("Area A", .7)] }, client, 100, true, area: 1);
        var second = new SpatialSession(new Source { Blocks = [Block("Area B", .7)] }, client, 100, true, area: -1);
        first.Progress += events.Enqueue; second.Progress += events.Enqueue;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        await Task.WhenAll(first.RunAsync(timeout.Token, once: true), second.RunAsync(timeout.Token, once: true));
        Assert.Contains(requests, x => x.Area == 1 && x.Body.Contains("Area A"));
        Assert.Contains(requests, x => x.Area == -1 && x.Body.Contains("Area B"));
        Assert.Contains(events, x => x.Code == "API_REQUEST_COMPLETE" && x.Area == 1 && x.BlockId == 1 && x.Revision == 1);
        Assert.Contains(events, x => x.Code == "API_REQUEST_COMPLETE" && x.Area == -1 && x.BlockId == 1 && x.Revision == 1);
        Assert.DoesNotContain(events, x => x.Area == 0);
    }

    [Theory]
    [InlineData(TranslationProvider.Cloud)]
    [InlineData(TranslationProvider.MyMemory)]
    [InlineData(TranslationProvider.Ollama)]
    public async Task RequestColdMetadataIsIndependentOfSourceFrameAndCache(TranslationProvider provider)
    {
        var events = new ConcurrentQueue<PipelineEvent>();
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(TranslationTests.Json(provider switch
        {
            TranslationProvider.Cloud => Reply,
            TranslationProvider.MyMemory => """{"responseStatus":200,"responseData":{"translatedText":"译文"}}""",
            _ => """{"message":{"content":"译文"}}"""
        }))));
        var settings = provider == TranslationProvider.Cloud ? CloudTests.Settings("zhipu") with { RequestIntervalMs = 0 } : new AppSettings { Provider = provider };
        var client = new TranslationClient(http, settings, "dummy") { Progress = events.Enqueue };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        using (new TranslationRequestScope(() => true, new(4, Stopwatch.GetTimestamp(), false)))
            await client.TranslateAsync("First request", timeout.Token);
        using (new TranslationRequestScope(() => true, new(5, Stopwatch.GetTimestamp(), true)))
        {
            await client.TranslateAsync("Second request", timeout.Token);
            await client.TranslateAsync("First request", timeout.Token);
        }
        var sent = events.Where(x => x.Code == "API_REQUEST_SENT").ToArray();
        Assert.Equal(2, sent.Length);
        Assert.Equal(1, sent[0].RequestOrdinal); Assert.True(sent[0].RequestColdStart); Assert.False(sent[0].ColdStart);
        Assert.Equal(2, sent[1].RequestOrdinal); Assert.False(sent[1].RequestColdStart); Assert.True(sent[1].ColdStart);
        foreach (var request in sent)
        {
            Assert.Contains(events, x => x.Code == "HTTP_200" && x.RequestOrdinal == request.RequestOrdinal && x.RequestColdStart == request.RequestColdStart);
            Assert.Contains(events, x => x.Code == "API_REQUEST_COMPLETE" && x.RequestOrdinal == request.RequestOrdinal && x.RequestColdStart == request.RequestColdStart);
        }
        var cache = Assert.Single(events, x => x.Code == "TRANSLATION_CACHE");
        Assert.Null(cache.RequestOrdinal); Assert.Null(cache.RequestColdStart);
    }

    [Fact]
    public async Task FailedCloudAttemptKeepsItsRequestOrdinal()
    {
        var calls = 0; var events = new ConcurrentQueue<PipelineEvent>();
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Interlocked.Increment(ref calls) == 1
            ? new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError) : TranslationTests.Json(Reply))));
        var client = new TranslationClient(http, CloudTests.Settings("zhipu") with { RequestIntervalMs = 0 }, "dummy") { Progress = events.Enqueue };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        await Assert.ThrowsAsync<CloudApiException>(() => client.TranslateAsync("Failure", timeout.Token));
        await client.TranslateAsync("Success", timeout.Token);
        Assert.Contains(events, x => x.Code == "HTTP_500" && x.RequestOrdinal == 1 && x.RequestColdStart == true);
        Assert.Contains(events, x => x.Code == "API_REQUEST_FAILED" && x.RequestOrdinal == 1 && x.RequestColdStart == true);
        Assert.Contains(events, x => x.Code == "API_REQUEST_COMPLETE" && x.RequestOrdinal == 2 && x.RequestColdStart == false);
    }

    [Fact]
    public async Task StoppingAnInFlightCloudRequestRecordsCancellationAndKeepsClientUsable()
    {
        var calls = 0; var sent = Signal(); var events = new ConcurrentQueue<PipelineEvent>();
        using var http = new HttpClient(new Handler(async (_, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 1) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return TranslationTests.Json(Reply);
        }));
        var client = new TranslationClient(http, CloudTests.Settings("zhipu") with { RequestIntervalMs = 0 }, "dummy");
        client.Progress = value => { events.Enqueue(value); if (value.Code == "API_REQUEST_SENT") sent.TrySetResult(); };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        using var scope = new TranslationRequestScope(() => true, new(23, Stopwatch.GetTimestamp()), 4, 7, area: -1);
        var pending = client.TranslateAsync("Interrupted subtitle", stop.Token);
        await sent.Task.WaitAsync(timeout.Token); await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        var cancelled = Assert.Single(events, x => x.Code == "API_REQUEST_CANCELLED");
        Assert.Equal(PipelineState.Skipped, cancelled.State);
        Assert.Equal(1, cancelled.RequestOrdinal); Assert.True(cancelled.RequestColdStart);
        Assert.Equal(-1, cancelled.Area); Assert.Equal(23, cancelled.Frame);
        Assert.Equal(4, cancelled.BlockId); Assert.Equal(7, cancelled.Revision);
        Assert.True(cancelled.DurationMilliseconds >= 0);
        Assert.DoesNotContain(events, x => x.Code == "API_REQUEST_FAILED");
        Assert.DoesNotContain(events, x => x.Code == "API_REQUEST_COMPLETE" && x.RequestOrdinal == 1);
        await client.TranslateAsync("Next subtitle", timeout.Token);
        Assert.Equal(2, client.Cloud.RequestsSent);
        Assert.Contains(events, x => x.Code == "API_REQUEST_COMPLETE" && x.RequestOrdinal == 2 && x.RequestColdStart == false);
    }

    [Fact]
    public async Task PendingRevisionsCoalesceWithoutSendingOldSubtitles()
    {
        var source = new Source { Blocks = [Block("Quest A", .1), Block("Quest B", .3), Block("Old", .7)] };
        var calls = new ConcurrentQueue<string>(); var release = Signal(); var bothRunning = Signal();
        var initial = Signal(); var middle = Signal(); var latest = Signal(); var visible = Signal(); var running = 0;
        var events = new ConcurrentQueue<PipelineEvent>();
        var session = new SpatialSession(source, new Translator(async (text, ct) =>
        {
            calls.Enqueue(text);
            if (text.StartsWith("Quest")) { if (Interlocked.Increment(ref running) == 2) bothRunning.TrySetResult(); await release.Task.WaitAsync(ct); }
            return "译:" + text;
        }), 100, true);
        session.Progress += value =>
        {
            events.Enqueue(value);
            if (value.Code != "BLOCK_QUEUED" || value.BlockId != 3) return;
            if (value.Revision == 1) initial.TrySetResult();
            if (value.Revision == 3) middle.TrySetResult();
            if (value.Revision == 5) latest.TrySetResult();
        };
        session.Updated += blocks => { if (blocks.Any(x => x.Source == "Latest" && x.Translation == "译:Latest")) visible.TrySetResult(); };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = session.RunAsync(timeout.Token);
        await Task.WhenAll(initial.Task, bothRunning.Task).WaitAsync(timeout.Token);
        source.Blocks = [Block("Quest A", .1), Block("Quest B", .3), Block("Middle", .7)];
        await middle.Task.WaitAsync(timeout.Token);
        source.Blocks = [Block("Quest A", .1), Block("Quest B", .3), Block("Latest", .7)];
        await latest.Task.WaitAsync(timeout.Token); release.TrySetResult();
        await visible.Task.WaitAsync(timeout.Token);
        Assert.DoesNotContain("Old", calls); Assert.DoesNotContain("Middle", calls); Assert.Contains("Latest", calls);
        Assert.Contains(events, x => x.Code == "BLOCK_QUEUE_REPLACED" && x.DroppedCount >= 2);
        Assert.All(events, x => Assert.InRange(x.QueueLength ?? 0, 0, 24));
        await timeout.CancelAsync(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task DisplayTimingRetainsFirstFrameInsteadOfLatestIdenticalOcrFrame()
    {
        var source = new TimedSource(); var began = Signal(); var thirdFrame = Signal(); var visible = Signal();
        SpatialBlock? displayed = null;
        source.ThirdFrame = thirdFrame;
        var session = new SpatialSession(source, new Translator(async (_, ct) =>
        { began.TrySetResult(); await thirdFrame.Task.WaitAsync(ct); return "译文"; }), 100, true);
        session.Updated += blocks => { if (blocks.FirstOrDefault(x => x.Translation != null) is { } block) { displayed = block; visible.TrySetResult(); } };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var run = session.RunAsync(timeout.Token); await visible.Task.WaitAsync(timeout.Token);
        Assert.True(began.Task.IsCompleted); Assert.Equal(1, displayed!.Timing!.Frame);
        Assert.Equal(source.FirstTimestamp, displayed.Timing.CapturedTimestamp);
        Assert.True(displayed.Timing.AgeMilliseconds >= 150);
        await timeout.CancelAsync(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task OldColdFrameIsSkippedAndTheSameTextRetriesFromFreshFrame()
    {
        var source = new ColdSource(); var skipped = Signal(); var visible = Signal(); var calls = 0;
        SpatialBlock? displayed = null;
        var session = new SpatialSession(source, new Translator((_, _) =>
        { Interlocked.Increment(ref calls); return Task.FromResult("新鲜译文"); }), 100, true, staleAfterMs: 1000);
        session.Progress += value => { if (value.Code == "BLOCK_REQUEST_STALE") skipped.TrySetResult(); };
        session.Updated += blocks => { if (blocks.FirstOrDefault(x => x.Translation != null) is { } block) { displayed = block; visible.TrySetResult(); } };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var run = session.RunAsync(timeout.Token);
        await skipped.Task.WaitAsync(timeout.Token); Assert.Equal(0, calls);
        source.AllowFresh.TrySetResult(); await visible.Task.WaitAsync(timeout.Token);
        Assert.Equal(1, calls); Assert.Equal(2, displayed!.Timing!.Frame);
        Assert.True(session.IsCurrent(displayed));
        await timeout.CancelAsync(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.False(session.IsCurrent(displayed));
    }

    [Fact]
    public async Task ExplicitSnapshotMayTranslateAnOldFrame()
    {
        var source = new ColdSource(); var calls = 0; SpatialBlock? displayed = null;
        var session = new SpatialSession(source, new Translator((_, _) =>
        { calls++; return Task.FromResult("快照译文"); }), 100, true, staleAfterMs: 100);
        session.Updated += blocks => displayed = blocks.FirstOrDefault(x => x.Translation != null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        await session.RunAsync(timeout.Token, once: true);
        Assert.Equal(1, calls); Assert.NotNull(displayed); Assert.True(session.IsCurrent(displayed));
        Assert.True(displayed.Timing!.AgeMilliseconds >= 5000);
    }

    [Fact]
    public async Task VerifiedStaticSurfaceKeepsOriginAgeWithoutExpiringCurrentTranslation()
    {
        var source = new VerifiedSource(); var visible = Signal(); var later = Signal(); var calls = 0; var reads = 0;
        SpatialBlock? displayed = null;
        var session = new SpatialSession(source, new Translator((_, _) =>
        { Interlocked.Increment(ref calls); return Task.FromResult("静态字幕译文"); }), 100, true, staleAfterMs: 150);
        session.Progress += value => { if (value.Code == "BLOCKS_LOCATED" && Interlocked.Increment(ref reads) >= 4) later.TrySetResult(); };
        session.Updated += blocks => { if (blocks.FirstOrDefault(x => x.Translation != null) is { } block) { displayed = block; visible.TrySetResult(); } };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var run = session.RunAsync(timeout.Token);
        await Task.WhenAll(visible.Task, later.Task).WaitAsync(timeout.Token);
        Assert.Equal(1, calls); Assert.True(session.IsCurrent(displayed!));
        Assert.True(displayed!.Timing!.AgeMilliseconds >= 5000);
        Assert.Equal(source.Origin, displayed.Timing.CapturedTimestamp);
        Assert.True(displayed.Timing.VerifiedTimestamp > displayed.Timing.CapturedTimestamp);
        await timeout.CancelAsync(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    private static RecognizedBlock Block(string text, double y) => new(text, new(.1, y, .5, .05));
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Source : IBlockTextSource
    {
        internal volatile IReadOnlyList<RecognizedBlock> Blocks = [];
        public Task<IReadOnlyList<RecognizedBlock>> ReadBlocksAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(Blocks); }
        public Task<string> ReadAsync(CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class TimedSource : IBlockTextSource, IFrameTimingSource
    {
        private int _frame;
        public long FirstTimestamp { get; } = Stopwatch.GetTimestamp();
        public TaskCompletionSource? ThirdFrame;
        public FrameTiming? CurrentFrame { get; private set; }
        public Task<IReadOnlyList<RecognizedBlock>> ReadBlocksAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); var frame = ++_frame;
            CurrentFrame = new(frame, frame == 1 ? FirstTimestamp : Stopwatch.GetTimestamp(), frame == 1) { Engine = "Test" };
            if (frame >= 3) ThirdFrame?.TrySetResult();
            return Task.FromResult<IReadOnlyList<RecognizedBlock>>([Block("Persistent subtitle", .7)]);
        }
        public Task<string> ReadAsync(CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class ColdSource : IBlockTextSource, IFrameTimingSource
    {
        private int _frame;
        public TaskCompletionSource AllowFresh { get; } = Signal();
        public FrameTiming? CurrentFrame { get; private set; }
        public async Task<IReadOnlyList<RecognizedBlock>> ReadBlocksAsync(CancellationToken ct)
        {
            var frame = ++_frame;
            if (frame > 1) await AllowFresh.Task.WaitAsync(ct);
            CurrentFrame = new(frame, Stopwatch.GetTimestamp() - (frame == 1 ? Stopwatch.Frequency * 5 : 0), frame == 1) { Engine = "Test" };
            return [Block("Same text", .7)];
        }
        public Task<string> ReadAsync(CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class VerifiedSource : IBlockTextSource, IFrameTimingSource
    {
        private int _frame;
        public long Origin { get; } = Stopwatch.GetTimestamp() - Stopwatch.Frequency * 5;
        public FrameTiming? CurrentFrame { get; private set; }
        public Task<IReadOnlyList<RecognizedBlock>> ReadBlocksAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); var frame = ++_frame;
            CurrentFrame = new(frame, Origin, frame == 1) { VerifiedTimestamp = Stopwatch.GetTimestamp(), Engine = "Test WGC repeat" };
            return Task.FromResult<IReadOnlyList<RecognizedBlock>>([Block("Static text", .7)]);
        }
        public Task<string> ReadAsync(CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Translator(Func<string, CancellationToken, Task<string>> translate) : ITranslator
    { public Task<string> TranslateAsync(string text, CancellationToken ct) => translate(text, ct); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct); }
}
