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

public sealed record ColumnDescriptor(
    string Key,
    string Header,
    double DefaultWidth,
    double MinWidth,
    ColumnKind Kind,
    bool Sortable = true);

public static class ColumnCatalog
{
    public static readonly IReadOnlyList<ColumnDescriptor> All =
    [
        new("cover", "封面", 48, 40, ColumnKind.Cover, false),
        new("status", "状态", 46, 42, ColumnKind.Status, false),
        new("index", "#", 40, 30, ColumnKind.Index, false),
        new("title", "标题", 260, 120, ColumnKind.Text),
        new("artist", "艺术家", 160, 90, ColumnKind.Text),
        new("album", "专辑", 170, 90, ColumnKind.Text),
        new("albumArtist", "专辑艺术家", 150, 90, ColumnKind.Text),
        new("trackNumber", "音轨", 54, 44, ColumnKind.Text),
        new("discNumber", "碟号", 50, 42, ColumnKind.Text),
        new("year", "年份", 56, 48, ColumnKind.Text),
        new("genre", "流派", 110, 60, ColumnKind.Text),
        new("duration", "时间", 62, 54, ColumnKind.Duration),
        new("bitrate", "比特率", 72, 60, ColumnKind.Text),
        new("sampleRate", "采样率", 72, 60, ColumnKind.Text),
        new("codec", "格式", 62, 48, ColumnKind.Text),
        new("rating", "评分", 72, 56, ColumnKind.Rating),
        new("addedAt", "添加时间", 100, 80, ColumnKind.Date),
        new("fileSize", "大小", 72, 60, ColumnKind.FileSize),
        new("path", "文件路径", 320, 120, ColumnKind.Text),
        new("comment", "备注", 160, 80, ColumnKind.Text)
    ];

    private static readonly Dictionary<string, ColumnDescriptor> ByKey =
        All.ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);

    public static ColumnDescriptor? Find(string key) =>
        ByKey.TryGetValue(key, out var descriptor) ? descriptor : null;

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
