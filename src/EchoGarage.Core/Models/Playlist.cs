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

public sealed class PlaylistColumn
{
    public string Key { get; set; } = string.Empty;
    public double Width { get; set; }
    public bool Visible { get; set; } = true;

    public PlaylistColumn Clone() => new() { Key = Key, Width = Width, Visible = Visible };
}

public sealed class PlaylistViewLayout
{
    public string? PresetName { get; set; }
    public List<PlaylistColumn> Columns { get; set; } = [];
    public string? SortColumn { get; set; }
    public bool SortDescending { get; set; }

    public PlaylistViewLayout Clone() => new()
    {
        PresetName = PresetName,
        Columns = Columns.Select(c => c.Clone()).ToList(),
        SortColumn = SortColumn,
        SortDescending = SortDescending
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
