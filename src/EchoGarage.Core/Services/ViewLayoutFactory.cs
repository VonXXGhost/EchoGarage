using System.Text.Json;
using EchoGarage.Core.Models;

namespace EchoGarage.Core.Services;

public static class ViewLayoutFactory
{
    public static PlaylistViewLayout CreateDefault() => ColumnCatalog.CreateDefault();

    public static List<ColumnPreset> CreateBuiltInPresets() =>
    [
        ColumnPreset.FromLayout("标准", ColumnCatalog.CreateDefault()),
        ColumnPreset.FromLayout("简洁", ColumnCatalog.CreateLayout("简洁",
            ["cover", "title", "artist", "album", "duration"])),
        ColumnPreset.FromLayout("文件管理", ColumnCatalog.CreateLayout("文件管理",
            ["cover", "title", "path", "codec", "sampleRate", "bitrate", "fileSize", "addedAt"]))
    ];

    /// <summary>
    /// Ensures a layout carries every known column exactly once, in a stable order,
    /// and that at least one useful column is visible.
    /// </summary>
    public static PlaylistViewLayout Normalize(PlaylistViewLayout? layout)
    {
        if (layout is null || layout.Columns.Count == 0)
        {
            return CreateDefault();
        }

        var byKey = ColumnCatalog.All.ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);
        var groupCoverSize = layout.GroupCoverSize;
        if (!double.IsFinite(groupCoverSize) || groupCoverSize <= 0)
        {
            // Older JSON has no value; fall back to the historical 96px group cover.
            groupCoverSize = PlaylistViewLayout.DefaultGroupCoverSize;
        }

        var normalized = new PlaylistViewLayout
        {
            PresetName = layout.PresetName,
            SortColumn = layout.SortColumn,
            SortDescending = layout.SortDescending,
            GroupCoverSize = Math.Clamp(groupCoverSize, PlaylistViewLayout.MinGroupCoverSize,
                PlaylistViewLayout.MaxGroupCoverSize),
            GroupSortByDiscTrack = layout.GroupSortByDiscTrack,
            ShowSecondaryInfo = layout.ShowSecondaryInfo
        };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var column in layout.Columns)
        {
            if (!byKey.TryGetValue(column.Key, out var descriptor) || !seen.Add(descriptor.Key))
            {
                continue;
            }

            normalized.Columns.Add(new PlaylistColumn
            {
                Key = descriptor.Key,
                Width = Math.Clamp(column.Width <= 0 ? descriptor.DefaultWidth : column.Width, descriptor.MinWidth, 1200),
                Visible = column.Visible,
                // Pins only make sense for the columns a user can see and drag in the grouped
                // header; older layouts deserialize without one and default to left.
                Pin = column.Visible ? column.Pin : ColumnPin.Left,
                SecondaryFormat = SecondaryTextFormat.Normalize(column.SecondaryFormat)
            });
        }

        foreach (var descriptor in ColumnCatalog.All)
        {
            if (!seen.Add(descriptor.Key))
            {
                continue;
            }

            normalized.Columns.Add(new PlaylistColumn
            {
                Key = descriptor.Key,
                Width = descriptor.DefaultWidth,
                Visible = false
            });
        }

        if (!normalized.Columns.Any(c => c.Visible && c.Key is "title" or "cover" or "path"))
        {
            var title = normalized.Columns.First(c => c.Key == "title");
            title.Visible = true;
        }

        if (normalized.SortColumn is not null)
        {
            // SortColumn stores a row *property* name (e.g. "DurationSort"), not a column key.
            // It is validated against the catalog, but does NOT require the column to be visible:
            // sorting by a hidden column is useful (e.g. sort by album artist while its column is
            // hidden — the grouped list still shows the album artist in the group header), and
            // album/album-artist sorts are what enable the grouped view.
            if (ColumnCatalog.FindBySortProperty(normalized.SortColumn) is null)
            {
                normalized.SortColumn = null;
                normalized.SortDescending = false;
            }
        }

        return normalized;
    }

    /// <summary>
    /// Builds the layout produced by applying a display preset to the current layout.
    /// Presets describe columns only: the grouped album-cover size and the per-group disc/track
    /// ordering preference are kept from <paramref name="current"/>.
    /// Like the column editor (ADR-013: 填了次行格式即自动启用次行显示), a preset that carries
    /// visible secondary-line formats must also turn the second line on — otherwise applying a
    /// "with secondary info" preset to a source that had the switch off silently hides it.
    /// </summary>
    public static PlaylistViewLayout ApplyPreset(PlaylistViewLayout current, ColumnPreset preset)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(preset);

        var applied = preset.ToLayout();
        applied.GroupCoverSize = current.GroupCoverSize;
        applied.GroupSortByDiscTrack = current.GroupSortByDiscTrack;
        applied.ShowSecondaryInfo = current.ShowSecondaryInfo;

        applied = Normalize(applied);
        applied.ShowSecondaryInfo |= applied.Columns.Any(column =>
            column.Visible && !string.IsNullOrWhiteSpace(column.SecondaryFormat));
        return applied;
    }

    public static string Serialize(PlaylistViewLayout layout) =>
        JsonSerializer.Serialize(Normalize(layout), JsonDefaults.Options);

    public static PlaylistViewLayout Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return CreateDefault();
        }

        try
        {
            return Normalize(JsonSerializer.Deserialize<PlaylistViewLayout>(json, JsonDefaults.Options));
        }
        catch (JsonException)
        {
            return CreateDefault();
        }
    }

    public static string SerializeSmart(SmartPlaylistDefinition definition) =>
        JsonSerializer.Serialize(definition, JsonDefaults.Options);

    public static SmartPlaylistDefinition DeserializeSmart(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new SmartPlaylistDefinition();
        }

        try
        {
            return JsonSerializer.Deserialize<SmartPlaylistDefinition>(json, JsonDefaults.Options)
                   ?? new SmartPlaylistDefinition();
        }
        catch (JsonException)
        {
            return new SmartPlaylistDefinition();
        }
    }
}
