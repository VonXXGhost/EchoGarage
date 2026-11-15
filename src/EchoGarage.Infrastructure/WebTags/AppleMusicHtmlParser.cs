using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using EchoGarage.Core.Abstractions;
using EchoGarage.Core.Models;

namespace EchoGarage.Infrastructure.WebTags;

/// <summary>搜索结果页解析结果：直接命中的专辑 + 需要逐个抓取专辑页补全的歌曲反推候选（最多 5 个）。</summary>
public sealed record AppleMusicSearchPageResult(
    IReadOnlyList<WebTagSearchItem> Albums,
    IReadOnlyList<string> DerivedAlbumIds);

/// <summary>
/// 专辑页解析结果。<see cref="TrackCount"/> 来自页面 header，<see cref="WebTagRelease"/> 本身没有该字段，
/// 供 Source 映射派生候选的曲目数使用。
/// </summary>
public sealed record AppleMusicReleasePageResult(WebTagRelease Release, int? TrackCount);

/// <summary>
/// Apple Music 日本站 HTML 解析器（ADR-022）：从页面内嵌的 serialized-server-data JSON 与 JSON-LD 中
/// 提取专辑字段与曲目字段；纯函数式解析，不发起网络请求，便于用固定 HTML fixture 覆盖。
/// </summary>
public sealed class AppleMusicHtmlParser
{
    /// <summary>搜索页无专辑结果时，最多反推抓取的专辑数量。</summary>
    public const int MaxDerivedAlbumCount = 5;

    /// <summary>站点根地址，用于补全相对链接。</summary>
    public const string PageBaseUrl = "https://music.apple.com";

    private static readonly Regex SerializedDataRegex = new(
        "<script[^>]*id=[\"']serialized-server-data[\"'][^>]*>(?<json>.*?)</script>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex JsonLdScriptRegex = new(
        "<script[^>]*type=[\"']application/ld\\+json[\"'][^>]*>(?<json>.*?)</script>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex YearRegex = new(@"(?<!\d)(?:19|20)\d{2}(?!\d)", RegexOptions.Compiled);

    private static readonly Regex YearOnlyRegex = new(@"^\s*\d{4}\s*年?\s*$", RegexOptions.Compiled);

    private readonly IAppLogger _logger;

    public AppleMusicHtmlParser(IAppLogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>解析 Apple Music 搜索页。</summary>
    /// <param name="html">搜索页 HTML。</param>
    /// <param name="sourceId">结果中回填的源 ID。</param>
    /// <exception cref="WebTagSourceException">页面缺少内嵌 JSON、JSON 损坏或结构无法识别。</exception>
    public AppleMusicSearchPageResult ParseSearchPage(string html, string sourceId)
    {
        using var document = LoadSerializedServerData(html, "搜索页");
        if (!TryGetDataPages(document.RootElement, out var pages))
        {
            throw StructureError("搜索页", "未找到 data 数组");
        }

        var sections = new List<JsonElement>();
        foreach (var page in pages.EnumerateArray())
        {
            if (!TryGetObject(page, "data", out var pageData) ||
                !TryGetArray(pageData, "sections", out var pageSections))
            {
                continue;
            }

            foreach (var section in pageSections.EnumerateArray())
            {
                if (section.ValueKind == JsonValueKind.Object)
                {
                    sections.Add(section);
                }
            }
        }

        var squareAlbums = new List<WebTagSearchItem>();
        var topSearchAlbums = new List<WebTagSearchItem>();
        var derivedAlbumIds = new List<string>();
        var seenDerived = new HashSet<string>(StringComparer.Ordinal);

        foreach (var section in sections)
        {
            var sectionKind = GetSectionKind(section);
            if (!TryGetArray(section, "items", out var items))
            {
                continue;
            }

            var isSquareLockup = sectionKind.Contains("squarelockup", StringComparison.Ordinal);
            var isTopSearchLockup = sectionKind.Contains("topsearch", StringComparison.Ordinal);
            var isTrackLockup = sectionKind.Contains("tracklockup", StringComparison.Ordinal);
            if (!isSquareLockup && !isTopSearchLockup && !isTrackLockup)
            {
                continue;
            }

            foreach (var rawItem in items.EnumerateArray())
            {
                var item = UnwrapLockup(rawItem);
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var descriptor = TryGetObject(item, "contentDescriptor", out var itemDescriptor)
                    ? itemDescriptor
                    : (JsonElement?)null;
                var kind = GetDescriptorKind(descriptor);

                if (IsAlbumDescriptorKind(kind) || (kind.Length == 0 && isSquareLockup))
                {
                    if (!isSquareLockup && !isTopSearchLockup)
                    {
                        continue;
                    }

                    if (TryMapSearchAlbum(item, descriptor, sourceId, out var album))
                    {
                        (isSquareLockup ? squareAlbums : topSearchAlbums).Add(album);
                    }

                    continue;
                }

                if ((IsTrackDescriptorKind(kind) || (kind.Length == 0 && isTrackLockup)) &&
                    (isTrackLockup || isTopSearchLockup))
                {
                    CollectDerivedAlbumId(item, descriptor, seenDerived, derivedAlbumIds);
                }
            }
        }

        // 真实搜索页中 topSearchLockup 排在 squareLockup 之前，但其中的专辑条目往往没有
        // subtitleLinks / trackCount，只有形如 "アルバム · 艺术家" 的 subtitle。
        // 存在专门的方形专辑区块时以 squareLockup 为准，避免粗略条目先到先得；
        // 没有专辑区块时才退回 topSearchLockup，并尽量从 subtitle 解析艺人。
        var sourceAlbums = squareAlbums.Count > 0 ? squareAlbums : topSearchAlbums;
        var albums = new List<WebTagSearchItem>();
        var seenReleases = new HashSet<string>(StringComparer.Ordinal);
        foreach (var album in sourceAlbums)
        {
            if (seenReleases.Add(album.ReleaseId))
            {
                albums.Add(album);
            }
        }

        IReadOnlyList<string> derived = albums.Count == 0
            ? derivedAlbumIds.Take(MaxDerivedAlbumCount).ToArray()
            : Array.Empty<string>();

        _logger.Debug($"Apple Music 搜索页解析完成：专辑 {albums.Count} 个，派生候选 {derived.Count} 个。");
        return new AppleMusicSearchPageResult(albums, derived);
    }

    /// <summary>解析 Apple Music 专辑页。</summary>
    /// <param name="html">专辑页 HTML。</param>
    /// <param name="releaseId">请求时使用的发行 ID；为空时从页面 header 的 contentDescriptor 提取。</param>
    /// <param name="sourceId">结果中回填的源 ID。</param>
    /// <exception cref="WebTagSourceException">页面缺少内嵌 JSON、JSON 损坏或缺少专辑 header。</exception>
    public AppleMusicReleasePageResult ParseReleasePage(
        string html,
        string? releaseId = null,
        string sourceId = WebTagSourceIds.AppleMusicJp)
    {
        var jsonLd = TryReadAlbumJsonLd(html);
        if (jsonLd is null)
        {
            _logger.Debug("Apple Music 专辑页未找到 JSON-LD MusicAlbum，年份/流派仅使用页面数据。");
        }

        using var document = LoadSerializedServerData(html, "专辑页");
        if (!TryGetDataPages(document.RootElement, out var pages))
        {
            throw StructureError("专辑页", "未找到 data 数组");
        }

        JsonElement? headerSection = null;
        var trackSections = new List<JsonElement>();
        string? footerDescription = null;

        foreach (var page in pages.EnumerateArray())
        {
            if (!TryGetObject(page, "data", out var pageData) ||
                !TryGetArray(pageData, "sections", out var sections))
            {
                continue;
            }

            foreach (var section in sections.EnumerateArray())
            {
                if (section.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var sectionKind = GetSectionKind(section);
                if (sectionKind.Contains("containerdetailheaderlockup", StringComparison.Ordinal))
                {
                    // 真实页面的 header 数据在 section.items[0]，旧结构也可能直接放在 section 根上。
                    headerSection ??= GetLockupContent(section);
                }
                else if (sectionKind.Contains("tracklockup", StringComparison.Ordinal))
                {
                    trackSections.Add(section);
                }
                else if (sectionKind.Contains("containerdetailtracklistfooterlockup", StringComparison.Ordinal))
                {
                    var footerContent = GetLockupContent(section);
                    footerDescription ??= FirstNonEmpty(
                        GetString(footerContent, "description"),
                        GetString(section, "description"));
                }
            }
        }

        if (headerSection is not { } header)
        {
            throw StructureError("专辑页", "未找到 containerDetailHeaderLockup");
        }

        var albumId = ResolveReleaseId(releaseId, header);
        var albumTitle = FirstNonEmpty(GetString(header, "title"), jsonLd?.Name) ?? string.Empty;
        var albumArtist = FirstNonEmpty(GetTitleLink(header, "subtitleLinks"), jsonLd?.ArtistName) ?? string.Empty;
        var artworkUrl = NormalizeArtworkUrl(GetArtworkUrl(header));
        var year = ParseYear(jsonLd?.DatePublished) ?? ParseYear(footerDescription);
        var genre = FirstNonEmpty(jsonLd?.Genre, ParseGenreFromQuaternary(GetString(header, "quaternaryTitle")));
        var trackCount = GetInt(header, "trackCount");

        var albumFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddIfNotEmpty(albumFields, WebTagFieldKeys.Album, albumTitle);
        AddIfNotEmpty(albumFields, WebTagFieldKeys.AlbumArtist, albumArtist);
        AddIfNotEmpty(albumFields, WebTagFieldKeys.Year, year);
        AddIfNotEmpty(albumFields, WebTagFieldKeys.Genre, genre);
        AddIfNotEmpty(albumFields, WebTagFieldKeys.Cover, artworkUrl);

        var tracks = new List<WebTagTrack>();
        foreach (var trackSection in trackSections)
        {
            if (!TryGetArray(trackSection, "items", out var items))
            {
                continue;
            }

            foreach (var rawTrack in items.EnumerateArray())
            {
                var track = UnwrapLockup(rawTrack);
                if (track.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                tracks.Add(MapTrack(track, albumArtist, artworkUrl));
            }
        }

        var release = new WebTagRelease
        {
            SourceId = sourceId,
            ReleaseId = albumId,
            Title = albumTitle,
            Artist = albumArtist,
            Kind = WebTagReleaseKind.Album,
            ArtworkUrl = artworkUrl,
            SourceUrl = BuildAlbumUrl(albumId),
            AlbumFields = albumFields,
            Tracks = tracks
        };

        _logger.Debug($"Apple Music 专辑页解析完成：{albumTitle}（{tracks.Count} 首曲目）。");
        return new AppleMusicReleasePageResult(release, trackCount);
    }

    /// <summary>
    /// 识别专辑链接或纯数字 ID：支持 music.apple.com 的 /album/ 链接（含可选 slug、storefront、?i= 曲目参数）。
    /// 非 Apple Music 域名、艺人/歌单链接或缺少数字 ID 时返回 false。
    /// </summary>
    public static bool TryParseReleaseId(string? input, out string releaseId)
    {
        releaseId = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var text = input.Trim();
        if (IsNumericId(text))
        {
            releaseId = text;
            return true;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || !IsAppleMusicHost(uri))
        {
            return false;
        }

        return TryFindAlbumIdInPath(uri.AbsolutePath, out releaseId);
    }

    /// <summary>从歌曲 / 专辑 URL 中提取所属专辑 ID；无法提取时返回 false。</summary>
    public static bool TryExtractAlbumId(string? url, out string albumId)
    {
        albumId = string.Empty;
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var path = url.Trim();
        var queryStart = path.IndexOfAny(['?', '#']);
        if (queryStart >= 0)
        {
            path = path[..queryStart];
        }

        if (Uri.TryCreate(path, UriKind.Absolute, out var absoluteUri))
        {
            // 绝对地址必须指向 Apple Music，避免从外站链接误推断出专辑 ID。
            if (!IsAppleMusicHost(absoluteUri))
            {
                return false;
            }

            path = absoluteUri.AbsolutePath;
        }

        return TryFindAlbumIdInPath(path, out albumId);
    }

    /// <summary>
    /// 把封面临时模板 URL（如 .../1200x1200bb.jpg 或 .../{w}x{h}bb.{f}）提升到 1200×1200 JPEG；
    /// 不含模板时保留原 URL。非法或非 http(s) 地址返回 null。
    /// </summary>
    public static string? NormalizeArtworkUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var text = url.Trim();
        if (text.Contains("{w}", StringComparison.OrdinalIgnoreCase) &&
            text.Contains("{h}", StringComparison.OrdinalIgnoreCase) &&
            text.Contains(".{f}", StringComparison.OrdinalIgnoreCase))
        {
            text = text
                .Replace("{w}", "1200", StringComparison.OrdinalIgnoreCase)
                .Replace("{h}", "1200", StringComparison.OrdinalIgnoreCase)
                .Replace(".{f}", ".jpg", StringComparison.OrdinalIgnoreCase);
        }

        if (text.StartsWith("//", StringComparison.Ordinal))
        {
            text = "https:" + text;
        }
        else if (text.StartsWith('/'))
        {
            text = PageBaseUrl + text;
        }

        return Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? text
            : null;
    }

    /// <summary>构造日本区专辑页地址。</summary>
    public static string BuildAlbumUrl(string releaseId) =>
        $"{PageBaseUrl}/jp/album/{Uri.EscapeDataString(releaseId)}?l=ja-JP";

    private sealed record JsonLdAlbumInfo(string? Name, string? ArtistName, string? DatePublished, string? Genre);

    private JsonDocument LoadSerializedServerData(string html, string pageLabel)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            _logger.Warn($"Apple Music {pageLabel}内容为空。");
            throw new WebTagSourceException($"Apple Music {pageLabel}内容为空，请稍后再试。");
        }

        var match = SerializedDataRegex.Match(html);
        if (!match.Success)
        {
            _logger.Warn($"Apple Music {pageLabel}未找到 serialized-server-data。");
            throw StructureError(pageLabel, "未找到 serialized-server-data");
        }

        try
        {
            return JsonDocument.Parse(match.Groups["json"].Value);
        }
        catch (JsonException exception)
        {
            _logger.Warn($"Apple Music {pageLabel}内嵌 JSON 解析失败：{exception.Message}");
            throw new WebTagSourceException($"Apple Music {pageLabel}数据解析失败，请稍后再试或更新应用。", exception);
        }
    }

    private bool TryMapSearchAlbum(
        JsonElement item,
        JsonElement? descriptor,
        string sourceId,
        out WebTagSearchItem album)
    {
        album = null!;
        if (!TryGetAlbumId(item, descriptor, out var releaseId))
        {
            _logger.Debug("Apple Music 搜索页专辑条目缺少专辑 ID，已跳过。");
            return false;
        }

        var title = FirstNonEmpty(GetTitleLink(item, "titleLinks"), GetString(item, "title")) ?? string.Empty;
        var artist = FirstNonEmpty(
            GetTitleLink(item, "subtitleLinks"),
            GetString(item, "artistName"),
            ParseSearchSubtitleArtist(GetString(item, "subtitle"))) ?? string.Empty;
        var artworkUrl = NormalizeArtworkUrl(GetArtworkUrl(item));
        var descriptorUrl = descriptor is { } descriptorElement ? GetString(descriptorElement, "url") : null;

        album = new WebTagSearchItem
        {
            SourceId = sourceId,
            ReleaseId = releaseId,
            Title = title,
            Artist = artist,
            TrackCount = GetInt(item, "trackCount"),
            ArtworkUrl = artworkUrl,
            SourceUrl = ResolvePageUrl(descriptorUrl) ?? BuildAlbumUrl(releaseId),
            ResultKind = WebTagResultKind.Album
        };
        return true;
    }

    private static void CollectDerivedAlbumId(
        JsonElement item,
        JsonElement? descriptor,
        HashSet<string> seen,
        List<string> albumIds)
    {
        if (albumIds.Count >= MaxDerivedAlbumCount)
        {
            return;
        }

        string? url = null;
        if (descriptor is { } descriptorElement &&
            TryGetStringValue(descriptorElement, "url", out var descriptorUrl))
        {
            url = descriptorUrl;
        }

        if (url is null && TryGetStringValue(item, "cdUrl", out var cdUrl))
        {
            url = cdUrl;
        }

        if (url is not null && TryExtractAlbumId(url, out var albumId) && seen.Add(albumId))
        {
            albumIds.Add(albumId);
        }
    }

    private static WebTagTrack MapTrack(JsonElement item, string albumArtist, string? albumArtworkUrl)
    {
        var title = GetString(item, "title") ?? string.Empty;
        var artist = FirstNonEmpty(ReadText(GetPropertyOrNull(item, "artistName")), albumArtist) ?? string.Empty;
        var composer = ReadText(GetPropertyOrNull(item, "composer"));
        var trackNumber = GetInt(item, "trackNumber") ?? 0;
        var discNumber = GetInt(item, "discNumber") ?? 0;
        var durationMs = GetLong(item, "duration");
        var remoteId = TryGetObject(item, "contentDescriptor", out var descriptor) &&
                       TryGetObject(descriptor, "identifiers", out var identifiers) &&
                       TryGetNumericId(identifiers, "storeAdamID", out var adamId)
            ? adamId
            : null;
        var artworkUrl = NormalizeArtworkUrl(GetArtworkUrl(item)) ?? albumArtworkUrl;

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddIfNotEmpty(fields, WebTagFieldKeys.Title, title);
        AddIfNotEmpty(fields, WebTagFieldKeys.Artist, artist);
        AddIfNotEmpty(fields, WebTagFieldKeys.Composer, composer);
        if (trackNumber > 0)
        {
            fields[WebTagFieldKeys.Track] = trackNumber.ToString(CultureInfo.InvariantCulture);
        }

        if (discNumber > 0)
        {
            fields[WebTagFieldKeys.Disc] = discNumber.ToString(CultureInfo.InvariantCulture);
        }

        return new WebTagTrack
        {
            DiscNumber = discNumber,
            TrackNumber = trackNumber,
            DurationMs = durationMs,
            RemoteId = remoteId,
            ArtworkUrl = artworkUrl,
            Fields = fields
        };
    }

    private string ResolveReleaseId(string? releaseId, JsonElement header)
    {
        if (!string.IsNullOrWhiteSpace(releaseId))
        {
            var trimmed = releaseId.Trim();
            if (TryParseReleaseId(trimmed, out var parsed))
            {
                return parsed;
            }

            if (IsNumericId(trimmed))
            {
                return trimmed;
            }
        }

        JsonElement? descriptor = TryGetObject(header, "contentDescriptor", out var headerDescriptor)
            ? headerDescriptor
            : null;
        if (TryGetAlbumId(header, descriptor, out var albumId))
        {
            return albumId;
        }

        _logger.Warn("Apple Music 专辑页未找到专辑 ID。");
        throw new WebTagSourceException("Apple Music 专辑页缺少专辑 ID，无法继续。");
    }

    private static bool TryGetAlbumId(JsonElement item, JsonElement? descriptor, out string albumId)
    {
        albumId = string.Empty;
        if (descriptor is { } descriptorElement)
        {
            if (TryGetStringValue(descriptorElement, "url", out var url) &&
                TryExtractAlbumId(url, out albumId))
            {
                return true;
            }

            if (TryGetObject(descriptorElement, "identifiers", out var identifiers) &&
                TryGetNumericId(identifiers, "storeAdamID", out albumId))
            {
                return true;
            }
        }

        if (TryGetObject(item, "identifiers", out var itemIdentifiers) &&
            TryGetNumericId(itemIdentifiers, "storeAdamID", out albumId))
        {
            return true;
        }

        return TryGetNumericId(item, "id", out albumId);
    }

    private static JsonElement UnwrapLockup(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return element;
        }

        if (TryGetProperty(element, "item", out var inner) && inner.ValueKind == JsonValueKind.Object)
        {
            var outerHasContent =
                TryGetProperty(element, "title", out _) ||
                TryGetProperty(element, "titleLinks", out _) ||
                TryGetProperty(element, "subtitleLinks", out _) ||
                TryGetProperty(element, "contentDescriptor", out _) ||
                TryGetProperty(element, "artwork", out _);
            if (!outerHasContent)
            {
                return inner;
            }
        }

        return element;
    }

    // 真实页面的 section 使用 itemKind（topSearchLockup/squareLockup/trackLockup/
    // containerDetailHeaderLockup/containerDetailTracklistFooterLockup），旧结构使用 sectionType。
    private static string GetSectionKind(JsonElement section)
    {
        var kind = GetString(section, "itemKind") ?? GetString(section, "sectionType");
        return kind?.Trim().ToLowerInvariant() ?? string.Empty;
    }

    private static string GetDescriptorKind(JsonElement? descriptor)
    {
        if (descriptor is not { } descriptorElement ||
            !TryGetStringValue(descriptorElement, "kind", out var kind))
        {
            return string.Empty;
        }

        return kind?.Trim().ToLowerInvariant() ?? string.Empty;
    }

    private static bool IsAlbumDescriptorKind(string kind) => kind == "album";

    private static bool IsTrackDescriptorKind(string kind) => kind is "song" or "track";

    // 真实专辑页的 header / footer 数据放在 section.items[0]，旧结构直接放在 section 根上。
    private static JsonElement GetLockupContent(JsonElement section)
    {
        if (TryGetArray(section, "items", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    return item;
                }
            }
        }

        return section;
    }

    // topSearchLockup 的专辑条目不提供 subtitleLinks，只有形如 "アルバム · 艺术家" 的 subtitle。
    private static string? ParseSearchSubtitleArtist(string? subtitle)
    {
        if (string.IsNullOrWhiteSpace(subtitle))
        {
            return null;
        }

        var text = subtitle.Trim();
        var separatorIndex = text.LastIndexOfAny(new[] { '\u00B7', '\u30FB' });
        if (separatorIndex < 0)
        {
            return null;
        }

        var artist = text[(separatorIndex + 1)..].Trim();
        return artist.Length == 0 ? null : artist;
    }

    private static bool TryGetDataPages(JsonElement root, out JsonElement pages)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            pages = root;
            return true;
        }

        return TryGetArray(root, "data", out pages);
    }

    private static JsonLdAlbumInfo? TryReadAlbumJsonLd(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        foreach (Match match in JsonLdScriptRegex.Matches(html))
        {
            try
            {
                using var document = JsonDocument.Parse(match.Groups["json"].Value);
                var nodes = new List<JsonElement>();
                CollectJsonLdNodes(document.RootElement, nodes);
                foreach (var node in nodes)
                {
                    if (!IsMusicAlbumNode(node))
                    {
                        continue;
                    }

                    var name = ReadText(GetPropertyOrNull(node, "name"));
                    var artist = ReadText(GetPropertyOrNull(node, "byArtist"));
                    var datePublished = ReadText(GetPropertyOrNull(node, "datePublished"));
                    var genre = GetFirstText(GetPropertyOrNull(node, "genre"));
                    return new JsonLdAlbumInfo(name, artist, datePublished, genre);
                }
            }
            catch (JsonException)
            {
                // 单个 JSON-LD 块损坏不影响 serialized-server-data 解析。
            }
        }

        return null;
    }

    private static void CollectJsonLdNodes(JsonElement root, List<JsonElement> nodes)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in root.EnumerateArray())
            {
                CollectJsonLdNodes(child, nodes);
            }

            return;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        nodes.Add(root);
        if (TryGetArray(root, "@graph", out var graph))
        {
            foreach (var child in graph.EnumerateArray())
            {
                CollectJsonLdNodes(child, nodes);
            }
        }
    }

    private static bool IsMusicAlbumNode(JsonElement node)
    {
        if (!TryGetProperty(node, "@type", out var type))
        {
            return false;
        }

        return ReadText(type)?.Contains("MusicAlbum", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string? GetArtworkUrl(JsonElement element)
    {
        if (!TryGetProperty(element, "artwork", out var artwork) || artwork.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (TryGetObject(artwork, "dictionary", out var dictionary) &&
            TryGetStringValue(dictionary, "url", out var dictionaryUrl))
        {
            return dictionaryUrl;
        }

        return TryGetStringValue(artwork, "url", out var directUrl) ? directUrl : null;
    }

    private static string? GetTitleLink(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var links))
        {
            return null;
        }

        if (links.ValueKind == JsonValueKind.Array)
        {
            foreach (var link in links.EnumerateArray())
            {
                var text = ReadText(link);
                if (text is not null)
                {
                    return text;
                }
            }

            return null;
        }

        return ReadText(links);
    }

    private static string? ResolvePageUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var text = url.Trim();
        if (text.StartsWith("//", StringComparison.Ordinal))
        {
            text = "https:" + text;
        }
        else if (text.StartsWith('/'))
        {
            text = PageBaseUrl + text;
        }

        return Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? text
            : null;
    }

    private static string? ParseYear(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = YearRegex.Match(text);
        return match.Success ? match.Value : null;
    }

    private static string? ParseGenreFromQuaternary(string? quaternaryTitle)
    {
        if (string.IsNullOrWhiteSpace(quaternaryTitle))
        {
            return null;
        }

        var text = quaternaryTitle.Trim();
        var separator = text.IndexOf('·');
        if (separator >= 0)
        {
            var genre = text[..separator].Trim();
            return genre.Length == 0 ? null : genre;
        }

        return YearOnlyRegex.IsMatch(text) ? null : text;
    }

    private static void AddIfNotEmpty(Dictionary<string, string> fields, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            fields[key] = value.Trim();
        }
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static bool IsNumericId(string? text) =>
        !string.IsNullOrWhiteSpace(text) && text.Length <= 20 && text.All(char.IsAsciiDigit);

    private static bool IsAppleMusicHost(Uri uri) =>
        string.Equals(uri.Host, "music.apple.com", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(uri.Host, "www.music.apple.com", StringComparison.OrdinalIgnoreCase);

    private static bool TryFindAlbumIdInPath(string path, out string albumId)
    {
        albumId = string.Empty;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var albumIndex = Array.FindIndex(
            segments,
            segment => string.Equals(segment, "album", StringComparison.OrdinalIgnoreCase));
        if (albumIndex < 0)
        {
            return false;
        }

        for (var index = segments.Length - 1; index > albumIndex; index--)
        {
            if (IsNumericId(segments[index]))
            {
                albumId = segments[index];
                return true;
            }
        }

        return false;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value);
    }

    private static bool TryGetObject(JsonElement element, string name, out JsonElement value)
    {
        if (TryGetProperty(element, name, out value) && value.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        value = default;
        return false;
    }

    private static bool TryGetArray(JsonElement element, string name, out JsonElement value)
    {
        if (TryGetProperty(element, name, out value) && value.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        value = default;
        return false;
    }

    private static JsonElement? GetPropertyOrNull(JsonElement element, string name) =>
        TryGetProperty(element, name, out var value) ? value : null;

    private static string? GetString(JsonElement element, string name) =>
        TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? NullIfWhitespace(value.GetString())
            : null;

    private static bool TryGetStringValue(JsonElement element, string name, out string? value)
    {
        value = null;
        if (!TryGetProperty(element, name, out var raw) || raw.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = NullIfWhitespace(raw.GetString());
        return value is not null;
    }

    private static bool TryGetNumericId(JsonElement element, string name, out string id)
    {
        id = string.Empty;
        if (!TryGetProperty(element, name, out var value))
        {
            return false;
        }

        var text = ScalarText(value);
        if (!IsNumericId(text))
        {
            return false;
        }

        id = text!;
        return true;
    }

    private static string? ReadText(JsonElement? maybeElement)
    {
        if (maybeElement is not { } element)
        {
            return null;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return NullIfWhitespace(element.GetString());

            case JsonValueKind.Array:
            {
                var parts = new List<string>();
                foreach (var child in element.EnumerateArray())
                {
                    var text = ReadText(child);
                    if (text is not null)
                    {
                        parts.Add(text);
                    }
                }

                return parts.Count == 0 ? null : string.Join(", ", parts);
            }

            case JsonValueKind.Object:
                if (TryGetProperty(element, "name", out var name) && ReadText(name) is { } nameText)
                {
                    return nameText;
                }

                if (TryGetProperty(element, "title", out var title) && ReadText(title) is { } titleText)
                {
                    return titleText;
                }

                return null;

            default:
                return null;
        }
    }

    private static string? ReadText(JsonElement element) => ReadText((JsonElement?)element);

    private static string? GetFirstText(JsonElement? maybeElement)
    {
        if (maybeElement is not { } element)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            return ReadText(element);
        }

        foreach (var child in element.EnumerateArray())
        {
            var text = ReadText(child);
            if (text is not null)
            {
                return text;
            }
        }

        return null;
    }

    private static string? ScalarText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => NullIfWhitespace(element.GetString()),
        JsonValueKind.Number => element.GetRawText(),
        _ => null
    };

    private static int? GetInt(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String &&
               int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static long? GetLong(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String &&
               long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static string? NullIfWhitespace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private WebTagSourceException StructureError(string pageLabel, string detail)
    {
        _logger.Warn($"Apple Music {pageLabel}结构无法识别：{detail}。");
        return new WebTagSourceException($"Apple Music {pageLabel}结构无法识别，请稍后再试或更新应用。");
    }
}
