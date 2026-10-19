using System.ComponentModel;
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
    private bool _preview;

    public MainWindow(string? dataDirectory = null)
    {
        _store = new LocalStore(dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LexiLens"));
        InitializeComponent();
        _settings = _store.Load("settings.json", new AppSettings()).Sanitize();
        _words = _store.Load("vocabulary.json", new List<VocabularyEntry>());
        _translator = new TranslationClient(_http, _settings);
        var languages = ScreenTextSource.Languages().Select(x => new LanguageOption(x.Tag, x.Name)).ToList();
        LanguageCombo.ItemsSource = languages;
        LanguageCombo.SelectedValue = _settings.SourceLanguage;
        // Keep the intended language explicit if its OCR package has not been installed.
        if (LanguageCombo.SelectedIndex < 0)
        {
            languages.Add(new LanguageOption(_settings.SourceLanguage, _settings.SourceLanguage + " · 未安装 OCR"));
            LanguageCombo.Items.Refresh(); LanguageCombo.SelectedValue = _settings.SourceLanguage;
        }
        ProviderCombo.SelectedIndex = (int)_settings.Provider;
        EndpointInput.Text = _settings.OllamaEndpoint; ModelInput.Text = _settings.OllamaModel;
        IntervalInput.Text = _settings.ScanIntervalMs.ToString(); OriginalCheck.IsChecked = _settings.ShowOriginal;
        FontSlider.Value = _settings.FontSize; OpacitySlider.Value = _settings.OverlayOpacity;
        DataLocation.Text = "设置与生词仅保存在本机：" + _store.DirectoryPath;
        RefreshSummary(); RefreshWords();
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
        }
    }

    private void EnsureOverlay()
    {
        if (_overlay != null) return;
        _overlay = new OverlayWindow();
        _overlay.WordClicked += word => OpenWord(word);
        _overlay.ApplySettings(_settings);
        _overlay.Show();
        _overlay.SetContent("", "等待识别游戏文字…", "待机");
        _overlay.Closed += (_, _) => _overlay = null;
        if (!_overlay.CaptureExcluded) MessageLabel.Text = "系统未能排除悬浮窗截图，请将悬浮窗移到识别区域外。";
    }

    private async Task ToggleAsync()
    {
        if (_transition) return;
        _transition = true;
        try
        {
            if (_sessionCancellation != null) { await StopAsync(); return; }
            if (_settings.Region == null)
            {
                MessageLabel.Text = "请先框选字幕或任务区域，再开始翻译。";
                return;
            }
            if (!NativeMethods.IsOnDesktop(_settings.Region)) throw new InvalidOperationException("保存的区域不在当前屏幕内，请重新框选。");
            var textSource = new ScreenTextSource(_settings.Region, _settings.SourceLanguage);
            EnsureOverlay(); _overlay!.Show(); _overlay.SetLocked(true);
            _source = ""; _translation = ""; _preview = false;
            _overlay.SetContent("", "等待识别游戏文字…", "正在识别");
            SourcePreview.Text = "等待稳定的屏幕文字…"; TranslationPreview.Text = "";
            PreviewBadge.Text = "实时识别";
            var cancellation = new CancellationTokenSource();
            _sessionCancellation = cancellation;
            var generation = ++_generation;
            var session = new LiveSession(textSource, _translator, _settings.ScanIntervalMs);
            session.Updated += update => Dispatcher.BeginInvoke(() =>
            {
                if (generation == _generation && !cancellation.IsCancellationRequested) ApplyUpdate(update);
            });
            StateLabel.Text = "●  正在翻译"; StartButton.Content = "暂停翻译  Ⅱ";
            MessageLabel.Text = "悬浮窗已锁定穿透。Ctrl+Alt+L 解锁查词；Ctrl+Alt+T 暂停。";
            _sessionTask = ObserveSessionAsync(session, cancellation, generation);
        }
        catch (Exception ex) { MessageLabel.Text = LiveSession.FriendlyError(ex); }
        finally { _transition = false; }
    }

    private async Task ObserveSessionAsync(LiveSession session, CancellationTokenSource cancellation, int generation)
    {
        try { await session.RunAsync(cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (generation == _generation)
            {
                MessageLabel.Text = LiveSession.FriendlyError(ex);
                StateLabel.Text = "●  识别已停止"; StartButton.Content = "开始翻译  →";
                _overlay?.SetContent(_source, "识别已停止，请检查主窗口提示。", "需要处理");
                if (ReferenceEquals(_sessionCancellation, cancellation)) _sessionCancellation = null;
                cancellation.Dispose();
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
        StateLabel.Text = "●  已暂停"; StartButton.Content = "开始翻译  →";
        _overlay?.SetContent(_source, _translation, "已暂停");
        MessageLabel.Text = "已暂停识别与翻译。Ctrl+Alt+T 可继续。";
    }

    private void ApplyUpdate(LiveUpdate update)
    {
        _source = update.Source;
        if (update.IsBusy) _translation = "翻译中…";
        else if (update.Error != null) _translation = "翻译未完成，请查看主窗口提示。";
        else _translation = update.Translation ?? "";
        SourcePreview.Text = _source.Length > 0 ? _source : "区域内暂无文字";
        TranslationPreview.Text = _translation;
        _overlay?.SetContent(_source, _translation, update.Error != null ? "翻译失败" : update.IsBusy ? "翻译中" : "实时翻译");
        if (update.Error != null) MessageLabel.Text = update.Error;
    }

    private async Task SelectRegionAsync()
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
            try { region = await RegionPicker.SelectAsync(); }
            finally { Show(); WindowState = oldState; if (overlayVisible) _overlay?.Show(); }
            if (region != null)
            {
                var next = _settings with { Region = region };
                _store.Save("settings.json", next); _settings = next;
                RefreshSummary(); MessageLabel.Text = "区域已保存。按 Ctrl+Alt+T 开始实时翻译。";
            }
        }
        catch (Exception ex) { MessageLabel.Text = ex.Message; }
        finally { _transition = false; }
    }

    private async void ToggleTranslation(object sender, RoutedEventArgs e) => await ToggleAsync();
    private async void PickRegion(object sender, RoutedEventArgs e) => await SelectRegionAsync();
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
            var provider = (TranslationProvider)ProviderCombo.SelectedIndex;
            if (provider == TranslationProvider.Ollama) TranslationClient.ValidateOllamaEndpoint(EndpointInput.Text);
            var next = (_settings with
            {
                Provider = provider, SourceLanguage = LanguageCombo.SelectedValue as string ?? "en-US",
                OllamaEndpoint = EndpointInput.Text, OllamaModel = ModelInput.Text,
                ScanIntervalMs = interval, OverlayOpacity = OpacitySlider.Value, FontSize = FontSlider.Value,
                ShowOriginal = OriginalCheck.IsChecked == true
            }).Sanitize();
            await StopAsync();
            _store.Save("settings.json", next); _settings = next;
            _translator = new TranslationClient(_http, _settings);
            _overlay?.ApplySettings(_settings); RefreshSummary();
            MessageLabel.Text = "设置已保存。重新开始后使用新设置。";
        }
        catch (Exception ex) { MessageLabel.Text = ex.Message; }
        finally { _transition = false; }
    }

    private void RefreshSummary()
    {
        RegionLabel.Text = _settings.Region?.ToString() ?? "先框选游戏中的任务详情或字幕区域";
        LanguageSummary.Text = _settings.SourceLanguage + " → 简体中文";
        ProviderSummary.Text = _settings.Provider == TranslationProvider.MyMemory ? "MyMemory · 免密钥" : "Ollama · " + _settings.OllamaModel;
        PrivacySummary.Text = _settings.Provider == TranslationProvider.MyMemory ? "文字发送至 MyMemory；截图仅在本机处理。" : "文字发送至配置的 Ollama 地址；本机地址可离线使用。";
    }

    private void ProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OllamaOptions == null || ProviderNotice == null) return;
        var local = ProviderCombo.SelectedIndex == 1;
        OllamaOptions.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
        ProviderNotice.Text = local ? "使用本机 Ollama 时，截图和文字均留在本机。请先启动服务并下载模型。" : "点击开始后，识别文字会发送至第三方 MyMemory 服务。免密钥但有服务额度，翻译质量和延迟受网络影响。";
    }

    private void ShowPage(Grid? grid, ScrollViewer? scroll, Button selected)
    {
        LivePage.Visibility = ReferenceEquals(grid, LivePage) ? Visibility.Visible : Visibility.Collapsed;
        WordsPage.Visibility = ReferenceEquals(grid, WordsPage) ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = scroll == SettingsPage ? Visibility.Visible : Visibility.Collapsed;
        foreach (var nav in new[] { LiveNav, WordsNav, SettingsNav })
        {
            nav.Background = ReferenceEquals(nav, selected) ? new SolidColorBrush(Color.FromRgb(43, 43, 40)) : Brushes.Transparent;
            nav.Foreground = ReferenceEquals(nav, selected) ? (Brush)FindResource("Accent") : Brushes.LightGray;
        }
    }
    private void ShowLive(object sender, RoutedEventArgs e) => ShowPage(LivePage, null, LiveNav);
    private void ShowWords(object sender, RoutedEventArgs e) => ShowPage(WordsPage, null, WordsNav);
    private void ShowSettings(object sender, RoutedEventArgs e) => ShowPage(null, SettingsPage, SettingsNav);
    private void OpenPractice(object sender, RoutedEventArgs e) => new PracticeWindow().Show();

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeReady) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        await StopAsync();
        _hotkeys?.Dispose(); _overlay?.Close(); _http.Dispose();
        _closeReady = true; _ = Dispatcher.BeginInvoke(Close);
    }

    internal sealed record LanguageOption(string Tag, string Name);

    internal async Task<(string Source, string Translation)> VerifyLiveSessionAsync(CaptureRegion region, CancellationToken ct)
    {
        _settings = _settings with { Region = region, ScanIntervalMs = 350 };
        RefreshSummary();
        await ToggleAsync();
        try
        {
            while (string.IsNullOrWhiteSpace(_translation) || _translation == "翻译中…")
                await Task.Delay(100, ct);
            return (_source, _translation);
        }
        finally { await StopAsync(); }
    }

    internal void RenderSettingsForDiagnostics() => ShowPage(null, SettingsPage, SettingsNav);
    internal void RenderLiveForDiagnostics() => ShowPage(LivePage, null, LiveNav);
}
