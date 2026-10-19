using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using LexiLens.App.Platform;
using LexiLens.Core;

namespace LexiLens.App.Views;

public partial class ProviderSetupWindow : Window
{
    private readonly AppSettings _original;
    private readonly HttpClient _http;
    private readonly CredentialVault _vault;
    private readonly CancellationTokenSource _closed = new();
    private readonly Dictionary<string, CloudProfile> _drafts;
    private readonly Dictionary<string, string> _draftKeys = new();
    private CloudPreset? _preset;
    private string? _loadedScope;
    private bool _loading, _busy;
    internal AppSettings? SelectedSettings { get; private set; }

    internal ProviderSetupWindow(AppSettings settings, HttpClient http, CredentialVault vault)
    {
        _original = settings; _http = http; _vault = vault;
        _drafts = new(settings.CloudProfiles);
        InitializeComponent();
        CloudModelCombo.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) =>
        {
            if (_loading || CreditCheck == null) return;
            CreditCheck.IsChecked = false;
            UpdateModelNotice();
        }));
        MaxHeight = SystemParameters.WorkArea.Height - 20;
        _loading = true;
        LocalEndpointInput.Text = settings.OllamaEndpoint;
        LocalModelCombo.ItemsSource = new[] { settings.OllamaModel, "qwen2.5:3b", "qwen2.5:7b" }.Distinct().ToList();
        LocalModelCombo.Text = settings.OllamaModel;
        ShowAllCheck.IsChecked = true;
        RefreshServices(settings.CloudServiceId);
        ModeCombo.SelectedIndex = (int)settings.Provider;
        _loading = false;
        PopulateService(); SetMode();
        SourceInitialized += (_, _) => NativeMethods.ExcludeFromCapture(this);
        Closed += (_, _) => _closed.Cancel();
    }

    private void SetMode()
    {
        QuickPanel.Visibility = ModeCombo.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        LocalPanel.Visibility = ModeCombo.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        CloudPanel.Visibility = ModeCombo.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ModeChanged(object sender, SelectionChangedEventArgs e) { if (!_loading && QuickPanel != null) SetMode(); }
    private void FilterChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || ServiceCombo == null) return;
        CaptureDraft(); var selected = _preset?.Id ?? _original.CloudServiceId;
        _loading = true; RefreshServices(selected); _loading = false; PopulateService();
    }
    private void RefreshServices(string id)
    {
        var choices = ProviderCatalog.All.Where(x => ShowAllCheck.IsChecked == true || x.Access == AccessCategory.FreeTier).ToList();
        ServiceCombo.ItemsSource = choices;
        ServiceCombo.SelectedItem = choices.FirstOrDefault(x => x.Id == id) ?? choices[0];
    }
    private void ServiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || CloudModelCombo == null) return;
        CaptureDraft(); PopulateService();
    }
    private void QuickService(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { Tag: string id }) return;
        ShowAllCheck.IsChecked = true;
        ServiceCombo.SelectedItem = ProviderCatalog.Get(id);
    }
    private void EndpointChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _preset == null || EndpointCombo.SelectedItem is not ServiceEndpoint endpoint) return;
        CloudEndpointInput.Text = endpoint.Url == _preset.Endpoint ? "" : endpoint.Url;
        ScopeChanged(this, new RoutedEventArgs());
    }
    private void PopulateService()
    {
        _loading = true;
        _preset = ServiceCombo.SelectedItem as CloudPreset;
        if (_preset == null) { _loading = false; return; }
        var profile = _drafts.GetValueOrDefault(_preset.Id) ?? _preset.DefaultProfile;
        PolicyText.Text = _preset.Name + " · " + _preset.CategoryLabel + "\n" + _preset.Notice;
        KeyLabel.Text = "3  粘贴自己的 " + _preset.KeyLabel;
        AccountPanel.Visibility = _preset.NeedsAccountId ? Visibility.Visible : Visibility.Collapsed;
        AccountInput.Text = profile.AccountId;
        CloudEndpointInput.Text = profile.Endpoint;
        EndpointPanel.Visibility = _preset.Endpoints.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        EndpointCombo.ItemsSource = _preset.Endpoints;
        EndpointCombo.SelectedItem = _preset.Endpoints.FirstOrDefault(x => x.Url == (profile.Endpoint.Length == 0 ? _preset.Endpoint : profile.Endpoint));
        DefaultEndpointLabel.Text = "预设：" + (_preset.Endpoint.Length == 0 ? "请填本机或可信服务的 API 根地址" : _preset.Endpoint);
        CloudModelCombo.ItemsSource = _preset.Models;
        CloudModelCombo.Text = string.IsNullOrWhiteSpace(profile.Model) ? _preset.DefaultProfile.Model : profile.Model;
        CreditCheck.IsChecked = profile.CreditUseAcknowledged;
        _loadedScope = null; KeyInput.Password = "";
        _loading = false;
        LoadScopedKey(); UpdateModelNotice();
    }
    private CloudProfile CurrentProfile() => new()
    {
        Model = CloudModelCombo.Text.Trim(), Endpoint = CloudEndpointInput.Text.Trim(), AccountId = AccountInput.Text.Trim(),
        CreditUseAcknowledged = CreditCheck.IsChecked == true
    };
    private void CaptureDraft()
    {
        if (_preset == null) return;
        _drafts[_preset.Id] = CurrentProfile();
        if (_loadedScope != null) _draftKeys[_loadedScope] = KeyInput.Password;
    }
    private void LoadScopedKey()
    {
        if (_preset == null) return;
        try
        {
            var scope = CredentialVault.Scope(_preset.Id, ProviderCatalog.Endpoint(_preset, CurrentProfile()));
            if (scope == _loadedScope) return;
            if (_loadedScope != null) _draftKeys[_loadedScope] = KeyInput.Password;
            KeyInput.Password = _draftKeys.GetValueOrDefault(scope) ?? _vault.Read(scope);
            _loadedScope = scope;
        }
        catch (Exception ex) { _loadedScope = null; KeyInput.Password = ""; ResultText.Text = ex.Message; }
    }
    private void ScopeChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _loading = true;
        EndpointCombo.SelectedItem = _preset?.Endpoints.FirstOrDefault(x => x.Url == (CloudEndpointInput.Text.Trim().Length == 0 ? _preset.Endpoint : CloudEndpointInput.Text.Trim()));
        _loading = false;
        CreditCheck.IsChecked = false; LoadScopedKey(); UpdateModelNotice();
    }
    private void ModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || CreditCheck == null) return;
        CreditCheck.IsChecked = false;
        _ = Dispatcher.BeginInvoke(UpdateModelNotice);
    }
    private void ModelFocusLost(object sender, KeyboardFocusChangedEventArgs e) { if (!_loading) UpdateModelNotice(); }
    private void UpdateModelNotice()
    {
        if (_preset == null) return;
        var needs = ProviderCatalog.NeedsCreditAcknowledgement(_preset, CurrentProfile());
        CreditCheck.Visibility = needs ? Visibility.Visible : Visibility.Collapsed;
        ModelNotice.Text = needs ? "当前选择的免费资格未核验，请确认控制台额度。读取到模型不代表拥有调用权限或免费资格。" : "这是内置的免费档候选；实际是否免费仍取决于账号档位和平台当前政策。";
    }

    private AppSettings BuildSettings()
    {
        CaptureDraft();
        return (_original with
        {
            Provider = (TranslationProvider)ModeCombo.SelectedIndex,
            CloudServiceId = _preset?.Id ?? _original.CloudServiceId,
            CloudProfiles = new(_drafts), OllamaEndpoint = LocalEndpointInput.Text.Trim(), OllamaModel = LocalModelCombo.Text.Trim()
        }).Sanitize();
    }
    private string CurrentKey(AppSettings settings)
    {
        if (settings.Provider != TranslationProvider.Cloud) return "";
        var preset = ProviderCatalog.Get(settings.CloudServiceId);
        var scope = CredentialVault.Scope(preset.Id, ProviderCatalog.Endpoint(preset, ProviderCatalog.Profile(settings)));
        if (_loadedScope != scope) LoadScopedKey();
        return KeyInput.Password.Trim();
    }

    private async Task TestAsync(bool apply)
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            UpdateModelNotice();
            var settings = BuildSettings(); var key = CurrentKey(settings);
            var translator = new TranslationClient(_http, settings, key);
            if (settings.Provider == TranslationProvider.Cloud) translator.Cloud.Validate(true);
            ResultText.Text = "正在翻译固定测试句，请稍候…";
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(50));
            var stopwatch = Stopwatch.StartNew();
            var result = await translator.TranslateAsync("Find the ancient relic before dawn.", timeout.Token);
            ResultText.Text = $"连接成功 · {stopwatch.Elapsed.TotalSeconds:F1} 秒\n{result}";
            if (apply)
            {
                if (settings.Provider == TranslationProvider.Cloud)
                {
                    var preset = ProviderCatalog.Get(settings.CloudServiceId);
                    _vault.Save(CredentialVault.Scope(preset.Id, ProviderCatalog.Endpoint(preset, ProviderCatalog.Profile(settings))), key);
                }
                SelectedSettings = settings;
                DialogResult = true;
            }
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested) { }
        catch (Exception ex) { ResultText.Text = "连接未成功：" + LiveSession.FriendlyError(ex); }
        finally { SetBusy(false); }
    }
    private void SetBusy(bool busy)
    {
        _busy = busy;
        ModeCombo.IsEnabled = !busy; ServiceCombo.IsEnabled = !busy; ShowAllCheck.IsEnabled = !busy;
        CloudModelCombo.IsEnabled = !busy; CloudEndpointInput.IsEnabled = !busy; KeyInput.IsEnabled = !busy;
        AccountInput.IsEnabled = !busy; CreditCheck.IsEnabled = !busy;
        EndpointCombo.IsEnabled = !busy; QuickServices.IsEnabled = !busy;
        TestButton.IsEnabled = !busy; UseButton.IsEnabled = !busy; FetchModelsButton.IsEnabled = !busy;
        LocalListButton.IsEnabled = !busy; PullButton.IsEnabled = !busy;
        LocalModelCombo.IsEnabled = !busy; LocalEndpointInput.IsEnabled = !busy;
    }
    private async void TestConnection(object sender, RoutedEventArgs e) => await TestAsync(false);
    private async void TestAndUse(object sender, RoutedEventArgs e) => await TestAsync(true);
    private async void FetchModels(object sender, RoutedEventArgs e)
    {
        if (_busy || _preset == null) return;
        SetBusy(true);
        try
        {
            var settings = BuildSettings();
            var client = new CloudTranslation(_http, settings, CurrentKey(settings));
            ResultText.Text = "正在读取账号模型列表…";
            var names = await client.ListModelsAsync(_closed.Token);
            var current = CloudModelCombo.Text;
            _loading = true;
            CloudModelCombo.ItemsSource = _preset.Models.Concat(names).Distinct().ToList();
            CloudModelCombo.Text = current.Length > 0 ? current : names.FirstOrDefault() ?? "";
            _loading = false; UpdateModelNotice();
            ResultText.Text = $"读取到 {names.Count} 个模型。列表不代表免费或有调用权限；请选择文本模型后测试。";
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested) { }
        catch (Exception ex) { ResultText.Text = "读取失败：" + LiveSession.FriendlyError(ex) + " 可手动填写控制台模型 ID。"; }
        finally { _loading = false; SetBusy(false); }
    }
    private async void LoadLocalModels(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            var endpoint = TranslationClient.ValidateOllamaEndpoint(LocalEndpointInput.Text);
            using var response = await _http.GetAsync(new Uri(endpoint, "api/tags"), _closed.Token);
            response.EnsureSuccessStatusCode();
            using var data = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(_closed.Token), cancellationToken: _closed.Token);
            var models = data.RootElement.GetProperty("models").EnumerateArray().Select(x => x.GetProperty("name").GetString() ?? "").Where(x => x.Length > 0).ToList();
            LocalModelCombo.ItemsSource = models;
            if (models.Count > 0) LocalModelCombo.Text = models.Contains(_original.OllamaModel) ? _original.OllamaModel : models[0];
            ResultText.Text = models.Count > 0 ? $"Ollama 已连接，发现 {models.Count} 个本机模型。选择后点击「测试并使用」。" : "Ollama 已连接，还没有模型。可点击「下载推荐 3B 模型」。";
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested) { }
        catch (Exception ex) { ResultText.Text = "未连接到 Ollama。请先安装并打开，再点击检测。 " + LiveSession.FriendlyError(ex); }
        finally { SetBusy(false); }
    }
    private async void PullModel(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            var endpoint = TranslationClient.ValidateOllamaEndpoint(LocalEndpointInput.Text);
            if (!endpoint.IsLoopback) throw new ArgumentException("向导只允许在本机下载模型，请使用 localhost 地址。");
            ResultText.Text = "开始下载 qwen2.5:3b（约 1.9 GB），可取消向导停止本次请求…";
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, "api/pull")) { Content = JsonContent.Create(new { name = "qwen2.5:3b", stream = true }) };
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _closed.Token);
            response.EnsureSuccessStatusCode();
            using var reader = new System.IO.StreamReader(await response.Content.ReadAsStreamAsync(_closed.Token));
            var success = false;
            while (await reader.ReadLineAsync(_closed.Token) is { } line)
            {
                using var data = JsonDocument.Parse(line); var root = data.RootElement;
                if (root.TryGetProperty("error", out _)) throw new InvalidOperationException("Ollama 下载失败，请检查网络、磁盘空间和 Ollama 日志。");
                var status = root.TryGetProperty("status", out var value) ? value.GetString() : "下载中";
                if (root.TryGetProperty("total", out var total) && total.GetDouble() > 0 && root.TryGetProperty("completed", out var completed))
                    ResultText.Text = $"下载中 · {completed.GetDouble() / total.GetDouble():P0} · {completed.GetDouble() / 1048576:F0} / {total.GetDouble() / 1048576:F0} MB";
                else ResultText.Text = "Ollama：" + status;
                if (status == "success") success = true;
            }
            if (!success) throw new InvalidOperationException("下载未报告完成，可重新检测或再次下载继续。");
            LocalModelCombo.ItemsSource = new[] { "qwen2.5:3b" }; LocalModelCombo.Text = "qwen2.5:3b";
            ResultText.Text = "模型已下载，点击「测试并使用」完成配置。";
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested) { }
        catch (Exception ex) { ResultText.Text = "下载未完成：" + LiveSession.FriendlyError(ex); }
        finally { SetBusy(false); }
    }
    private void ForgetKey(object sender, RoutedEventArgs e)
    {
        if (_busy || _loadedScope == null) return;
        try { _vault.Save(_loadedScope, ""); _draftKeys[_loadedScope] = ""; KeyInput.Password = ""; ResultText.Text = "此平台和地址的本机密钥已清除。其他平台不受影响。"; }
        catch (Exception ex) { ResultText.Text = ex.Message; }
    }
    private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    private void OpenKeyPage(object sender, RoutedEventArgs e) { if (_preset != null) OpenUrl(_preset.KeyUrl); }
    private void OpenDocs(object sender, RoutedEventArgs e) { if (_preset != null) OpenUrl(_preset.DocsUrl); }
    private void OpenOllama(object sender, RoutedEventArgs e) => OpenUrl("https://ollama.com/download/windows");
    private void Cancel(object sender, RoutedEventArgs e) => Close();

    internal async Task<bool> VerifyForDiagnosticsAsync()
    {
        ModeCombo.SelectedIndex = 2;
        if (ServiceCombo.Items.Count != ProviderCatalog.All.Count) return false;
        QuickService(new Button { Tag = "deepseek" }, new RoutedEventArgs());
        if (_preset?.Id != "deepseek" || string.IsNullOrWhiteSpace(CloudModelCombo.Text)) return false;
        QuickService(new Button { Tag = "doubao" }, new RoutedEventArgs());
        if (_preset?.Id != "doubao" || string.IsNullOrWhiteSpace(CloudModelCombo.Text)) return false;
        QuickService(new Button { Tag = "dashscope" }, new RoutedEventArgs());
        KeyInput.Password = "regional-diagnostic-key";
        EndpointCombo.SelectedIndex = 1;
        if (KeyInput.Password.Length != 0 || !CloudEndpointInput.Text.Contains("dashscope-intl", StringComparison.Ordinal)) return false;
        EndpointCombo.SelectedIndex = 0;
        if (KeyInput.Password != "regional-diagnostic-key") return false;
        foreach (var preset in ProviderCatalog.All)
        {
            ServiceCombo.SelectedItem = preset;
            if (preset.NeedsAccountId) AccountInput.Text = new string('a', 32);
            if (preset.Id == "custom") CloudEndpointInput.Text = "http://localhost:1234/v1";
            UpdateLayout();
            if (_preset?.Id != preset.Id || PolicyText.Text.Length == 0) return false;
        }
        ServiceCombo.SelectedItem = ProviderCatalog.Get("zhipu");
        KeyInput.Password = "diagnostic-not-a-real-key";
        await TestAsync(false);
        var success = ResultText.Text.Contains("连接成功", StringComparison.Ordinal);
        CloudEndpointInput.Text = "https://example.invalid/v1";
        ScopeChanged(this, new RoutedEventArgs());
        var isolated = KeyInput.Password.Length == 0;
        CloudEndpointInput.Text = "";
        ScopeChanged(this, new RoutedEventArgs());
        return success && isolated && KeyInput.Password == "diagnostic-not-a-real-key";
    }

    internal async Task ApplyForDiagnosticsAsync()
    {
        ModeCombo.SelectedIndex = 2;
        ServiceCombo.SelectedItem = ProviderCatalog.Get("zhipu");
        KeyInput.Password = "diagnostic-not-a-real-key";
        await TestAsync(true);
    }
}
