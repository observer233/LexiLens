using System.Collections.Concurrent;
using LexiLens.Core;
using Xunit;

namespace LexiLens.Core.Tests;

public class LiveSessionTests
{
    [Fact]
    public async Task NewSubtitleCancelsOldRequestAndNeverPublishesItsLateResult()
    {
        var source = new MutableSource { Text = "First quest" };
        var translator = new DelayedTranslator();
        var session = new LiveSession(source, translator, 50);
        var updates = new ConcurrentQueue<LiveUpdate>();
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Updated += update => { updates.Enqueue(update); if (update.Translation == "最新译文") complete.TrySetResult(); };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var running = session.RunAsync(cancellation.Token);
        await translator.Started.Task.WaitAsync(cancellation.Token);
        source.Text = "Second quest";
        await complete.Task.WaitAsync(cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.True(translator.OldWasCancelled);
        Assert.DoesNotContain(updates, u => u.Translation == "过时译文");
        Assert.Contains(updates, u => u.Translation == "最新译文");
    }

    [Fact]
    public async Task RepeatedFramesOnlyTranslateOnceAndBlankFramesClearOutput()
    {
        var source = new MutableSource { Text = "Find the relic" };
        var translator = new CountingTranslator();
        var session = new LiveSession(source, translator, 50);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clear = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Updated += u =>
        {
            if (u.Translation == "译文") done.TrySetResult();
            if (u.Source == "" && u.Translation == "") clear.TrySetResult();
        };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var running = session.RunAsync(cancellation.Token);
        await done.Task.WaitAsync(cancellation.Token);
        await Task.Delay(230, cancellation.Token);
        Assert.Equal(1, translator.Count);
        source.Text = "";
        await clear.Task.WaitAsync(cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(1, translator.Count);
    }

    private sealed class MutableSource : ITextSource
    {
        public volatile string Text = "";
        public Task<string> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Text);
    }

    private sealed class CountingTranslator : ITranslator
    {
        public int Count;
        public Task<string> TranslateAsync(string text, CancellationToken ct) { Interlocked.Increment(ref Count); return Task.FromResult("译文"); }
    }

    private sealed class DelayedTranslator : ITranslator
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool OldWasCancelled;
        public async Task<string> TranslateAsync(string text, CancellationToken ct)
        {
            if (text == "First quest")
            {
                Started.TrySetResult();
                // Intentionally ignore cancellation to simulate an uncooperative remote provider.
                await Task.Delay(300);
                OldWasCancelled = ct.IsCancellationRequested;
                return "过时译文";
            }
            return "最新译文";
        }
    }
}
