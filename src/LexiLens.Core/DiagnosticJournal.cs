using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;
using System.Text.Unicode;

namespace LexiLens.Core;

public sealed class DiagnosticJournal(string directory)
{
    private readonly object _sync = new();
    private readonly Queue<PipelineEvent> _events = ReadRecent(Path.Combine(directory, "pipeline.jsonl"));
    private readonly string _path = Path.Combine(directory, "pipeline.jsonl");
    private static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All), Converters = { new JsonStringEnumConverter() } };
    public string? WriteError { get; private set; }
    public string DirectoryPath => directory;
    private static Queue<PipelineEvent> ReadRecent(string path)
    {
        var events = new Queue<PipelineEvent>();
        try
        {
            for (var file = 3; file >= 0; file--)
            {
                var source = file == 0 ? path : path + "." + file;
                if (!File.Exists(source)) continue;
                foreach (var line in File.ReadLines(source))
                {
                    try { if (JsonSerializer.Deserialize<PipelineEvent>(line, Options) is { } item) events.Enqueue(item); }
                    catch (JsonException) { }
                    while (events.Count > 500) events.Dequeue();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return events;
    }

    public void Append(PipelineEvent item)
    {
        lock (_sync)
        {
            _events.Enqueue(item);
            while (_events.Count > 500) _events.Dequeue();
            try
            {
                Directory.CreateDirectory(directory);
                if (File.Exists(_path) && new FileInfo(_path).Length > 1_048_576)
                {
                    for (var i = 2; i >= 0; i--)
                    {
                        var source = i == 0 ? _path : _path + "." + i;
                        if (File.Exists(source)) File.Move(source, _path + "." + (i + 1), true);
                    }
                }
                File.AppendAllText(_path, JsonSerializer.Serialize(item, Options) + "\n", Encoding.UTF8);
                WriteError = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { WriteError = "日志文件无法写入；当前会话记录仍可导出。"; }
        }
    }

    public IReadOnlyList<PipelineEvent> Snapshot() { lock (_sync) return _events.ToArray(); }

    public string Export(AppSettings settings, string version)
    {
        // Explicit allowlist: never serialize AppSettings/CloudProfiles/endpoint.
        var profile = ProviderCatalog.Profile(settings);
        return JsonSerializer.Serialize(new
        {
            version, exportedAt = DateTimeOffset.Now, operatingSystem = Environment.OSVersion.VersionString,
            architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            provider = settings.Provider.ToString(), service = settings.Provider == TranslationProvider.Cloud ? settings.CloudServiceId : settings.Provider.ToString(),
            model = settings.Provider == TranslationProvider.Cloud ? profile.Model : settings.Provider == TranslationProvider.Ollama ? settings.OllamaModel : "MyMemory",
            ocrMode = settings.OcrMode.ToString(), imageMode = settings.OcrImageMode.ToString(), language = settings.SourceLanguage,
            regionSize = settings.Region == null ? null : new { settings.Region.Width, settings.Region.Height },
            scanIntervalMs = settings.ScanIntervalMs, events = Snapshot()
        }, new JsonSerializerOptions(Options) { WriteIndented = true });
    }
}
