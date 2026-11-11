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
    public void Normalize_ClearsSortPropertyWhenItsColumnIsHidden()
    {
        // albumArtist is hidden in the default layout.
        var layout = ViewLayoutFactory.CreateDefault();
        layout.SortColumn = "AlbumArtist";
        layout.SortDescending = true;

        var normalized = ViewLayoutFactory.Normalize(layout);

        Assert.Null(normalized.SortColumn);
        Assert.False(normalized.SortDescending);
    }

    [Fact]
    public void Normalize_ClearsUnknownSortProperty()
    {
        var layout = ViewLayoutFactory.CreateDefault();
        layout.SortColumn = "NotARealSortProperty";

        Assert.Null(ViewLayoutFactory.Normalize(layout).SortColumn);
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
