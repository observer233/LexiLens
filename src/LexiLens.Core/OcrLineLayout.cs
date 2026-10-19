namespace LexiLens.Core;

public sealed record OcrTextBox(string Text, double CenterX, double CenterY, double Height, double Width = 0);

public static class OcrLineLayout
{
    public static string Join(IEnumerable<OcrTextBox> boxes)
    {
        var rows = new List<List<OcrTextBox>>();
        foreach (var box in boxes.Where(x => !string.IsNullOrWhiteSpace(x.Text)).OrderBy(x => x.CenterY))
        {
            var row = rows.LastOrDefault();
            if (row == null || Math.Abs(box.CenterY - row.Average(x => x.CenterY)) > Math.Min(box.Height, row.Average(x => x.Height)) * .6)
                rows.Add([box]);
            else row.Add(box);
        }
        return string.Join("\n", rows.Select(row => string.Join(" ", row.OrderBy(x => x.CenterX).Select(x => x.Text.Trim()))));
    }
}
