namespace EchoGarage.Core.Models;

/// <summary>网络代理设置（ADR-022）。</summary>
public sealed class NetworkSettings
{
    public WebProxyMode ProxyMode { get; set; } = WebProxyMode.System;

    /// <summary>自定义 HTTP/HTTPS 代理地址，例如 http://127.0.0.1:7890。</summary>
    public string CustomProxyUrl { get; set; } = string.Empty;

    public NetworkSettings Clone() => new()
    {
        ProxyMode = ProxyMode,
        CustomProxyUrl = CustomProxyUrl
    };

    /// <summary>校验并规范化；非法自定义代理回退为系统代理。</summary>
    public static NetworkSettings Normalize(NetworkSettings? settings)
    {
        var result = settings ?? new NetworkSettings();
        if (!Enum.IsDefined(result.ProxyMode))
        {
            result.ProxyMode = WebProxyMode.System;
        }

        result.CustomProxyUrl = (result.CustomProxyUrl ?? string.Empty).Trim();
        if (result.ProxyMode == WebProxyMode.Custom)
        {
            if (!TryNormalizeProxyUrl(result.CustomProxyUrl, out var normalized))
            {
                result.ProxyMode = WebProxyMode.System;
                result.CustomProxyUrl = string.Empty;
            }
            else
            {
                result.CustomProxyUrl = normalized;
            }
        }

        return result;
    }

    public static bool TryNormalizeProxyUrl(string? text, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(text) ||
            !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        normalized = uri.ToString().TrimEnd('/');
        return true;
    }

    /// <summary>返回当前设置对应的代理 URI；System/Direct 返回 null，由调用方决定是否使用系统代理。</summary>
    public Uri? GetProxyUri() =>
        ProxyMode == WebProxyMode.Custom && TryNormalizeProxyUrl(CustomProxyUrl, out var normalized)
            ? new Uri(normalized)
            : null;
}

/// <summary>Web 标签设置：启用源、默认源、字段勾选记忆（ADR-022）。</summary>
public sealed class WebTagSettings
{
    public List<string> EnabledSourceIds { get; set; } = [WebTagSourceIds.AppleMusicJp];
    public string DefaultSourceId { get; set; } = WebTagSourceIds.AppleMusicJp;

    /// <summary>sourceId -> 已勾选字段 key；显式空列表表示用户全部取消勾选。</summary>
    public Dictionary<string, List<string>> SelectedFields { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public WebTagSettings Clone() => new()
    {
        EnabledSourceIds = [.. EnabledSourceIds],
        DefaultSourceId = DefaultSourceId,
        SelectedFields = SelectedFields.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToList(),
            StringComparer.OrdinalIgnoreCase)
    };

    public IReadOnlyList<string> GetSelectedFields(string? sourceId)
    {
        if (sourceId is not null &&
            SelectedFields.TryGetValue(sourceId, out var selected))
        {
            return WebTagFieldCatalog.NormalizeSelection(selected);
        }

        return WebTagFieldCatalog.GetDefaultsForSource(sourceId);
    }

    public void SetSelectedFields(string sourceId, IEnumerable<string> fields)
    {
        SelectedFields[sourceId] = WebTagFieldCatalog.NormalizeSelection(fields).ToList();
    }

    public static WebTagSettings Normalize(WebTagSettings? settings)
    {
        var result = settings ?? new WebTagSettings();
        result.EnabledSourceIds = NormalizeSourceIds(result.EnabledSourceIds);
        if (result.EnabledSourceIds.Count == 0)
        {
            result.EnabledSourceIds = [WebTagSourceIds.AppleMusicJp];
        }

        if (!WebTagSourceIds.IsKnown(result.DefaultSourceId) ||
            !result.EnabledSourceIds.Contains(result.DefaultSourceId, StringComparer.OrdinalIgnoreCase))
        {
            result.DefaultSourceId = result.EnabledSourceIds[0];
        }

        var normalizedFields = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (result.SelectedFields is not null)
        {
            foreach (var (sourceId, fields) in result.SelectedFields)
            {
                if (!WebTagSourceIds.IsKnown(sourceId))
                {
                    continue;
                }

                // 显式空列表保留，表示用户明确取消全部勾选。
                normalizedFields[sourceId] = WebTagFieldCatalog.NormalizeSelection(fields).ToList();
            }
        }

        result.SelectedFields = normalizedFields;
        return result;
    }

    private static List<string> NormalizeSourceIds(IEnumerable<string>? sourceIds)
    {
        var result = new List<string>();
        if (sourceIds is null)
        {
            return result;
        }

        foreach (var sourceId in sourceIds)
        {
            if (WebTagSourceIds.IsKnown(sourceId) &&
                !result.Contains(sourceId, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(sourceId);
            }
        }

        return result;
    }
}
