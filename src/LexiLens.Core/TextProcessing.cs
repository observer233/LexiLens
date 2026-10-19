using System.Text;
using System.Text.RegularExpressions;

namespace LexiLens.Core;

public static partial class TextProcessing
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    public static string Normalize(string text) => Whitespace().Replace(text, " ").Trim();

    // Split on Unicode scalar boundaries, honoring MyMemory's UTF-8 byte limit.
    public static IReadOnlyList<string> SplitUtf8(string text, int maxBytes = 480)
    {
        if (maxBytes < 4) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        var parts = new List<string>();
        var builder = new StringBuilder();
        var bytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > maxBytes)
            {
                parts.Add(builder.ToString());
                builder.Clear();
                bytes = 0;
            }
            builder.Append(rune.ToString());
            bytes += rune.Utf8SequenceLength;
        }
        if (builder.Length > 0) parts.Add(builder.ToString());
        return parts;
    }
}
