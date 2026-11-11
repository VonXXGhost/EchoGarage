using EchoGarage.Core.Services;

namespace EchoGarage.Tests;

public sealed class PlaylistSortPolicyTests
{
    [Theory]
    [InlineData("Album")]
    [InlineData("AlbumArtist")]
    public void AlbumOrientedSorts_SupportGrouping(string sortColumn) =>
        Assert.True(PlaylistSortPolicy.SupportsAlbumGrouping(sortColumn));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Title")]
    [InlineData("Artist")]
    [InlineData("TrackNumberSort")]
    [InlineData("YearSort")]
    [InlineData("DurationSort")]
    public void OtherSorts_DoNotSupportGrouping(string? sortColumn) =>
        Assert.False(PlaylistSortPolicy.SupportsAlbumGrouping(sortColumn));

    [Fact]
    public void DefaultOrder_IsRowNumber() =>
        Assert.Equal("RowNumber", PlaylistSortPolicy.DefaultOrder);
}
