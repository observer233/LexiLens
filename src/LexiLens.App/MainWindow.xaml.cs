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
    private readonly Dictionary<string, AreaState> _areas = new();
    private readonly ObservableCollection<ReplayEntry> _replay = new();
    private string? _selectedAreaId;
    private bool _refreshingAreas;
    private volatile bool _picking;
    private bool _overlaysHidden;
    private AreaState? _quickArea;
    private CancellationTokenSource? _quickCancellation;
    private Task? _quickTask;
    private int _quickGeneration;
    private CancellationTokenSource? _sessionCancellation;
    private Task? _sessionTask;
    private int _generation;
    private bool _transition, _closing, _closeReady;
    private string _source = "", _translation = "";
    private string _latestOcr = "";
    private bool _preview;
    private string? _expectedDiagnosticText;
    private readonly Dictionary<string, string> _diagnosticAreaTexts = new();

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
        ReplayList.ItemsSource = _replay;
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
        FastCheck.IsChecked = _settings.FastMode; FastSettingsCheck.IsChecked = _settings.FastMode;
        RequestIntervalInput.Text = _settings.RequestIntervalMs.ToString();
        OverlayModeCombo.SelectedIndex = (int)_settings.OverlayMode;
        BlockLimitInput.Text = _settings.AutoBlockLimit.ToString();
        QuickFollowCheck.IsChecked = _settings.QuickFollow;
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
            case 1: if (_sessionCancellation == null && _quickCancellation != null) await StopQuickAsync(); else await ToggleAsync(); break;
            case 2: await SelectRegionAsync(fromGame: !IsActive); break;
            case 3:
                _overlaysHidden = !_overlaysHidden;
                foreach (var overlay in AllOverlayWindows()) { if (_overlaysHidden) overlay.Hide(); else overlay.Show(); }
                break;
            case 4:
                var locked = !AllOverlayWindows().Any(IsOverlayLocked);
                foreach (var overlay in AllOverlayWindows()) { overlay.Show(); LockOverlay(overlay, locked); }
                break;
            case 5: await SelectRegionAsync(true, fromGame: !IsActive); break;
            case 6: await ToggleAsync(true); break;
            case 7: await SelectRegionAsync(add: true, fromGame: !IsActive); break;
            case 8: await QuickSelectAsync(fromGame: !IsActive); break;
            case 9: await StopQuickAsync(); break;
            case 10: ShowMainWindow(); break;
            case 11: OverlayModeCombo.SelectedIndex = ((int)_settings.OverlayMode + 1) % 3; break;
        }
    }

    private CaptureArea? SelectedArea => _settings.CaptureAreas().FirstOrDefault(x => x.Id == _selectedAreaId) ?? _settings.CaptureAreas().FirstOrDefault();
    private IEnumerable<OverlayWindow> AllOverlays() => _areas.Values.Select(x => x.Overlay).OfType<OverlayWindow>().Append(_overlay).OfType<OverlayWindow>().Distinct().ToArray();
    private IEnumerable<Window> AllOverlayWindows() => AllOverlays().Cast<Window>().Concat(_areas.Values.Select(x => x.Spatial).OfType<Window>())
        .Concat(new Window?[] { _quickArea?.Overlay, _quickArea?.Spatial }.OfType<Window>()).Distinct().ToArray();
    private static bool IsOverlayLocked(Window overlay) => overlay is SpatialOverlayWindow spatial ? spatial.IsLocked : ((OverlayWindow)overlay).IsLocked;
    private static void LockOverlay(Window overlay, bool locked) { if (overlay is SpatialOverlayWindow spatial) spatial.SetLocked(locked); else ((OverlayWindow)overlay).SetLocked(locked); }
    private void ShowMainWindow() { ShowActivated = true; Show(); WindowState = WindowState.Normal; Activate(); }

    private OverlayWindow EnsureOverlay(CaptureArea? area = null)
    {
        area ??= SelectedArea;
        AreaState? state = null;
        if (area != null)
        {
            if (!_areas.TryGetValue(area.Id, out state)) _areas[area.Id] = state = new(area, _settings.CaptureAreas().ToList().FindIndex(x => x.Id == area.Id) + 1);
            if (state.Overlay != null) { if (area.Id == SelectedArea?.Id) _overlay = state.Overlay; return state.Overlay; }
        }
        else if (_overlay != null) return _overlay;
        var overlay = new OverlayWindow();
        if (state != null) state.Overlay = overlay;
        if (area == null || area.Id == SelectedArea?.Id) _overlay = overlay;
        overlay.WordClicked += word => OpenWord(word, state?.Source);
        overlay.ApplySettings(_settings); overlay.Show();
        if (area != null) overlay.PlaceNear(area.Region, area.Overlay);
        overlay.AreaName = area?.Name ?? "预览";
        overlay.SetContent("", "尚未识别", "未开始");
        overlay.Closed += (_, _) => { if (state != null) state.Overlay = null; if (ReferenceEquals(_overlay, overlay)) _overlay = null; };
        if (!overlay.CaptureExcluded) MessageLabel.Text = "系统未能排除悬浮窗截图，请将悬浮窗移到识别区域外。";
        return overlay;
    }

    private async Task ToggleAsync(bool once = false)
    {
        if (_transition) return;
        _transition = true;
        try
        {
            if (_sessionCancellation != null) { await StopAsync(); if (!once) return; }
            var areas = _settings.CaptureAreas();
            if (areas.Count == 0)
            {
                MessageLabel.Text = "请先框选字幕或任务区域，再开始翻译。";
                return;
            }
            if (areas.Any(x => !NativeMethods.IsOnDesktop(x.Region))) throw new InvalidOperationException("有区域不在当前屏幕内，请重新框选或移除该区域。");
            if (_settings.Provider == TranslationProvider.Cloud) _translator.Cloud.Validate(true);
            _stages.Clear(); _overlaysHidden = false;
            Record(new(PipelineStage.Session, PipelineState.Running, once ? "SESSION_ONCE" : "SESSION_START", once ? "开始单次翻译" : "开始连续翻译"));
            _source = ""; _translation = ""; _latestOcr = ""; _preview = false;
            SourcePreview.Text = "正在识别选区…"; TranslationPreview.Text = "尚未发送翻译请求。";
            PreviewBadge.Text = "实时识别";
            var cancellation = new CancellationTokenSource();
            _sessionCancellation = cancellation;
            var generation = ++_generation;
            var jobs = new List<Task>();
            foreach (var area in areas)
            {
                if (!_areas.TryGetValue(area.Id, out var state)) _areas[area.Id] = state = new(area, areas.ToList().FindIndex(x => x.Id == area.Id) + 1);
                if (_settings.OverlayMode == OverlayMode.Combined)
                {
                    var overlay = EnsureOverlay(area); overlay.Show(); overlay.SetLocked(true); overlay.SetContent("", "正在截取选区…", "截图");
                }
                state.Source = ""; state.Translation = ""; state.LatestOcr = ""; state.Stages.Clear();
                jobs.Add(RunAreaAsync(state, cancellation, generation, once));
            }
            StateLabel.Text = once ? "单次翻译中" : "连续翻译中"; StartButton.Content = "停止翻译";
            MessageLabel.Text = "";
            RecognitionStatus.Text = "";
            _sessionTask = ObserveAreasAsync(jobs, cancellation, generation, once);
        }
        catch (Exception ex) { MessageLabel.Text = LiveSession.FriendlyError(ex); Record(new(PipelineStage.Session, PipelineState.Error, "START_FAILED", LiveSession.FriendlyError(ex))); }
        finally { _transition = false; }
    }

    private async Task RunAreaAsync(AreaState state, CancellationTokenSource cancellation, int generation, bool once, bool quick = false)
    {
        bool Current() => (quick ? generation == _quickGeneration : generation == _generation) && !cancellation.IsCancellationRequested;
        try
        {
            using var source = new ScreenTextSource(state.Area.Region, _settings.SourceLanguage, _diagnosticAreaTexts.GetValueOrDefault(state.Area.Id) ?? _expectedDiagnosticText, _settings.OcrMode, _settings.OcrImageMode,
                fastMode: _settings.FastMode, captureInterval: _settings.FastIntervalMs)
            { Paused = () => _picking };
            if (_settings.OverlayMode != OverlayMode.Combined)
            {
                await RunSpatialAsync(state, source, cancellation.Token, Current, once);
                return;
            }
            source.Status = status => Dispatcher.BeginInvoke(() =>
            {
                if (!Current()) return;
                state.Status = status;
                if (SelectedArea?.Id == state.Area.Id) RecognitionStatus.Text = status;
            });
            void Progress(PipelineEvent item) { if (Current()) Record(item with { Area = state.Number }); }
            source.Progress = Progress;
            var session = new LiveSession(source, _translator, _settings.FastMode ? _settings.FastIntervalMs : _settings.ScanIntervalMs, _settings.FastMode);
            session.Progress += Progress;
            session.Observed += text => Dispatcher.BeginInvoke(() =>
            {
                if (!Current()) return;
                state.LatestOcr = text;
                if (SelectedArea?.Id == state.Area.Id) { _latestOcr = text; SourcePreview.Text = text.Length == 0 ? "未识别到文字" : text; }
            });
            session.Updated += update => Dispatcher.BeginInvoke(() =>
            { if (Current()) ApplyUpdate(update, state); });
            session.Completed += update => Dispatcher.BeginInvoke(() =>
            {
                if (!Current()) return;
                var capturedAt = update.CapturedAt ?? DateTimeOffset.Now;
                var index = _replay.TakeWhile(x => x.CapturedAt > capturedAt).Count();
                _replay.Insert(index, new(capturedAt, state.Area.Name, update.Source, update.Translation ?? ""));
                while (_replay.Count > 100) _replay.RemoveAt(_replay.Count - 1);
                ReplayCount.Text = $"最近 {_replay.Count} 条 · 仅在本次打开期间保留";
            });
            if (once) await session.RunOnceAsync(cancellation.Token); else await session.RunAsync(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (Current())
            {
                MessageLabel.Text = LiveSession.FriendlyError(ex);
                var stage = _stages.Values.Where(x => x.State == PipelineState.Running).OrderBy(x => x.Time).LastOrDefault()?.Stage ?? PipelineStage.Ocr;
                Record(new(stage, PipelineState.Error, stage == PipelineStage.Capture ? "CAPTURE_FAILED" : "OCR_FAILED", LiveSession.FriendlyError(ex), Area: state.Number));
                state.Overlay?.SetContent(state.Source, "识别已停止，请检查诊断日志。", "需要处理");
                state.Spatial?.SetBlocks([new(0, "", new(0, 0, 1, .15), Error: LiveSession.FriendlyError(ex))]);
                Record(new(PipelineStage.Session, PipelineState.Error, "AREA_FAILED", "该区域已停止，其他区域继续运行", Area: state.Number));
            }
        }
    }

    private async Task RunSpatialAsync(AreaState state, ScreenTextSource source, CancellationToken ct, Func<bool> current, bool once)
    {
        source.ImmediateEmpty = true;
        if (state.Number >= 0) source.IncludeBlock = block =>
        {
            var x = state.Area.Region.X + (block.Bounds.X + block.Bounds.Width / 2) * state.Area.Region.Width;
            var y = state.Area.Region.Y + (block.Bounds.Y + block.Bounds.Height / 2) * state.Area.Region.Height;
            static bool Contains(CaptureRegion r, double px, double py) => px >= r.X && px <= r.X + r.Width && py >= r.Y && py <= r.Y + r.Height;
            if (_quickArea is { } quick && Contains(quick.Area.Region, x, y)) return false;
            return _settings.CaptureAreas().Where(a => Contains(a.Region, x, y)).OrderBy(a => (long)a.Region.Width * a.Region.Height).ThenBy(a => a.Id, StringComparer.Ordinal).FirstOrDefault()?.Id == state.Area.Id;
        };
        var overlay = state.Spatial;
        if (overlay == null)
        {
            overlay = new SpatialOverlayWindow(state.Area.Region, _settings);
            state.Spatial = overlay;
            overlay.Closed += (_, _) => { if (ReferenceEquals(state.Spatial, overlay)) state.Spatial = null; };
            overlay.ToggleRequested += async () => await ToggleAsync();
            overlay.PickRequested += async () => await QuickSelectAsync(true);
            overlay.ClearRequested += async () => await StopQuickAsync();
            overlay.Show();
            if (!overlay.CaptureExcluded)
            {
                overlay.Close(); state.Spatial = null;
                throw new InvalidOperationException("系统无法排除译文层截图，已停止原位覆盖以避免识别自己的译文。可切换固定悬浮模式。");
            }
        }
        overlay.ApplySettings(_settings); overlay.SetLocked(true);
        source.Progress = item => { if (current()) Record(item with { Area = state.Number }); };
        source.Status = status => Dispatcher.BeginInvoke(() => { if (current()) { state.Status = status; if (state.Area.Id == SelectedArea?.Id) RecognitionStatus.Text = status; } });
        var session = new SpatialSession(source, _translator, _settings.FastMode ? _settings.FastIntervalMs : _settings.ScanIntervalMs, _settings.FastMode, _settings.AutoBlockLimit);
        session.Progress += item => { if (current()) Record(item with { Area = state.Number }); };
        session.Updated += blocks => Dispatcher.BeginInvoke(() =>
        {
            if (!current()) return;
            state.Blocks = blocks;
            state.Source = string.Join("\n\n", blocks.Select(x => $"[{x.Id}] {x.Source}"));
            state.Translation = string.Join("\n\n", blocks.Select(x => $"[{x.Id}] {x.Error ?? x.Translation ?? "翻译中…"}"));
            if (ReferenceEquals(state.Spatial, overlay))
            {
                overlay.SetBlocks(blocks);
                if (!_picking && !_overlaysHidden && !overlay.IsVisible) overlay.Show();
                var signature = string.Join("|", blocks.Where(x => x.Translation != null).Select(x => x.Id + ":" + x.Translation!.GetHashCode(StringComparison.Ordinal)));
                if (overlay.IsVisible && signature.Length > 0 && signature != state.LastShown)
                    Record(new(PipelineStage.Overlay, PipelineState.Success, "BLOCK_OVERLAY_OK", $"已按位置显示 {blocks.Count(x => x.Translation != null)} 个译文块", Area: state.Number));
                state.LastShown = signature;
            }
            if (state.Area.Id == SelectedArea?.Id)
            {
                _source = state.Source; _translation = state.Translation; _latestOcr = state.Source;
                SourcePreview.Text = state.Source.Length == 0 ? "未识别到文字块" : state.Source;
                TranslationPreview.Text = state.Translation;
                PreviewBadge.Text = $"{blocks.Count} 个定位文本块";
            }
        });
        session.Completed += update => Dispatcher.BeginInvoke(() =>
        {
            if (!current()) return;
            var time = update.CapturedAt ?? DateTimeOffset.Now;
            _replay.Insert(_replay.TakeWhile(x => x.CapturedAt > time).Count(), new(time, state.Area.Name, update.Source, update.Translation ?? ""));
            while (_replay.Count > 100) _replay.RemoveAt(_replay.Count - 1);
            ReplayCount.Text = $"最近 {_replay.Count} 条 · 仅在本次打开期间保留";
            if (!_settings.OnboardingCompleted) { _settings = _settings with { OnboardingCompleted = true }; _store.Save("settings.json", _settings); GuidePanel.Visibility = Visibility.Collapsed; }
        });
        await session.RunAsync(ct, once);
    }

    private async Task ObserveAreasAsync(List<Task> jobs, CancellationTokenSource cancellation, int generation, bool once)
    {
        try { await Task.WhenAll(jobs); }
        finally
        {
            if (generation == _generation && ReferenceEquals(_sessionCancellation, cancellation))
            {
                _sessionCancellation = null; cancellation.Dispose();
                StateLabel.Text = _stages.Values.Any(x => x.State == PipelineState.Error) ? "未完成" : once ? "单次完成" : "已停止";
                StartButton.Content = "开始连续翻译";
            }
        }
    }

    private async Task StopAsync(bool includeQuick = true)
    {
        if (includeQuick) await StopQuickAsync();
        SaveOverlayPlacements();
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
        foreach (var area in _areas.Values) area.Overlay?.SetContent(area.Source, area.Translation, "已暂停");
        foreach (var area in _areas.Values) area.Spatial?.Hide();
        MessageLabel.Text = "已停止。";
        if (cancellation != null) Record(new(PipelineStage.Session, PipelineState.Success, "SESSION_STOP", "会话已停止"));
    }

    private void ApplyUpdate(LiveUpdate update, AreaState? state = null)
    {
        var source = update.Source;
        var translation = update.IsBusy ? "翻译中…" : update.Error ?? update.Translation ?? "";
        if (state != null) { state.Source = source; state.Translation = translation; }
        var overlay = state != null ? state.Overlay : _overlay;
        overlay?.SetContent(source, translation, update.Error != null ? "翻译失败" : update.IsBusy ? "翻译中" : update.IsPrevious ? "稍早字幕 · 可在对白回看中查看" : "实时翻译");
        if (!update.IsBusy && update.Translation != null) overlay?.AvoidOverlaps(AllOverlays());
        if (state != null && state.Area.Id != SelectedArea?.Id)
        {
            if (!update.IsBusy && update.Translation != null) Record(new(PipelineStage.Overlay, PipelineState.Success, "OVERLAY_OK", "译文已显示", Area: state.Number));
            return;
        }
        _source = source; _translation = translation;
        SourcePreview.Text = _source.Length > 0 ? _source : "区域内暂无文字";
        TranslationPreview.Text = _translation;
        if (update.Error != null) MessageLabel.Text = update.Error;
        else if (!update.IsBusy && update.Source.Length > 0)
        {
            Record(new(PipelineStage.Overlay, PipelineState.Success, "OVERLAY_OK", "译文已显示", Area: state?.Number ?? 0));
            if (!_settings.OnboardingCompleted) { _settings = _settings with { OnboardingCompleted = true }; _store.Save("settings.json", _settings); GuidePanel.Visibility = Visibility.Collapsed; }
        }
    }

    private async Task<CaptureRegion?> PickOnScreenAsync(bool window, bool fromGame, bool instant, CaptureRegion? diagnosticSelection = null, bool diagnosticCancel = false)
    {
        var oldState = WindowState; var foreground = NativeMethods.GetForegroundWindow();
        var visible = AllOverlayWindows().Where(x => x.IsVisible).ToArray();
        _picking = true;
        Hide(); foreach (var overlay in visible) overlay.Hide();
        try { return window ? await WindowPicker.SelectAsync() : await RegionPicker.SelectAsync(diagnosticSelection, instant, diagnosticCancel); }
        finally
        {
            _picking = false;
            if (fromGame)
            {
                ShowActivated = false; WindowState = WindowState.Minimized; Show(); ShowActivated = true;
                NativeMethods.SetForegroundWindow(foreground);
            }
            else { Show(); WindowState = oldState; }
            foreach (var overlay in visible.Where(x => x.IsLoaded)) if (!_overlaysHidden) overlay.Show();
        }
    }

    private async Task QuickSelectAsync(bool fromGame = false, CaptureRegion? diagnosticSelection = null, bool diagnosticCancel = false)
    {
        if (_transition) return;
        _transition = true;
        try
        {
            if (_settings.Provider == TranslationProvider.Cloud) _translator.Cloud.Validate(true);
            var region = await PickOnScreenAsync(false, fromGame, true, diagnosticSelection, diagnosticCancel);
            if (region == null) return;
            await StartQuickAsync(region);
        }
        catch (Exception ex) { MessageLabel.Text = LiveSession.FriendlyError(ex); Record(new(PipelineStage.Session, PipelineState.Error, "QUICK_FAILED", LiveSession.FriendlyError(ex), Area: -1)); }
        finally { _transition = false; }
    }

    private async Task StartQuickAsync(CaptureRegion region)
    {
        await StopQuickAsync(); await Task.Delay(90); _overlaysHidden = false;
        _quickArea = new(new CaptureArea("quick", "临时划选", region), -1);
        if (_settings.OverlayMode == OverlayMode.Combined)
        {
            _quickArea.Overlay = new OverlayWindow { AreaName = "临时划选" };
            _quickArea.Overlay.ApplySettings(_settings); _quickArea.Overlay.Show(); _quickArea.Overlay.PlaceNear(region, null); _quickArea.Overlay.SetLocked(true);
        }
        _quickCancellation = new();
        Record(new(PipelineStage.Session, PipelineState.Running, "QUICK_START", "选区已提交，立即识别翻译；固定区域保持原配置", Area: -1));
        _quickTask = RunAreaAsync(_quickArea, _quickCancellation, ++_quickGeneration, once: !_settings.QuickFollow, quick: true);
    }

    private async Task StopQuickAsync()
    {
        ++_quickGeneration;
        var cancellation = _quickCancellation; _quickCancellation = null;
        if (cancellation != null) { await cancellation.CancelAsync(); if (_quickTask != null) await _quickTask; cancellation.Dispose(); }
        _quickTask = null; _quickArea?.Overlay?.Close(); _quickArea?.Spatial?.Close(); _quickArea = null;
    }

    private async void QuickPick(object sender, RoutedEventArgs e) => await QuickSelectAsync();
    private async void ClearQuick(object sender, RoutedEventArgs e) => await StopQuickAsync();

    private async void ChangeQuickFollow(object sender, RoutedEventArgs e)
    {
        if (_settings == null || _transition || _settings.QuickFollow == (QuickFollowCheck.IsChecked == true)) return;
        _transition = true;
        try
        {
            await StopQuickAsync(); _settings = _settings with { QuickFollow = QuickFollowCheck.IsChecked == true };
            _store.Save("settings.json", _settings);
        }
        finally { _transition = false; }
    }

    private async void ChangeOverlayMode(object sender, SelectionChangedEventArgs e)
    {
        if (_settings == null || _transition || _refreshingAreas || OverlayModeCombo.SelectedIndex < 0 || (int)_settings.OverlayMode == OverlayModeCombo.SelectedIndex) return;
        _transition = true; var resume = _sessionCancellation != null;
        var quickRegion = _quickArea?.Area.Region;
        try
        {
            var mode = (OverlayMode)OverlayModeCombo.SelectedIndex;
            if (_settings.OverlayMode != OverlayMode.Combined && mode != OverlayMode.Combined)
            {
                _settings = _settings with { OverlayMode = mode }; _store.Save("settings.json", _settings);
                foreach (var spatial in AllOverlayWindows().OfType<SpatialOverlayWindow>()) spatial.ApplySettings(_settings);
                return;
            }
            await StopAsync(); CloseAreaOverlays();
            _settings = _settings with { OverlayMode = (OverlayMode)OverlayModeCombo.SelectedIndex };
            _store.Save("settings.json", _settings);
            MessageLabel.Text = _settings.OverlayMode == OverlayMode.Combined ? "固定悬浮：每个选区合并显示。" : _settings.OverlayMode == OverlayMode.InPlace ? "原位覆盖：自动分段，在原文处显示半透明译文。" : "旁置分段：每段独立显示在原文附近。";
        }
        catch (Exception ex) { MessageLabel.Text = ex.Message; resume = false; }
        finally { _transition = false; }
        if (resume) await ToggleAsync();
        if (quickRegion != null) await StartQuickAsync(quickRegion);
    }

    private async Task SelectRegionAsync(bool window = false, bool add = false, bool fromGame = false, CaptureRegion? diagnosticSelection = null)
    {
        if (_transition) return;
        _transition = true;
        try
        {
            if (add && _settings.CaptureAreas().Count >= AppSettings.MaxAreas) { MessageLabel.Text = $"最多同时使用 {AppSettings.MaxAreas} 个区域，可先移除一个。"; return; }
            var region = await PickOnScreenAsync(window, fromGame, false, diagnosticSelection);
            if (region != null)
            {
                await StopAsync(includeQuick: false);
                var areas = _settings.CaptureAreas().ToList();
                var current = add ? null : SelectedArea;
                var selected = current == null ? new CaptureArea(Guid.NewGuid().ToString("N"), "区域 " + (areas.Count + 1), region) : current with { Region = region, Overlay = null };
                if (current == null) areas.Add(selected); else areas[areas.FindIndex(x => x.Id == current.Id)] = selected;
                CloseAreaOverlays(); _selectedAreaId = selected.Id;
                var next = _settings with { Region = region, Areas = areas };
                _store.Save("settings.json", next); _settings = next;
                RefreshSummary(); MessageLabel.Text = "固定区域已选择，已自动开始连续翻译。";
                _transition = false;
                await ToggleAsync();
            }
        }
        catch (Exception ex) { MessageLabel.Text = ex.Message; }
        finally { _transition = false; }
    }

    private async void ToggleTranslation(object sender, RoutedEventArgs e) => await ToggleAsync();
    private async void PickRegion(object sender, RoutedEventArgs e) => await SelectRegionAsync();
    private async void PickWindow(object sender, RoutedEventArgs e) => await SelectRegionAsync(true);
    private async void AddRegion(object sender, RoutedEventArgs e) => await SelectRegionAsync(add: true);
    private void CloseAreaOverlays()
    {
        foreach (var overlay in AllOverlays()) overlay.Close();
        foreach (var spatial in _areas.Values.Select(x => x.Spatial).OfType<SpatialOverlayWindow>().ToArray()) spatial.Close();
        _areas.Clear(); _overlay = null;
    }

    private void SaveOverlayPlacements()
    {
        if (_settings == null || _areas.Count == 0) return;
        var areas = _settings.CaptureAreas().Select(x => _areas.TryGetValue(x.Id, out var state) && state.Overlay != null
            ? x with { Overlay = state.Overlay.Placement } : x).ToList();
        _settings = _settings with { Areas = areas };
        try { _store.Save("settings.json", _settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Record(new(PipelineStage.Session, PipelineState.Warning, "PLACEMENT_SAVE_FAILED", "悬浮框位置未能保存，当前会话仍可停止")); }
    }

    private void SelectArea(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingAreas || AreaCombo.SelectedItem is not CaptureArea area) return;
        _selectedAreaId = area.Id; _settings = _settings with { Region = area.Region };
        AreaNameInput.Text = area.Name;
        _stages.Clear();
        if (_areas.TryGetValue(area.Id, out var state))
        {
            _overlay = state.Overlay; _source = state.Source; _translation = state.Translation; _latestOcr = state.LatestOcr;
            RecognitionStatus.Text = state.Status;
            foreach (var entry in state.Stages) _stages[entry.Key] = entry.Value;
        }
        else { _overlay = null; _source = ""; _translation = ""; _latestOcr = ""; }
        SourcePreview.Text = _latestOcr.Length > 0 ? _latestOcr : "区域内暂无文字"; TranslationPreview.Text = _translation;
        RegionLabel.Text = $"共 {_settings.CaptureAreas().Count} 个区域 · 当前 {area.Name} · {area.Region}";
        RefreshPipeline();
    }

    private async void RemoveArea(object sender, RoutedEventArgs e)
    {
        if (_transition || SelectedArea is not { } selected) return;
        _transition = true;
        try
        {
            await StopAsync();
            var areas = _settings.CaptureAreas().Where(x => x.Id != selected.Id).ToList();
            CloseAreaOverlays(); _selectedAreaId = areas.FirstOrDefault()?.Id;
            _settings = _settings with { Areas = areas, Region = areas.FirstOrDefault()?.Region };
            _store.Save("settings.json", _settings); RefreshSummary();
        }
        finally { _transition = false; }
    }

    private async void RenameArea(object sender, RoutedEventArgs e)
    {
        if (_transition || SelectedArea is not { } selected) return;
        var name = AreaNameInput.Text.Trim();
        if (name.Length is < 1 or > 40) { MessageLabel.Text = "区域名称需为 1 到 40 个字符。"; return; }
        _transition = true;
        try
        {
            await StopAsync();
            _settings = _settings with { Areas = _settings.CaptureAreas().Select(x => x.Id == selected.Id ? x with { Name = name } : x).ToList() };
            CloseAreaOverlays(); _store.Save("settings.json", _settings); RefreshSummary();
        }
        finally { _transition = false; }
    }

    private async void ChangeFastMode(object sender, RoutedEventArgs e)
    {
        if (_settings == null || _transition || _settings.FastMode == (FastCheck.IsChecked == true)) return;
        _transition = true;
        try
        {
            await StopAsync(); _settings = _settings with { FastMode = FastCheck.IsChecked == true };
            FastSettingsCheck.IsChecked = _settings.FastMode;
            _store.Save("settings.json", _settings); _translator = CreateTranslator();
            MessageLabel.Text = _settings.FastMode ? "快速字幕：系统 OCR 优先，首次识别立即翻译，短对白进入回看。" : "清晰优先：使用所选引擎，确认文字后翻译。";
        }
        finally { _transition = false; }
    }

    private void ShowReplay(object sender, RoutedEventArgs e) => ShowPage(ReplayPage, null, ReplayNav);
    private void ClearReplay(object sender, RoutedEventArgs e) { _replay.Clear(); ReplayCount.Text = "暂无记录 · 仅在本次打开期间保留"; }
    private async void TranslateOnce(object sender, RoutedEventArgs e) => await ToggleAsync(true);
    private async void PreviewRecognition(object sender, RoutedEventArgs e)
    {
        if (_transition) return;
        _transition = true;
        try
        {
            await StopAsync();
            var region = SelectedArea?.Region;
            if (region == null) throw new InvalidOperationException("请先框选游戏中的字幕或任务文字区域。");
            MessageLabel.Text = "正在本机识别选区，首次加载模型可能需要几秒…";
            using var source = new ScreenTextSource(region, _settings.SourceLanguage, mode: _settings.OcrMode, imageMode: _settings.OcrImageMode) { Progress = Record };
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

    private void OpenWord(string word, string? context = null)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(word, @"^[A-Za-z]+(?:['’-][A-Za-z]+)*$"))
        { MessageLabel.Text = "请输入一个英文单词。"; return; }
        new WordWindow(word, _preview ? "[示例] " + _source : context ?? _source, _translator, entry =>
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
            if (!int.TryParse(IntervalInput.Text, out var interval) || interval < 100 || interval > 5000)
                throw new ArgumentException("扫描间隔需填写 100 到 5000 之间的整数。");
            if (!int.TryParse(RequestIntervalInput.Text, out var requestInterval) || requestInterval < 200 || requestInterval > 5000)
                throw new ArgumentException("API 最小间隔需填写 200 到 5000 之间的整数。");
            if (!int.TryParse(BlockLimitInput.Text, out var blockLimit) || blockLimit is < 4 or > 24) throw new ArgumentException("自动文本块数量需为 4 到 24。");
            await StopAsync();
            var next = (_settings with
            {
                SourceLanguage = LanguageCombo.SelectedValue as string ?? "en-US",
                ScanIntervalMs = interval,
                OverlayOpacity = OpacitySlider.Value,
                FontSize = FontSlider.Value,
                FastMode = FastSettingsCheck.IsChecked == true,
                RequestIntervalMs = requestInterval,
                AutoBlockLimit = blockLimit,
                OcrMode = (OcrMode)OcrModeCombo.SelectedIndex,
                OcrImageMode = (OcrImageMode)OcrImageCombo.SelectedIndex,
                ShowOriginal = OriginalCheck.IsChecked == true
            }).Sanitize();
            _store.Save("settings.json", next); _settings = next;
            _translator = CreateTranslator();
            foreach (var overlay in AllOverlays()) overlay.ApplySettings(_settings);
            foreach (var spatial in _areas.Values.Select(x => x.Spatial).OfType<SpatialOverlayWindow>()) spatial.ApplySettings(_settings);
            FastCheck.IsChecked = _settings.FastMode; RefreshSummary();
            MessageLabel.Text = "设置已保存。重新开始后使用新设置。";
        }
        catch (Exception ex) { MessageLabel.Text = ex.Message; }
        finally { _transition = false; }
    }

    private void RefreshSummary()
    {
        _refreshingAreas = true;
        OverlayModeCombo.SelectedIndex = (int)_settings.OverlayMode;
        AreaCombo.ItemsSource = _settings.CaptureAreas();
        AreaCombo.SelectedItem = SelectedArea;
        AreaNameInput.Text = SelectedArea?.Name ?? "";
        _refreshingAreas = false;
        RegionLabel.Text = SelectedArea is { } selected ? $"共 {_settings.CaptureAreas().Count} 个区域 · 当前 {selected.Name} · {selected.Region}" : "尚未选择区域";
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
            foreach (var overlay in AllOverlayWindows()) overlay.Hide();
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
        ReplayPage.Visibility = ReferenceEquals(grid, ReplayPage) ? Visibility.Visible : Visibility.Collapsed;
        foreach (var nav in new[] { LiveNav, WordsNav, SettingsNav, DiagnosticsNav, ReplayNav })
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
        _hotkeys?.Dispose(); CloseAreaOverlays(); _http.Dispose();
        _closeReady = true; _ = Dispatcher.BeginInvoke(Close);
    }

    internal sealed record LanguageOption(string Tag, string Name);
    internal sealed record ReplayEntry(DateTimeOffset CapturedAt, string AreaName, string Source, string Translation)
    { public string Label => AreaName + " · " + CapturedAt.ToString("HH:mm:ss"); }
    private sealed class AreaState(CaptureArea area, int number)
    {
        internal CaptureArea Area { get; } = area;
        internal int Number { get; } = number;
        internal OverlayWindow? Overlay { get; set; }
        internal SpatialOverlayWindow? Spatial { get; set; }
        internal IReadOnlyList<SpatialBlock> Blocks { get; set; } = [];
        internal string Source = "", Translation = "", LatestOcr = "", Status = "", LastShown = "";
        internal Dictionary<PipelineStage, PipelineEvent> Stages { get; } = new();
    }

    internal async Task<(string Source, string Translation)> VerifyLiveSessionAsync(CaptureRegion region, CancellationToken ct, bool once = false)
    {
        CloseAreaOverlays();
        _settings = (_settings with { Region = region, Areas = [], ScanIntervalMs = 350, FastMode = false, OverlayMode = OverlayMode.Combined }).Sanitize();
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

    internal async Task<bool> VerifyMultipleAreasAsync(CaptureRegion[] regions, HttpClient http, Action<Window, string> render, CancellationToken ct)
    {
        await StopAsync(); CloseAreaOverlays();
        var original = _settings; var originalTranslator = _translator;
        try
        {
            _settings = (new AppSettings
            {
                Provider = TranslationProvider.Cloud,
                CloudServiceId = "zhipu",
                OnboardingCompleted = true,
                OverlayMode = OverlayMode.Combined,
                CloudProfiles = new() { ["zhipu"] = new() { Model = ProviderCatalog.Get("zhipu").Models.First() } },
                RequestIntervalMs = 200,
                Areas = regions.Select((r, i) => new CaptureArea("diagnostic-" + i, i == 0 ? "任务" : "对白", r)).ToList()
            }).Sanitize();
            for (var i = 0; i < regions.Length; i++) _diagnosticAreaTexts["diagnostic-" + i] = PracticeWindow.Samples[i].Text;
            _translator = new TranslationClient(http, _settings, "diagnostic-not-a-real-key") { Progress = Record };
            _selectedAreaId = "diagnostic-0"; RefreshSummary(); GuidePanel.Visibility = Visibility.Collapsed;
            await ToggleAsync();
            while (_areas.Values.Count(x => x.Translation == PracticeWindow.Samples[x.Number - 1].Translation) != regions.Length) await Task.Delay(50, ct);
            var overlays = _areas.Values.Select(x => x.Overlay!).ToArray();
            var passed = overlays.Length == regions.Length && overlays.Distinct().Count() == regions.Length && overlays.All(x => x.CaptureExcluded && x.IsLocked)
                && _replay.Any(x => x.Source == PracticeWindow.Samples[0].Text) && _replay.Any(x => x.Source == PracticeWindow.Samples[1].Text);
            RenderLiveForDiagnostics(); await Task.Delay(150); render(this, "multi-region-main.png");
            for (var i = 0; i < overlays.Length; i++) render(overlays[i], $"region-{i + 1}-overlay.png");
            AreaCombo.SelectedIndex = 1;
            passed &= SourcePreview.Text == PracticeWindow.Samples[1].Text && TranslationPreview.Text == PracticeWindow.Samples[1].Translation;
            ShowPage(ReplayPage, null, ReplayNav); await Task.Delay(150); render(this, "replay.png");
            await StopAsync();
            var saved = _store.Load("settings.json", new AppSettings()).Sanitize();
            passed &= saved.Areas.Count == regions.Length && saved.Areas.All(x => x.Overlay?.IsValid == true);
            return passed;
        }
        finally
        {
            await StopAsync(); CloseAreaOverlays(); _diagnosticAreaTexts.Clear();
            _settings = original; _translator = originalTranslator; _selectedAreaId = null; RefreshSummary();
        }
    }

    internal async Task<Dictionary<string, bool>> VerifySpatialInteractionAsync(CaptureRegion[] regions, HttpClient http, Action<Window, string> render, CancellationToken ct, Action<bool>? showFixture = null)
    {
        var result = new Dictionary<string, bool>();
        await StopAsync(); CloseAreaOverlays();
        var original = _settings; var translator = _translator;
        var full = new CaptureRegion(regions.Min(x => x.X), regions.Min(x => x.Y), regions.Max(x => x.X + x.Width) - regions.Min(x => x.X), regions.Max(x => x.Y + x.Height) - regions.Min(x => x.Y));
        try
        {
            _settings = (new AppSettings
            {
                Provider = TranslationProvider.Cloud,
                CloudServiceId = "zhipu",
                OnboardingCompleted = true,
                CloudProfiles = new() { ["zhipu"] = new() { Model = ProviderCatalog.Get("zhipu").Models.First() } },
                OverlayMode = OverlayMode.InPlace,
                RequestIntervalMs = 200,
                Areas = [new("spatial", "整窗自动分段", full)]
            }).Sanitize();
            _expectedDiagnosticText = string.Join(" ", PracticeWindow.Samples.Take(2).Select(x => x.Text));
            _translator = new TranslationClient(http, _settings, "diagnostic-not-a-real-key") { Progress = Record };
            _selectedAreaId = "spatial"; RefreshSummary(); GuidePanel.Visibility = Visibility.Collapsed;
            await ToggleAsync();
            while (!_areas.TryGetValue("spatial", out var state) || state.Blocks.Count != 2 || state.Blocks.Any(x => x.Translation == null)) await Task.Delay(50, ct);
            var fixedState = _areas["spatial"];
            result["automaticParagraphsPassed"] = fixedState.Blocks.Count == 2 && fixedState.Blocks.All(x => x.Bounds.IsValid)
                && fixedState.Blocks.Any(x => x.Translation == PracticeWindow.Samples[0].Translation)
                && fixedState.Blocks.Any(x => x.Translation == PracticeWindow.Samples[1].Translation)
                && fixedState.Blocks[0].Bounds.Bottom < fixedState.Blocks[1].Bounds.Y;
            result["inPlaceOverlayPassed"] = fixedState.Spatial?.CaptureExcluded == true && fixedState.Spatial.RenderedCount == 2 && fixedState.Spatial.IsLocked;
            RenderLiveForDiagnostics(); await Task.Delay(150); render(this, "spatial-main.png"); render(fixedState.Spatial!, "in-place-layer.png");
            if (showFixture != null)
            {
                // The controlled fixture hides its text without moving the crop.
                _expectedDiagnosticText = null;
                var clock = System.Diagnostics.Stopwatch.StartNew(); showFixture(false);
                while (fixedState.Blocks.Count != 0) await Task.Delay(20, ct);
                result["blankFrameRemovesOverlays"] = fixedState.Spatial?.RenderedCount == 0 && clock.ElapsedMilliseconds < 1500;
                showFixture(true);
                while (fixedState.Blocks.Count != 2 || fixedState.Blocks.Any(x => x.Translation == null)) await Task.Delay(20, ct);
                _expectedDiagnosticText = string.Join(" ", PracticeWindow.Samples.Take(2).Select(x => x.Text));
            }
            _settings = _settings with { OverlayMode = OverlayMode.Nearby }; fixedState.Spatial!.ApplySettings(_settings); render(fixedState.Spatial, "nearby-layer.png");
            _settings = _settings with { OverlayMode = OverlayMode.InPlace }; fixedState.Spatial.ApplySettings(_settings);
            var running = _sessionCancellation; var foreground = NativeMethods.GetForegroundWindow();
            var originalIds = _settings.CaptureAreas().Select(x => x.Id).ToArray();
            await QuickSelectAsync(fromGame: true, diagnosticSelection: full);
            while (_quickArea?.Blocks.Count != 2 || _quickArea.Blocks.Any(x => x.Translation == null)) await Task.Delay(50, ct);
            result["quickSelectionAutoTranslated"] = _quickArea.Spatial?.RenderedCount == 2;
            result["quickSelectionKeptFixedSession"] = ReferenceEquals(running, _sessionCancellation) && running?.IsCancellationRequested == false && _settings.CaptureAreas().Select(x => x.Id).SequenceEqual(originalIds);
            result["gameFocusRestored"] = WindowState == WindowState.Minimized && !IsActive && NativeMethods.GetForegroundWindow() == foreground;
            render(_quickArea.Spatial!, "quick-selection-layer.png");
            var quick = _quickArea;
            await QuickSelectAsync(fromGame: true, diagnosticCancel: true);
            result["escapePreservesSelection"] = ReferenceEquals(quick, _quickArea) && ReferenceEquals(running, _sessionCancellation);
            await StopQuickAsync();
            while (fixedState.Blocks.Count != 2 || fixedState.Blocks.Any(x => x.Translation == null)) await Task.Delay(50, ct);
            result["clearingQuickRestoresFixed"] = _quickArea == null && fixedState.Spatial?.RenderedCount == 2;
            _expectedDiagnosticText = null; // Remaining requests use this controlled fixture and an in-process mock HTTP handler only.
            await SelectRegionAsync(add: true, fromGame: true, diagnosticSelection: regions[0]);
            while (_areas.Count != 2 || _areas.Values.Sum(x => x.Blocks.Count(b => b.Translation != null)) != 2) await Task.Delay(50, ct);
            result["fixedSelectionAutoStarts"] = _sessionCancellation != null && _settings.CaptureAreas().Count == 2 && WindowState == WindowState.Minimized;
            result["overlapHasOneOwner"] = _areas.Values.SelectMany(x => x.Blocks).Count() == 2;
            ShowMainWindow();
            return result;
        }
        finally
        {
            await StopAsync(); CloseAreaOverlays(); _expectedDiagnosticText = null;
            _settings = original; _translator = translator; _selectedAreaId = null; RefreshSummary(); ShowMainWindow();
        }
    }

    private void Record(PipelineEvent item)
    {
        _journal.Append(item);
        void Apply()
        {
            if (_closing) return;
            _logs.Add(item); while (_logs.Count > 250) _logs.RemoveAt(0);
            var area = _areas.Values.FirstOrDefault(x => x.Number == item.Area);
            if (area != null) area.Stages[item.Stage] = item;
            if (item.Area == 0 || area?.Area.Id == SelectedArea?.Id) _stages[item.Stage] = item;
            if (item.State is PipelineState.Error or PipelineState.Warning) MessageLabel.Text = item.Message;
            if (_sessionCancellation != null && (item.Area == 0 || area?.Area.Id == SelectedArea?.Id) && _source.Length == 0 && item.Stage is PipelineStage.Capture or PipelineStage.Ocr or PipelineStage.Stability)
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
        try { File.WriteAllText(dialog.FileName, _journal.Export(_settings, "0.6.0"), new UTF8Encoding(false)); MessageLabel.Text = "诊断报告已导出。"; }
        catch (Exception ex) { MessageLabel.Text = ex.Message; }
    }

    private void OpenLogFolder(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_journal.DirectoryPath);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_journal.DirectoryPath) { UseShellExecute = true });
    }

    internal string ExportDiagnosticReport() => _journal.Export(_settings, "0.6.0");
    internal void RecordForDiagnostics(PipelineEvent item) => Record(item);
    internal double[] DiagnosticColumnWidths() => LogGrid.Columns.Select(x => x.ActualWidth).ToArray();
}
