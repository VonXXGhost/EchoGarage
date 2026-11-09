using EchoGarage.Core.Services;

namespace EchoGarage.Tests;

public sealed class PathUtilTests
{
    [Theory]
    [InlineData(@"C:\Music\a.mp3", true)]
    [InlineData(@"C:\Music\a.FLAC", true)]
    [InlineData(@"C:\Music\a.m4a", true)]
    [InlineData(@"C:\Music\a.wav", true)]
    [InlineData(@"C:\Music\a.txt", false)]
    [InlineData(null, false)]
    public void IsSupportedAudio_ChecksExtension(string? path, bool expected)
    {
        Assert.Equal(expected, PathUtil.IsSupportedAudio(path));
    }

    [Fact]
    public void IsUnderFolder_HandlesCaseAndSeparators()
    {
        Assert.True(PathUtil.IsUnderFolder(@"G:\Music\Album\track.flac", @"G:\Music"));
        Assert.True(PathUtil.IsUnderFolder(@"G:\Music\Album\track.flac", @"g:\music\album"));
        Assert.True(PathUtil.IsUnderFolder(@"G:\Music", @"G:\Music\"));
        Assert.False(PathUtil.IsUnderFolder(@"G:\Musicology\track.flac", @"G:\Music"));
        Assert.False(PathUtil.IsUnderFolder(@"G:\Other\track.flac", @"G:\Music"));
    }

    [Fact]
    public void EscapeLike_EscapesWildcards()
    {
        Assert.Equal("50\\%\\_off", PathUtil.EscapeLike("50%_off"));
    }

    [Fact]
    public void CoverKey_GroupsTracksOfSameAlbum()
    {
        var a = CoverKeyFactory.ForTrack(@"G:\a\1.flac", "Album", "Artist");
        var b = CoverKeyFactory.ForTrack(@"G:\b\2.flac", " album ", "artist");

        Assert.Equal(a, b);
        Assert.StartsWith("album_", a);
    }

    [Fact]
    public void CoverKey_IsPerFileWithoutAlbum()
    {
        var a = CoverKeyFactory.ForTrack(@"G:\a\1.flac", string.Empty, "Artist");
        var b = CoverKeyFactory.ForTrack(@"G:\a\2.flac", string.Empty, "Artist");

        Assert.NotEqual(a, b);
        Assert.StartsWith("file_", a);
    }
}
