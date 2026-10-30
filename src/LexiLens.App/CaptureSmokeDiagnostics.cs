using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LexiLens.App.Platform;
using LexiLens.App.Views;
using LexiLens.Core;

namespace LexiLens.App;

// Engineering-only pixel/geometry checks. No OCR text, game footage, translator,
// user profile or HTTP is used; this is not an end-to-end game benchmark.
internal static class CaptureSmokeDiagnostics
{
    internal static async Task RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new Dictionary<string, object>();
        var timings = new List<object>();
        var errors = new List<string>();
        var overlayEvents = new List<PipelineEvent>();
        var target = new Window
        {
            Title = "LexiLens Capture Engineering Pattern", Width = 640, Height = 360,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, ShowActivated = false, Topmost = true,
            Background = Brushes.Black
        };
        var grid = new UniformGrid { Rows = 3, Columns = 4 };
        var colors = new[] { Colors.DarkRed, Colors.DarkGreen, Colors.DarkBlue, Colors.Goldenrod,
            Colors.DarkCyan, Colors.DarkMagenta, Colors.SaddleBrown, Colors.DarkSlateGray,
            Colors.LimeGreen, Colors.OrangeRed, Colors.DodgerBlue, Colors.Gold };
        foreach (var color in colors) grid.Children.Add(new Border { Background = new SolidColorBrush(color) });
        target.Content = grid;
        Window? overlay = null;
        IRegionCapture? gdi = null, wgc = null;
        SpatialOverlayWindow? spatial = null;
        var originalForeground = NativeMethods.GetForegroundWindow();
        try
        {
            CheckGeometry(checks);
            target.Show();
            var hwnd = new WindowInteropHelper(target).Handle;
            var monitor = NativeMethods.Monitors().OrderByDescending(m => (long)(m.Right - m.Left) * (m.Bottom - m.Top)).First();
            var x = monitor.Left + 40; var y = monitor.Top + 50;
            NativeMethods.SetWindowPos(hwnd, -1, x, y, 640, 360, 0x10);
            await PaintAsync();
            var roi = new TextBounds(.1, .6, .8, .3);
            var initialWindow = RegionCapture.WindowRegion(hwnd);
            var expectedInitial = RegionCapture.Crop(initialWindow, roi);
            var dpiBefore = NativeMethods.GetDpiForWindow(hwnd);
            gdi = RegionCapture.Create(CaptureBackend.Gdi, hwnd, roi);
            var baseline = await gdi.CaptureAsync(CancellationToken.None);
            Save(baseline, Path.Combine(directory, "gdi-roi.png"));
            checks["gdiNativeRoi"] = baseline.Width == expectedInitial.Width && baseline.Height == expectedInitial.Height && baseline.ScreenRegion == expectedInitial;
            checks["gdiPattern"] = HasPattern(baseline);
            checks["initialPhysicalWindow"] = initialWindow;
            checks["initialPhysicalRoi"] = expectedInitial;
            var startup = Stopwatch.GetTimestamp();
            wgc = RegionCapture.Create(CaptureBackend.Wgc, hwnd, roi);
            var first = await wgc.CaptureAsync(CancellationToken.None);
            timings.Add(Sample("WGC", "cold", first, Stopwatch.GetElapsedTime(startup).TotalMilliseconds));
            Save(first, Path.Combine(directory, "wgc-roi.png"));
            checks["wgcNativeRoi"] = first.Width == expectedInitial.Width && first.Height == expectedInitial.Height && first.ScreenRegion == expectedInitial;
            checks["wgcCompositorTimestamp"] = first.SourceSystemRelativeTime != null && first.CapturedTimestamp <= Stopwatch.GetTimestamp() && Stopwatch.GetElapsedTime(first.CapturedTimestamp).TotalSeconds < 3;
            checks["wgcPattern"] = HasPattern(first);
            checks["gdiWgcRgbAgreement"] = SameRgb(baseline, first);

            overlay = new Window
            {
                Title = "LexiLens Capture Exclusion Check", WindowStyle = WindowStyle.None,
                AllowsTransparency = true, Background = Brushes.Magenta, ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false, ShowActivated = false, Topmost = true, Width = 200, Height = 80
            };
            var excluded = false;
            overlay.SourceInitialized += (_, _) =>
            {
                excluded = NativeMethods.ExcludeFromCapture(overlay);
                NativeMethods.SetClickThrough(overlay, true);
            };
            overlay.Show();
            var overlayHwnd = new WindowInteropHelper(overlay).Handle;
            NativeMethods.SetWindowPos(overlayHwnd, -1, expectedInitial.X, expectedInitial.Y,
                expectedInitial.Width, expectedInitial.Height, 0x10);
            await PaintAsync();
            checks["exclusionApiAccepted"] = excluded;
            var styles = NativeMethods.GetWindowLong(overlayHwnd, -20);
            checks["overlayClickThroughAndNoActivateStyles"] = (styles & (0x20 | 0x08000000)) == (0x20 | 0x08000000);
            checks["foregroundUnchangedByShows"] = NativeMethods.GetForegroundWindow() == originalForeground;
            var underGdi = await gdi.CaptureAsync(CancellationToken.None);
            // Force a fresh target frame without changing its pixels.
            target.InvalidateVisual();
            var underWgc = await wgc.CaptureAsync(CancellationToken.None);
            checks["gdiExclusionPixelsUnchanged"] = SameRgb(baseline, underGdi);
            checks["wgcOwnOverlayPixelsUnchanged"] = SameRgb(first, underWgc);
            Save(underGdi, Path.Combine(directory, "gdi-overlay-excluded.png"));
            Save(underWgc, Path.Combine(directory, "wgc-overlay-excluded.png"));
            overlay.Close(); overlay = null;

            var staticFirst = await wgc.CaptureAsync(CancellationToken.None);
            await Task.Delay(100);
            var staticAgain = await wgc.CaptureAsync(CancellationToken.None);
            checks["staticFrameReusableWithoutFakeTimestamp"] = staticAgain.IsRepeatedFrame && staticAgain.CapturedTimestamp == staticFirst.CapturedTimestamp;
            timings.Add(Sample("WGC", "static-repeat", staticAgain));
            NativeMethods.SetWindowPos(hwnd, -1, x + 15, y + 17, 640, 360, 0x10);
            await PaintAsync();
            var moveOnly = await wgc.CaptureAsync(CancellationToken.None);
            checks["wgcMoveWithoutResizeAligned"] = moveOnly.ScreenRegion == RegionCapture.Crop(RegionCapture.WindowRegion(hwnd), roi) && SameRgb(first, moveOnly);

            NativeMethods.SetWindowPos(hwnd, -1, x + 72, y + 55, 760, 440, 0x10);
            await PaintAsync();
            var movedWindow = RegionCapture.WindowRegion(hwnd);
            var expectedMoved = RegionCapture.Crop(movedWindow, roi);
            var movedGdi = await gdi.CaptureAsync(CancellationToken.None);
            CapturedRegionFrame movedWgc = first;
            // A resize may produce one old-size frame; retain all samples and wait
            // for a matching surface rather than asserting against a transition.
            for (var attempt = 0; attempt < 6; attempt++)
            {
                target.InvalidateVisual();
                movedWgc = await wgc.CaptureAsync(CancellationToken.None);
                timings.Add(Sample("WGC", "resize", movedWgc));
                if (movedWgc.ScreenRegion == expectedMoved && movedWgc.Width == expectedMoved.Width && movedWgc.Height == expectedMoved.Height) break;
                await PaintAsync();
            }
            checks["movedPhysicalWindow"] = movedWindow;
            checks["movedPhysicalRoi"] = expectedMoved;
            checks["gdiMoveResizeAligned"] = movedGdi.ScreenRegion == expectedMoved && movedGdi.Width == expectedMoved.Width && movedGdi.Height == expectedMoved.Height;
            checks["wgcMoveResizeAligned"] = movedWgc.ScreenRegion == expectedMoved && movedWgc.Width == expectedMoved.Width && movedWgc.Height == expectedMoved.Height;
            checks["moveResizeRgbAgreement"] = SameRgb(movedGdi, movedWgc);
            checks["dpiBefore"] = dpiBefore; checks["dpiAfter"] = NativeMethods.GetDpiForWindow(hwnd);
            checks["actualDpiChangeExercised"] = dpiBefore != NativeMethods.GetDpiForWindow(hwnd);
            Save(movedWgc, Path.Combine(directory, "wgc-moved-resized.png"));
            for (var i = 0; i < 8; i++)
            {
                // Change one cell to guarantee frame arrival on a static scene.
                ((Border)grid.Children[0]).Background = i % 2 == 0 ? Brushes.White : Brushes.DarkRed;
                await PaintAsync();
                var warmGdi = await gdi.CaptureAsync(CancellationToken.None);
                var warmWgc = await wgc.CaptureAsync(CancellationToken.None);
                timings.Add(Sample("GDI", "warm", warmGdi)); timings.Add(Sample("WGC", "warm", warmWgc));
            }
            var spatialBaseline = await gdi.CaptureAsync(CancellationToken.None);
            spatial = new SpatialOverlayWindow(expectedMoved, new AppSettings { OverlayMode = OverlayMode.InPlace, FontSize = 21 }) { Progress = overlayEvents.Add };
            var shortBlock = new SpatialBlock(1, "", new(.1, .1, .7, .3), "位置验证")
                { Revision = 1, Timing = new(1001, Stopwatch.GetTimestamp()) };
            spatial.Show(); spatial.SetBlocks([shortBlock]);
            await PaintAsync();
            checks["productOverlayShortLabelRendered"] = spatial.RenderedCount == 1 && overlayEvents.Any(e => e.Code == "OVERLAY_VISIBLE" && e.Revision == 1);
            checks["productOverlayInitialBoundsAligned"] = OverlayAligned(spatial, expectedMoved, shortBlock.Bounds);
            checks["productOverlayGdiExcluded"] = spatial.CaptureExcluded && SameRgb(spatialBaseline, await gdi.CaptureAsync(CancellationToken.None));
            checks["productOverlayWgcExcluded"] = SameRgb(spatialBaseline, await wgc.CaptureAsync(CancellationToken.None));
            NativeMethods.SetWindowPos(hwnd, -1, x + 88, y + 66, 800, 480, 0x10);
            await PaintAsync();
            var resizedAgain = await wgc.CaptureAsync(CancellationToken.None);
            spatial.SetRegion(resizedAgain.ScreenRegion);
            spatial.SetBlocks([shortBlock with { Revision = 2, Timing = new(1002, Stopwatch.GetTimestamp()) }]);
            await PaintAsync();
            checks["productOverlayMoveResizeBoundsAligned"] = OverlayAligned(spatial, resizedAgain.ScreenRegion, shortBlock.Bounds);
            var longBlock = shortBlock with { Bounds = new(.1, .1, .15, .1), Revision = 3,
                Translation = string.Concat(Enumerable.Repeat("这是用于检查裁剪的诊断标签", 100)), Timing = new(1003, Stopwatch.GetTimestamp()) };
            spatial.SetBlocks([longBlock]);
            await PaintAsync();
            checks["productOverlayLongLabelLimitReported"] = overlayEvents.Any(e => e.Code == "OVERLAY_LAYOUT_LIMIT" && e.Revision == 3);
            checks["productOverlayClippedLabelNotCountedAsEffective"] = !overlayEvents.Any(e => e.Code == "OVERLAY_VISIBLE" && e.Revision == 3);
            spatial.Hide();
            spatial.SetBlocks([shortBlock with { Revision = 4, Timing = new(1004, Stopwatch.GetTimestamp()) }]);
            await PaintAsync();
            checks["productOverlayHiddenLabelNotCountedAsEffective"] = !overlayEvents.Any(e => e.Code == "OVERLAY_VISIBLE" && e.Revision == 4);
            spatial.Close(); spatial = null;
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { _ = await wgc.CaptureAsync(cancelled.Token); checks["cancelHonored"] = false; }
            catch (OperationCanceledException) { checks["cancelHonored"] = true; }
            target.Close();
            await Task.Delay(100);
            try { _ = await wgc.CaptureAsync(CancellationToken.None); checks["closedTargetRejected"] = false; }
            catch (InvalidOperationException) { checks["closedTargetRejected"] = true; }
        }
        catch (Exception ex) { errors.Add(ex.ToString()); }
        finally
        {
            wgc?.Dispose(); gdi?.Dispose(); overlay?.Close(); spatial?.Close(); target.Close();
            await File.WriteAllTextAsync(Path.Combine(directory, "capture-smoke.json"), JsonSerializer.Serialize(new
            {
                evidenceKind = "engineering-smoke-no-game-no-service", utc = DateTimeOffset.UtcNow,
                success = errors.Count == 0 && checks.Where(x => x.Value is bool && x.Key != "actualDpiChangeExercised").All(x => (bool)x.Value),
                operatingSystem = Environment.OSVersion.VersionString, stopwatchFrequency = Stopwatch.Frequency,
                checks, timings, overlayEvents, errors,
                limitations = new[] { "No real game, OCR accuracy, translation request, subtitle miss/stale rate or game frame-time conclusion.",
                    "No actual hardware hotkey or drag selection; no exclusive fullscreen/HDR/mixed-DPI coverage.",
                    "WGC dropped count covers application coalescing/resize only; compositor internal loss is not observable.",
                    "Frame age uses WGC compositor QPC; GDI uses acquisition start and cannot measure compositor age." }
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static async Task PaintAsync()
    {
        await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        await Task.Delay(120);
    }

    private static void CheckGeometry(IDictionary<string, object> checks)
    {
        static bool Near(double actual, double expected) => Math.Abs(actual - expected) < .002;
        var horizontal = LocalOcr.AxisAlignedBox("r0", new(new(100, 50), new(160, 20), 0));
        var quarterTurn = LocalOcr.AxisAlignedBox("r0", new(new(100, 50), new(20, 160), 90));
        var diagonal = LocalOcr.AxisAlignedBox("r0", new(new(100, 50), new(160, 20), 45));
        checks["geometryHorizontalBounds"] = Near(horizontal.Width, 160) && Near(horizontal.Height, 20);
        checks["geometryRightAngleEquivalentBounds"] = Near(quarterTurn.Width, 160) && Near(quarterTurn.Height, 20)
            && Near(quarterTurn.CenterX, 100) && Near(quarterTurn.CenterY, 50);
        checks["geometryDiagonalProjectedBounds"] = Near(diagonal.Width, 180 / Math.Sqrt(2)) && Near(diagonal.Height, 180 / Math.Sqrt(2));

        // Actual detector coordinates from the recorded game ROI (1538×358,
        // prepared 2048×477). Region IDs replace recognized text. The middle
        // horizontal line had a near-90° rectangle and was sorted above the first
        // line when its local height was mistaken for its screen-space height.
        var fixture = new[]
        {
            (Id: "r3", Rect: new OpenCvSharp.RotatedRect(new(895.5096f, 251.74251f), new(275.0908f, 70.164856f), .43405068f)),
            (Id: "r5", Rect: new OpenCvSharp.RotatedRect(new(1061.5615f, 339.53638f), new(69.30342f, 607.9411f), 89.53038f)),
            (Id: "r6", Rect: new OpenCvSharp.RotatedRect(new(884.588f, 431.24564f), new(172.50735f, 84.24228f), 1.5481577f))
        };
        var oldBoxes = fixture.Select(x => new OcrTextBox(x.Id, x.Rect.Center.X, x.Rect.Center.Y, x.Rect.Size.Height, x.Rect.Size.Width));
        var fixedBoxes = fixture.Select(x => LocalOcr.AxisAlignedBox(x.Id, x.Rect));
        var oldOrder = string.Join("\n", ParagraphLayout.Group(oldBoxes, 2048, 477).Select(x => x.Text));
        var fixedOrder = string.Join("\n", ParagraphLayout.Group(fixedBoxes, 2048, 477).Select(x => x.Text));
        checks["geometryActualDetectionOrderRegression"] = oldOrder.IndexOf("r5", StringComparison.Ordinal) < oldOrder.IndexOf("r3", StringComparison.Ordinal)
            && fixedOrder.IndexOf("r3", StringComparison.Ordinal) < fixedOrder.IndexOf("r5", StringComparison.Ordinal)
            && fixedOrder.IndexOf("r5", StringComparison.Ordinal) < fixedOrder.IndexOf("r6", StringComparison.Ordinal);
    }

    private static object Sample(string backend, string phase, CapturedRegionFrame frame, double? startupMs = null) => new
    {
        backend, phase, startupMs, frame.FrameWaitMs, frame.CaptureMs,
        frameAgeMs = Stopwatch.GetElapsedTime(frame.CapturedTimestamp).TotalMilliseconds,
        frame.DroppedFrames, frame.IsRepeatedFrame, frame.Width, frame.Height,
        sourceCompositor100ns = frame.SourceSystemRelativeTime?.Ticks
    };

    private static bool HasPattern(CapturedRegionFrame frame)
    {
        var colors = new HashSet<int>();
        for (var y = 0; y < frame.Height; y += Math.Max(1, frame.Height / 8))
            for (var x = 0; x < frame.Width; x += Math.Max(1, frame.Width / 12))
            { var offset = (y * frame.Width + x) * 4; colors.Add(frame.Bytes[offset] << 16 | frame.Bytes[offset + 1] << 8 | frame.Bytes[offset + 2]); }
        return colors.Count >= 4;
    }

    private static bool SameRgb(CapturedRegionFrame a, CapturedRegionFrame b)
    {
        if (a.Width != b.Width || a.Height != b.Height || a.Bytes.Length != b.Bytes.Length) return false;
        // Alpha differs across GDI and WGC; SDR color rounding may differ by 1–2.
        var outsideTolerance = 0;
        for (var pixel = 0; pixel < a.Bytes.Length; pixel += 4)
            if (Math.Abs(a.Bytes[pixel] - b.Bytes[pixel]) > 3 || Math.Abs(a.Bytes[pixel + 1] - b.Bytes[pixel + 1]) > 3 || Math.Abs(a.Bytes[pixel + 2] - b.Bytes[pixel + 2]) > 3) outsideTolerance++;
        return outsideTolerance <= a.Width * a.Height / 1000;
    }

    private static bool OverlayAligned(SpatialOverlayWindow overlay, CaptureRegion region, TextBounds bounds)
    {
        if (overlay.Content is not Canvas canvas || canvas.Children.Count != 1 || canvas.Children[0] is not Border border) return false;
        var dpi = VisualTreeHelper.GetDpi(overlay);
        var position = border.PointToScreen(new(0, 0));
        return Math.Abs(position.X - (region.X + bounds.X * region.Width - 3 * dpi.DpiScaleX)) <= 2
            && Math.Abs(position.Y - (region.Y + bounds.Y * region.Height - 3 * dpi.DpiScaleY)) <= 2
            && Math.Abs(border.ActualWidth * dpi.DpiScaleX - (bounds.Width * region.Width + 6 * dpi.DpiScaleX)) <= 2;
    }

    private static void Save(CapturedRegionFrame frame, string path)
    {
        var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, frame.Bytes, frame.Width * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
