namespace LexiLens.Core;

// Relative to the captured image, before any OCR preprocessing/scaling.
public sealed record TextBounds(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool IsValid => double.IsFinite(X + Y + Width + Height) && X >= 0 && Y >= 0 && Width > 0 && Height > 0 && Right <= 1.001 && Bottom <= 1.001;
    public double Overlap(TextBounds b)
    {
        var intersection = Math.Max(0, Math.Min(Right, b.Right) - Math.Max(X, b.X)) * Math.Max(0, Math.Min(Bottom, b.Bottom) - Math.Max(Y, b.Y));
        return intersection / Math.Max(.000001, Width * Height + b.Width * b.Height - intersection);
    }
}

public sealed record RecognizedBlock(string Text, TextBounds Bounds);
public sealed record SpatialBlock(int Id, string Source, TextBounds Bounds, string? Translation = null, string? Error = null);
public interface IBlockTextSource : ITextSource
{
    Task<IReadOnlyList<RecognizedBlock>> ReadBlocksAsync(CancellationToken ct);
}

public static class ParagraphLayout
{
    private sealed record Line(string Text, double Left, double Top, double Right, double Bottom)
    {
        internal double Height => Bottom - Top;
        internal double Width => Right - Left;
    }

    public static IReadOnlyList<RecognizedBlock> Group(IEnumerable<OcrTextBox> boxes, int width, int height)
    {
        if (width <= 0 || height <= 0) return [];
        var fragments = boxes.Where(x => !string.IsNullOrWhiteSpace(x.Text) && x.Text.Any(char.IsLetterOrDigit) && x.Height > 0 && double.IsFinite(x.CenterX + x.CenterY + x.Height + x.Width))
            .Select(x => new Line(x.Text.Trim(), Math.Max(0, x.CenterX - x.Width / 2), Math.Max(0, x.CenterY - x.Height / 2),
                Math.Min(width, x.CenterX + x.Width / 2), Math.Min(height, x.CenterY + x.Height / 2)))
            .Where(x => x.Width > 0 && x.Height > 0).OrderBy(x => x.Top).ThenBy(x => x.Left).ToList();
        // Components use the individual word baselines, not a partially merged
        // row's top edge. Descenders can arrive later in OCR order and still join.
        var parents = Enumerable.Range(0, fragments.Count).ToArray();
        int Root(int i) { while (parents[i] != i) { parents[i] = parents[parents[i]]; i = parents[i]; } return i; }
        for (var i = 0; i < fragments.Count; i++)
            for (var j = i + 1; j < fragments.Count; j++)
            {
                var a = fragments[i]; var b = fragments[j];
                if (Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top) < Math.Min(a.Height, b.Height) * .55) continue;
                if (Math.Max(a.Height, b.Height) >= Math.Min(a.Height, b.Height) * 1.8) continue;
                if (Math.Max(b.Left - a.Right, a.Left - b.Right) > Math.Max(a.Height, b.Height) * 1.5) continue;
                parents[Root(j)] = Root(i);
            }
        var lines = fragments.Select((line, index) => (line, root: Root(index))).GroupBy(x => x.root).Select(group =>
        {
            var words = group.Select(x => x.line).OrderBy(x => x.Left).ToArray();
            return new Line(string.Join(" ", words.Select(x => x.Text)), words.Min(x => x.Left), words.Min(x => x.Top), words.Max(x => x.Right), words.Max(x => x.Bottom));
        }).ToList();
        var paragraphs = new List<List<Line>>();
        foreach (var line in lines.OrderBy(x => x.Top).ThenBy(x => x.Left))
        {
            var target = paragraphs.Where(p =>
            {
                var previous = p[^1]; var gap = line.Top - previous.Bottom;
                var overlap = Math.Min(line.Right, previous.Right) - Math.Max(line.Left, previous.Left);
                return gap >= -Math.Min(line.Height, previous.Height) * .2 && gap <= Math.Min(line.Height, previous.Height) * 1.05
                    && Math.Max(line.Height, previous.Height) < Math.Min(line.Height, previous.Height) * 1.65
                    && (Math.Abs(line.Left - previous.Left) < Math.Min(line.Height, previous.Height) * 1.2
                        || overlap >= Math.Min(line.Width, previous.Width) * .65);
            }).OrderBy(p => line.Top - p[^1].Bottom).FirstOrDefault();
            if (target == null) paragraphs.Add([line]); else target.Add(line);
        }
        return paragraphs.Select(p =>
        {
            var x = p.Min(x => x.Left) / width; var y = p.Min(x => x.Top) / height;
            return new RecognizedBlock(string.Join("\n", p.Select(x => x.Text)), new(x, y, p.Max(x => x.Right) / width - x, p.Max(x => x.Bottom) / height - y));
        }).OrderBy(x => x.Bounds.Y).ThenBy(x => x.Bounds.X).ToArray();
    }
}
