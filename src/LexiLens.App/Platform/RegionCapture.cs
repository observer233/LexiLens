using System.Diagnostics;
using LexiLens.Core;

namespace LexiLens.App.Platform;

internal enum CaptureBackend { Gdi, Wgc }

// Pixels are the ROI at native capture resolution, never a resized whole window.
// CapturedTimestamp uses Stopwatch/QPC; GDI records acquisition start because it
// has no compositor presentation timestamp. WGC supplies SystemRelativeTime.
internal sealed record CapturedRegionFrame(byte[] Bytes, int Width, int Height,
    CaptureRegion ScreenRegion, long CapturedTimestamp, double FrameWaitMs,
    double CaptureMs, string Backend, long DroppedFrames = 0,
    TimeSpan? SourceSystemRelativeTime = null)
{
    internal long Sequence { get; init; }
    internal long VerifiedTimestamp { get; init; }
    internal long SampledTimestamp { get; init; }
    internal bool IsRepeatedFrame { get; init; }
}

internal interface IRegionCapture : IDisposable
{
    ValueTask<CapturedRegionFrame> CaptureAsync(CancellationToken ct);
}

internal static class RegionCapture
{
    internal static IRegionCapture Create(CaptureBackend backend, nint hwnd, TextBounds roi)
    {
        if (!roi.IsValid || roi.Right > 1 || roi.Bottom > 1)
            throw new ArgumentException("字幕 ROI 必须位于目标窗口内（归一化坐标 0–1）。", nameof(roi));
        if (!NativeMethods.IsWindow(hwnd)) throw new ArgumentException("目标窗口已关闭或 HWND 无效。", nameof(hwnd));
        return backend switch
        {
            CaptureBackend.Gdi => new GdiRegionCapture(hwnd, roi),
            CaptureBackend.Wgc => new WgcRegionCapture(hwnd, roi),
            _ => throw new ArgumentOutOfRangeException(nameof(backend))
        };
    }

    internal static CaptureRegion WindowRegion(nint hwnd)
    {
        if (!NativeMethods.IsWindow(hwnd) || NativeMethods.IsIconic(hwnd) || !NativeMethods.IsWindowVisible(hwnd))
            throw new InvalidOperationException("目标窗口已关闭、隐藏或最小化；请恢复窗口后重试。");
        if (!NativeMethods.TryGetCaptureBounds(hwnd, out var bounds))
            throw new InvalidOperationException("无法读取目标窗口边界。");
        var region = new CaptureRegion(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
        if (!region.IsValid) throw new InvalidOperationException("目标窗口大小无效。");
        return region;
    }

    internal static CaptureRegion Crop(CaptureRegion window, TextBounds roi)
    {
        var left = Math.Clamp((int)Math.Floor(roi.X * window.Width), 0, window.Width - 1);
        var top = Math.Clamp((int)Math.Floor(roi.Y * window.Height), 0, window.Height - 1);
        var right = Math.Clamp((int)Math.Ceiling(roi.Right * window.Width), left + 1, window.Width);
        var bottom = Math.Clamp((int)Math.Ceiling(roi.Bottom * window.Height), top + 1, window.Height);
        return new(window.X + left, window.Y + top, right - left, bottom - top);
    }
}

internal sealed class GdiRegionCapture(nint hwnd, TextBounds roi) : IRegionCapture
{
    private bool _disposed;

    public ValueTask<CapturedRegionFrame> CaptureAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        var window = RegionCapture.WindowRegion(hwnd);
        var region = RegionCapture.Crop(window, roi);
        // GDI reads visible desktop pixels, so an occluded target is not equivalent
        // to HWND WGC. Never resize it or silently call this a WGC frame.
        if (!NativeMethods.IsOnDesktop(region))
            throw new InvalidOperationException("GDI 字幕 ROI 超出单个显示器；请移动窗口或改用 WGC。");
        var start = Stopwatch.GetTimestamp();
        var bytes = NativeMethods.Capture(region, int.MaxValue, out var width, out var height);
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new CapturedRegionFrame(bytes, width, height, region,
            start, 0, Stopwatch.GetElapsedTime(start).TotalMilliseconds, "GDI"));
    }

    public void Dispose() => _disposed = true;
}
