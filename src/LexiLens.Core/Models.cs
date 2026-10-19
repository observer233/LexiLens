namespace LexiLens.Core;

public enum TranslationProvider { MyMemory, Ollama, Cloud }
public enum OcrMode { Enhanced, Windows }
public enum OcrImageMode { Auto, Original, LightOnDark, LowContrast }

public sealed record CloudProfile
{
    public string Model { get; init; } = "";
    public string Endpoint { get; init; } = "";
    public string AccountId { get; init; } = "";
    public bool CreditUseAcknowledged { get; init; }
}

public sealed record CaptureRegion(int X, int Y, int Width, int Height)
{
    public bool IsValid => Width >= 20 && Height >= 20 && Width <= 16384 && Height <= 16384;
    public override string ToString() => $"{Width} × {Height} px · ({X}, {Y})";
}

public sealed record AppSettings
{
    public TranslationProvider Provider { get; init; } = TranslationProvider.MyMemory;
    public string SourceLanguage { get; init; } = "en-US";
    public string TargetLanguage { get; init; } = "zh-CN";
    public string OllamaEndpoint { get; init; } = "http://localhost:11434";
    public string OllamaModel { get; init; } = "qwen2.5:3b";
    public string CloudServiceId { get; init; } = "zhipu";
    public Dictionary<string, CloudProfile> CloudProfiles { get; init; } = new();
    public bool OnboardingCompleted { get; init; }
    public OcrMode OcrMode { get; init; } = OcrMode.Enhanced;
    public OcrImageMode OcrImageMode { get; init; } = OcrImageMode.Auto;
    public int ScanIntervalMs { get; init; } = 700;
    public double OverlayOpacity { get; init; } = 0.92;
    public double FontSize { get; init; } = 21;
    public bool ShowOriginal { get; init; } = true;
    public CaptureRegion? Region { get; init; }

    public AppSettings Sanitize() => this with
    {
        ScanIntervalMs = Math.Clamp(ScanIntervalMs, 350, 5000),
        OverlayOpacity = double.IsFinite(OverlayOpacity) ? Math.Clamp(OverlayOpacity, 0.35, 1) : 0.92,
        FontSize = double.IsFinite(FontSize) ? Math.Clamp(FontSize, 14, 36) : 21,
        Region = Region?.IsValid == true ? Region : null,
        SourceLanguage = string.IsNullOrWhiteSpace(SourceLanguage) ? "en-US" : SourceLanguage,
        TargetLanguage = "zh-CN",
        OllamaEndpoint = string.IsNullOrWhiteSpace(OllamaEndpoint) ? "http://localhost:11434" : OllamaEndpoint.Trim(),
        OllamaModel = string.IsNullOrWhiteSpace(OllamaModel) ? "qwen2.5:3b" : OllamaModel.Trim(),
        CloudServiceId = ProviderCatalog.Find(CloudServiceId)?.Id ?? "zhipu",
        CloudProfiles = CloudProfiles ?? new(),
        Provider = Enum.IsDefined(Provider) ? Provider : TranslationProvider.MyMemory,
        OcrMode = Enum.IsDefined(OcrMode) ? OcrMode : OcrMode.Enhanced,
        OcrImageMode = Enum.IsDefined(OcrImageMode) ? OcrImageMode : OcrImageMode.Auto
    };
}

public sealed record VocabularyEntry(string Word, string Meaning, string Context, DateTimeOffset AddedAt);
public sealed record LiveUpdate(string Source, string? Translation = null, string? Error = null, bool IsBusy = false);

public interface ITextSource
{
    Task<string> ReadAsync(CancellationToken cancellationToken);
}

public interface ITranslator
{
    Task<string> TranslateAsync(string text, CancellationToken cancellationToken);
}
