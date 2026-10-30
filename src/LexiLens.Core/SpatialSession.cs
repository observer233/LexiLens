using System.Diagnostics;

namespace LexiLens.Core;

// One OCR pass per area, independent text/translation identity for each paragraph.
public sealed class SpatialSession(IBlockTextSource source, ITranslator translator, int intervalMs, bool fastMode, int maxBlocks = 12, int staleAfterMs = 2000, int area = 0)
{
    private sealed class Track(int id, RecognizedBlock block)
    {
        internal int Id = id, Revision, Misses;
        internal RecognizedBlock Block = block;
        internal string? Translation, Error;
        internal TextStabilizer Stabilizer = new();
        internal FrameTiming? CandidateTiming, Timing;
        internal long CandidateTimestamp = Stopwatch.GetTimestamp();
        internal long ReadyTimestamp;
        internal bool Retry;
    }
    private sealed record Work(int Id, int Revision, string Text, DateTimeOffset Time, long QueuedTimestamp, FrameTiming? Timing);

    // Only the latest pending revision of a paragraph is useful. Keep a bounded
    // queue without leaving a dropped current revision permanently untranslated.
    private sealed class WorkQueue(int capacity) : IDisposable
    {
        private readonly object _sync = new();
        private readonly LinkedList<Work> _items = new();
        private readonly SemaphoreSlim _available = new(0);
        private bool _completed;
        public int Count { get { lock (_sync) return _items.Count; } }
        public Work? Enqueue(Work work)
        {
            lock (_sync)
            {
                var existing = _items.First;
                while (existing != null && existing.Value.Id != work.Id) existing = existing.Next;
                Work? dropped = null;
                if (existing != null) { dropped = existing.Value; _items.Remove(existing); }
                else if (_items.Count >= capacity) { dropped = _items.First!.Value; _items.RemoveFirst(); }
                else _available.Release();
                _items.AddLast(work);
                return dropped;
            }
        }
        public async Task<Work?> ReadAsync(CancellationToken ct)
        {
            while (true)
            {
                await _available.WaitAsync(ct);
                lock (_sync)
                {
                    if (_items.First is { } first) { _items.RemoveFirst(); return first.Value; }
                    if (_completed) return null;
                }
            }
        }
        public void Complete() { lock (_sync) { _completed = true; _available.Release(2); } }
        public void Dispose() => _available.Dispose();
    }

    private readonly object _sync = new();
    private readonly List<Track> _tracks = [];
    private int _nextId;
    private long _droppedCount;
    private IReadOnlyList<SpatialBlock>? _lastPublished;
    private long _freshUntil;
    private bool _expireStale;
    private bool _closed;
    public event Action<IReadOnlyList<SpatialBlock>>? Updated;
    public event Action<LiveUpdate>? Completed;
    public event Action<PipelineEvent>? Progress;

    // Dispatcher/render callbacks may run after a newer OCR result. Verify the
    // complete publication, including position, instead of just its text ID.
    public bool IsCurrent(SpatialBlock block)
    {
        lock (_sync) return !_closed && (!_expireStale || Stopwatch.GetTimestamp() <= _freshUntil)
            && _tracks.Any(x => x.Id == block.Id && x.Revision == block.Revision && x.Misses == 0
                && x.Block.Text == block.Source && x.Block.Bounds == block.Bounds
                && x.Translation == block.Translation && x.Error == block.Error);
    }

    public async Task RunAsync(CancellationToken ct, bool once = false)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _expireStale = !once; _closed = false;
        var completedSnapshot = false;
        using var queue = new WorkQueue(24);
        bool IsCurrent(Work work)
        {
            lock (_sync) return !_closed && !linked.IsCancellationRequested && (!_expireStale || Stopwatch.GetTimestamp() <= _freshUntil)
                && (!_expireStale || work.Timing == null || work.Timing.VerificationAgeMilliseconds <= Math.Clamp(staleAfterMs, 100, 10000))
                && _tracks.Any(x => x.Id == work.Id && x.Revision == work.Revision && x.Misses == 0);
        }
        PipelineEvent Metadata(PipelineEvent value, Work? work = null, FrameTiming? timing = null)
        {
            timing ??= work?.Timing;
            return value with
            {
                Frame = timing?.Frame ?? value.Frame, CaptureTimestamp = timing?.CapturedTimestamp,
                VerifiedTimestamp = timing?.VerifiedTimestamp, FrameVerificationAgeMilliseconds = timing?.VerificationAgeMilliseconds,
                FrameAgeMilliseconds = timing?.AgeMilliseconds, ColdStart = timing?.ColdStart,
                OcrEngine = timing?.Engine, FallbackReason = timing?.FallbackReason,
                QueueLength = queue.Count, DroppedCount = Interlocked.Read(ref _droppedCount),
                BlockId = work?.Id, Revision = work?.Revision, Area = area
            };
        }
        void Skip(Work work, string code, string message)
        {
            Interlocked.Increment(ref _droppedCount);
            lock (_sync)
            {
                var track = _tracks.FirstOrDefault(x => x.Id == work.Id && x.Revision == work.Revision);
                if (track != null) track.Retry = true;
            }
            Progress?.Invoke(Metadata(new(PipelineStage.Translation, PipelineState.Skipped, code, message), work));
        }
        async Task Produce()
        {
            try
            {
                do
                {
                    var clock = Stopwatch.StartNew();
                    var blocks = (await source.ReadBlocksAsync(linked.Token)).Where(x => x.Bounds.IsValid && !string.IsNullOrWhiteSpace(x.Text)).ToArray();
                    var timing = (source as IFrameTimingSource)?.CurrentFrame;
                    var stabilityStart = Stopwatch.GetTimestamp();
                    if (blocks.Length > maxBlocks) Progress?.Invoke(Metadata(new(PipelineStage.Stability, PipelineState.Warning, "BLOCK_LIMIT", $"检测到 {blocks.Length} 块，本轮最多处理 {maxBlocks} 块；可缩小选区"), timing: timing));
                    lock (_sync)
                    {
                        // A slow first OCR does not make its old source frame fresh.
                        // Snapshots intentionally retain their selected frame.
                        _freshUntil = (timing?.FreshnessTimestamp ?? Stopwatch.GetTimestamp())
                            + (long)(Math.Clamp(staleAfterMs, 100, 10000) * (Stopwatch.Frequency / 1000d));
                        var remaining = _tracks.ToList();
                        foreach (var block in blocks.Take(Math.Clamp(maxBlocks, 4, 24)))
                        {
                            var track = remaining.Where(x => x.Block.Bounds.Overlap(block.Bounds) >= .25)
                                .OrderByDescending(x => x.Block.Bounds.Overlap(block.Bounds)).FirstOrDefault();
                            if (track == null) { track = new(++_nextId, block) { CandidateTiming = timing }; _tracks.Add(track); }
                            remaining.Remove(track); track.Misses = 0;
                            if (track.Block.Text != block.Text)
                            { track.CandidateTiming = timing; track.CandidateTimestamp = Stopwatch.GetTimestamp(); }
                            if (!TextStabilizer.Similar(track.Block.Text, block.Text)) { track.Revision++; track.Translation = null; track.Error = null; track.ReadyTimestamp = 0; }
                            track.Block = block;
                            var decision = track.Stabilizer.Observe(block.Text, fastMode || once);
                            if (decision.Submit || track.Retry)
                            {
                                if (track.Retry) { track.CandidateTiming = timing; track.CandidateTimestamp = Stopwatch.GetTimestamp(); }
                                track.Revision++; track.Translation = null; track.Error = null; track.Retry = false; track.ReadyTimestamp = 0;
                                // Retain the originating frame through queues and UI. Repeated OCR
                                // of unchanged text must not make an old subtitle appear young.
                                track.Timing = track.CandidateTiming ?? timing;
                                var work = new Work(track.Id, track.Revision, block.Text, DateTimeOffset.Now, Stopwatch.GetTimestamp(), track.Timing);
                                if (queue.Enqueue(work) is { } dropped) Skip(dropped, "BLOCK_QUEUE_REPLACED", "已合并或丢弃待译的旧修订");
                                Progress?.Invoke(Metadata(new(PipelineStage.Stability, PipelineState.Success, "BLOCK_STABLE", "文本块已确认", Characters: block.Text.Length)
                                { DurationMilliseconds = FrameTiming.ElapsedMilliseconds(track.CandidateTimestamp) }, work));
                                Progress?.Invoke(Metadata(new(PipelineStage.Translation, PipelineState.Waiting, "BLOCK_QUEUED", "文本块已进入待译队列", Characters: block.Text.Length), work));
                            }
                        }
                        foreach (var track in remaining) track.Misses++;
                        _tracks.RemoveAll(x => x.Misses >= 2);
                        Publish();
                    }
                    Progress?.Invoke(Metadata(new(PipelineStage.Stability, PipelineState.Success, "BLOCKS_LOCATED", $"按位置分为 {Math.Min(blocks.Length, maxBlocks)} 个文本块", Characters: blocks.Sum(x => x.Text.Length))
                    { DurationMilliseconds = FrameTiming.ElapsedMilliseconds(stabilityStart) }, timing: timing));
                    if (once) break;
                    var delay = Math.Clamp(intervalMs, 100, 5000) - (int)clock.ElapsedMilliseconds;
                    if (delay > 0) await Task.Delay(delay, linked.Token);
                } while (!linked.IsCancellationRequested);
            }
            finally { queue.Complete(); }
        }
        async Task Consume()
        {
            while (await queue.ReadAsync(linked.Token) is { } work)
            {
                using var scope = new TranslationRequestScope(() => IsCurrent(work), work.Timing, work.Id, work.Revision,
                    () => queue.Count, () => Interlocked.Read(ref _droppedCount), area);
                Progress?.Invoke(Metadata(new(PipelineStage.Translation, PipelineState.Success, "BLOCK_QUEUE_WAIT", "文本块已出队", Characters: work.Text.Length)
                { DurationMilliseconds = FrameTiming.ElapsedMilliseconds(work.QueuedTimestamp) }, work));
                var clock = Stopwatch.StartNew();
                try
                {
                    TranslationRequestScope.EnsureCurrent();
                    Progress?.Invoke(Metadata(new(PipelineStage.Translation, PipelineState.Running, "BLOCK_TRANSLATING", $"正在翻译文本块 {work.Id}", Characters: work.Text.Length), work));
                    var translation = await translator.TranslateAsync(work.Text, linked.Token);
                    linked.Token.ThrowIfCancellationRequested();
                    Completed?.Invoke(new(work.Text, translation, CapturedAt: work.Time));
                    var applied = false;
                    lock (_sync)
                    {
                        var track = _tracks.FirstOrDefault(x => x.Id == work.Id && x.Revision == work.Revision && x.Misses == 0);
                        if (track != null && (!_expireStale || Stopwatch.GetTimestamp() <= _freshUntil))
                        { track.Translation = translation; track.Error = null; track.ReadyTimestamp = Stopwatch.GetTimestamp(); applied = true; Publish(); }
                    }
                    Progress?.Invoke(Metadata(new(PipelineStage.Translation, applied ? PipelineState.Success : PipelineState.Skipped,
                        applied ? "BLOCK_TRANSLATED" : "BLOCK_RESULT_STALE", applied ? "当前文本块译文返回" : "迟到译文仅保留在回放中", clock.ElapsedMilliseconds, work.Text.Length)
                    { DurationMilliseconds = clock.Elapsed.TotalMilliseconds }, work));
                }
                catch (StaleTranslationException) { Skip(work, "BLOCK_REQUEST_STALE", "发请求前发现修订已过时，已跳过"); }
                catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    lock (_sync)
                    {
                        var track = _tracks.FirstOrDefault(x => x.Id == work.Id && x.Revision == work.Revision);
                        if (track != null) { track.Error = LiveSession.FriendlyError(ex); Publish(); }
                    }
                    Progress?.Invoke(Metadata(new(PipelineStage.Translation, PipelineState.Error, ex is CloudApiException cloud ? cloud.Code : "BLOCK_TRANSLATION_FAILED", LiveSession.FriendlyError(ex))
                    { DurationMilliseconds = clock.Elapsed.TotalMilliseconds }, work));
                }
            }
        }
        async Task Expire()
        {
            while (!linked.IsCancellationRequested)
            {
                await Task.Delay(Math.Clamp(staleAfterMs / 3, 50, 250), linked.Token);
                lock (_sync) { if (_expireStale && Stopwatch.GetTimestamp() > _freshUntil) Publish(); }
            }
        }
        var producer = Produce(); var consumers = new[] { Consume(), Consume() };
        var expiration = once ? Task.CompletedTask : Expire();
        try
        {
            var first = await Task.WhenAny(producer, Task.WhenAll(consumers));
            await first;
            if (once) { await Task.WhenAll(consumers); completedSnapshot = true; }
            else ct.ThrowIfCancellationRequested();
        }
        finally
        {
            lock (_sync) _closed = !completedSnapshot;
            await linked.CancelAsync();
            try { await Task.WhenAll(consumers.Prepend(producer).Append(expiration)); } catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        }
    }

    private void Publish()
    {
        var blocks = (_closed || (_expireStale && Stopwatch.GetTimestamp() > _freshUntil) ? [] : _tracks.Where(x => x.Misses == 0)
            .Select(x => new SpatialBlock(x.Id, x.Block.Text, x.Block.Bounds, x.Translation, x.Error)
            { Timing = x.Timing, Revision = x.Revision, ReadyTimestamp = x.Translation != null ? x.ReadyTimestamp : 0 }).ToArray());
        if (_lastPublished != null && _lastPublished.SequenceEqual(blocks)) return;
        _lastPublished = blocks; Updated?.Invoke(blocks);
    }
}
