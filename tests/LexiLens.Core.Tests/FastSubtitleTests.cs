using System.Collections.Concurrent;
using LexiLens.Core;
using Xunit;

namespace LexiLens.Core.Tests;

public class FastSubtitleTests
{
    [Fact]
    public async Task FirstRecognitionTranslatesWithoutWaitingForASecondFrame()
    {
        var source = new FirstFrameSource();
        var translator = new DelegateTranslator((text, _) => Task.FromResult("译文:" + text));
        var session = new LiveSession(source, translator, 50, fastMode: true);
        var done = new TaskCompletionSource<LiveUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Completed += update => done.TrySetResult(update);
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var run = session.RunAsync(ct.Token);
        var result = await done.Task.WaitAsync(ct.Token);
        Assert.Equal("A brief subtitle.", result.Source);
        Assert.Equal("译文:A brief subtitle.", result.Translation);
        await ct.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task DisappearedDialogueStillCompletesAndOlderResultCannotReplaceNewerResult()
    {
        var source = new MutableSource { Text = "First brief dialogue" };
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bothDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var history = new ConcurrentQueue<LiveUpdate>(); var shown = new ConcurrentQueue<LiveUpdate>();
        var session = new LiveSession(source, new DelegateTranslator(async (text, ct) =>
        {
            if (text.StartsWith("First")) { firstStarted.TrySetResult(); await releaseFirst.Task.WaitAsync(ct); }
            return "译文:" + text;
        }), 50, fastMode: true);
        session.Completed += update => { history.Enqueue(update); if (history.Count == 2) bothDone.TrySetResult(); };
        session.Updated += update => { shown.Enqueue(update); if (update.Translation?.Contains("Second") == true) secondDone.TrySetResult(); };
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var run = session.RunAsync(ct.Token);
        await firstStarted.Task.WaitAsync(ct.Token);
        source.Text = "Second short dialogue";
        await secondDone.Task.WaitAsync(ct.Token);
        source.Text = ""; releaseFirst.TrySetResult();
        await bothDone.Task.WaitAsync(ct.Token);
        Assert.Contains(history, x => x.Translation?.Contains("First") == true);
        Assert.Contains(history, x => x.Translation?.Contains("Second") == true);
        Assert.Equal("译文:Second short dialogue", shown.Last(x => x.Translation != null).Translation);
        await ct.CancelAsync(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task SlowServiceHasBoundedBacklogAndReportsSkippedSubtitles()
    {
        var index = 0;
        var source = new DelegateSource(_ => Task.FromResult("Dialogue " + Interlocked.Increment(ref index)));
        var warning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new LiveSession(source, new DelegateTranslator(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return ""; }), 50, fastMode: true);
        session.Progress += e => { if (e.Code == "SUBTITLE_QUEUE_FULL") warning.TrySetResult(); };
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var run = session.RunAsync(ct.Token);
        await warning.Task.WaitAsync(ct.Token);
        await ct.CancelAsync(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public void LegacyAreaMigratesAndInvalidOrExcessAreasAreRemoved()
    {
        var region = new CaptureRegion(-800, 10, 400, 100);
        var migrated = new AppSettings { Region = region }.Sanitize();
        Assert.Equal(region, Assert.Single(migrated.Areas).Region);
        var settings = (migrated with
        {
            Areas = [new("bad", "bad", new(0, 0, 1, 1)), .. Enumerable.Range(0, 8).Select(i => new CaptureArea(i.ToString(), "", region, new(double.NaN, 0, 400)))],
            FastIntervalMs = 1, RequestIntervalMs = 1
        }).Sanitize();
        Assert.Equal(AppSettings.MaxAreas, settings.Areas.Count);
        Assert.All(settings.Areas, x => { Assert.NotEmpty(x.Name); Assert.Null(x.Overlay); });
        Assert.Equal(100, settings.FastIntervalMs); Assert.Equal(200, settings.RequestIntervalMs);
    }

    [Fact]
    public async Task TwoRegionsCanTranslateTogetherAndIdenticalTextSharesTheCache()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0; var max = 0; var calls = 0;
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var http = new HttpClient(new Handler(async (_, token) =>
        {
            Interlocked.Increment(ref calls);
            var count = Interlocked.Increment(ref active); Interlocked.Exchange(ref max, Math.Max(max, count));
            if (count == 2) started.TrySetResult();
            await release.Task.WaitAsync(token); Interlocked.Decrement(ref active);
            return TranslationTests.Json("""{"responseStatus":200,"responseData":{"translatedText":"译文"}}""");
        }));
        var client = new TranslationClient(http, new AppSettings());
        var a = "Subtitle A";
        var b = Enumerable.Range(0, 100).Select(i => "Quest " + i).First(x => (x.GetHashCode(StringComparison.Ordinal) & 31) != (a.GetHashCode(StringComparison.Ordinal) & 31));
        var tasks = new[] { client.TranslateAsync(a, ct.Token), client.TranslateAsync(b, ct.Token), client.TranslateAsync(a, ct.Token) };
        await started.Task.WaitAsync(ct.Token); release.TrySetResult();
        Assert.All(await Task.WhenAll(tasks), x => Assert.Equal("译文", x));
        Assert.Equal(2, calls); Assert.Equal(2, max);
    }

    [Fact]
    public async Task CloudStartSpacingDoesNotWaitForAnotherAreasResponseBody()
    {
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var http = new HttpClient(new Handler(async (_, token) =>
        {
            var number = Interlocked.Increment(ref calls);
            if (number == 1) firstStarted.TrySetResult(); else secondStarted.TrySetResult();
            await release.Task.WaitAsync(token);
            return TranslationTests.Json("""{"choices":[{"message":{"content":"译文"}}],"usage":{"total_tokens":7}}""");
        }));
        var cloud = new CloudTranslation(http, CloudTests.Settings("zhipu") with { FastMode = true, RequestIntervalMs = 200 }, "dummy");
        var first = cloud.TranslateAsync("First", ct.Token); await firstStarted.Task.WaitAsync(ct.Token);
        var second = cloud.TranslateAsync("Second", ct.Token); await secondStarted.Task.WaitAsync(ct.Token);
        Assert.False(first.IsCompleted); release.TrySetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(2, cloud.RequestsSent); Assert.Equal(14, cloud.ReportedTokens);
    }

    [Fact]
    public void AreaNamesAndReplayNeverEnterDiagnosticExport()
    {
        var journal = new DiagnosticJournal(Path.Combine(Path.GetTempPath(), "lexilens-unused-" + Guid.NewGuid()));
        var settings = new AppSettings { Areas = [new("secret-id", "private-game-name", new(1, 2, 400, 100))] };
        var report = journal.Export(settings, "test");
        Assert.Contains("areaSizes", report); Assert.DoesNotContain("private-game-name", report); Assert.DoesNotContain("secret-id", report);
    }

    private sealed class FirstFrameSource : ITextSource
    {
        private int _calls;
        public async Task<string> ReadAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1) return "A brief subtitle.";
            await Task.Delay(Timeout.Infinite, ct); return "";
        }
    }
    private sealed class MutableSource : ITextSource { internal volatile string Text = ""; public Task<string> ReadAsync(CancellationToken ct) => Task.FromResult(Text); }
    private sealed class DelegateSource(Func<CancellationToken, Task<string>> read) : ITextSource { public Task<string> ReadAsync(CancellationToken ct) => read(ct); }
    private sealed class DelegateTranslator(Func<string, CancellationToken, Task<string>> translate) : ITranslator { public Task<string> TranslateAsync(string text, CancellationToken ct) => translate(text, ct); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct); }
}
