using System.Net;
using EchoGarage.Core.Models;

namespace EchoGarage.Infrastructure.WebTags;

/// <summary>
/// 按 ADR-022 创建访问 Web 标签源的 <see cref="HttpClient"/>：
/// 统一解压、超时、浏览器 User-Agent、语言与代理策略；页面抓取与封面下载共用。
/// 每次请求创建并释放一个 HttpClient（Web 标签是低频操作，优先保证设置即时生效）。
/// </summary>
public sealed class WebTagHttpClientFactory
{
    /// <summary>建立 TCP/TLS 连接的超时（秒）。</summary>
    public const int ConnectTimeoutSeconds = 15;

    /// <summary>单次请求的整体超时（秒）。</summary>
    public const int RequestTimeoutSeconds = 20;

    /// <summary>常见桌面浏览器 UA，避免 Apple Music 返回简化页面。</summary>
    public const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    /// <summary>语言偏好：日文优先，便于拿到日本区日文标题。</summary>
    public const string AcceptLanguage = "ja-JP,ja;q=0.9";

    private const string AcceptHeader = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";

    private readonly Func<HttpMessageHandler>? _handlerFactory;

    public WebTagHttpClientFactory()
    {
    }

    /// <summary>测试用构造函数：注入自定义 handler（例如 FakeHttpMessageHandler），取代 <see cref="SocketsHttpHandler"/>。</summary>
    public WebTagHttpClientFactory(Func<HttpMessageHandler> handlerFactory)
    {
        _handlerFactory = handlerFactory ?? throw new ArgumentNullException(nameof(handlerFactory));
    }

    /// <summary>
    /// 按当前网络设置创建 <see cref="SocketsHttpHandler"/>。<paramref name="settings"/> 为 null 时按系统代理处理。
    /// </summary>
    /// <exception cref="ArgumentException">自定义代理地址不是合法的 http/https URL。</exception>
    public SocketsHttpHandler CreateHandler(NetworkSettings? settings)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(ConnectTimeoutSeconds),
            UseCookies = false
        };

        switch (settings?.ProxyMode ?? WebProxyMode.System)
        {
            case WebProxyMode.Direct:
                handler.UseProxy = false;
                break;

            case WebProxyMode.Custom:
                var customProxyUrl = settings?.CustomProxyUrl ?? string.Empty;
                if (!NetworkSettings.TryNormalizeProxyUrl(customProxyUrl, out var normalizedProxyUrl))
                {
                    throw new ArgumentException(
                        $"自定义代理地址无效：\"{customProxyUrl}\"。请输入形如 http://127.0.0.1:7890 的 HTTP/HTTPS 代理。",
                        nameof(settings));
                }

                handler.UseProxy = true;
                handler.Proxy = new WebProxy(normalizedProxyUrl);
                break;

            default:
                // 交给 HttpClient.DefaultProxy（系统 / 环境变量代理）。
                handler.UseProxy = true;
                handler.Proxy = null;
                break;
        }

        return handler;
    }

    /// <summary>创建一次请求使用的 <see cref="HttpClient"/>；调用方负责 Dispose。</summary>
    public HttpClient Create(NetworkSettings? settings)
    {
        var handler = _handlerFactory?.Invoke() ?? CreateHandler(settings);
        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(RequestTimeoutSeconds)
        };

        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", AcceptLanguage);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", AcceptHeader);
        return client;
    }
}
