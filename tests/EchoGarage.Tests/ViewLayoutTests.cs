using EchoGarage.Core.Models;
using EchoGarage.Core.Services;

namespace EchoGarage.Tests;

public sealed class ViewLayoutTests
{
    [Fact]
    public void DefaultLayout_IsNormalizedAndNonEmpty()
    {
        var layout = ViewLayoutFactory.CreateDefault();

        Assert.Contains(layout.Columns, c => c.Visible);
        Assert.Contains(layout.Columns, c => c.Key == "cover" && c.Visible);
        Assert.Contains(layout.Columns, c => c.Key == "title" && c.Visible);
        Assert.Equal(ColumnCatalog.All.Count, layout.Columns.Count);
        Assert.Equal(layout.Columns.Count, layout.Columns.Select(c => c.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Normalize_RemovesUnknownAndDuplicateColumns()
    {
        var layout = new PlaylistViewLayout
        {
            PresetName = "x",
            Columns =
            [
                new PlaylistColumn { Key = "title", Width = 100, Visible = true },
                new PlaylistColumn { Key = "title", Width = 200, Visible = false },
                new PlaylistColumn { Key = "notARealColumn", Width = 100, Visible = true },
                new PlaylistColumn { Key = "artist", Width = 10, Visible = true }
            ]
        };

        var normalized = ViewLayoutFactory.Normalize(layout);

        Assert.Single(normalized.Columns, c => c.Key == "title");
        Assert.DoesNotContain(normalized.Columns, c => c.Key == "notARealColumn");
        Assert.True(normalized.Columns.Single(c => c.Key == "artist").Width >= ColumnCatalog.Find("artist")!.MinWidth);
        Assert.Equal(ColumnCatalog.All.Count, normalized.Columns.Count);
    }

    [Fact]
    public void Normalize_KeepsAtLeastTitleVisible()
    {
        var layout = new PlaylistViewLayout
        {
            Columns = ColumnCatalog.All.Select(c => new PlaylistColumn { Key = c.Key, Width = c.DefaultWidth, Visible = false }).ToList()
        };

        var normalized = ViewLayoutFactory.Normalize(layout);

        Assert.True(normalized.Columns.Single(c => c.Key == "title").Visible || normalized.Columns.Single(c => c.Key == "cover").Visible);
    }

    [Fact]
    public void SerializeRoundTrip_PreservesVisibilityWidthAndSort()
    {
        var layout = ViewLayoutFactory.CreateDefault();
        layout.SortColumn = "Title";
        layout.SortDescending = true;
        layout.Columns.Single(c => c.Key == "artist").Width = 321;

        var json = ViewLayoutFactory.Serialize(layout);
        var restored = ViewLayoutFactory.Deserialize(json);

        Assert.Equal("Title", restored.SortColumn);
        Assert.True(restored.SortDescending);
        Assert.Equal(321, restored.Columns.Single(c => c.Key == "artist").Width, 3);
    }

    [Fact]
    public void Normalize_PreservesRightPinOnlyForVisibleColumns()
    {
        var layout = ViewLayoutFactory.CreateDefault();
        var duration = layout.Columns.Single(c => c.Key == "duration");
        duration.Visible = true;
        duration.Pin = ColumnPin.Right;
        // albumArtist is hidden by default; a hidden column cannot stay pinned to the right edge.
        layout.Columns.Single(c => c.Key == "albumArtist").Pin = ColumnPin.Right;

        var normalized = ViewLayoutFactory.Normalize(layout);

        Assert.Equal(ColumnPin.Right, normalized.Columns.Single(c => c.Key == "duration").Pin);
        Assert.Equal(ColumnPin.Left, normalized.Columns.Single(c => c.Key == "albumArtist").Pin);
    }

    [Fact]
    public void SerializeRoundTrip_PreservesColumnPin()
    {
        var layout = ViewLayoutFactory.CreateDefault();
        layout.Columns.Single(c => c.Key == "album").Pin = ColumnPin.Right;

        var restored = ViewLayoutFactory.Deserialize(ViewLayoutFactory.Serialize(layout));

        Assert.Equal(ColumnPin.Right, restored.Columns.Single(c => c.Key == "album").Pin);
        Assert.Equal(ColumnPin.Left, restored.Columns.Single(c => c.Key == "title").Pin);
    }

    [Fact]
    public void Normalize_KeepsSortPropertyForVisibleSortableColumns()
    {
        foreach (var descriptor in ColumnCatalog.All.Where(c => !string.IsNullOrWhiteSpace(c.SortProperty)))
        {
            var layout = ViewLayoutFactory.CreateDefault();
            layout.Columns.First(c => c.Key == descriptor.Key).Visible = true;
            layout.SortColumn = descriptor.SortProperty;
            layout.SortDescending = true;

            var normalized = ViewLayoutFactory.Normalize(layout);

            Assert.Equal(descriptor.SortProperty, normalized.SortColumn);
            Assert.True(normalized.SortDescending);
        }
    }

    [Fact]
    public void Normalize_KeepsSortPropertyForHiddenColumns()
    {
        // Sorting must not depend on the column being visible: the grouped list can sort by
        // album/album artist (which drives grouping) while the column itself is hidden, because
        // the group header already shows that information.
        foreach (var descriptor in ColumnCatalog.All.Where(c => !string.IsNullOrWhiteSpace(c.SortProperty)))
        {
            var layout = ViewLayoutFactory.CreateDefault();
            layout.Columns.First(c => c.Key == descriptor.Key).Visible = false;
            layout.SortColumn = descriptor.SortProperty;
            layout.SortDescending = true;

            var normalized = ViewLayoutFactory.Normalize(layout);

            Assert.Equal(descriptor.SortProperty, normalized.SortColumn);
            Assert.True(normalized.SortDescending);
        }
    }

    [Fact]
    public void SerializeRoundTrip_PreservesSortOnHiddenColumn()
    {
        var layout = ViewLayoutFactory.CreateDefault();
        layout.Columns.Single(c => c.Key == "albumArtist").Visible = false;
        layout.Columns.Single(c => c.Key == "album").Visible = false;
        layout.SortColumn = "AlbumArtist";

        var restored = ViewLayoutFactory.Deserialize(ViewLayoutFactory.Serialize(layout));

        Assert.Equal("AlbumArtist", restored.SortColumn);
        Assert.False(restored.Columns.Single(c => c.Key == "albumArtist").Visible);
    }

    [Fact]
    public void Normalize_ClearsUnknownSortProperty()
    {
        var layout = ViewLayoutFactory.CreateDefault();
        layout.SortColumn = "NotARealSortProperty";

        Assert.Null(ViewLayoutFactory.Normalize(layout).SortColumn);
    }

    [Fact]
    public void ColumnCatalog_OffersFileModifiedTimeSort()
    {
        var descriptor = ColumnCatalog.FindBySortProperty("ModifiedSort");

        Assert.NotNull(descriptor);
        Assert.Equal("modifiedAt", descriptor!.Key);
        Assert.Equal("文件修改时间", descriptor.Header);

        var layout = ViewLayoutFactory.CreateDefault();
        layout.Columns.First(c => c.Key == "modifiedAt").Visible = true;
        layout.SortColumn = "ModifiedSort";
        layout.SortDescending = true;

        var normalized = ViewLayoutFactory.Normalize(layout);
        Assert.Equal("ModifiedSort", normalized.SortColumn);
        Assert.True(normalized.SortDescending);
        Assert.True(normalized.Columns.Single(c => c.Key == "modifiedAt").Visible);
    }

    [Fact]
    public void Normalize_ClearsSortByRemovedGenreAndCommentColumns()
    {
        // Genre / comment columns stay available for display, but they are no longer offered as
        // sort methods; previously saved layouts that sorted by them fall back to the default order.
        Assert.Null(ColumnCatalog.Find("genre")!.SortProperty);
        Assert.Null(ColumnCatalog.Find("comment")!.SortProperty);

        foreach (var removedSortProperty in new[] { "Genre", "Comment" })
        {
            var layout = ViewLayoutFactory.CreateDefault();
            layout.SortColumn = removedSortProperty;
            layout.SortDescending = true;

            var normalized = ViewLayoutFactory.Normalize(layout);
            Assert.Null(normalized.SortColumn);
            Assert.False(normalized.SortDescending);
        }
    }

    [Fact]
    public void SortOptions_IncludeModifiedTimeAndExcludeGenreAndComment()
    {
        var options = ColumnCatalog.SortOptions;

        Assert.Equal("（默认顺序）", options[0].Label);
        Assert.Null(options[0].Property);
        Assert.Contains(options, option => option is { Property: "ModifiedSort", Label: "文件修改时间" });
        Assert.DoesNotContain(options, option => option.Property is "Genre" or "Comment");
        Assert.DoesNotContain(options, option => option.Label is "流派" or "备注");
        Assert.Equal(
            1 + ColumnCatalog.All.Count(c => c is { Sortable: true, SortProperty: not null }),
            options.Count);
    }

    [Fact]
    public void SerializeRoundTrip_PreservesDurationSort()
    {
        // Regression: the old normalization compared SortColumn with column *keys*, so sorting
        // by time ("DurationSort") was silently cleared on every layout save.
        var layout = ViewLayoutFactory.CreateDefault();
        layout.SortColumn = "DurationSort";
        layout.SortDescending = true;

        var restored = ViewLayoutFactory.Deserialize(ViewLayoutFactory.Serialize(layout));

        Assert.Equal("DurationSort", restored.SortColumn);
        Assert.True(restored.SortDescending);
    }

    [Fact]
    public void Deserialize_InvalidJsonFallsBackToDefault()
    {
        var restored = ViewLayoutFactory.Deserialize("{ not json");

        Assert.Contains(restored.Columns, c => c.Visible);
    }

    [Fact]
    public void Normalize_ClampsGroupedCoverSize_AndRoundTrips()
    {
        var layout = ViewLayoutFactory.CreateDefault();
        layout.GroupCoverSize = 220;

        var json = ViewLayoutFactory.Serialize(layout);
        var restored = ViewLayoutFactory.Deserialize(json);
        Assert.Equal(220, restored.GroupCoverSize, 3);
        Assert.Equal(220, restored.Clone().GroupCoverSize, 3);

        layout.GroupCoverSize = 10;
        Assert.Equal(PlaylistViewLayout.MinGroupCoverSize,
            ViewLayoutFactory.Normalize(layout).GroupCoverSize);

        layout.GroupCoverSize = 1000;
        Assert.Equal(PlaylistViewLayout.MaxGroupCoverSize,
            ViewLayoutFactory.Normalize(layout).GroupCoverSize);
    }

    [Fact]
    public void GroupSortByDiscTrack_DefaultsToTrueAndRoundTrips()
    {
        var layout = ViewLayoutFactory.CreateDefault();
        Assert.True(layout.GroupSortByDiscTrack);
        Assert.True(ViewLayoutFactory.Normalize(layout).GroupSortByDiscTrack);
        Assert.True(layout.Clone().GroupSortByDiscTrack);

        layout.GroupSortByDiscTrack = false;
        var json = ViewLayoutFactory.Serialize(layout);
        var restored = ViewLayoutFactory.Deserialize(json);
        Assert.False(restored.GroupSortByDiscTrack);
        Assert.False(restored.Clone().GroupSortByDiscTrack);
    }

    [Fact]
    public void Normalize_CleansSecondaryFormatAndKeepsItForHiddenColumns()
    {
        var layout = ViewLayoutFactory.CreateDefault();
        layout.Columns.Single(c => c.Key == "duration").SecondaryFormat = "  {bitrate}/{sampleRate}  ";
        // Hidden columns keep their configuration so toggling visibility does not lose it.
        layout.Columns.Single(c => c.Key == "genre").Visible = false;
        layout.Columns.Single(c => c.Key == "genre").SecondaryFormat = "{composer}";
        layout.Columns.Single(c => c.Key == "title").SecondaryFormat = "   ";

        var normalized = ViewLayoutFactory.Normalize(layout);

        Assert.Equal("{bitrate}/{sampleRate}", normalized.Columns.Single(c => c.Key == "duration").SecondaryFormat);
        Assert.Equal("{composer}", normalized.Columns.Single(c => c.Key == "genre").SecondaryFormat);
        Assert.Null(normalized.Columns.Single(c => c.Key == "title").SecondaryFormat);
    }

    [Fact]
    public void SerializeRoundTrip_PreservesSecondaryInfoSettings()
    {
        var layout = ViewLayoutFactory.CreateDefault();
        layout.ShowSecondaryInfo = true;
        layout.Columns.Single(c => c.Key == "title").SecondaryFormat = "{genre}";
        layout.Columns.Single(c => c.Key == "duration").SecondaryFormat = "{bitrate}/{sampleRate}";

        var restored = ViewLayoutFactory.Deserialize(ViewLayoutFactory.Serialize(layout));

        Assert.True(restored.ShowSecondaryInfo);
        Assert.Equal("{genre}", restored.Columns.Single(c => c.Key == "title").SecondaryFormat);
        Assert.Equal("{bitrate}/{sampleRate}", restored.Columns.Single(c => c.Key == "duration").SecondaryFormat);
        Assert.True(restored.Clone().ShowSecondaryInfo);
        Assert.Equal("{genre}", restored.Clone().Columns.Single(c => c.Key == "title").SecondaryFormat);
    }

    [Fact]
    public void ApplyPreset_EnablesSecondaryInfoWhenPresetHasVisibleFormats()
    {
        var current = ViewLayoutFactory.CreateDefault();
        current.GroupCoverSize = 220;
        current.GroupSortByDiscTrack = false;

        var source = ViewLayoutFactory.CreateDefault();
        source.Columns.Single(c => c.Key == "title").SecondaryFormat = "{genre}";
        source.SortColumn = "AlbumArtist";
        var preset = ColumnPreset.FromLayout("简洁分组", source);

        var applied = ViewLayoutFactory.ApplyPreset(current, preset);

        Assert.True(applied.ShowSecondaryInfo);
        Assert.Equal("{genre}", applied.Columns.Single(c => c.Key == "title").SecondaryFormat);
        Assert.Equal(220, applied.GroupCoverSize, 3);
        Assert.False(applied.GroupSortByDiscTrack);
        Assert.Equal("AlbumArtist", applied.SortColumn);
    }

    [Fact]
    public void ApplyPreset_DoesNotEnableSecondaryInfoForFormatlessOrHiddenFormats()
    {
        var current = ViewLayoutFactory.CreateDefault();
        var formatless = ColumnPreset.FromLayout("标准", ViewLayoutFactory.CreateDefault());
        Assert.False(ViewLayoutFactory.ApplyPreset(current, formatless).ShowSecondaryInfo);

        var source = ViewLayoutFactory.CreateDefault();
        var hidden = source.Columns.Single(c => c.Key == "genre");
        hidden.Visible = false;
        hidden.SecondaryFormat = "{composer}";
        var preset = ColumnPreset.FromLayout("隐藏列格式", source);
        Assert.False(ViewLayoutFactory.ApplyPreset(current, preset).ShowSecondaryInfo);
    }

    [Fact]
    public void ApplyPreset_KeepsSecondaryInfoWhenCurrentLayoutAlreadyShowsIt()
    {
        var current = ViewLayoutFactory.CreateDefault();
        current.ShowSecondaryInfo = true;
        var preset = ColumnPreset.FromLayout("标准", ViewLayoutFactory.CreateDefault());

        Assert.True(ViewLayoutFactory.ApplyPreset(current, preset).ShowSecondaryInfo);
    }

    [Fact]
    public void DefaultLayout_HasSecondaryInfoDisabled()
    {
        var layout = ViewLayoutFactory.CreateDefault();

        Assert.False(layout.ShowSecondaryInfo);
        Assert.All(layout.Columns, c => Assert.Null(c.SecondaryFormat));
    }

    [Fact]
    public void SmartDefinition_RoundTrips()
    {
        var definition = new SmartPlaylistDefinition
        {
            MatchMode = SmartMatchMode.Any,
            Rules = [new SmartRule { Field = "artist", Operator = "contains", Value = "测试" }]
        };

        var json = ViewLayoutFactory.SerializeSmart(definition);
        var restored = ViewLayoutFactory.DeserializeSmart(json);

        Assert.Equal(SmartMatchMode.Any, restored.MatchMode);
        Assert.Single(restored.Rules);
        Assert.Equal("测试", restored.Rules[0].Value);
    }
}
