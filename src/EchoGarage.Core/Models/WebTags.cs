namespace EchoGarage.Core.Models;

/// <summary>Web 标签字段的作用域：专辑级字段对本次所选全部文件生效，歌曲级字段只对匹配文件生效。</summary>
public enum WebTagFieldScope
{
    Album,
    Track
}

/// <summary>搜索结果类型；DerivedAlbum 表示由单曲结果反推的专辑候选。</summary>
public enum WebTagResultKind
{
    Album,
    DerivedAlbum
}

/// <summary>远程条目的形态；用于兼容不区分专辑/单曲的源。</summary>
public enum WebTagReleaseKind
{
    Album,
    Single,
    Unspecified
}

/// <summary>代理模式（ADR-022）。</summary>
public enum WebProxyMode
{
    System,
    Direct,
    Custom
}

/// <summary>本地文件与远程曲目的匹配方式。</summary>
public enum WebTagMatchKind
{
    None,
    TrackNumber,
    Title,
    Manual
}

/// <summary>内置 Web 标签源 ID。新增源时同步加入 <see cref="Known"/>。</summary>
public static class WebTagSourceIds
{
    public const string AppleMusicJp = "apple-music-jp";

    public static IReadOnlyList<string> Known { get; } = [AppleMusicJp];

    public static bool IsKnown(string? sourceId) =>
        !string.IsNullOrWhiteSpace(sourceId) &&
        Known.Contains(sourceId, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Web 标签字段 key；与本地标签字段 key 保持一致，封面单独使用 cover。</summary>
public static class WebTagFieldKeys
{
    public const string Album = TagFieldKeys.Album;
    public const string AlbumArtist = TagFieldKeys.AlbumArtist;
    public const string Year = TagFieldKeys.Year;
    public const string Genre = TagFieldKeys.Genre;
    public const string Cover = "cover";

    public const string Title = TagFieldKeys.Title;
    public const string Artist = TagFieldKeys.Artist;
    public const string Composer = TagFieldKeys.Composer;
    public const string Track = TagFieldKeys.TrackNumber;
    public const string Disc = TagFieldKeys.DiscNumber;
}

/// <summary>一个可写入的 Web 标签字段定义。</summary>
public sealed record WebTagFieldDefinition(
    string Key,
    string DisplayName,
    WebTagFieldScope Scope,
    bool DefaultSelected)
{
    public bool IsAlbumField => Scope == WebTagFieldScope.Album;
    public bool IsTrackField => Scope == WebTagFieldScope.Track;
}

/// <summary>
/// Web 标签字段目录：字段顺序、中文名、专辑/歌曲作用域与默认勾选。
/// 覆盖字段由 App 层单独处理，但也在目录中占位以便设置记忆。
/// </summary>
public static class WebTagFieldCatalog
{
    public static IReadOnlyList<WebTagFieldDefinition> All { get; } =
    [
        new(WebTagFieldKeys.Album, "专辑", WebTagFieldScope.Album, true),
        new(WebTagFieldKeys.AlbumArtist, "专辑艺术家", WebTagFieldScope.Album, true),
        new(WebTagFieldKeys.Year, "年份", WebTagFieldScope.Album, true),
        new(WebTagFieldKeys.Genre, "流派", WebTagFieldScope.Album, true),
        new(WebTagFieldKeys.Cover, "封面", WebTagFieldScope.Album, true),
        new(WebTagFieldKeys.Title, "标题", WebTagFieldScope.Track, true),
        new(WebTagFieldKeys.Artist, "艺术家", WebTagFieldScope.Track, true),
        new(WebTagFieldKeys.Composer, "作曲", WebTagFieldScope.Track, true),
        new(WebTagFieldKeys.Track, "音轨号", WebTagFieldScope.Track, true),
        new(WebTagFieldKeys.Disc, "碟号", WebTagFieldScope.Track, true)
    ];

    public static IReadOnlyList<string> DefaultSelectedKeys { get; } =
        All.Where(f => f.DefaultSelected).Select(f => f.Key).ToArray();

    public static WebTagFieldDefinition? Find(string? key) =>
        string.IsNullOrWhiteSpace(key)
            ? null
            : All.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<string> NormalizeSelection(IEnumerable<string>? selected)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (selected is not null)
        {
            foreach (var key in selected)
            {
                if (Find(key) is { } definition && seen.Add(definition.Key))
                {
                    result.Add(definition.Key);
                }
            }
        }

        return result;
    }

    public static IReadOnlyList<string> GetDefaultsForSource(string? sourceId) =>
        WebTagSourceIds.IsKnown(sourceId) ? DefaultSelectedKeys : DefaultSelectedKeys;
}

/// <summary>远程源能力描述；UI 按能力显示/禁用对应区块，不硬编码 Apple Music 结构。</summary>
public sealed record WebTagSourceCapabilities
{
    public bool SupportsAlbumFields { get; init; } = true;
    public bool SupportsTrackFields { get; init; } = true;
    public bool SupportsCover { get; init; } = true;
    public bool SupportsComposer { get; init; } = true;
}

public sealed record WebTagSearchQuery(string SourceId, string Term);

public sealed record WebTagSearchItem
{
    public required string SourceId { get; init; }
    public required string ReleaseId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Artist { get; init; } = string.Empty;
    public int? TrackCount { get; init; }
    public int? Year { get; init; }
    public string? ArtworkUrl { get; init; }
    public string? SourceUrl { get; init; }
    public WebTagResultKind ResultKind { get; init; } = WebTagResultKind.Album;

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? ReleaseId : Title;
    public string DisplayArtist => string.IsNullOrWhiteSpace(Artist) ? "未知艺术家" : Artist;
    public string TrackCountDisplay => TrackCount is > 0 ? TrackCount.Value.ToString() : "—";
    public string YearDisplay => Year is > 0 ? Year.Value.ToString() : "—";
    public bool IsDerived => ResultKind == WebTagResultKind.DerivedAlbum;
}

public sealed record WebTagRelease
{
    public required string SourceId { get; init; }
    public required string ReleaseId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Artist { get; init; } = string.Empty;
    public WebTagReleaseKind Kind { get; init; } = WebTagReleaseKind.Album;
    public string? ArtworkUrl { get; init; }
    public string? SourceUrl { get; init; }

    /// <summary>专辑级字段：album / albumArtist / year / genre / cover(URL)。没有值时不出现。</summary>
    public Dictionary<string, string> AlbumFields { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<WebTagTrack> Tracks { get; init; } = [];

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? ReleaseId : Title;
    public string DisplayArtist => string.IsNullOrWhiteSpace(Artist) ? "未知艺术家" : Artist;
}

/// <summary>远程曲目；Fields 使用 <see cref="WebTagFieldKeys"/> 中的歌曲级 key。</summary>
public sealed record WebTagTrack
{
    public int DiscNumber { get; init; }
    public int TrackNumber { get; init; }
    public long? DurationMs { get; init; }
    public string? RemoteId { get; init; }
    public string? ArtworkUrl { get; init; }
    public Dictionary<string, string> Fields { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public string DisplayTitle => Fields.TryGetValue(WebTagFieldKeys.Title, out var title) && !string.IsNullOrWhiteSpace(title)
        ? title
        : "（无标题）";

    public string DisplayArtist => Fields.TryGetValue(WebTagFieldKeys.Artist, out var artist) && !string.IsNullOrWhiteSpace(artist)
        ? artist
        : string.Empty;

    public string DisplayComposer => Fields.TryGetValue(WebTagFieldKeys.Composer, out var composer) && !string.IsNullOrWhiteSpace(composer)
        ? composer
        : "无数据";

    public string DisplayDuration => DurationMs is > 0
        ? TimeSpan.FromMilliseconds(DurationMs.Value) is var duration && duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes}:{duration.Seconds:00}"
        : "—";

    public string OrdinalDisplay => TrackNumber > 0
        ? DiscNumber > 0 ? $"{DiscNumber}-{TrackNumber:00}" : $"{TrackNumber:00}"
        : "—";
}

public sealed record WebTagMatch(
    Track LocalTrack,
    WebTagTrack? RemoteTrack,
    WebTagMatchKind Kind)
{
    public bool IsMatched => RemoteTrack is not null && Kind != WebTagMatchKind.None;
}

public sealed record WebTagFileEdit(Track Track, TagBatchEdit Edit);

public sealed record WebTagApplyPlan(IReadOnlyList<WebTagFileEdit> Edits);

/// <summary>封面下载结果；Mime 可能是 image/jpeg 或 image/png。</summary>
public sealed record WebTagCover(byte[] Bytes, string Mime);

/// <summary>设置页连接测试结果。</summary>
public sealed record WebTagConnectionTestResult(bool Success, string Message, TimeSpan Elapsed)
{
    public static WebTagConnectionTestResult Ok(TimeSpan elapsed) =>
        new(true, "连接成功", elapsed);

    public static WebTagConnectionTestResult Fail(string message, TimeSpan elapsed) =>
        new(false, message, elapsed);
}
