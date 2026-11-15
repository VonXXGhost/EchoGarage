using EchoGarage.Core.Models;

namespace EchoGarage.Tests;

public sealed class TagBatchEditTests
{
    private static AudioTagData CreateTag() => new(
        "Title", "Artist", "Album", "AlbumArtist", "Composer", "Genre", "Comment",
        2024, 3, 1, 0, 0, 0, 0, "FLAC", null, null);

    [Fact]
    public void GetFieldValues_FormatsNumbersAndEmptyZeros()
    {
        var values = TagBatchEditor.GetFieldValues(CreateTag());

        Assert.Equal("2024", values[TagFieldKeys.Year]);
        Assert.Equal("3", values[TagFieldKeys.TrackNumber]);
        Assert.Equal("1", values[TagFieldKeys.DiscNumber]);
        Assert.Equal("Title", values[TagFieldKeys.Title]);
    }

    [Fact]
    public void CreateEdit_WithoutEdits_KeepsEveryFieldFromSource()
    {
        var source = CreateTag();
        var edit = TagBatchEditor.CreateEdit(source, new TagBatchEdit());

        Assert.Equal(source.Title, edit.Title);
        Assert.Equal(source.Artist, edit.Artist);
        Assert.Equal(source.Album, edit.Album);
        Assert.Equal(source.AlbumArtist, edit.AlbumArtist);
        Assert.Equal(source.Composer, edit.Composer);
        Assert.Equal(source.Genre, edit.Genre);
        Assert.Equal(source.Comment, edit.Comment);
        Assert.Equal(2024, edit.Year);
        Assert.Equal(3, edit.TrackNumber);
        Assert.Equal(1, edit.DiscNumber);
        Assert.Null(edit.NewCoverBytes);
        Assert.False(edit.RemoveCover);
    }

    [Fact]
    public void CreateEdit_OverridesOnlyModifiedFields()
    {
        var source = CreateTag();
        var edit = new TagBatchEdit();
        edit.Fields[TagFieldKeys.Title] = "New Title";
        edit.Fields[TagFieldKeys.Comment] = string.Empty;

        var model = TagBatchEditor.CreateEdit(source, edit);

        Assert.Equal("New Title", model.Title);
        Assert.Equal(string.Empty, model.Comment);
        Assert.Equal("Artist", model.Artist);
        Assert.Equal("Album", model.Album);
        Assert.Equal("Composer", model.Composer);
        Assert.Equal(2024, model.Year);
        Assert.Equal(3, model.TrackNumber);
    }

    [Fact]
    public void CreateEdit_ParsesNumericFieldsAndTreatsInvalidAsZero()
    {
        var source = CreateTag();
        var edit = new TagBatchEdit();
        edit.Fields[TagFieldKeys.Year] = "1999";
        edit.Fields[TagFieldKeys.TrackNumber] = string.Empty;
        edit.Fields[TagFieldKeys.DiscNumber] = "abc";

        var model = TagBatchEditor.CreateEdit(source, edit);

        Assert.Equal(1999, model.Year);
        Assert.Equal(0, model.TrackNumber);
        Assert.Equal(0, model.DiscNumber);
    }

    [Fact]
    public void CreateEdit_CoverChangeAppliedOnlyWhenFlagged()
    {
        var source = CreateTag();
        var bytes = new byte[] { 1, 2, 3 };
        var edit = new TagBatchEdit { NewCoverBytes = bytes, NewCoverMime = "image/jpeg" };

        var untouched = TagBatchEditor.CreateEdit(source, edit);
        Assert.Null(untouched.NewCoverBytes);
        Assert.False(untouched.RemoveCover);

        edit.CoverChanged = true;
        var changed = TagBatchEditor.CreateEdit(source, edit);
        Assert.Same(bytes, changed.NewCoverBytes);
        Assert.Equal("image/jpeg", changed.NewCoverMime);
        Assert.False(changed.RemoveCover);
    }

    [Fact]
    public void CreateEdit_RemovedCoverClearsCoverFields()
    {
        var source = CreateTag();
        var edit = new TagBatchEdit { CoverChanged = true, RemoveCover = true, NewCoverBytes = [1] };

        var model = TagBatchEditor.CreateEdit(source, edit);

        Assert.True(model.RemoveCover);
        Assert.Null(model.NewCoverBytes);
    }

    [Fact]
    public void CreateEdit_CopiesAdditionalTagChanges()
    {
        var edit = new TagBatchEdit();
        edit.AdditionalTagChanges.Add(new AudioTagChange(AdditionalTagKinds.Id3Text, "TIT1", "Grouping"));
        edit.AdditionalTagChanges.Add(new AudioTagChange(AdditionalTagKinds.Id3UserText, "MOOD", null));

        var model = TagBatchEditor.CreateEdit(CreateTag(), edit);

        Assert.Collection(
            model.AdditionalTagChanges,
            change =>
            {
                Assert.Equal(AdditionalTagKinds.Id3Text, change.Kind);
                Assert.Equal("TIT1", change.Key);
                Assert.Equal("Grouping", change.Value);
            },
            change =>
            {
                Assert.Equal(AdditionalTagKinds.Id3UserText, change.Kind);
                Assert.True(change.IsRemoval);
            });
    }

    [Fact]
    public void AdditionalTagKey_RoundTripsAppleFreeformParts()
    {
        var key = AdditionalTagKey.AppleFreeform("com.apple.iTunes", "MOOD");

        var (mean, name) = AdditionalTagKey.SplitAppleFreeform(key);

        Assert.Equal("com.apple.iTunes", mean);
        Assert.Equal("MOOD", name);
    }
}
