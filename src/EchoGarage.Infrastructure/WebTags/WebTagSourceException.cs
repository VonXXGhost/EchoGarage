using System.Net;

namespace EchoGarage.Infrastructure.WebTags;

/// <summary>
/// Web 标签源的可呈现错误。<see cref="Exception.Message"/> 为中文，可直接展示给用户；
/// 由 HTTP 响应引起的错误会带上 <see cref="StatusCode"/>，便于上层区分 403 / 429 等情况。
/// </summary>
public sealed class WebTagSourceException : Exception
{
    public WebTagSourceException(string message)
        : base(message)
    {
    }

    public WebTagSourceException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    public WebTagSourceException(string message, HttpStatusCode statusCode)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public WebTagSourceException(string message, HttpStatusCode statusCode, Exception? innerException)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }

    /// <summary>引起错误的 HTTP 状态码；非 HTTP 错误（解析失败、超时等）为 null。</summary>
    public HttpStatusCode? StatusCode { get; }
}
