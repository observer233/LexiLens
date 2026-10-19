using System.Text.RegularExpressions;

namespace LexiLens.Core;

// Small OCR differences must not indefinitely prevent the first translation.
public sealed class TextStabilizer
{
    private string _previous = "", _accepted = "";
    private int _repeats, _pending;

    public StabilizedText Observe(string text)
    {
        text = TextProcessing.Normalize(text);
        _repeats = Similar(text, _previous) ? _repeats + 1 : 1;
        _previous = text;
        if (text == "")
        {
            _pending = 0;
            if (_repeats >= 3 && _accepted.Length > 0) { _accepted = ""; return new("", true, "TEXT_CLEARED"); }
            return new(text, false, "OCR_EMPTY");
        }
        if (Similar(text, _accepted)) { _pending = 0; return new(text, false, "TEXT_UNCHANGED"); }
        _pending++;
        if (_repeats >= 2 || _pending >= 3)
        {
            var code = _repeats >= 2 ? "TEXT_READY" : "TEXT_DEBOUNCE_LIMIT";
            _accepted = text; _pending = 0;
            return new(text, true, code);
        }
        return new(text, false, "TEXT_PENDING");
    }

    public static bool Similar(string a, string b)
    {
        if (a == b) return true;
        if (a.Length < 16 || b.Length < 16 || Math.Abs(a.Length - b.Length) > 2) return false;
        // Do not collapse changing quest counts, prices or item quantities.
        if (!Regex.Matches(a, @"\d+").Select(x => x.Value).SequenceEqual(Regex.Matches(b, @"\d+").Select(x => x.Value))) return false;
        var allowance = Math.Min(2, Math.Max(a.Length, b.Length) / 30 + 1);
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        var current = new int[b.Length + 1];
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var start = Math.Max(1, i - allowance); var end = Math.Min(b.Length, i + allowance); var minimum = allowance + 1;
            if (start > 1) current[start - 1] = allowance + 1;
            if (end < b.Length) current[end + 1] = allowance + 1;
            for (var j = start; j <= end; j++)
            {
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1));
                minimum = Math.Min(minimum, current[j]);
            }
            if (minimum > allowance) return false;
            (previous, current) = (current, previous);
        }
        return previous[b.Length] <= allowance;
    }
}

public sealed record StabilizedText(string Text, bool Submit, string Code);
