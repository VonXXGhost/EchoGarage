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
        var normalized = new PlaylistViewLayout
        {
            PresetName = layout.PresetName,
            SortColumn = layout.SortColumn,
            SortDescending = layout.SortDescending
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
                Visible = column.Visible
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

        if (normalized.SortColumn is not null &&
            !normalized.Columns.Any(c => c.Visible && string.Equals(c.Key, normalized.SortColumn, StringComparison.OrdinalIgnoreCase)))
        {
            normalized.SortColumn = null;
            normalized.SortDescending = false;
        }

        return normalized;
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
