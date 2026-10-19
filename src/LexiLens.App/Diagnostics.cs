using System.IO;
using System.Net.Http;
using System.Net;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LexiLens.App.Platform;
using LexiLens.App.Views;
using LexiLens.Core;
using Windows.Globalization;
using Windows.Media.Ocr;

namespace LexiLens.App;

internal static class Diagnostics
{
    internal static async Task RunAsync(MainWindow window, string directory)
    {
        Directory.CreateDirectory(directory);
        var report = new Dictionary<string, object?>();
        try
        {
            await Task.Delay(250);
            Render(window, Path.Combine(directory, "main.png"));
            report["mainRendered"] = true;
            window.RenderSettingsForDiagnostics();
            Render(window, Path.Combine(directory, "settings.png"));
            window.RenderLiveForDiagnostics();
            var vault = new CredentialVault(Path.Combine(directory, "vault-test"));
            const string vaultScope = "diagnostic|https://example.invalid/";
            const string dummyKey = "diagnostic-not-a-real-key";
            vault.Save(vaultScope, dummyKey);
            report["credentialVaultRoundTrip"] = vault.Read(vaultScope) == dummyKey;
            var vaultBytes = File.ReadAllBytes(Path.Combine(directory, "vault-test", "credentials.dat"));
            report["credentialVaultEncrypted"] = !System.Text.Encoding.UTF8.GetString(vaultBytes).Contains(dummyKey, StringComparison.Ordinal);
            vault.Save(vaultScope, "");
            report["credentialVaultDeletion"] = vault.Read(vaultScope) == "";
            vault.Save("deepseek|https://api.deepseek.com/v1/", dummyKey);
            var migratedScope = CredentialVault.Scope("deepseek", new Uri("https://api.deepseek.com/"));
            report["deepseekCredentialUpgrade"] = vault.Read(migratedScope) == dummyKey;
            vault.Save(migratedScope, "");
            report["deepseekCredentialUpgrade"] = report["deepseekCredentialUpgrade"] is true && vault.Read("deepseek|https://api.deepseek.com/v1/") == "";
            using var mockHttp = new HttpClient(new DiagnosticHandler());
            var wizard = new ProviderSetupWindow(new AppSettings(), mockHttp, vault) { Owner = window };
            wizard.Show();
            report["providerWizardInteraction"] = await wizard.VerifyForDiagnosticsAsync();
            Render(wizard, Path.Combine(directory, "providers.png"));
            wizard.Close();
            var applyWizard = new ProviderSetupWindow(new AppSettings(), mockHttp, vault) { Owner = window };
            var watchdog = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            watchdog.Tick += (_, _) => { watchdog.Stop(); applyWizard.Close(); };
            applyWizard.Loaded += async (_, _) => await applyWizard.ApplyForDiagnosticsAsync();
            watchdog.Start();
            var applied = applyWizard.ShowDialog();
            watchdog.Stop();
            report["providerWizardApply"] = applied == true && applyWizard.SelectedSettings?.Provider == TranslationProvider.Cloud
                && vault.Read(CredentialVault.Scope("zhipu", new Uri("https://open.bigmodel.cn/api/paas/v4/"))) == dummyKey;
            var overlay = new OverlayWindow();
            overlay.SetContent(PracticeWindow.Samples[0].Text, PracticeWindow.Samples[0].Translation, "示例预览 · 非实时识别");
            overlay.Show();
            await Task.Delay(150);
            Render(overlay, Path.Combine(directory, "overlay.png"));
            report["overlayRendered"] = true;
            report["captureExclusion"] = overlay.CaptureExcluded;
            overlay.SetLocked(true);
            report["clickThrough"] = (NativeMethods.GetWindowLong(new WindowInteropHelper(overlay).Handle, -20) & 0x20) != 0;
            overlay.SetLocked(false);
            report["clickThroughUnlock"] = (NativeMethods.GetWindowLong(new WindowInteropHelper(overlay).Handle, -20) & 0x20) == 0;
            overlay.Close();

            var languages = ScreenTextSource.Languages();
            report["ocrLanguages"] = languages.Select(x => x.Tag).ToArray();
            var engine = OcrEngine.TryCreateFromLanguage(new Language("en-US"));
            report["localOcrFontDiagnostics"] = await OcrFontDiagnostics.RunAsync(directory, engine);
            report["englishOcrAvailable"] = engine != null;
            if (engine != null)
            {
                var text = new TextBlock { Text = "Find the ancient relic before dawn.", FontFamily = new FontFamily("Segoe UI"), FontSize = 34, Foreground = Brushes.Black, Background = Brushes.White, Padding = new Thickness(20) };
                text.Measure(new Size(1000, 100)); text.Arrange(new Rect(0, 0, 1000, 100));
                var image = new RenderTargetBitmap(1000, 100, 96, 96, PixelFormats.Pbgra32);
                image.Render(text);
                var pixels = new byte[1000 * 100 * 4]; image.CopyPixels(pixels, 4000, 0);
                var recognitionPreview = new RecognitionPreviewWindow(pixels, 1000, 100, "Find the ancient relic before dawn.", "本地识别检查 · 固定诊断样张") { Owner = window };
                recognitionPreview.Show();
                Render(recognitionPreview, Path.Combine(directory, "recognition-preview.png"));
                recognitionPreview.Close();
                var recognized = await ScreenTextSource.RecognizeBytesAsync(engine, pixels, 1000, 100, CancellationToken.None);
                report["generatedImageOcr"] = recognized;
                report["generatedImageOcrPassed"] = recognized.Contains("ancient relic", StringComparison.OrdinalIgnoreCase);

                var practice = new PracticeWindow { Topmost = true }; practice.Show(); practice.Activate();
                await Task.Delay(500);
                var practiceHandle = new WindowInteropHelper(practice).Handle;
                var snapped = await WindowPicker.SelectAsync(practiceHandle, picker => Render(picker, Path.Combine(directory, "window-picker.png")));
                report["windowSnapSelectionPassed"] = snapped?.IsValid == true && NativeMethods.IsOnDesktop(snapped)
                    && snapped.Width > 300 && snapped.Height > 200;
                var region = practice.SubtitleRegion();
                using var source = new ScreenTextSource(region, "en-US", PracticeWindow.Samples[0].Text);
                var captured = await source.ReadAsync(CancellationToken.None);
                report["desktopCaptureOcr"] = captured;
                report["desktopCaptureOcrEngine"] = source.LastRecognition?.Engine;
                report["enhancedOcrCoordinatesPassed"] = source.LastRecognition?.Blocks?.Any(x => x.Bounds.IsValid) == true;
                report["desktopCaptureOcrPassed"] = TextProcessing.Normalize(captured).Contains("ancient relic", StringComparison.OrdinalIgnoreCase);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(55));
                var live = await window.VerifyLiveSessionAsync(region, timeout.Token);
                report["liveSource"] = live.Source;
                report["liveTranslation"] = live.Translation;
                report["liveSessionPassed"] = live.Source.Contains("ancient relic", StringComparison.OrdinalIgnoreCase)
                    && live.Translation.Any(c => c >= '\u4E00' && c <= '\u9FFF')
                    && !live.Translation.Contains("未完成");
                var once = await window.VerifyLiveSessionAsync(region, timeout.Token, once: true);
                report["singleTranslationPassed"] = once.Source == live.Source && once.Translation == live.Translation;
                var worker = new OcrWorker(stallForDiagnostics: true, initialTimeoutMs: 3000);
                var faultEvents = new List<PipelineEvent>();
                using (var stalledSource = new ScreenTextSource(region, "en-US", PracticeWindow.Samples[0].Text, workerFactory: () => worker)
                { Progress = item => { lock (faultEvents) faultEvents.Add(item); window.RecordForDiagnostics(item); } })
                {
                    var recovered = await stalledSource.ReadAsync(CancellationToken.None);
                    report["ocrTimeoutFallbackPassed"] = worker.WasConnected && worker.WasTerminated && recovered.Contains("ancient relic", StringComparison.OrdinalIgnoreCase)
                        && stalledSource.LastRecognition?.Engine.StartsWith("Windows OCR", StringComparison.Ordinal) == true
                        && faultEvents.Any(x => x.Code == "OCR_TIMEOUT_FALLBACK");
                }
                using (var cancelledWorker = new OcrWorker(stallForDiagnostics: true))
                using (var cancel = new CancellationTokenSource(150))
                {
                    try { await cancelledWorker.ReadAsync(new byte[80 * 40 * 4], 80, 40, OcrImageMode.Auto, cancel.Token); }
                    catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
                    report["ocrWorkerCancellationPassed"] = cancelledWorker.WasTerminated;
                }
                window.RenderLogForDiagnostics(); await Task.Delay(250); Render(window, Path.Combine(directory, "diagnostics.png"));
                report["diagnosticColumnWidths"] = window.DiagnosticColumnWidths();
                report["diagnosticLayoutPassed"] = window.DiagnosticColumnWidths().Length == 7 && window.DiagnosticColumnWidths().Take(4).All(x => x >= 50);
                var exported = window.ExportDiagnosticReport();
                File.WriteAllText(Path.Combine(directory, "diagnostic-export.json"), exported);
                report["diagnosticExportPassed"] = exported.Contains("OCR_TIMEOUT_FALLBACK", StringComparison.Ordinal) && !exported.Contains(dummyKey, StringComparison.Ordinal)
                    && !exported.Contains(PracticeWindow.Samples[0].Text, StringComparison.Ordinal);
                practice.Close();

                var multi = new Window
                {
                    Title = "LexiLens 多区域诊断练习", Width = 1000, Height = 480, WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    Background = Brushes.Black, Topmost = true, Style = (Style)Application.Current.FindResource(typeof(Window))
                };
                var panel = new StackPanel { Margin = new Thickness(30) };
                var labels = PracticeWindow.Samples.Take(2).Select(x => new TextBlock
                { Text = x.Text, FontFamily = new FontFamily("Segoe UI"), FontSize = 28, Foreground = Brushes.White, Height = 150 }).ToArray();
                foreach (var label in labels) panel.Children.Add(label);
                multi.Content = panel; multi.Show(); multi.Activate(); await Task.Delay(350);
                try
                {
                    var regions = labels.Select(label =>
                    {
                        var p = label.PointToScreen(new Point()); var q = label.PointToScreen(new Point(label.ActualWidth, label.ActualHeight));
                        return new CaptureRegion((int)p.X, (int)p.Y, (int)(q.X - p.X), (int)(q.Y - p.Y));
                    }).ToArray();
                    using var fast = new ScreenTextSource(regions[0], "en-US", PracticeWindow.Samples[0].Text, fastMode: true);
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    var fastText = await fast.ReadAsync(timeout.Token);
                    report["fastDesktopFirstReadMs"] = clock.ElapsedMilliseconds;
                    report["fastDesktopEngine"] = fast.LastRecognition?.Engine;
                    report["fastDesktopPassed"] = fastText == PracticeWindow.Samples[0].Text && fast.LastRecognition?.Engine.Contains("快速字幕") == true;
                    report["multipleIndependentAreasPassed"] = await window.VerifyMultipleAreasAsync(regions, mockHttp, (w, file) => Render(w, Path.Combine(directory, file)), timeout.Token);
                    using var spatialTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
                    foreach (var pair in await window.VerifySpatialInteractionAsync(regions, mockHttp, (w, file) => Render(w, Path.Combine(directory, file)), spatialTimeout.Token, show => { foreach (var label in labels) label.Visibility = show ? Visibility.Visible : Visibility.Hidden; })) report[pair.Key] = pair.Value;
                }
                finally { multi.Close(); }
            }
            report["success"] = report.GetValueOrDefault("desktopCaptureOcrPassed") is true
                && report.GetValueOrDefault("generatedImageOcrPassed") is true
                && report.GetValueOrDefault("clickThrough") is true
                && report.GetValueOrDefault("clickThroughUnlock") is true
                && report.GetValueOrDefault("liveSessionPassed") is true
                && report.GetValueOrDefault("credentialVaultRoundTrip") is true
                && report.GetValueOrDefault("credentialVaultEncrypted") is true
                && report.GetValueOrDefault("credentialVaultDeletion") is true
                && report.GetValueOrDefault("providerWizardInteraction") is true
                && report.GetValueOrDefault("providerWizardApply") is true;
            report["success"] = report["success"] is true && report.GetValueOrDefault("localOcrFontDiagnostics") is true
                && report.GetValueOrDefault("deepseekCredentialUpgrade") is true
                && (report.GetValueOrDefault("desktopCaptureOcrEngine") as string)?.Contains("PaddleOCR", StringComparison.Ordinal) == true;
            report["success"] = report["success"] is true && report.GetValueOrDefault("windowSnapSelectionPassed") is true
                && report.GetValueOrDefault("ocrTimeoutFallbackPassed") is true && report.GetValueOrDefault("ocrWorkerCancellationPassed") is true
                && report.GetValueOrDefault("diagnosticExportPassed") is true;
            report["success"] = report["success"] is true && report.GetValueOrDefault("singleTranslationPassed") is true;
            report["success"] = report["success"] is true && report.GetValueOrDefault("diagnosticLayoutPassed") is true;
            report["success"] = report["success"] is true && report.GetValueOrDefault("fastDesktopPassed") is true && report.GetValueOrDefault("multipleIndependentAreasPassed") is true;
            foreach (var key in new[] { "enhancedOcrCoordinatesPassed", "automaticParagraphsPassed", "inPlaceOverlayPassed", "quickSelectionAutoTranslated", "quickSelectionKeptFixedSession", "gameFocusRestored", "escapePreservesSelection", "clearingQuickRestoresFixed", "fixedSelectionAutoStarts", "overlapHasOneOwner", "blankFrameRemovesOverlays" }) report["success"] = report["success"] is true && report.GetValueOrDefault(key) is true;
        }
        catch (Exception ex) { report["error"] = ex.ToString(); report["success"] = false; }
        finally
        {
            File.WriteAllText(Path.Combine(directory, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            window.Close();
        }
    }

    internal static async Task RunSpatialAsync(MainWindow window, string directory)
    {
        Directory.CreateDirectory(directory);
        var report = new Dictionary<string, object?>();
        var scene = new Window { Title = "LexiLens 自动分段测试", Width = 1000, Height = 480, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = Brushes.Black, Topmost = true, Style = (Style)Application.Current.FindResource(typeof(Window)) };
        try
        {
            var panel = new StackPanel { Margin = new Thickness(30) };
            var labels = PracticeWindow.Samples.Take(2).Select(x => new TextBlock { Text = x.Text, FontFamily = new FontFamily("Segoe UI"), FontSize = 28, Foreground = Brushes.White, Height = 150 }).ToArray();
            foreach (var label in labels) panel.Children.Add(label);
            scene.Content = panel; scene.Show(); scene.Activate(); await Task.Delay(350);
            var regions = labels.Select(label => { var p = label.PointToScreen(new Point()); var q = label.PointToScreen(new Point(label.ActualWidth, label.ActualHeight)); return new CaptureRegion((int)p.X, (int)p.Y, (int)(q.X - p.X), (int)(q.Y - p.Y)); }).ToArray();
            var full = new CaptureRegion(regions[0].X, regions[0].Y, regions[0].Width, regions.Sum(x => x.Height));
            var bytes = NativeMethods.Capture(full, 1920, out var w, out var h);
            var engine = OcrEngine.TryCreateFromLanguage(new Language("en-US"))!;
            var reading = await ScreenTextSource.RecognizeDetailedAsync(engine, bytes, w, h, CancellationToken.None,
                boxes => File.WriteAllText(Path.Combine(directory, "fixture-words.json"), JsonSerializer.Serialize(boxes, new JsonSerializerOptions { WriteIndented = true })));
            File.WriteAllText(Path.Combine(directory, "fixture-blocks.json"), JsonSerializer.Serialize(reading, new JsonSerializerOptions { WriteIndented = true }));
            using var http = new HttpClient(new DiagnosticHandler()); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            foreach (var pair in await window.VerifySpatialInteractionAsync(regions, http, (target, file) => Render(target, Path.Combine(directory, file)), timeout.Token, show => { foreach (var label in labels) label.Visibility = show ? Visibility.Visible : Visibility.Hidden; })) report[pair.Key] = pair.Value;
            report["success"] = report.Values.All(x => x is true);
        }
        catch (Exception ex) { report["error"] = ex.ToString(); report["success"] = false; }
        finally { scene.Close(); File.WriteAllText(Path.Combine(directory, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })); window.Close(); }
    }

    internal static async Task RunUiAsync(MainWindow window, string directory)
    {
        Directory.CreateDirectory(directory);
        window.RecordForDiagnostics(new(PipelineStage.Capture, PipelineState.Success, "CAPTURE_OK", "诊断样例 · 截图完成", 8));
        window.RecordForDiagnostics(new(PipelineStage.Ocr, PipelineState.Warning, "OCR_TIMEOUT_FALLBACK", "诊断样例 · 增强识别超时，已切换 Windows OCR", 15000));
        window.RenderLogForDiagnostics(); await Task.Delay(350);
        Render(window, Path.Combine(directory, "diagnostics.png"));
        File.WriteAllText(Path.Combine(directory, "ui-report.json"), JsonSerializer.Serialize(window.DiagnosticColumnWidths()));
        window.Close();
    }

    private sealed class DiagnosticHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            using var json = JsonDocument.Parse(body);
            var text = json.RootElement.GetProperty("messages")[1].GetProperty("content").GetString();
            var translation = text == PracticeWindow.Samples[1].Text ? PracticeWindow.Samples[1].Translation : PracticeWindow.Samples[0].Translation;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content = translation } } } }))
            };
        }
    }

    private static void Render(Window window, string path)
    {
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var image = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
