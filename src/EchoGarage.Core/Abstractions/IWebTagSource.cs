using EchoGarage.Core.Models;

namespace EchoGarage.Core.Abstractions;

/// <summary>Web 标签源适配器：搜索、取详情、识别专辑链接/ID。实现不得引用 WPF。</summary>
public interface IWebTagSource
{
    string Id { get; }
    string DisplayName { get; }
    WebTagSourceCapabilities Capabilities { get; }

    Task<IReadOnlyList<WebTagSearchItem>> SearchAsync(
        WebTagSearchQuery query,
        CancellationToken cancellationToken);

    Task<WebTagRelease> GetReleaseAsync(
        string releaseId,
        CancellationToken cancellationToken);

    /// <summary>尝试把用户输入识别为专辑链接/ID；不匹配返回 false。</summary>
    bool TryParseReleaseId(string input, out string releaseId);

    /// <summary>设置页“测试连接”：请求源的基础页面并验证可访问。</summary>
    Task TestConnectionAsync(CancellationToken cancellationToken);
}

/// <summary>App 使用的跨层 Web 标签服务：源注册表、搜索/详情、封面下载、连接测试。</summary>
public interface IWebTagService
{
    IReadOnlyList<IWebTagSource> Sources { get; }

    /// <summary>按当前设置过滤后的启用源；至少返回一个内置源。</summary>
    IReadOnlyList<IWebTagSource> EnabledSources { get; }

    string DefaultSourceId { get; }

    IWebTagSource? FindSource(string? sourceId);

    Task<IReadOnlyList<WebTagSearchItem>> SearchAsync(
        string sourceId,
        string term,
        CancellationToken cancellationToken);

    Task<WebTagRelease> GetReleaseAsync(
        string sourceId,
        string releaseId,
        CancellationToken cancellationToken);

    Task<WebTagCover> DownloadCoverAsync(
        string artworkUrl,
        CancellationToken cancellationToken);

    bool TryParseReleaseId(string sourceId, string input, out string releaseId);

    Task<WebTagConnectionTestResult> TestConnectionAsync(
        string sourceId,
        CancellationToken cancellationToken);
}
