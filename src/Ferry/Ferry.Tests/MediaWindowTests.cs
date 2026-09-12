using System.Collections.Generic;
using Ferry;
using Xunit;

namespace Ferry.Tests;

public class MediaWindowTests
{
    private static FerryMessage Photo(int id, long createdAt, bool deleted = false) =>
        new(id, "file", "phone", "OnePlus", null, $"IMG_{id}.jpg", 1024, deleted, null, createdAt);

    private static FerryMessage Doc(int id, long createdAt) =>
        new(id, "file", "phone", "OnePlus", null, $"notes_{id}.pdf", 1024, false, null, createdAt);

    private static FerryMessage Text(int id, long createdAt) =>
        new(id, "text", "phone", "OnePlus", $"hello {id}", null, null, false, null, createdAt);

    [Fact]
    public void SelectImages_KeepsOnlyLiveImages()
    {
        var messages = new List<FerryMessage>
        {
            Photo(1, 100), Doc(2, 200), Text(3, 300), Photo(4, 400, deleted: true),
        };

        var ids = MediaWindow.SelectImages(messages).Select(m => m.Id);
        Assert.Equal([1], ids);
    }

    [Fact]
    public void SelectImages_OrdersNewestFirst()
    {
        var messages = new List<FerryMessage> { Photo(1, 100), Photo(2, 300), Photo(3, 200) };

        Assert.Equal([2, 3, 1], MediaWindow.SelectImages(messages).Select(m => m.Id));
    }

    [Fact]
    public void SelectImages_BreaksTimestampTiesByIdDescending()
    {
        // Twenty photos uploaded in one burst can share a millisecond; id is the
        // server's insertion order, so it settles the tie.
        var messages = new List<FerryMessage> { Photo(7, 500), Photo(9, 500), Photo(8, 500) };

        Assert.Equal([9, 8, 7], MediaWindow.SelectImages(messages).Select(m => m.Id));
    }

    [Fact]
    public void IsStale_TrueWhenANewerPhotoArrived()
    {
        var shown = MediaWindow.SelectImages([Photo(1, 100)]);
        var fresh = MediaWindow.SelectImages([Photo(1, 100), Photo(2, 200)]);

        Assert.True(MediaWindow.IsStale(shown, fresh));
    }

    [Fact]
    public void IsStale_TrueWhenAPhotoWasDeleted()
    {
        var shown = MediaWindow.SelectImages([Photo(1, 100), Photo(2, 200)]);
        var fresh = MediaWindow.SelectImages([Photo(1, 100), Photo(2, 200, deleted: true)]);

        Assert.True(MediaWindow.IsStale(shown, fresh));
    }

    [Fact]
    public void IsStale_FalseWhenOnlyNonImagesChanged()
    {
        var photos = new List<FerryMessage> { Photo(1, 100), Photo(2, 200) };
        var shown = MediaWindow.SelectImages(photos);
        var fresh = MediaWindow.SelectImages([.. photos, Text(3, 300), Doc(4, 400)]);

        Assert.False(MediaWindow.IsStale(shown, fresh));
    }

    [Fact]
    public void IsStale_FalseOnTwoEmptyRuns()
    {
        Assert.False(MediaWindow.IsStale([], []));
    }

    [Fact]
    public void Caption_CarriesNameSenderAndSize()
    {
        var caption = MediaWindow.Caption(Photo(1, 0));

        Assert.StartsWith("IMG_1.jpg\n", caption);
        Assert.Contains("OnePlus", caption);
        Assert.Contains("1.0 KB", caption);
    }
}
