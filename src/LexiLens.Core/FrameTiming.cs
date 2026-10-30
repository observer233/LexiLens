using System.Diagnostics;

namespace LexiLens.Core;

// Stopwatch timestamps share one monotonic timebase across capture/OCR/UI threads.
// They are process-local; wall-clock Time is for locating a log, never durations.
public sealed record FrameTiming(long Frame, long CapturedTimestamp, bool ColdStart = false)
{
    public long CaptureStartedTimestamp { get; init; }
    public long OcrCompletedTimestamp { get; init; }
    // A static WGC surface may have an old compositor timestamp while being
    // verified as the latest available surface now. Never overwrite its origin.
    public long VerifiedTimestamp { get; init; }
    public string? Engine { get; init; }
    public string? FallbackReason { get; init; }
    public double AgeMilliseconds => ElapsedMilliseconds(CapturedTimestamp);
    public long FreshnessTimestamp => VerifiedTimestamp > 0 ? VerifiedTimestamp : CapturedTimestamp;
    public double VerificationAgeMilliseconds => ElapsedMilliseconds(FreshnessTimestamp);
    public static double ElapsedMilliseconds(long start, long? end = null) =>
        start > 0 ? Math.Max(0, ((end ?? Stopwatch.GetTimestamp()) - start) * 1000d / Stopwatch.Frequency) : 0;
}

public interface IFrameTimingSource
{
    FrameTiming? CurrentFrame { get; }
}

public sealed class StaleTranslationException() : OperationCanceledException("Translation revision is no longer current.");

// Preserve ITranslator and existing provider interfaces while checking again
// after shared concurrency/rate waits, immediately before spending a request.
public sealed class TranslationRequestScope : IDisposable
{
    private static readonly AsyncLocal<TranslationRequestScope?> Ambient = new();
    private readonly TranslationRequestScope? _previous;
    private readonly Func<bool> _isCurrent;
    private readonly Func<int>? _queueLength;
    private readonly Func<long>? _droppedCount;
    public FrameTiming? Timing { get; }
    public int BlockId { get; }
    public int Revision { get; }
    public int Area { get; }
    public static TranslationRequestScope? Current => Ambient.Value;

    public TranslationRequestScope(Func<bool> isCurrent, FrameTiming? timing = null, int blockId = 0, int revision = 0,
        Func<int>? queueLength = null, Func<long>? droppedCount = null, int area = 0)
    {
        _previous = Ambient.Value; _isCurrent = isCurrent; Timing = timing; BlockId = blockId; Revision = revision;
        Area = area;
        _queueLength = queueLength; _droppedCount = droppedCount; Ambient.Value = this;
    }

    public static void EnsureCurrent() { if (Current is { } scope && !scope._isCurrent()) throw new StaleTranslationException(); }
    public static PipelineEvent Enrich(PipelineEvent value)
    {
        if (Current is not { } scope) return value;
        return value with
        {
            Frame = scope.Timing?.Frame ?? value.Frame,
            CaptureTimestamp = scope.Timing?.CapturedTimestamp,
            VerifiedTimestamp = scope.Timing?.VerifiedTimestamp,
            FrameVerificationAgeMilliseconds = scope.Timing?.VerificationAgeMilliseconds,
            FrameAgeMilliseconds = scope.Timing?.AgeMilliseconds,
            ColdStart = scope.Timing?.ColdStart,
            OcrEngine = scope.Timing?.Engine,
            FallbackReason = scope.Timing?.FallbackReason,
            QueueLength = scope._queueLength?.Invoke(), DroppedCount = scope._droppedCount?.Invoke(),
            BlockId = scope.BlockId, Revision = scope.Revision, Area = scope.Area
        };
    }
    public void Dispose() => Ambient.Value = _previous;
}
