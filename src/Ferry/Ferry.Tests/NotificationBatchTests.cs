using System.Collections.Generic;
using Ferry;
using Xunit;

namespace Ferry.Tests;

public class NotificationBatchTests
{
    private static FerryMessage Photo(int id) =>
        new(id, "file", "phone", "OnePlus", null, $"IMG_{id}.jpg", 1024, false, null, id);

    private static FerryMessage File(int id) =>
        new(id, "file", "phone", "OnePlus", null, $"notes_{id}.pdf", 1024, false, null, id);

    private static FerryMessage Text(int id) =>
        new(id, "text", "phone", "OnePlus", $"hello {id}", null, null, false, null, id);

    [Fact]
    public void Summarize_PhotosOnly_CountsAndPluralizes()
    {
        Assert.Equal("2 photos", MainWindow.SummarizeBatch([Photo(1), Photo(2)]));
        Assert.Equal("20 photos", MainWindow.SummarizeBatch([.. Enumerable(20)]));
    }

    [Fact]
    public void Summarize_TwoKinds_JoinsWithAnd()
    {
        var batch = new List<FerryMessage> { Photo(1), Photo(2), File(3) };
        Assert.Equal("2 photos and 1 file", MainWindow.SummarizeBatch(batch));
    }

    [Fact]
    public void Summarize_ThreeKinds_UsesCommasAndFinalAnd()
    {
        var batch = new List<FerryMessage> { Photo(1), File(2), Text(3), Text(4) };
        Assert.Equal("1 photo, 1 file and 2 messages", MainWindow.SummarizeBatch(batch));
    }

    [Fact]
    public void Summarize_TextsOnly_SaysMessages()
    {
        Assert.Equal("3 messages", MainWindow.SummarizeBatch([Text(1), Text(2), Text(3)]));
    }

    private static IEnumerable<FerryMessage> Enumerable(int n)
    {
        for (var i = 0; i < n; i++) yield return Photo(i);
    }
}
