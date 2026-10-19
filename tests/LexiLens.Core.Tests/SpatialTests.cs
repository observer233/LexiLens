using System.Collections.Concurrent;
using LexiLens.Core;
using Xunit;

namespace LexiLens.Core.Tests;

public class SpatialTests
{
    [Fact]
    public void SeparateColumnsAndParagraphGapsDoNotMerge()
    {
        OcrTextBox[] boxes = [new("Quest title", 200, 50, 24, 280), new("Quest details", 200, 86, 24, 280),
            new("Inventory", 800, 50, 24, 280), new("Item details", 800, 86, 24, 280), new("Dialogue below", 200, 240, 24, 280)];
        var groups = ParagraphLayout.Group(boxes, 1000, 500);
        Assert.Equal(3, groups.Count);
        Assert.Contains(groups, x => x.Text == "Quest title\nQuest details");
        Assert.Contains(groups, x => x.Text == "Inventory\nItem details");
        Assert.Contains(groups, x => x.Text == "Dialogue below");
        Assert.All(groups, x => Assert.True(x.Bounds.IsValid));
        Assert.True(groups[0].Bounds.Right < groups[1].Bounds.X);
    }

    [Fact]
    public void WordsFormLinesWithoutBridgingDistantColumns()
    {
        OcrTextBox[] boxes = [new("ancient", 140, 40, 20, 70), new("Find", 55, 40, 20, 45), new("relic", 210, 40, 20, 50),
            new("Inventory", 700, 40, 20, 120), new(" ", 10, 10, 20, 10), new("...", 20, 200, 3, 10)];
        var groups = ParagraphLayout.Group(boxes, 1000, 400);
        Assert.Equal(2, groups.Count); Assert.Equal("Find ancient relic", groups[0].Text); Assert.Equal("Inventory", groups[1].Text);
    }

    [Fact]
    public void DescendersAndShortWordsCannotSplitAnOtherwiseContinuousLine()
    {
        // Windows OCR returns different top/height for enemy/near even at the
        // same font and baseline. Their later ordering must not split the row.
        OcrTextBox[] boxes = [new("the", 372.5, 19.5, 21, 39), new("enemy", 442, 26, 20, 82), new("arrives.", 534, 19.5, 21, 84),
            new("healing", 208.5, 172.5, 27, 87), new("herbs", 296, 169.5, 21, 66), new("near", 365.5, 173, 14, 53), new("the", 419.5, 169.5, 21, 39)];
        var groups = ParagraphLayout.Group(boxes, 924, 300);
        Assert.Equal(2, groups.Count);
        Assert.Equal("the enemy arrives.", groups[0].Text); Assert.Equal("healing herbs near the", groups[1].Text);
    }

    [Fact]
    public void FontChangesAndInvalidGeometryAreKeptSeparateOrRejected()
    {
        OcrTextBox[] boxes = [new("Heading", 200, 50, 40, 300), new("Small description", 200, 90, 18, 300),
            new("bad", double.NaN, 20, 20, 30), new("outside", 2000, 30, 20, 30)];
        Assert.Equal(2, ParagraphLayout.Group(boxes, 800, 400).Count);
        Assert.Empty(ParagraphLayout.Group(boxes, 0, 100));
    }

    [Fact]
    public async Task EachParagraphGetsItsOwnTranslationAndCoordinates()
    {
        var blocks = new[] { Block("Quest", .1, .1), Block("Dialogue", .6, .7) };
        var source = new Source { Blocks = blocks };
        var calls = new ConcurrentQueue<string>();
        var session = new SpatialSession(source, new Translator((text, _) => { calls.Enqueue(text); return Task.FromResult("译:" + text); }), 100, true);
        IReadOnlyList<SpatialBlock> last = [];
        session.Updated += value => last = value;
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        await session.RunAsync(ct.Token, once: true);
        Assert.Equal(2, calls.Count); Assert.DoesNotContain(calls, x => x.Contains("\n"));
        Assert.Equal("译:Quest", last.Single(x => x.Source == "Quest").Translation);
        Assert.Equal(blocks[1].Bounds, last.Single(x => x.Source == "Dialogue").Bounds);
    }

    [Fact]
    public async Task LateTranslationCannotCoverReplacementAtTheSamePosition()
    {
        var source = new Source { Blocks = [Block("First dialogue", .1, .1)] };
        var began = Signal(); var release = Signal(); var newer = Signal(); var oldCompleted = Signal();
        IReadOnlyList<SpatialBlock> last = [];
        var session = new SpatialSession(source, new Translator(async (text, token) =>
        { if (text.StartsWith("First")) { began.TrySetResult(); await release.Task.WaitAsync(token); } return "译:" + text; }), 100, true);
        session.Updated += value => { last = value; if (value.Any(x => x.Translation == "译:New dialogue")) newer.TrySetResult(); };
        session.Completed += value => { if (value.Source.StartsWith("First")) oldCompleted.TrySetResult(); };
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = session.RunAsync(ct.Token); await began.Task.WaitAsync(ct.Token);
        source.Blocks = [Block("New dialogue", .1, .1)]; await newer.Task.WaitAsync(ct.Token);
        release.TrySetResult(); await oldCompleted.Task.WaitAsync(ct.Token); await Task.Delay(20, ct.Token);
        Assert.Equal("译:New dialogue", Assert.Single(last).Translation);
        await ct.CancelAsync(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task DisappearedParagraphIsNotResurrectedButItsTranslationRemainsInReplay()
    {
        var source = new Source { Blocks = [Block("Brief subtitle", .1, .1)] };
        var began = Signal(); var release = Signal(); var gone = Signal(); var complete = Signal();
        IReadOnlyList<SpatialBlock> last = [];
        var session = new SpatialSession(source, new Translator(async (_, token) => { began.TrySetResult(); await release.Task.WaitAsync(token); return "短对白"; }), 100, true);
        session.Updated += value => { last = value; if (value.Count == 0) gone.TrySetResult(); };
        session.Completed += _ => complete.TrySetResult();
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = session.RunAsync(ct.Token); await began.Task.WaitAsync(ct.Token); source.Blocks = [];
        await gone.Task.WaitAsync(ct.Token); release.TrySetResult(); await complete.Task.WaitAsync(ct.Token); await Task.Delay(20, ct.Token);
        Assert.Empty(last);
        await ct.CancelAsync(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task SmallPositionJitterMovesTheSameBlockWithoutAnotherApiRequest()
    {
        var source = new Source { Blocks = [Block("Stable subtitle", .1, .1)] };
        var first = Signal(); var moved = Signal(); var calls = 0;
        var session = new SpatialSession(source, new Translator((_, _) => { Interlocked.Increment(ref calls); return Task.FromResult("稳定字幕"); }), 100, true);
        var id = 0;
        session.Updated += blocks =>
        {
            if (blocks.Count != 1 || blocks[0].Translation == null) return;
            if (id == 0) { id = blocks[0].Id; first.TrySetResult(); }
            if (blocks[0].Bounds.X == .12 && blocks[0].Id == id) moved.TrySetResult();
        };
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var run = session.RunAsync(ct.Token); await first.Task.WaitAsync(ct.Token);
        source.Blocks = [Block("Stable subtitle", .12, .1)]; await moved.Task.WaitAsync(ct.Token);
        Assert.Equal(1, calls);
        await ct.CancelAsync(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task AStalledRecognizerCannotLeaveOldTranslationCoveringTheScreen()
    {
        var source = new StallingSource(); var visible = Signal(); var expired = Signal();
        var session = new SpatialSession(source, new Translator((_, _) => Task.FromResult("旧字幕")), 100, true, staleAfterMs: 200);
        session.Updated += blocks =>
        { if (blocks.Any(x => x.Translation != null)) visible.TrySetResult(); if (visible.Task.IsCompleted && blocks.Count == 0) expired.TrySetResult(); };
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var run = session.RunAsync(ct.Token); await expired.Task.WaitAsync(ct.Token);
        await ct.CancelAsync(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task AutomaticBlockLimitIsVisibleAndDoesNotSilentlyMergeExtraText()
    {
        var source = new Source { Blocks = Enumerable.Range(0, 8).Select(i => Block("Label " + i, .1, i * .1)).ToArray() };
        var calls = 0; var limit = false;
        var session = new SpatialSession(source, new Translator((_, _) => { Interlocked.Increment(ref calls); return Task.FromResult("译文"); }), 100, true, maxBlocks: 4);
        session.Progress += e => { if (e.Code == "BLOCK_LIMIT") limit = true; };
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(4)); await session.RunAsync(ct.Token, once: true);
        Assert.True(limit); Assert.Equal(4, calls);
    }

    private static RecognizedBlock Block(string text, double x, double y) => new(text, new(x, y, .25, .06));
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Source : IBlockTextSource
    {
        internal volatile IReadOnlyList<RecognizedBlock> Blocks = [];
        public Task<IReadOnlyList<RecognizedBlock>> ReadBlocksAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(Blocks); }
        public Task<string> ReadAsync(CancellationToken ct) => Task.FromResult(string.Join("\n", Blocks.Select(x => x.Text)));
    }
    private sealed class Translator(Func<string, CancellationToken, Task<string>> translate) : ITranslator
    { public Task<string> TranslateAsync(string text, CancellationToken ct) => translate(text, ct); }
    private sealed class StallingSource : IBlockTextSource
    {
        private int _reads;
        public Task<string> ReadAsync(CancellationToken ct) => throw new NotSupportedException();
        public async Task<IReadOnlyList<RecognizedBlock>> ReadBlocksAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref _reads) > 1) await Task.Delay(Timeout.Infinite, ct);
            return [Block("Old subtitle", .1, .1)];
        }
    }
}
