using System.Text;
using System.Text.Json;

namespace LexiLens.Core;

public sealed class LocalStore(string directory)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public string DirectoryPath => directory;

    public T Load<T>(string fileName, T fallback)
    {
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path)) return fallback;
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? fallback; }
        catch (JsonException)
        {
            // Retain malformed user data for recovery instead of overwriting it silently.
            File.Copy(path, path + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmssfff}", true);
            return fallback;
        }
    }

    public void Save<T>(string fileName, T value)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, Options), new UTF8Encoding(false));
        File.Move(temporary, path, true);
    }

    public static string ToCsv(IEnumerable<VocabularyEntry> entries)
    {
        static string Quote(string value)
        {
            // Prevent a saved word/context from becoming a spreadsheet formula.
            if (value.Length > 0 && "=+-@\t\r\n".Contains(value[0])) value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        var lines = new List<string> { "Word,Meaning,Context,AddedAt" };
        lines.AddRange(entries.Select(x => string.Join(",", Quote(x.Word), Quote(x.Meaning), Quote(x.Context), Quote(x.AddedAt.ToString("O")))));
        return string.Join("\r\n", lines) + "\r\n";
    }
}
