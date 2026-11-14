namespace EchoGarage.Core.Models;

/// <summary>标签编辑页可修改的字段 key（与 UI 一一对应）。</summary>
public static class TagFieldKeys
{
    public const string Title = "title";
    public const string Artist = "artist";
    public const string Album = "album";
    public const string AlbumArtist = "albumArtist";
    public const string Composer = "composer";
    public const string Genre = "genre";
    public const string Year = "year";
    public const string TrackNumber = "track";
    public const string DiscNumber = "disc";
    public const string Comment = "comment";
}

/// <summary>
/// 多文件标签编辑的结果：<see cref="Fields"/> 只包含被用户修改过的字段，
/// 未修改字段在保存时保持每个文件自己的原值；封面改动单独标记，统一应用到所有选中文件。
/// </summary>
public sealed class TagBatchEdit
{
    public Dictionary<string, string> Fields { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool CoverChanged { get; set; }

    public byte[]? NewCoverBytes { get; set; }

    public string? NewCoverMime { get; set; }

    public bool RemoveCover { get; set; }
}

public static class TagBatchEditor
{
    /// <summary>取一个文件的各字段文本值（0 表示空，供 UI 比较多值）。</summary>
    public static IReadOnlyDictionary<string, string> GetFieldValues(AudioTagData tag) =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [TagFieldKeys.Title] = tag.Title,
            [TagFieldKeys.Artist] = tag.Artist,
            [TagFieldKeys.Album] = tag.Album,
            [TagFieldKeys.AlbumArtist] = tag.AlbumArtist,
            [TagFieldKeys.Composer] = tag.Composer,
            [TagFieldKeys.Genre] = tag.Genre,
            [TagFieldKeys.Year] = tag.Year is > 0 ? tag.Year.Value.ToString() : string.Empty,
            [TagFieldKeys.TrackNumber] = tag.TrackNumber > 0 ? tag.TrackNumber.ToString() : string.Empty,
            [TagFieldKeys.DiscNumber] = tag.DiscNumber > 0 ? tag.DiscNumber.ToString() : string.Empty,
            [TagFieldKeys.Comment] = tag.Comment
        };

    /// <summary>
    /// 以文件当前标签为底稿生成写回模型：只覆盖被修改字段与封面，
    /// 其余字段沿用该文件自己的当前值，避免批量编辑把未修改字段清空。
    /// </summary>
    public static TagEditModel CreateEdit(AudioTagData source, TagBatchEdit edit)
    {
        var model = new TagEditModel
        {
            Title = source.Title,
            Artist = source.Artist,
            Album = source.Album,
            AlbumArtist = source.AlbumArtist,
            Composer = source.Composer,
            Genre = source.Genre,
            Comment = source.Comment,
            Year = source.Year ?? 0,
            TrackNumber = source.TrackNumber,
            DiscNumber = source.DiscNumber
        };

        if (edit.Fields.TryGetValue(TagFieldKeys.Title, out var title)) model.Title = title;
        if (edit.Fields.TryGetValue(TagFieldKeys.Artist, out var artist)) model.Artist = artist;
        if (edit.Fields.TryGetValue(TagFieldKeys.Album, out var album)) model.Album = album;
        if (edit.Fields.TryGetValue(TagFieldKeys.AlbumArtist, out var albumArtist)) model.AlbumArtist = albumArtist;
        if (edit.Fields.TryGetValue(TagFieldKeys.Composer, out var composer)) model.Composer = composer;
        if (edit.Fields.TryGetValue(TagFieldKeys.Genre, out var genre)) model.Genre = genre;
        if (edit.Fields.TryGetValue(TagFieldKeys.Comment, out var comment)) model.Comment = comment;
        if (edit.Fields.TryGetValue(TagFieldKeys.Year, out var year)) model.Year = ParseInt(year);
        if (edit.Fields.TryGetValue(TagFieldKeys.TrackNumber, out var track)) model.TrackNumber = ParseInt(track);
        if (edit.Fields.TryGetValue(TagFieldKeys.DiscNumber, out var disc)) model.DiscNumber = ParseInt(disc);

        if (edit.CoverChanged)
        {
            model.NewCoverBytes = edit.RemoveCover ? null : edit.NewCoverBytes;
            model.NewCoverMime = edit.RemoveCover ? null : edit.NewCoverMime;
            model.RemoveCover = edit.RemoveCover;
        }

        return model;
    }

    private static int ParseInt(string text) => int.TryParse(text, out var value) && value > 0 ? value : 0;
}
