using System.Collections.Concurrent;
using LexiLens.Core;
using Xunit;

namespace LexiLens.Core.Tests;

public class PipelineTests
{
    [Fact]
    public async Task AlternatingMinorOcrErrorsStillTranslateAndReportEveryStage()
    {
        var source = new AlternatingSource(); var translator = new CountingTranslator();
        var session = new LiveSession(source, translator, 50);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new ConcurrentQueue<PipelineEvent>(); var observations = 0;
        session.Observed += _ => Interlocked.Increment(ref observations);
        session.Progress += progress.Enqueue;
        session.Updated += x => { if (x.Translation == "译文") complete.TrySetResult(); };
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var running = session.RunAsync(ct.Token);
        await complete.Task.WaitAsync(ct.Token);
        await Task.Delay(220, ct.Token);
        await ct.CancelAsync(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(1, translator.Calls);
        Assert.True(observations >= 2);
        Assert.Contains(progress, e => e.Code == "TEXT_READY");
        Assert.Contains(progress, e => e.Code == "TRANSLATION_OK");
    }

    [Fact]
    public void UnstableOcrCannotWaitForeverAndNumbersRemainDistinct()
    {
        var stabilizer = new TextStabilizer();
        Assert.False(stabilizer.Observe("Read the first unknown quest.").Submit);
        Assert.False(stabilizer.Observe("An entirely different message.").Submit);
        var third = stabilizer.Observe("Find the third quest target.");
        Assert.True(third.Submit); Assert.Equal("TEXT_DEBOUNCE_LIMIT", third.Code);
        Assert.False(TextStabilizer.Similar("Collect 3 of the ancient relics", "Collect 4 of the ancient relics"));
        Assert.False(TextStabilizer.Similar("First quest", "Other quest"));
        var letters = new TextStabilizer();
        letters.Observe("Return to the western gate A.");
        Assert.True(letters.Observe("Return to the western gate A.").Submit);
        Assert.False(letters.Observe("Return to the western gate B.").Submit);
        Assert.True(letters.Observe("Return to the western gate B.").Submit);
    }

    [Theory]
    [InlineData("Find the ancient relic before dawn.", "Find the ancient relic before davvn.", true)]
    [InlineData("Read an unfamiliar word in this quest.", "Read an unfamiliar word in this quests.", true)]
    [InlineData("Retrieve the ancient relic", "Retrieve the ancient ring", false)]
    [InlineData("Collect 13 ancient relics", "Collect 14 ancient relics", false)]
    public void SimilarityIsConservative(string a, string b, bool same) => Assert.Equal(same, TextStabilizer.Similar(a, b));

    [Fact]
    public async Task SingleTranslationBypassesStabilityAndEmptyOcrNeverCallsApi()
    {
        var translator = new CountingTranslator();
        var session = new LiveSession(new FixedSource("Find it."), translator, 50);
        await session.RunOnceAsync(CancellationToken.None); Assert.Equal(1, translator.Calls);
        var empty = new LiveSession(new FixedSource(""), translator, 50); PipelineEvent? last = null;
        empty.Progress += x => last = x;
        await empty.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, translator.Calls); Assert.Equal("OCR_EMPTY", last?.Code);
    }

    [Fact]
    public async Task ApiFailureRemainsTranslationFailureWithItsHttpCode()
    {
        var session = new LiveSession(new FixedSource("Find it."), new FailedTranslator(), 50);
        var events = new List<PipelineEvent>(); LiveUpdate? update = null;
        session.Progress += events.Add; session.Updated += x => update = x;
        await session.RunOnceAsync(CancellationToken.None);
        Assert.Contains(events, x => x.Stage == PipelineStage.Translation && x.State == PipelineState.Error && x.Code == "HTTP_429");
        Assert.NotNull(update?.Error);
    }

    [Fact]
    public void DiagnosticExportExcludesEndpointAccountAndGameTextAndRotates()
    {
        var dir = Path.Combine(Path.GetTempPath(), "LexiLens-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var journal = new DiagnosticJournal(dir);
            journal.Append(new(PipelineStage.Ocr, PipelineState.Warning, "OCR_EMPTY", "未识别到文字"));
            var settings = CloudTests.Settings("cloudflare", consent: true, endpoint: "https://private-host.invalid/v1");
            var export = journal.Export(settings, "0.4.0");
            Assert.DoesNotContain("private-host", export); Assert.DoesNotContain(new string('a', 32), export);
            Assert.Contains("OCR_EMPTY", export);
            Assert.Contains(new DiagnosticJournal(dir).Snapshot(), x => x.Code == "OCR_EMPTY");
            File.WriteAllText(Path.Combine(dir, "pipeline.jsonl"), new string('x', 1_048_600));
            journal.Append(new(PipelineStage.Translation, PipelineState.Error, "HTTP_401", "鉴权失败"));
            Assert.True(File.Exists(Path.Combine(dir, "pipeline.jsonl.1")));
            Assert.True(new FileInfo(Path.Combine(dir, "pipeline.jsonl")).Length < 4096);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    private sealed class AlternatingSource : ITextSource
    {
        private int _count;
        public Task<string> ReadAsync(CancellationToken ct) => Task.FromResult(Interlocked.Increment(ref _count) % 2 == 0 ? "Find the ancient relic before dawn." : "Find the ancient relic before davvn.");
    }
    private sealed class FixedSource(string text) : ITextSource { public Task<string> ReadAsync(CancellationToken ct) => Task.FromResult(text); }
    private sealed class CountingTranslator : ITranslator { public int Calls; public Task<string> TranslateAsync(string text, CancellationToken ct) { Calls++; return Task.FromResult("译文"); } }
    private sealed class FailedTranslator : ITranslator { public Task<string> TranslateAsync(string text, CancellationToken ct) => throw new CloudApiException("请求限流", "HTTP_429"); }
}
