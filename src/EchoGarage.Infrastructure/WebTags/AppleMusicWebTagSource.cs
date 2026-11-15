using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using EchoGarage.Core.Abstractions;
using EchoGarage.Core.Models;

namespace EchoGarage.Infrastructure.WebTags;

/// <summary>
/// Apple Music（日本）Web 标签源（ADR-022）：抓取搜索页 / 专辑页 HTML，交给
/// <see cref="AppleMusicHtmlParser"/> 解析；每次请求按当前网络设置创建 HttpClient。
/// </summary>
public sealed class AppleMusicWebTagSource : IWebTagSource
{
    /// <summary>设置页“测试连接”请求的基础页面。</summary>
    public const string BasePageUrl = "https://music.apple.com/jp/";

    /// <summary>搜索页地址模板（term 参数需 EscapeDataString）。</summary>
    public const string SearchUrlFormat = "https://music.apple.com/jp/search?term={0}&l=ja-JP";

    /// <summary>搜索页 / 专辑页响应大小上限。</summary>
    public const int MaxPageBytes = 20 * 1024 * 1024;

    private const int InitialReadBufferSize = 16 * 1024;

    private readonly WebTagHttpClientFactory _httpClientFactory;
    private readonly ISettingsService _settings;
    private readonly IAppLogger _logger;
    private readonly AppleMusicHtmlParser _parser;

    public AppleMusicWebTagSource(
        WebTagHttpClientFactory httpClientFactory,
        ISettingsService settings,
        IAppLogger logger,
        AppleMusicHtmlParser? parser = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _parser = parser ?? new AppleMusicHtmlParser(logger);
    }

    public string Id => WebTagSourceIds.AppleMusicJp;

    public string DisplayName => "Apple Music（日本）";

    public WebTagSourceCapabilities Capabilities { get; } = new();

    public async Task<IReadOnlyList<WebTagSearchItem>> SearchAsync(
        WebTagSearchQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var term = query.Term?.Trim() ?? string.Empty;
        if (term.Length == 0)
        {
            throw new WebTagSourceException("搜索关键词不能为空。");
        }

        var url = string.Format(CultureInfo.InvariantCulture, SearchUrlFormat, Uri.EscapeDataString(term));
        _logger.Info($"Apple Music 搜索开始：关键词 \"{term}\"。");

        var html = await FetchPageAsync(url, "搜索页", cancellationToken).ConfigureAwait(false);
        var page = _parser.ParseSearchPage(html, Id);

        if (page.Albums.Count > 0)
        {
            _logger.Info($"Apple Music 搜索完成：关键词 \"{term}\" 命中 {page.Albums.Count} 个专辑。");
            return page.Albums;
        }

        if (page.DerivedAlbumIds.Count == 0)
        {
            _logger.Info($"Apple Music 搜索完成：关键词 \"{term}\" 无结果。");
            return Array.Empty<WebTagSearchItem>();
        }

        _logger.Info($"Apple Music 搜索无专辑结果，尝试由 {page.DerivedAlbumIds.Count} 个歌曲结果反推专辑。");
        var results = new List<WebTagSearchItem>(page.DerivedAlbumIds.Count);
        foreach (var derivedId in page.DerivedAlbumIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var releasePage = await FetchReleasePageAsync(derivedId, cancellationToken).ConfigureAwait(false);
                results.Add(ToSearchItem(derivedId, releasePage));
            }
            catch (WebTagSourceException exception)
            {
                _logger.Warn($"Apple Music 反推专辑 {derivedId} 失败，已跳过：{exception.Message}");
            }
        }

        _logger.Info($"Apple Music 搜索完成：关键词 \"{term}\" 反推得到 {results.Count} 个专辑。");
        return results;
    }

    public async Task<WebTagRelease> GetReleaseAsync(string releaseId, CancellationToken cancellationToken)
    {
        if (!AppleMusicHtmlParser.TryParseReleaseId(releaseId, out var normalizedId))
        {
            throw new WebTagSourceException($"无法识别的 Apple Music 专辑链接或 ID：{releaseId}");
        }

        var page = await FetchReleasePageAsync(normalizedId, cancellationToken).ConfigureAwait(false);
        return page.Release;
    }

    public bool TryParseReleaseId(string input, out string releaseId) =>
        AppleMusicHtmlParser.TryParseReleaseId(input, out releaseId);

    public async Task TestConnectionAsync(CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.Create(_settings.Current.Network);
        using var response = await SendAsync(client, BasePageUrl, "基础页面", cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, "基础页面");
        _logger.Info("Apple Music 连接测试成功。");
    }

    private WebTagSearchItem ToSearchItem(string releaseId, AppleMusicReleasePageResult page) => new()
    {
        SourceId = Id,
        ReleaseId = releaseId,
        Title = page.Release.Title,
        Artist = page.Release.Artist,
        TrackCount = page.TrackCount,
        Year = ReadYear(page.Release.AlbumFields),
        ArtworkUrl = page.Release.ArtworkUrl,
        SourceUrl = page.Release.SourceUrl,
        ResultKind = WebTagResultKind.DerivedAlbum
    };

    private static int? ReadYear(IReadOnlyDictionary<string, string>? albumFields) =>
        albumFields is not null &&
        albumFields.TryGetValue(WebTagFieldKeys.Year, out var year) &&
        int.TryParse(year, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private async Task<AppleMusicReleasePageResult> FetchReleasePageAsync(
        string releaseId,
        CancellationToken cancellationToken)
    {
        var url = AppleMusicHtmlParser.BuildAlbumUrl(releaseId);
        var html = await FetchPageAsync(url, "专辑页", cancellationToken).ConfigureAwait(false);
        return _parser.ParseReleasePage(html, releaseId, Id);
    }

    private async Task<string> FetchPageAsync(string url, string pageLabel, CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.Create(_settings.Current.Network);
        using var response = await SendAsync(client, url, pageLabel, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, pageLabel);
        return await ReadContentAsync(response, pageLabel, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        string url,
        string pageLabel,
        CancellationToken cancellationToken)
    {
        try
        {
            return await client
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.Warn($"Apple Music 请求超时：{pageLabel}（{url}）。");
            throw new WebTagSourceException(
                $"Apple Music 请求超时（{WebTagHttpClientFactory.RequestTimeoutSeconds} 秒），请检查网络或代理设置。");
        }
        catch (HttpRequestException exception)
        {
            _logger.Warn($"Apple Music 请求失败：{pageLabel}（{url}）：{exception.Message}");
            throw new WebTagSourceException("无法访问 Apple Music，请检查网络连接或代理设置。", exception);
        }
    }

    private void EnsureSuccess(HttpResponseMessage response, string pageLabel)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var statusCode = (int)response.StatusCode;
        _logger.Warn($"Apple Music 请求失败：{pageLabel} 返回 HTTP {statusCode}。");
        if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden)
        {
            throw new WebTagSourceException(
                "Apple Music 请求过于频繁或暂时不可用，请稍后再试。",
                response.StatusCode);
        }

        throw new WebTagSourceException($"Apple Music 返回异常状态（HTTP {statusCode}），请稍后再试。", response.StatusCode);
    }

    private async Task<string> ReadContentAsync(
        HttpResponseMessage response,
        string pageLabel,
        CancellationToken cancellationToken)
    {
        var content = response.Content;
        var declaredLength = content.Headers.ContentLength;
        if (declaredLength > MaxPageBytes)
        {
            _logger.Warn($"Apple Music {pageLabel}声明长度 {declaredLength} 字节，超过 20 MB 限制。");
            throw new WebTagSourceException($"Apple Music {pageLabel}响应超过 20 MB 限制，已中止。");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(InitialReadBufferSize);
        try
        {
            using var memory = new MemoryStream(
                declaredLength is > 0 and <= int.MaxValue ? (int)declaredLength.Value : InitialReadBufferSize);
            long total = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > MaxPageBytes)
                {
                    _logger.Warn($"Apple Music {pageLabel}响应超过 20 MB 限制。");
                    throw new WebTagSourceException($"Apple Music {pageLabel}响应超过 20 MB 限制，已中止。");
                }

                memory.Write(buffer, 0, read);
            }

            return ResolveEncoding(content.Headers.ContentType).GetString(memory.ToArray());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static Encoding ResolveEncoding(MediaTypeHeaderValue? contentType)
    {
        var charset = contentType?.CharSet;
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                return Encoding.GetEncoding(charset.Trim('"', ' '));
            }
            catch (ArgumentException)
            {
                // 未知 charset 回退 UTF-8。
            }
        }

        return Encoding.UTF8;
    }
}
