using EchoGarage.Core.Models;

namespace EchoGarage.Core.Services;

public enum ColumnKind
{
    Text,
    Cover,
    Status,
    Index,
    Duration,
    FileSize,
    Date,
    Rating
}

/// <summary>
/// One playlist column. <paramref name="SortProperty"/> is the name of the row property used
/// for sorting (for example <c>DurationSort</c> for the duration column). The layout JSON
/// stores that property name in <see cref="PlaylistViewLayout.SortColumn"/>, so this mapping is
/// the single source of truth for both the grid/list column headers and layout normalization.
/// </summary>
public sealed record ColumnDescriptor(
    string Key,
    string Header,
    double DefaultWidth,
    double MinWidth,
    ColumnKind Kind,
    bool Sortable = true,
    string? SortProperty = null);

public static class ColumnCatalog
{
    public static readonly IReadOnlyList<ColumnDescriptor> All =
    [
        new("cover", "封面", 48, 40, ColumnKind.Cover, false),
        new("status", "状态", 46, 42, ColumnKind.Status, false),
        // index is not sortable by clicking the header, but "default order" sorting (RowNumber)
        // can still be saved from the columns dialog, so it carries a sort property.
        new("index", "#", 40, 30, ColumnKind.Index, false, PlaylistSortPolicy.DefaultOrder),
        new("title", "标题", 260, 120, ColumnKind.Text, SortProperty: "Title"),
        new("artist", "艺术家", 160, 90, ColumnKind.Text, SortProperty: "Artist"),
        new("album", "专辑", 170, 90, ColumnKind.Text, SortProperty: PlaylistSortPolicy.Album),
        new("albumArtist", "专辑艺术家", 150, 90, ColumnKind.Text, SortProperty: PlaylistSortPolicy.AlbumArtist),
        new("trackNumber", "音轨", 54, 44, ColumnKind.Text, SortProperty: "TrackNumberSort"),
        new("discNumber", "碟号", 50, 42, ColumnKind.Text, SortProperty: "DiscNumberSort"),
        new("year", "年份", 56, 48, ColumnKind.Text, SortProperty: "YearSort"),
        new("genre", "流派", 110, 60, ColumnKind.Text, SortProperty: "Genre"),
        new("duration", "时间", 62, 54, ColumnKind.Duration, SortProperty: "DurationSort"),
        new("bitrate", "比特率", 72, 60, ColumnKind.Text, SortProperty: "BitrateSort"),
        new("sampleRate", "采样率", 72, 60, ColumnKind.Text, SortProperty: "SampleRateSort"),
        new("codec", "格式", 62, 48, ColumnKind.Text, SortProperty: "Codec"),
        new("rating", "评分", 72, 56, ColumnKind.Rating, SortProperty: "RatingSort"),
        new("addedAt", "添加时间", 100, 80, ColumnKind.Date, SortProperty: "AddedSort"),
        new("fileSize", "大小", 72, 60, ColumnKind.FileSize, SortProperty: "FileSizeSort"),
        new("path", "文件路径", 320, 120, ColumnKind.Text, SortProperty: "PathText"),
        new("comment", "备注", 160, 80, ColumnKind.Text, SortProperty: "Comment")
    ];

    private static readonly Dictionary<string, ColumnDescriptor> ByKey =
        All.ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, ColumnDescriptor> BySortProperty =
        All.Where(c => !string.IsNullOrWhiteSpace(c.SortProperty))
            .ToDictionary(c => c.SortProperty!, StringComparer.OrdinalIgnoreCase);

    public static ColumnDescriptor? Find(string key) =>
        ByKey.TryGetValue(key, out var descriptor) ? descriptor : null;

    /// <summary>Finds the column whose row property is used for sorting with this sort property.</summary>
    public static ColumnDescriptor? FindBySortProperty(string? sortProperty) =>
        !string.IsNullOrWhiteSpace(sortProperty) && BySortProperty.TryGetValue(sortProperty, out var descriptor)
            ? descriptor
            : null;

    public static string HeaderFor(string key) => Find(key)?.Header ?? key;

    public static PlaylistViewLayout CreateDefault() => CreateLayout("标准",
    [
        "cover", "status", "index", "title", "artist", "album", "duration"
    ]);

    public static PlaylistViewLayout CreateLayout(string presetName, IEnumerable<string> visibleKeys)
    {
        var visible = visibleKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new PlaylistViewLayout
        {
            PresetName = presetName,
            Columns = All.Select(c => new PlaylistColumn
            {
                Key = c.Key,
                Width = c.DefaultWidth,
                Visible = visible.Contains(c.Key)
            }).ToList()
        };
    }
}
