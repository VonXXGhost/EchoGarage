using EchoGarage.Core.Services;

namespace EchoGarage.Tests;

public sealed class AlbumCoverMatcherTests
{
    [Fact]
    public void MatchCover_NoImages_ReturnsNull()
    {
        Assert.Null(AlbumCoverMatcher.MatchCover(["song.mp3", "notes.txt"]));
    }

    [Fact]
    public void MatchCover_SingleImage_ReturnsIt()
    {
        var result = AlbumCoverMatcher.MatchCover(["song.mp3", "art.PNG"]);
        Assert.EndsWith("art.PNG", result);
    }

    [Fact]
    public void MatchCover_MultipleImages_PrefersCoverOverFolder()
    {
        var result = AlbumCoverMatcher.MatchCover(["song.mp3", "folder.png", "cover.JPG", "random.png"]);
        Assert.EndsWith("cover.JPG", result);
    }

    [Fact]
    public void MatchCover_MultipleImages_PrefersFolderWhenNoCover()
    {
        var result = AlbumCoverMatcher.MatchCover(["song.mp3", "folder.png", "random.jpg"]);
        Assert.EndsWith("folder.png", result);
    }

    [Fact]
    public void MatchCover_MultipleImagesWithoutPreferredName_ReturnsNull()
    {
        Assert.Null(AlbumCoverMatcher.MatchCover(["song.mp3", "a.jpg", "b.png"]));
    }

    [Fact]
    public void FindImages_FiltersAndSorts()
    {
        var images = AlbumCoverMatcher.FindImages(["z.webp", "song.mp3", "a.jpg", "notes.txt"]);
        Assert.Equal(2, images.Count);
        Assert.EndsWith("a.jpg", images[0]);
        Assert.EndsWith("z.webp", images[1]);
    }
}
