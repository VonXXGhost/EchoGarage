using System.Diagnostics;
using System.Net;
using EchoGarage.Core.Abstractions;
using EchoGarage.Core.Models;

namespace EchoGarage.Infrastructure.WebTags;

/// <summary>
/// App 使用的 Web 标签服务实现（ADR-022）：源注册表、按设置过滤启用源、请求路由、
/// 封面下载（校验 Content-Type 与文件头、15 MB 上限）与连接测试计时。
/// 构造过程不发起网络请求。
/// </summary>
public sealed class WebTagService : IWebTagService
{
    /// <summary>封面响应大小上限。</summary>
    public const int MaxCoverBytes = 15 * 1024 * 1024;

    private readonly IReadOnlyList<IWebTagSource> _sources;
    private readonly Dictionary<string, IWebTagSource> _sourcesById;
    private readonly ISettingsService _settings;
    private readonly WebTagHttpClientFactory _httpClientFactory;
    private readonly IAppLogger _logger;

    public WebTagService(
        IEnumerable<IWebTagSource> sources,
        ISettingsService settings,
        WebTagHttpClientFactory httpClientFactory,
        IAppLogger logger)
    {
        ArgumentNullException.ThrowIfNull(sources);

        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var unique = new List<IWebTagSource>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            if (source is null || string.IsNullOrWhiteSpace(source.Id) || !seen.Add(source.Id))
            {
                continue;
            }

            unique.Add(source);
        }

        _sources = unique;
        _sourcesById = unique.ToDictionary(source => source.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<IWebTagSource> Sources => _sources;

    public IReadOnlyList<IWebTagSource> EnabledSources
    {
        get
        {
            var enabledIds = _settings.Current?.WebTags?.EnabledSourceIds;
            if (enabledIds is { Count: > 0 })
            {
                var enabled = new List<IWebTagSource>();
                foreach (var sourceId in enabledIds)
                {
                    if (sourceId is not null &&
                        _sourcesById.TryGetValue(sourceId, out var source) &&
                        !enabled.Contains(source))
                    {
                        enabled.Add(source);
                    }
                }

                if (enabled.Count > 0)
                {
                    return enabled;
                }
            }

            // 设置里没有可用源时回退：内置 Apple Music，其次第一个注册源。
            var fallback = FindSource(WebTagSourceIds.AppleMusicJp) ?? (_sources.Count > 0 ? _sources[0] : null);
            return fallback is null ? Array.Empty<IWebTagSource>() : new[] { fallback };
        }
    }

    public string DefaultSourceId
    {
        get
        {
            var enabled = EnabledSources;
            if (enabled.Count == 0)
            {
                return string.Empty;
            }

            var configured = _settings.Current?.WebTags?.DefaultSourceId;
            if (!string.IsNullOrWhiteSpace(configured))
            {
                foreach (var source in enabled)
                {
                    if (string.Equals(source.Id, configured, StringComparison.OrdinalIgnoreCase))
                    {
                        return source.Id;
                    }
                }
            }

            return enabled[0].Id;
        }
    }

    public IWebTagSource? FindSource(string? sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            return null;
        }

        return _sourcesById.TryGetValue(sourceId.Trim(), out var source) ? source : null;
    }

    public Task<IReadOnlyList<WebTagSearchItem>> SearchAsync(
        string sourceId,
        string term,
        CancellationToken cancellationToken)
    {
        var source = RequireSource(sourceId);
        return source.SearchAsync(new WebTagSearchQuery(source.Id, term), cancellationToken);
    }

    public Task<WebTagRelease> GetReleaseAsync(
        string sourceId,
        string releaseId,
        CancellationToken cancellationToken)
    {
        var source = RequireSource(sourceId);
        return source.GetReleaseAsync(releaseId, cancellationToken);
    }

    public bool TryParseReleaseId(string sourceId, string input, out string releaseId)
    {
        releaseId = string.Empty;
        var source = FindSource(sourceId);
        return source is not null && source.TryParseReleaseId(input, out releaseId);
    }

    public async Task<WebTagCover> DownloadCoverAsync(string artworkUrl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(artworkUrl) ||
            !Uri.TryCreate(artworkUrl.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new WebTagSourceException("封面地址无效。");
        }

        using var client = _httpClientFactory.Create(_settings.Current?.Network);
        HttpResponseMessage response;
        try
        {
            response = await client
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.Warn("封面下载超时。");
            throw new WebTagSourceException("封面下载超时，请稍后再试。");
        }
        catch (HttpRequestException exception)
        {
            _logger.Warn($"封面下载失败：{exception.Message}");
            throw new WebTagSourceException("封面下载失败，请检查网络连接或代理设置。", exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var statusCode = (int)response.StatusCode;
                _logger.Warn($"封面下载失败：服务器返回 HTTP {statusCode}。");
                if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden)
                {
                    throw new WebTagSourceException(
                        "Apple Music 请求过于频繁或暂时不可用，请稍后再试。",
                        response.StatusCode);
                }

                throw new WebTagSourceException($"封面下载失败：服务器返回 HTTP {statusCode}。", response.StatusCode);
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            if (mediaType is not ("image/jpeg" or "image/png"))
            {
                _logger.Warn($"封面下载失败：Content-Type 为 {mediaType ?? "(空)"}。");
                throw new WebTagSourceException("封面响应不是有效的 JPEG/PNG 图片。");
            }

            var mime = mediaType == "image/png" ? "image/png" : "image/jpeg";
            var bytes = await ReadCoverBytesAsync(response, cancellationToken).ConfigureAwait(false);
            if (!MatchesImageSignature(bytes, mime))
            {
                _logger.Warn($"封面下载失败：文件头与 Content-Type（{mime}）不匹配。");
                throw new WebTagSourceException("封面响应不是有效的 JPEG/PNG 图片。");
            }

            _logger.Info($"封面下载成功：{mime}，{bytes.Length} 字节。");
            return new WebTagCover(bytes, mime);
        }
    }

    public async Task<WebTagConnectionTestResult> TestConnectionAsync(
        string sourceId,
        CancellationToken cancellationToken)
    {
        var source = FindSource(sourceId);
        if (source is null)
        {
            var message = $"未知的 Web 标签源：{sourceId}";
            _logger.Warn(message);
            return WebTagConnectionTestResult.Fail(message, TimeSpan.Zero);
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await source.TestConnectionAsync(cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            _logger.Info($"Web 标签源连接测试成功：{source.DisplayName}（{stopwatch.ElapsedMilliseconds} ms）。");
            return WebTagConnectionTestResult.Ok(stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (WebTagSourceException exception)
        {
            stopwatch.Stop();
            _logger.Warn($"Web 标签源连接测试失败：{source.DisplayName}：{exception.Message}");
            return WebTagConnectionTestResult.Fail(exception.Message, stopwatch.Elapsed);
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            _logger.Error($"Web 标签源连接测试异常：{source.DisplayName}。", exception);
            return WebTagConnectionTestResult.Fail($"连接测试失败：{exception.Message}", stopwatch.Elapsed);
        }
    }

    private async Task<byte[]> ReadCoverBytesAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var declaredLength = response.Content.Headers.ContentLength;
        if (declaredLength > MaxCoverBytes)
        {
            _logger.Warn($"封面响应声明长度 {declaredLength} 字节，超过 15 MB 限制。");
            throw new WebTagSourceException("封面响应超过 15 MB 限制，已中止。");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var memory = new MemoryStream(
            declaredLength is > 0 and <= MaxCoverBytes ? (int)declaredLength.Value : 64 * 1024);
        var buffer = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaxCoverBytes)
            {
                _logger.Warn("封面响应超过 15 MB 限制。");
                throw new WebTagSourceException("封面响应超过 15 MB 限制，已中止。");
            }

            memory.Write(buffer, 0, read);
        }

        return memory.ToArray();
    }

    private static bool MatchesImageSignature(ReadOnlySpan<byte> bytes, string mime) => mime switch
    {
        "image/jpeg" => bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF,
        "image/png" => bytes.Length >= 8 && bytes[..8].SequenceEqual(PngSignature),
        _ => false
    };

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private IWebTagSource RequireSource(string sourceId)
    {
        var source = FindSource(sourceId);
        if (source is null)
        {
            throw new WebTagSourceException($"未知的 Web 标签源：{sourceId}");
        }

        return source;
    }
}
