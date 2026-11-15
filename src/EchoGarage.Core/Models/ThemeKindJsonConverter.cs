using System.Text.Json;
using System.Text.Json.Serialization;

namespace EchoGarage.Core.Models;

/// <summary>
/// 主题枚举的 JSON 转换器。旧配置里的 "Light"/"Dark" 分别映射到 Pure Studio / Slate Studio，
/// 避免升级后反序列化失败导致整份设置被重置为默认值。未知取值一律回落到默认主题。
/// </summary>
public sealed class ThemeKindJsonConverter : JsonConverter<ThemeKind>
{
    public override ThemeKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return TryParse(reader.GetString(), out var kind) ? kind : ThemeKind.PureStudio;
        }

        if (reader.TokenType == JsonTokenType.Number
            && reader.TryGetInt32(out var value)
            && Enum.IsDefined(typeof(ThemeKind), value))
        {
            return (ThemeKind)value;
        }

        return ThemeKind.PureStudio;
    }

    public override void Write(Utf8JsonWriter writer, ThemeKind value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }

    /// <summary>
    /// 解析主题名。支持 5 个正式名称、"Light"/"Dark" 两个历史名称，大小写不敏感。
    /// </summary>
    public static bool TryParse(string? name, out ThemeKind kind)
    {
        kind = ThemeKind.PureStudio;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        // 历史配置兼容：旧版本只有浅色/深色两种。
        if (name.Equals("Light", StringComparison.OrdinalIgnoreCase))
        {
            kind = ThemeKind.PureStudio;
            return true;
        }

        if (name.Equals("Dark", StringComparison.OrdinalIgnoreCase))
        {
            kind = ThemeKind.SlateStudio;
            return true;
        }

        if (Enum.TryParse<ThemeKind>(name, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
        {
            kind = parsed;
            return true;
        }

        return false;
    }
}
