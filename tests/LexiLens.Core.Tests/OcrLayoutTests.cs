using LexiLens.Core;
using Xunit;

namespace LexiLens.Core.Tests;

public class OcrLayoutTests
{
    [Fact]
    public void FragmentsOnTheSameLineRemainOneSentenceDespiteUnevenBaselines()
    {
        OcrTextBox[] boxes = [new("relic.", 380, 33, 30), new("Retrieve", 70, 30, 32), new("the ancient", 240, 35, 28)];
        Assert.Equal("Retrieve the ancient relic.", OcrLineLayout.Join(boxes));
    }

    [Fact]
    public void QuestLinesStayInTopToBottomOrderAndBlankFragmentsAreIgnored()
    {
        OcrTextBox[] boxes = [new("before dawn.", 240, 75, 25), new("Find the relic", 150, 30, 30), new("Return", 60, 77, 30), new(" ", 1, 1, 1)];
        Assert.Equal("Find the relic\nReturn before dawn.", OcrLineLayout.Join(boxes));
    }
}
