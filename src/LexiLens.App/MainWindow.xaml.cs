using System.ComponentModel;
using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LexiLens.App.Platform;
using LexiLens.App.Views;
using LexiLens.Core;
using Microsoft.Win32;

namespace LexiLens.App;

public partial class MainWindow : Window
{
    private readonly LocalStore _store;
    private readonly CredentialVault _vault;
    private readonly DiagnosticJournal _journal;
    private readonly ObservableCollection<PipelineEvent> _logs = new();
    private readonly Dictionary<PipelineStage, PipelineEvent> _stages = new();
    private readonly System.Windows.Threading.DispatcherTimer _stageTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(45) };
    private AppSettings _settings;
    private TranslationClient _translator;
    private List<VocabularyEntry> _words;
    private Hotkeys? _hotkeys;
    private OverlayWindow? _overlay;
    private CancellationTokenSource? _sessionCancellation;
    private Task? _sessionTask;
    private int _generation;
    private bool _transition, _closing, _closeReady;
    private string _source = "", _translation = "";
    private string _latestOcr = "";
    private bool _preview;
    private string? _expectedDiagnosticText;

    public MainWindow(string? dataDirectory = null)
    {
        _store = new LocalStore(dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LexiLens"));
        _vault = new CredentialVault(_store.DirectoryPath);
        _journal = new DiagnosticJournal(Path.Combine(_store.DirectoryPath, "logs"));
        InitializeComponent();
        LogGrid.ItemsSource = _logs;
        foreach (var entry in _journal.Snapshot()) _logs.Add(entry);
        _stageTimer.Tick += (_, _) => RefreshPipeline(); _stageTimer.Start();
        _settings = _store.Load("settings.json", new AppSettings()).Sanitize();
        _words = _store.Load("vocabulary.json", new List<VocabularyEntry>());
        _translator = CreateTranslator();
        var languages = ScreenTextSource.Languages().Select(x => new LanguageOption(x.Tag, x.Name)).ToList();
        if (!languages.Any(x => x.Tag == "en-US")) languages.Add(new LanguageOption("en-US", "English · en-US（增强识别已内置）"));
        if (!languages.Any(x => x.Tag.StartsWith("zh", StringComparison.OrdinalIgnoreCase))) languages.Add(new LanguageOption("zh-Hans-CN", "简体中文 · zh-Hans-CN（增强识别已内置）"));
        LanguageCombo.ItemsSource = languages;
        LanguageCombo.SelectedValue = _settings.SourceLanguage;
        // Keep the intended language explicit if its OCR package has not been installed.
        if (LanguageCombo.SelectedIndex < 0)
        {
            languages.Add(new LanguageOption(_settings.SourceLanguage, _settings.SourceLanguage + " · 未安装 OCR"));
            LanguageCombo.Items.Refresh(); LanguageCombo.SelectedValue = _settings.SourceLanguage;
        }
        IntervalInput.Text = _settings.ScanIntervalMs.ToString(); OriginalCheck.IsChecked = _settings.ShowOriginal;
        FontSlider.Value = _settings.FontSize; OpacitySlider.Value = _settings.OverlayOpacity;
        OcrModeCombo.SelectedIndex = (int)_settings.OcrMode; OcrImageCombo.SelectedIndex = (int)_settings.OcrImageMode;
        DataLocation.Text = "设置与生词仅保存在本机：" + _store.DirectoryPath;
        RefreshSummary(); RefreshWords();
        GuidePanel.Visibility = _settings.OnboardingCompleted ? Visibility.Collapsed : Visibility.Visible;
        SourceInitialized += (_, _) =>
        {
            NativeMethods.ExcludeFromCapture(this);
            if (dataDirectory != null) return;
            _hotkeys = new Hotkeys(this);
            _hotkeys.Pressed += HandleHotkey;
            if (_hotkeys.Conflicts.Count > 0) MessageLabel.Text = "快捷键被占用：" + string.Join("、", _hotkeys.Conflicts) + "。仍可使用界面按钮。";
        };
        Closing += OnClosing;
    }

    private TranslationClient CreateTranslator()
    {
        var key = "";
        if (_settings.Provider == TranslationProvider.Cloud)
        {
            try
            {
                var preset = ProviderCatalog.Get(_settings.CloudServiceId);
                key = _vault.Read(CredentialVault.Scope(preset.Id, ProviderCatalog.Endpoint(preset, ProviderCatalog.Profile(_settings))));
            }
            catch (Exception ex) { MessageLabel.Text = ex.Message; }
        }
        return new TranslationClient(_http, _settings, key) { Progress = Record };
    }

    private async void HandleHotkey(int id)
    {
        if (_closing) return;
        switch (id)
        {
            case 1: await ToggleAsync(); break;
            case 2: await SelectRegionAsync(); break;
            case 3:
                EnsureOverlay();
                if (_overlay!.IsVisible) _overlay.Hide(); else _overlay.Show();
                break;
            case 4:
                EnsureOverlay(); _overlay!.Show(); _overlay.SetLocked(!_overlay.IsLocked);
                break;
            case 5: await SelectRegionAsync(true); break;
            case 6: await ToggleAsync(true); break;
        }
    }

    private void EnsureOverlay()
    {
        if (_overlay != null) return;
        _overlay = new OverlayWindow();
        _overlay.WordClicked += word => OpenWord(word);
        _overlay.ApplySettings(_settings);
        _overlay.Show();
        _overlay.SetContent("", "尚未识别", "未开始");
        _overlay.Closed += (_, _) => _overlay = null;
        if (!_overlay.CaptureExcluded) MessageLabel.Text = "系统未能排除悬浮窗截图，请将悬浮窗移到识别区域外。";
    }

    private async Task ToggleAsync(bool once = false)
    {
        if (_transition) return;
        _transition = true;
        try
        {
            if (_sessionCancellation != null) { await StopAsync(); if (!once) return; }
            if (_settings.Region == null)
            {
                MessageLabel.Text = "请先框选字幕或任务区域，再开始翻译。";
                return;
            }
            if (!NativeMethods.IsOnDesktop(_settings.Region)) throw new InvalidOperationException("保存的区域不在当前屏幕内，请重新框选。");
            if (_settings.Provider == TranslationProvider.Cloud) _translator.Cloud.Validate(true);
            _stages.Clear();
            Record(new(PipelineStage.Session, PipelineState.Running, once ? "SESSION_ONCE" : "SESSION_START", once ? "开始单次翻译" : "开始连续翻译"));
            var textSource = new ScreenTextSource(_settings.Region, _settings.SourceLanguage, _expectedDiagnosticText, _settings.OcrMode, _settings.OcrImageMode);
            EnsureOverlay(); _overlay!.Show(); _overlay.SetLocked(true);
            _source = ""; _translation = ""; _latestOcr = ""; _preview = false;
            _overlay.SetContent("", "正在截取选区…", "截图");
            SourcePreview.Text = "正在识别选区…"; TranslationPreview.Text = "尚未发送翻译请求。";
            PreviewBadge.Text = "实时识别";
            var cancellation = new CancellationTokenSource();
            _sessionCancellation = cancellation;
            var generation = ++_generation;
            textSource.Status = status => Dispatcher.BeginInvoke(() => { if (generation == _generation) RecognitionStatus.Text = status; });
            textSource.Progress = Record;
            var session = new LiveSession(textSource, _translator, _settings.ScanIntervalMs);
            session.Progress += Record;
            session.Observed += text => Dispatcher.BeginInvoke(() =>
            {
                if (generation != _generation) return;
                _latestOcr = text;
                SourcePreview.Text = text.Length == 0 ? "未识别到文字" : text;
                if (_source.Length == 0 && text.Length == 0) _overlay?.SetContent("", "未识别到文字，请查看主窗口的识别状态。", "未识别到文字");
            });
            session.Updated += update => Dispatcher.BeginInvoke(() =>
            {
                if (generation == _generation && !cancellation.IsCancellationRequested) ApplyUpdate(update);
            });
            StateLabel.Text = once ? "单次翻译中" : "连续翻译中"; StartButton.Content = "停止翻译";
            MessageLabel.Text = "";
            RecognitionStatus.Text = "";
            _sessionTask = ObserveSessionAsync(session, textSource, cancellation, generation, once);
        }
        catch (Exception ex) { MessageLabel.Text = LiveSession.FriendlyError(ex); Record(new(PipelineStage.Session, PipelineState.Error, "START_FAILED", LiveSession.FriendlyError(ex))); }
        finally { _transition = false; }
    }

    private async Task ObserveSessionAsync(LiveSession session, ScreenTextSource source, CancellationTokenSource cancellation, int generation, bool once)
    {
        try { if (once) await session.RunOnceAsync(cancellation.Token); else await session.RunAsync(cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (generation == _generation)
            {
                MessageLabel.Text = LiveSession.FriendlyError(ex);
                var stage = _stages.Values.Where(x => x.State == PipelineState.Running).OrderBy(x => x.Time).LastOrDefault()?.Stage ?? PipelineStage.Ocr;
                Record(new(stage, PipelineState.Error, stage == PipelineStage.Capture ? "CAPTURE_FAILED" : "OCR_FAILED", LiveSession.FriendlyError(ex)));
                StateLabel.Text = "识别已停止"; StartButton.Content = "开始连续翻译";
                _overlay?.SetContent(_source, "识别已停止，请检查主窗口提示。", "需要处理");
                if (ReferenceEquals(_sessionCancellation, cancellation)) _sessionCancellation = null;
                cancellation.Dispose();
            }
        }
        finally
        {
            source.Dispose();
            if (once && generation == _generation && ReferenceEquals(_sessionCancellation, cancellation))
            {
                _sessionCancellation = null; cancellation.Dispose();
                StateLabel.Text = _stages.Values.Any(x => x.State == PipelineState.Error) ? "未完成" : _latestOcr.Length == 0 ? "未识别到文字" : "单次完成";
                StartButton.Content = "开始连续翻译";
            }
        }
    }

    private async Task StopAsync()
    {
        ++_generation;
        var cancellation = _sessionCancellation;
        _sessionCancellation = null;
        if (cancellation != null)
        {
            await cancellation.CancelAsync();
            if (_sessionTask != null) await _sessionTask;
            cancellation.Dispose();
        }
        _sessionTask = null;
        foreach (var pending in _stages.Values.Where(x => x.State == PipelineState.Running).ToArray())
            Record(new(pending.Stage, PipelineState.Skipped, "CANCELLED", "已停止"));
        StateLabel.Text = "已停止"; StartButton.Content = "开始连续翻译";
        _overlay?.SetContent(_source, _translation, "已暂停");
        MessageLabel.Text = "已停止。";
        if (cancellation != null) Record(new(PipelineStage.Session, PipelineState.Success, "SESSION_STOP", "会话已停止"));
    }

    private void ApplyUpdate(LiveUpdate update)
    {
        _source = update.Source;
        if (update.IsBusy) _translation = "翻译中…";
        else if (update.Error != null) _translation = update.Error;
        else _translation = update.Translation ?? "";
        SourcePreview.Text = _source.Length > 0 ? _source : "区域内暂无文字";
        TranslationPreview.Text = _translation;
        _overlay?.SetContent(_source, _translation, update.Error != null ? "翻译失败" : update.IsBusy ? "翻译中" : "实时翻译");
        if (update.Error != null) MessageLabel.Text = update.Error;
        else if (!update.IsBusy && update.Source.Length > 0)
        {
            Record(new(PipelineStage.Overlay, PipelineState.Success, "OVERLAY_OK", "译文已显示"));
            if (!_settings.OnboardingCompleted) { _settings = _settings with { OnboardingCompleted = true }; _store.Save("settings.json", _settings); GuidePanel.Visibility = Visibility.Collapsed; }
        }
    }

    private async Task SelectRegionAsync(bool window = false)
    {
        if (_transition) return;
        _transition = true;
        try
        {
            await StopAsync();
            var oldState = WindowState;
            var overlayVisible = _overlay?.IsVisible == true;
            Hide(); _overlay?.Hide();
            CaptureRegion? region;
            try { region = window ? await WindowPicker.SelectAsync() : await RegionPicker.SelectAsync(); }
            finally { Show(); WindowState = oldState; if (overlayVisible) _overlay?.Show(); }
            if (region != null)
            {
                var next = _settings with { Region = region };
                _store.Save("settings.json", next); _settings = next;
                RefreshSummary(); MessageLabel.Text = "区域已选择，可先翻译一次。";
            }
        }
        catch (Exception ex) { MessageLabel.Text = ex.Message; }
        finally { _transition = false; }
    }

    private async void ToggleTranslation(object sender, RoutedEventArgs e) => await ToggleAsync();
    private async void PickRegion(object sender, RoutedEventArgs e) => await SelectRegionAsync();
    private async void PickWindow(object sender, RoutedEventArgs e) => await SelectRegionAsync(true);
    private async void TranslateOnce(object sender, RoutedEventArgs e) => await ToggleAsync(true);
    private async void PreviewRecognition(object sender, RoutedEventArgs e)
    {
        if (_transition) return;
        _transition = true;
        try
        {
            await StopAsync();
            if (_settings.Region == null) throw new InvalidOperationException("请先框选游戏中的字幕或任务文字区域。");
            MessageLabel.Text = "正在本机识别选区，首次加载模型可能需要几秒…";
            using var source = new ScreenTextSource(_settings.Region, _settings.SourceLanguage, mode: _settings.OcrMode, imageMode: _settings.OcrImageMode) { Progress = Record };
            var text = await source.ReadAsync(CancellationToken.None);
            var frame = source.LastFrame!.Value;
            new RecognitionPreviewWindow(frame.Bytes, frame.Width, frame.Height, text, source.LastRecognition!.Summary) { Owner = this }.Show();
            RecognitionStatus.Text = source.LastRecognition.Summary;
            MessageLabel.Text = "识别检查完成，未请求翻译服务。";
        }
        catch (Exception ex) { MessageLabel.Text = LiveSession.FriendlyError(ex); Record(new(PipelineStage.Ocr, PipelineState.Error, "OCR_CHECK_FAILED", LiveSession.FriendlyError(ex))); }
        finally { _transition = false; }
    }
    private void PreviewOverlay(object sender, RoutedEventArgs e)
    {
        EnsureOverlay(); _overlay!.Show();
        if (_sessionCancellation != null) return;
        _preview = true;
        _source = PracticeWindow.Samples[0].Text; _translation = PracticeWindow.Samples[0].Translation;
        _overlay.SetContent(_source, _translation, "示例预览 · 非实时识别");
        _overlay.SetLocked(false);
        MessageLabel.Text = "当前为固定示例。点击 ancient 或 relic 试用查词；框选屏幕后可开始真实翻译。";
    }

    private void OpenWord(string word)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(word, @"^[A-Za-z]+(?:['’-][A-Za-z]+)*$"))
        { MessageLabel.Text = "请输入一个英文单词。"; return; }
        new WordWindow(word, _preview ? "[示例] " + _source : _source, _translator, entry =>
        {
            var next = _words.Where(x => !string.Equals(x.Word, entry.Word, StringComparison.OrdinalIgnoreCase)).Prepend(entry).ToList();
            _store.Save("vocabulary.json", next); _words = next;
            RefreshWords(); MessageLabel.Text = "已收藏 " + entry.Word + "。";
        }).Show();
    }

    private void RefreshWords()
    {
        WordsList.ItemsSource = _words;
        WordCountLabel.Text = $"已收藏 {_words.Count} 个单词";
        WordsHint.Text = _words.Count == 0 ? "还没有收藏。可输入单词查词，或点击悬浮窗中的英文。" : $"共 {_words.Count} 个单词 · 导出后可导入 Anki 或表格软件";
    }

    private void LookupWord(object sender, RoutedEventArgs e) => OpenWord(LookupInput.Text.Trim());
    private void LookupKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) OpenWord(LookupInput.Text.Trim()); }
    private void DeleteWord(object sender, RoutedEventArgs e)
    {
        if (WordsList.SelectedItem is not VocabularyEntry entry) return;
        var next = _words.Where(x => x != entry).ToList();
        _store.Save("vocabulary.json", next); _words = next; RefreshWords();
    }
    private void ExportWords(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "CSV 文件|*.csv", FileName = "LexiLens-vocabulary.csv" };
        if (dialog.ShowDialog() == true)
        {
            File.WriteAllText(dialog.FileName, LocalStore.ToCsv(_words), new UTF8Encoding(true));
            MessageLabel.Text = "生词本已导出：" + dialog.FileName;
        }
    }

    private async void SaveSettings(object sender, RoutedEventArgs e)
    {
        if (_transition) return;
        _transition = true;
        try
        {
            if (!int.TryParse(IntervalInput.Text, out var interval) || interval < 350 || interval > 5000)
                throw new ArgumentException("扫描间隔需填写 350 到 5000 之间的整数。");
            var next = (_settings with
            {
                SourceLanguage = LanguageCombo.SelectedValue as string ?? "en-US",
                ScanIntervalMs = interval, OverlayOpacity = OpacitySlider.Value, FontSize = FontSlider.Value,
                OcrMode = (OcrMode)OcrModeCombo.SelectedIndex, OcrImageMode = (OcrImageMode)OcrImageCombo.SelectedIndex,
                ShowOriginal = OriginalCheck.IsChecked == true
            }).Sanitize();
            await StopAsync();
            _store.Save("settings.json", next); _settings = next;
            _translator = CreateTranslator();
            _overlay?.ApplySettings(_settings); RefreshSummary();
            MessageLabel.Text = "设置已保存。重新开始后使用新设置。";
        }
        catch (Exception ex) { MessageLabel.Text = ex.Message; }
        finally { _transition = false; }
    }

    private void RefreshSummary()
    {
        RegionLabel.Text = _settings.Region?.ToString() ?? "尚未选择区域";
        LanguageSummary.Text = _settings.SourceLanguage + " → 简体中文";
        ProviderSummary.Text = _settings.Provider switch
        {
            TranslationProvider.MyMemory => "MyMemory · 无需密钥",
            TranslationProvider.Ollama => "Ollama · " + _settings.OllamaModel,
            _ => ProviderCatalog.Get(_settings.CloudServiceId).Name + " · " + ProviderCatalog.Profile(_settings).Model
        };
        SettingsProviderSummary.Text = ProviderSummary.Text;
        PrivacySummary.Text = _settings.Provider switch
        {
            TranslationProvider.MyMemory => "文字发送至 MyMemory；截图仅在本机处理。",
            TranslationProvider.Ollama => "文字发送至配置的 Ollama 地址；本机地址可离线使用。",
            _ => "识别文字发送至所选 AI 服务；不上传截图。密钥在本机加密保存。"
        };
        ProviderNotice.Text = PrivacySummary.Text;
        GuideService.Text = "1. 连接服务 · " + (_settings.Provider == TranslationProvider.Cloud ? ProviderCatalog.Get(_settings.CloudServiceId).Name : _settings.Provider.ToString());
        GuideRegion.Text = _settings.Region == null ? "2. 选择窗口或字幕区域" : "2. 区域已选择";
    }

    private async void SetupProvider(object sender, RoutedEventArgs e) => await SetupProviderAsync();
    private async Task SetupProviderAsync()
    {
        if (_transition || _closing) return;
        _transition = true;
        try
        {
            await StopAsync();
            _overlay?.Hide();
            var wizard = new ProviderSetupWindow(_settings, _http, _vault, Record) { Owner = this };
            if (wizard.ShowDialog() == true && wizard.SelectedSettings is { } selected)
            {
                _store.Save("settings.json", selected); _settings = selected;
                _translator = CreateTranslator(); RefreshSummary();
                MessageLabel.Text = "服务已连接。选择区域后可翻译一次。";
            }
        }
        catch (Exception ex) { MessageLabel.Text = LiveSession.FriendlyError(ex); }
        finally { _transition = false; }
    }

    private void ShowPage(Grid? grid, ScrollViewer? scroll, Button selected)
    {
        LivePage.Visibility = ReferenceEquals(grid, LivePage) ? Visibility.Visible : Visibility.Collapsed;
        WordsPage.Visibility = ReferenceEquals(grid, WordsPage) ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = scroll == SettingsPage ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsPage.Visibility = ReferenceEquals(grid, DiagnosticsPage) ? Visibility.Visible : Visibility.Collapsed;
        foreach (var nav in new[] { LiveNav, WordsNav, SettingsNav, DiagnosticsNav })
        {
            nav.Background = ReferenceEquals(nav, selected) ? new SolidColorBrush(Color.FromRgb(223, 234, 248)) : Brushes.Transparent;
            nav.Foreground = ReferenceEquals(nav, selected) ? (Brush)FindResource("Accent") : new SolidColorBrush(Color.FromRgb(32, 41, 52));
        }
    }
    private void ShowLive(object sender, RoutedEventArgs e) => ShowPage(LivePage, null, LiveNav);
    private void ShowWords(object sender, RoutedEventArgs e) => ShowPage(WordsPage, null, WordsNav);
    private void ShowSettings(object sender, RoutedEventArgs e) => ShowPage(null, SettingsPage, SettingsNav);
    private void ShowDiagnostics(object sender, RoutedEventArgs e) => ShowPage(DiagnosticsPage, null, DiagnosticsNav);
    private void ShowGuide(object sender, RoutedEventArgs e) { ShowPage(LivePage, null, LiveNav); GuidePanel.Visibility = Visibility.Visible; }
    private void DismissGuide(object sender, RoutedEventArgs e)
    {
        GuidePanel.Visibility = Visibility.Collapsed;
        _settings = _settings with { OnboardingCompleted = true }; _store.Save("settings.json", _settings);
    }
    private void OpenPractice(object sender, RoutedEventArgs e) => new PracticeWindow().Show();
    private void OpenOcrRuntimeHelp(object sender, RoutedEventArgs e) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist") { UseShellExecute = true });

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeReady) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        _stageTimer.Stop();
        await StopAsync();
        _hotkeys?.Dispose(); _overlay?.Close(); _http.Dispose();
        _closeReady = true; _ = Dispatcher.BeginInvoke(Close);
    }

    internal sealed record LanguageOption(string Tag, string Name);

    internal async Task<(string Source, string Translation)> VerifyLiveSessionAsync(CaptureRegion region, CancellationToken ct, bool once = false)
    {
        _settings = _settings with { Region = region, ScanIntervalMs = 350 };
        _expectedDiagnosticText = PracticeWindow.Samples[0].Text;
        RefreshSummary();
        await ToggleAsync(once);
        try
        {
            while (string.IsNullOrWhiteSpace(_translation) || _translation == "翻译中…")
                await Task.Delay(100, ct);
            return (_source, _translation);
        }
        finally { await StopAsync(); _expectedDiagnosticText = null; }
    }

    internal void RenderSettingsForDiagnostics() => ShowPage(null, SettingsPage, SettingsNav);
    internal void RenderLiveForDiagnostics() => ShowPage(LivePage, null, LiveNav);
    internal void RenderLogForDiagnostics() => ShowPage(DiagnosticsPage, null, DiagnosticsNav);

    private void Record(PipelineEvent item)
    {
        _journal.Append(item);
        void Apply()
        {
            if (_closing) return;
            _logs.Add(item); while (_logs.Count > 250) _logs.RemoveAt(0);
            _stages[item.Stage] = item;
            if (item.State is PipelineState.Error or PipelineState.Warning) MessageLabel.Text = item.Message;
            if (_sessionCancellation != null && _source.Length == 0 && item.Stage is PipelineStage.Capture or PipelineStage.Ocr or PipelineStage.Stability)
            {
                if (item.State == PipelineState.Running || item.Code is "OCR_EMPTY" or "TEXT_PENDING")
                    _overlay?.SetContent(_latestOcr, item.Message, item.StageLabel);
            }
            RefreshPipeline();
        }
        if (Dispatcher.CheckAccess()) Apply(); else _ = Dispatcher.BeginInvoke(Apply);
    }

    private void RefreshPipeline()
    {
        var labels = new[] { (PipelineStage.Capture, CaptureStage), (PipelineStage.Ocr, OcrStage), (PipelineStage.Stability, StabilityStage), (PipelineStage.Translation, TranslationStage), (PipelineStage.Overlay, OverlayStage) };
        foreach (var (stage, label) in labels)
        {
            if (!_stages.TryGetValue(stage, out var value)) { label.Text = "未开始"; label.Foreground = (Brush)FindResource("Muted"); continue; }
            label.Text = value.StateLabel + (value.State == PipelineState.Running ? $" · {(DateTimeOffset.Now - value.Time).TotalSeconds:F0}s" : value.Milliseconds > 0 ? $" · {value.Milliseconds}ms" : "");
            label.Foreground = value.State == PipelineState.Error ? Brushes.Firebrick : value.State == PipelineState.Warning ? Brushes.DarkGoldenrod : value.State == PipelineState.Success ? Brushes.SeaGreen : (Brush)FindResource("Muted");
            label.ToolTip = value.Code + "\n" + value.Message;
        }
        var failure = _stages.Values.Where(x => x.State == PipelineState.Error).OrderByDescending(x => x.Time).FirstOrDefault();
        var latest = _stages.Values.Where(x => x.Stage != PipelineStage.Session).OrderByDescending(x => x.Time).FirstOrDefault();
        DiagnosisText.Text = failure != null ? $"{failure.StageLabel}失败 · {failure.Code}\n{failure.Message}" : latest == null ? "尚无会话记录。" : latest.Code is "OCR_EMPTY" or "OCR_FRAME_CACHE" && latest.Characters == 0 ? "截图已完成，但没有识别到文字。检查选区或改用 Windows OCR。" : latest.Code == "TEXT_PENDING" ? "已识别文字，尚在确认；还没有请求翻译服务。" : latest.Code == "TEXT_UNCHANGED" ? "文字未变化，没有重复请求 API。" : latest.Code == "OVERLAY_OK" ? "截图、识别、翻译和显示均已完成。" : latest.Message;
        if (_stages.Count == 0 && _logs.LastOrDefault(x => x.State == PipelineState.Error) is { } historical)
            DiagnosisText.Text = $"上次记录 · {historical.TimeLabel} · {historical.StageLabel}\n{historical.Code}：{historical.Message}";
    }

    private async void TestCurrentService(object sender, RoutedEventArgs e)
    {
        if (_transition) return;
        _transition = true;
        try
        {
            await StopAsync();
            Record(new(PipelineStage.Translation, PipelineState.Running, "API_TEST_START", "测试当前服务，发送固定英文短句"));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var translator = CreateTranslator();
            var result = await translator.TranslateAsync("Find the ancient relic before dawn.", timeout.Token);
            Record(new(PipelineStage.Translation, PipelineState.Success, "API_TEST_OK", "连接测试成功，已返回译文"));
            MessageLabel.Text = "连接测试成功：" + result;
        }
        catch (Exception ex) { Record(new(PipelineStage.Translation, PipelineState.Error, ex is CloudApiException cloud ? cloud.Code : "API_TEST_FAILED", LiveSession.FriendlyError(ex))); }
        finally { _transition = false; }
    }

    private async void UseWindowsOcr(object sender, RoutedEventArgs e)
    {
        if (_transition) return;
        _transition = true;
        try { await StopAsync(); _settings = _settings with { OcrMode = OcrMode.Windows }; _store.Save("settings.json", _settings); OcrModeCombo.SelectedIndex = 1; MessageLabel.Text = "已切换 Windows OCR，可重新翻译一次。"; }
        finally { _transition = false; }
    }

    private void ExportDiagnostics(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "诊断报告|*.json", FileName = "LexiLens-diagnostic-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json" };
        if (dialog.ShowDialog() != true) return;
        try { File.WriteAllText(dialog.FileName, _journal.Export(_settings, "0.4.0"), new UTF8Encoding(false)); MessageLabel.Text = "诊断报告已导出。"; }
        catch (Exception ex) { MessageLabel.Text = ex.Message; }
    }

    private void OpenLogFolder(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_journal.DirectoryPath);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_journal.DirectoryPath) { UseShellExecute = true });
    }

    internal string ExportDiagnosticReport() => _journal.Export(_settings, "0.4.0");
    internal void RecordForDiagnostics(PipelineEvent item) => Record(item);
    internal double[] DiagnosticColumnWidths() => LogGrid.Columns.Select(x => x.ActualWidth).ToArray();
}
