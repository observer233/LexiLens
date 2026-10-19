using System.Threading.Channels;

namespace LexiLens.Core;

// One OCR pass per area, independent text/translation identity for each paragraph.
public sealed class SpatialSession(IBlockTextSource source, ITranslator translator, int intervalMs, bool fastMode, int maxBlocks = 12, int staleAfterMs = 2000)
{
    private sealed class Track(int id, RecognizedBlock block)
    {
        internal int Id = id, Revision, Misses;
        internal RecognizedBlock Block = block;
        internal string? Translation, Error;
        internal TextStabilizer Stabilizer = new();
    }
    private sealed record Work(int Id, int Revision, string Text, DateTimeOffset Time);
    private readonly object _sync = new();
    private readonly List<Track> _tracks = [];
    private int _nextId;
    private IReadOnlyList<SpatialBlock>? _lastPublished;
    private long _freshUntil;
    private bool _expireStale;
    public event Action<IReadOnlyList<SpatialBlock>>? Updated;
    public event Action<LiveUpdate>? Completed;
    public event Action<PipelineEvent>? Progress;

    public async Task RunAsync(CancellationToken ct, bool once = false)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _expireStale = !once;
        var queue = Channel.CreateBounded<Work>(new BoundedChannelOptions(24)
        { SingleWriter = true, SingleReader = false, FullMode = BoundedChannelFullMode.DropOldest },
            _ => Progress?.Invoke(new(PipelineStage.Translation, PipelineState.Warning, "BLOCK_QUEUE_FULL", "分段翻译跟不上画面，已跳过最早待译文本块")));
        async Task Produce()
        {
            try
            {
                do
                {
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    var blocks = (await source.ReadBlocksAsync(linked.Token)).Where(x => x.Bounds.IsValid && !string.IsNullOrWhiteSpace(x.Text)).ToArray();
                    if (blocks.Length > maxBlocks) Progress?.Invoke(new(PipelineStage.Stability, PipelineState.Warning, "BLOCK_LIMIT", $"检测到 {blocks.Length} 块，本轮最多处理 {maxBlocks} 块；可缩小选区"));
                    lock (_sync)
                    {
                        _freshUntil = Environment.TickCount64 + Math.Clamp(staleAfterMs, 100, 10000);
                        var remaining = _tracks.ToList();
                        foreach (var block in blocks.Take(Math.Clamp(maxBlocks, 4, 24)))
                        {
                            var track = remaining.Where(x => x.Block.Bounds.Overlap(block.Bounds) >= .25)
                                .OrderByDescending(x => x.Block.Bounds.Overlap(block.Bounds)).FirstOrDefault();
                            if (track == null) { track = new(++_nextId, block); _tracks.Add(track); }
                            remaining.Remove(track); track.Misses = 0;
                            if (!TextStabilizer.Similar(track.Block.Text, block.Text)) { track.Revision++; track.Translation = null; track.Error = null; }
                            track.Block = block;
                            var decision = track.Stabilizer.Observe(block.Text, fastMode || once);
                            if (decision.Submit)
                            {
                                track.Revision++; track.Translation = null; track.Error = null;
                                queue.Writer.TryWrite(new(track.Id, track.Revision, block.Text, DateTimeOffset.Now));
                            }
                        }
                        foreach (var track in remaining) { track.Misses++; }
                        _tracks.RemoveAll(x => x.Misses >= 2);
                        Publish();
                    }
                    Progress?.Invoke(new(PipelineStage.Stability, PipelineState.Success, "BLOCKS_LOCATED", $"按位置分为 {Math.Min(blocks.Length, maxBlocks)} 个文本块", Characters: blocks.Sum(x => x.Text.Length)));
                    if (once) break;
                    var delay = Math.Clamp(intervalMs, 100, 5000) - (int)clock.ElapsedMilliseconds;
                    if (delay > 0) await Task.Delay(delay, linked.Token);
                } while (!linked.IsCancellationRequested);
            }
            finally { queue.Writer.TryComplete(); }
        }
        async Task Consume()
        {
            await foreach (var work in queue.Reader.ReadAllAsync(linked.Token))
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    Progress?.Invoke(new(PipelineStage.Translation, PipelineState.Running, "BLOCK_TRANSLATING", $"正在翻译文本块 {work.Id}", Characters: work.Text.Length));
                    var translation = await translator.TranslateAsync(work.Text, linked.Token);
                    linked.Token.ThrowIfCancellationRequested();
                    Completed?.Invoke(new(work.Text, translation, CapturedAt: work.Time));
                    lock (_sync)
                    {
                        var track = _tracks.FirstOrDefault(x => x.Id == work.Id && x.Revision == work.Revision);
                        if (track != null) { track.Translation = translation; track.Error = null; Publish(); }
                    }
                    Progress?.Invoke(new(PipelineStage.Translation, PipelineState.Success, "BLOCK_TRANSLATED", $"文本块 {work.Id} 译文返回", clock.ElapsedMilliseconds, work.Text.Length));
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    lock (_sync)
                    {
                        var track = _tracks.FirstOrDefault(x => x.Id == work.Id && x.Revision == work.Revision);
                        if (track != null) { track.Error = LiveSession.FriendlyError(ex); Publish(); }
                    }
                    Progress?.Invoke(new(PipelineStage.Translation, PipelineState.Error, ex is CloudApiException cloud ? cloud.Code : "BLOCK_TRANSLATION_FAILED", LiveSession.FriendlyError(ex)));
                }
            }
        }
        async Task Expire()
        {
            while (!linked.IsCancellationRequested)
            {
                await Task.Delay(Math.Clamp(staleAfterMs / 3, 50, 250), linked.Token);
                lock (_sync) { if (_expireStale && Environment.TickCount64 > _freshUntil) Publish(); }
            }
        }
        var producer = Produce(); var consumers = new[] { Consume(), Consume() };
        var expiration = once ? Task.CompletedTask : Expire();
        try
        {
            var first = await Task.WhenAny(producer, Task.WhenAll(consumers));
            await first;
            if (once) await Task.WhenAll(consumers);
            else ct.ThrowIfCancellationRequested();
        }
        finally
        {
            await linked.CancelAsync();
            try { await Task.WhenAll(consumers.Prepend(producer).Append(expiration)); } catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        }
    }

    private void Publish()
    {
        var blocks = (_expireStale && Environment.TickCount64 > _freshUntil ? [] : _tracks.Where(x => x.Misses == 0)
            .Select(x => new SpatialBlock(x.Id, x.Block.Text, x.Block.Bounds, x.Translation, x.Error)).ToArray());
        if (_lastPublished != null && _lastPublished.SequenceEqual(blocks)) return;
        _lastPublished = blocks; Updated?.Invoke(blocks);
    }
}
