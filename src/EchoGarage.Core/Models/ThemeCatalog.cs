namespace EchoGarage.Core.Models;

/// <summary>描述一个可选界面主题，供设置窗口与「视图 → 主题」菜单共用。</summary>
/// <param name="Kind">主题枚举值。</param>
/// <param name="DisplayName">界面显示名称（中文），菜单与设置页直接展示。</param>
/// <param name="EnglishName">英文专有名称，与主题字典文件名一致；用于日志、文档与提示。</param>
/// <param name="Description">一句话说明，显示在菜单/设置项的提示中。</param>
/// <param name="IsDark">是否为深色主题；用于标题栏 DWM 属性与阴影强度等。</param>
public sealed record ThemeInfo(ThemeKind Kind, string DisplayName, string EnglishName, string Description, bool IsDark);

/// <summary>
/// 主题清单。顺序必须与 <see cref="ThemeKind"/> 的数值顺序一致，索引即菜单/设置中的顺序。
/// </summary>
public static class ThemeCatalog
{
    public static IReadOnlyList<ThemeInfo> All { get; } =
    [
        new(ThemeKind.PureStudio, "纯净极简白", "Pure Studio", "通透克制的默认浅色主题", false),
        new(ThemeKind.WarmPaper, "温润纸感白", "Warm Paper", "暖白纸张质感，长时间阅读更柔和", false),
        new(ThemeKind.NordicIce, "极简冰青白", "Nordic Ice", "冷调冰蓝，清爽利落", false),
        new(ThemeKind.MidnightVinyl, "午夜黑胶", "Midnight Vinyl", "暖色深色主题，黑胶夜色氛围", true),
        new(ThemeKind.SlateStudio, "深海蓝灰", "Slate Studio", "冷灰深色主题，沉稳专业", true)
    ];

    /// <summary>按 UI 顺序取主题；越界时归一到最近的有效值。</summary>
    public static ThemeInfo ByIndex(int index) => All[Math.Clamp(index, 0, All.Count - 1)];

    public static ThemeInfo Get(ThemeKind kind) => All[(int)kind];

    public static bool IsDark(ThemeKind kind) => Get(kind).IsDark;

    public static string DisplayName(ThemeKind kind) => Get(kind).DisplayName;

    public static string EnglishName(ThemeKind kind) => Get(kind).EnglishName;

    /// <summary>把主题枚举换算为 UI 索引。</summary>
    public static int IndexOf(ThemeKind kind) => All[(int)kind].Kind == kind ? (int)kind : 0;
}
