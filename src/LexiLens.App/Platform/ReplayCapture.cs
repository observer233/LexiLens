using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LexiLens.Core;

namespace LexiLens.App.Platform;

internal sealed record ReplayFrame(string File, double OffsetMs, CaptureRegion ScreenRegion, string? ReferenceText = null, string? Sha256 = null,
    double? SourceOffsetMs = null, double? VerifiedOffsetMs = null, bool IsRepeatedFrame = false);
internal sealed record ReplayManifest(IReadOnlyList<ReplayFrame> Frames, double DurationMs);

// Replays the latest recorded frame at its original relative time. It does not
// stretch short subtitles to accommodate OCR or issue synthetic HTTP responses.
internal sealed class ReplayCapture : IRegionCapture
{
    private readonly string _directory;
    private readonly ReplayManifest _manifest;
    private long _started;
    private int _index = -1;
    private byte[]? _bytes;
    private int _width, _height;
    internal double DurationMs => _manifest.DurationMs;
    internal ReplayManifest Manifest => _manifest;
    internal event Action? Started;

    internal ReplayCapture(string path)
    {
        _directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        _manifest = JsonSerializer.Deserialize<ReplayManifest>(System.IO.File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("回放清单为空。");
        if (_manifest.Frames.Count == 0 || !double.IsFinite(_manifest.DurationMs) || _manifest.DurationMs <= _manifest.Frames[^1].OffsetMs || _manifest.Frames.Any(f => !double.IsFinite(f.OffsetMs) || f.OffsetMs < 0 || !f.ScreenRegion.IsValid
                || f.SourceOffsetMs is { } s && (!double.IsFinite(s) || s > f.OffsetMs + 20)
                || f.VerifiedOffsetMs is { } v && (!double.IsFinite(v) || v > f.OffsetMs + 20)
                || f.IsRepeatedFrame && f.VerifiedOffsetMs == null)
            || _manifest.Frames.Zip(_manifest.Frames.Skip(1)).Any(p => p.First.OffsetMs >= p.Second.OffsetMs))
            throw new InvalidDataException("回放必须有明确的 DurationMs、按 OffsetMs 严格递增的帧和有效屏幕区域。");
    }

    public async ValueTask<CapturedRegionFrame> CaptureAsync(CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        if (_started == 0) { _started = started; Started?.Invoke(); }
        var elapsed = FrameTiming.ElapsedMilliseconds(_started);
        if (elapsed < _manifest.Frames[0].OffsetMs)
            await Task.Delay(TimeSpan.FromMilliseconds(_manifest.Frames[0].OffsetMs - elapsed), ct);
        var loadStarted = Stopwatch.GetTimestamp();
        var frameWaitMs = FrameTiming.ElapsedMilliseconds(started, loadStarted);
        elapsed = FrameTiming.ElapsedMilliseconds(_started);
        var selected = 0;
        while (selected + 1 < _manifest.Frames.Count && _manifest.Frames[selected + 1].OffsetMs <= elapsed) selected++;
        var frame = _manifest.Frames[selected];
        if (_index != selected)
        {
            var path = Path.GetFullPath(Path.Combine(_directory, frame.File));
            if (!Path.GetRelativePath(_directory, path).StartsWith("..", StringComparison.Ordinal) && System.IO.File.Exists(path))
            {
                if (frame.Sha256 is { Length: > 0 } hash && !Convert.ToHexString(SHA256.HashData(await System.IO.File.ReadAllBytesAsync(path, ct))).Equals(hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("回放帧 SHA256 不一致。");
                using var stream = System.IO.File.OpenRead(path);
                var bitmap = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
                _width = converted.PixelWidth; _height = converted.PixelHeight;
                if (_width > 16384 || _height > 16384 || (long)_width * _height * 4 > 134_217_728) throw new InvalidDataException("回放帧过大。");
                _bytes = new byte[checked(_width * _height * 4)]; converted.CopyPixels(_bytes, _width * 4, 0);
                _index = selected;
            }
            else throw new InvalidDataException("回放帧必须是清单目录内的现有图片。");
        }
        return new(_bytes!, _width, _height, frame.ScreenRegion,
            _started + (long)((frame.SourceOffsetMs ?? frame.OffsetMs) * Stopwatch.Frequency / 1000d), frameWaitMs, FrameTiming.ElapsedMilliseconds(loadStarted), "Replay")
        { VerifiedTimestamp = _started + (long)((frame.VerifiedOffsetMs ?? frame.SourceOffsetMs ?? frame.OffsetMs) * Stopwatch.Frequency / 1000d), IsRepeatedFrame = frame.IsRepeatedFrame };
    }

    internal ReplayFrame? FrameFor(FrameTiming? timing)
    {
        if (timing == null) return null;
        var offset = (timing.FreshnessTimestamp - _started) * 1000d / Stopwatch.Frequency;
        return _manifest.Frames.MinBy(f => Math.Abs((f.VerifiedOffsetMs ?? f.SourceOffsetMs ?? f.OffsetMs) - offset));
    }
    public void Dispose() { _bytes = null; }
}
