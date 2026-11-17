using EchoGarage.Core.Models;

namespace EchoGarage.Tests;

public sealed class TagBatchEditTests
{
    private static AudioTagData CreateTag(int? year = 2024, string? yearText = null) => new(
        "Title", "Artist", "Album", "AlbumArtist", "Composer", "Genre", "Comment",
        year, 3, 1, 0, 0, 0, 0, "FLAC", null, null)
    {
        YearText = yearText
    };

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
    public void GetFieldValues_YearStaysFourDigitTextEvenWhenRawYearTextHasDate()
    {
        // 列表 / 分组 / 规范化依赖数字语义，因此面板以外的消费者仍只能看到 4 位年份。
        var values = TagBatchEditor.GetFieldValues(CreateTag(2025, "2025.06.04"));

        Assert.Equal("2025", values[TagFieldKeys.Year]);
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
    public void CreateEdit_WithoutYearField_DoesNotMarkYearChangedAndKeepsRawText()
    {
        // 只改其它字段时不能把文件里的详细日期（如 2025.06.04）覆盖成纯年份。
        var source = CreateTag(2025, "2025.06.04");
        var edit = new TagBatchEdit();
        edit.Fields[TagFieldKeys.Title] = "New Title";

        var model = TagBatchEditor.CreateEdit(source, edit);

        Assert.False(model.YearChanged);
        Assert.Equal(2025, model.Year);
        Assert.Equal("2025.06.04", model.YearText);
    }

    [Fact]
    public void CreateEdit_WithNumericYear_MarksYearChangedAndWritesNumberText()
    {
        var edit = new TagBatchEdit();
        edit.Fields[TagFieldKeys.Year] = " 1999 ";

        var model = TagBatchEditor.CreateEdit(CreateTag(2025, "2025.06.04"), edit);

        Assert.True(model.YearChanged);
        Assert.Equal(1999, model.Year);
        Assert.Equal("1999", model.YearText);
    }

    [Fact]
    public void CreateEdit_WithDetailedYearText_KeepsRawTextForWriteBack()
    {
        var edit = new TagBatchEdit();
        edit.Fields[TagFieldKeys.Year] = " 2023-05-12 ";

        var model = TagBatchEditor.CreateEdit(CreateTag(), edit);

        Assert.True(model.YearChanged);
        Assert.Equal("2023-05-12", model.YearText);
    }

    [Fact]
    public void CreateEdit_EmptyYear_MarksYearChangedAndClearsRawText()
    {
        var edit = new TagBatchEdit();
        edit.Fields[TagFieldKeys.Year] = "   ";

        var model = TagBatchEditor.CreateEdit(CreateTag(2025, "2025.06.04"), edit);

        Assert.True(model.YearChanged);
        Assert.Equal(0, model.Year);
        Assert.Equal(string.Empty, model.YearText);
    }

    [Fact]
    public void TagEditModel_DefaultsToYearChangedForNonBatchCallers()
    {
        // 音频转换等直接构造 TagEditModel 的调用方没有“字段是否编辑”的上下文，默认仍写年份。
        var model = new TagEditModel();

        Assert.True(model.YearChanged);
        Assert.Null(model.YearText);
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
