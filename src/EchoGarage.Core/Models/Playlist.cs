using System.Text.Json;
using System.Text.Json.Serialization;

namespace EchoGarage.Core.Models;

public enum PlaylistKind
{
    Normal,
    Smart
}

public sealed class Playlist
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public PlaylistKind Kind { get; set; } = PlaylistKind.Normal;
    public bool IsDefault { get; set; }
    public int SortOrder { get; set; }
    public string? SmartRulesJson { get; set; }
    public string? ViewLayoutJson { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public bool IsSmart => Kind == PlaylistKind.Smart;

    public SmartPlaylistDefinition GetSmartDefinition()
    {
        if (string.IsNullOrWhiteSpace(SmartRulesJson))
        {
            return new SmartPlaylistDefinition();
        }

        try
        {
            return JsonSerializer.Deserialize<SmartPlaylistDefinition>(SmartRulesJson, JsonDefaults.Options)
                   ?? new SmartPlaylistDefinition();
        }
        catch (JsonException)
        {
            return new SmartPlaylistDefinition();
        }
    }
}

/// <summary>
/// Where a column is anchored in the grouped view's header. Left columns flow from the left
/// edge after the cover gutter; right columns hug the right edge of the list, so widening a
/// left column squeezes the flexible space instead of pushing the right block out of view.
/// </summary>
public enum ColumnPin
{
    Left = 0,
    Right = 1
}

public sealed class PlaylistColumn
{
    public string Key { get; set; } = string.Empty;
    public double Width { get; set; }
    public bool Visible { get; set; } = true;

    /// <summary>Header anchor in the grouped view. Stored per layout so each source remembers it.</summary>
    public ColumnPin Pin { get; set; } = ColumnPin.Left;

    /// <summary>
    /// Optional format of the secondary (second-row) text shown under this column, e.g.
    /// "{genre}" or "{bitrate}/{sampleRate}". Empty means no secondary text.
    /// </summary>
    public string? SecondaryFormat { get; set; }

    public PlaylistColumn Clone() => new()
    {
        Key = Key,
        Width = Width,
        Visible = Visible,
        Pin = Pin,
        SecondaryFormat = SecondaryFormat
    };
}

public sealed class PlaylistViewLayout
{
    public const double DefaultGroupCoverSize = 96;
    public const double MinGroupCoverSize = 48;
    public const double MaxGroupCoverSize = 320;

    public string? PresetName { get; set; }
    public List<PlaylistColumn> Columns { get; set; } = [];
    public string? SortColumn { get; set; }
    public bool SortDescending { get; set; }

    /// <summary>
    /// Size (in DIPs) of the album cover shown in group headers when grouping by album.
    /// The cover column acts as the resize handle for it in grouped mode.
    /// </summary>
    public double GroupCoverSize { get; set; } = 96;

    /// <summary>
    /// When true, songs inside an album group are ordered by disc number and then by
    /// track number (ascending) instead of the playlist's current sort order.
    /// </summary>
    public bool GroupSortByDiscTrack { get; set; } = true;

    /// <summary>
    /// When true, list rows show an optional secondary line per column (see
    /// <see cref="PlaylistColumn.SecondaryFormat"/>), rendered smaller and dimmer than the title.
    /// </summary>
    public bool ShowSecondaryInfo { get; set; }

    public PlaylistViewLayout Clone() => new()
    {
        PresetName = PresetName,
        Columns = Columns.Select(c => c.Clone()).ToList(),
        SortColumn = SortColumn,
        SortDescending = SortDescending,
        GroupCoverSize = GroupCoverSize,
        GroupSortByDiscTrack = GroupSortByDiscTrack,
        ShowSecondaryInfo = ShowSecondaryInfo
    };
}

public sealed class ColumnPreset
{
    public string Name { get; set; } = string.Empty;
    public List<PlaylistColumn> Columns { get; set; } = [];
    public string? SortColumn { get; set; }
    public bool SortDescending { get; set; }

    public PlaylistViewLayout ToLayout() => new()
    {
        PresetName = Name,
        Columns = Columns.Select(c => c.Clone()).ToList(),
        SortColumn = SortColumn,
        SortDescending = SortDescending
    };

    public static ColumnPreset FromLayout(string name, PlaylistViewLayout layout) => new()
    {
        Name = name,
        Columns = layout.Columns.Select(c => c.Clone()).ToList(),
        SortColumn = layout.SortColumn,
        SortDescending = layout.SortDescending
    };
}

public enum SmartMatchMode
{
    All,
    Any
}

public sealed class SmartPlaylistDefinition
{
    public SmartMatchMode MatchMode { get; set; } = SmartMatchMode.All;
    public List<SmartRule> Rules { get; set; } = [];

    public SmartPlaylistDefinition Clone() => new()
    {
        MatchMode = MatchMode,
        Rules = Rules.Select(r => new SmartRule { Field = r.Field, Operator = r.Operator, Value = r.Value }).ToList()
    };
}

public sealed class SmartRule
{
    public string Field { get; set; } = "artist";
    public string Operator { get; set; } = "contains";
    public string Value { get; set; } = string.Empty;
}

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
}
