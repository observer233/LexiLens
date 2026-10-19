using LexiLens.Core;
using Xunit;

namespace LexiLens.Core.Tests;

public class StorageTests
{
    [Fact]
    public void CorruptFilesArePreservedAndAtomicSaveRoundTrips()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lexilens-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "settings.json"), "{broken");
            var store = new LocalStore(directory);
            Assert.Equal(new AppSettings(), store.Load("settings.json", new AppSettings()));
            Assert.Single(Directory.GetFiles(directory, "*.corrupt-*"));
            var settings = new AppSettings { ScanIntervalMs = 1234, Region = new CaptureRegion(-100, 20, 80, 80) };
            store.Save("settings.json", settings);
            Assert.Equal(settings, store.Load("settings.json", new AppSettings()));
            Assert.False(File.Exists(Path.Combine(directory, "settings.json.tmp")));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void CsvEscapesQuotesNewlinesAndSpreadsheetFormulas()
    {
        var csv = LocalStore.ToCsv([new VocabularyEntry("=SUM(1)", "遗物,\"稀有\"", "line1\nline2", DateTimeOffset.UnixEpoch)]);
        Assert.Contains("\"'=SUM(1)\"", csv);
        Assert.Contains("\"遗物,\"\"稀有\"\"\"", csv);
        Assert.Contains("\"line1\nline2\"", csv);
    }
}
