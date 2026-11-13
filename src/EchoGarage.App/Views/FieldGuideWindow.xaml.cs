using System.Text;
using System.Windows;
using System.Windows.Input;
using EchoGarage.Core.Services;

namespace EchoGarage.App.Views;

/// <summary>
/// In-app reference for the display-related field names: secondary-row placeholders, the
/// available columns, and the rules for sorting/grouping. The body is a read-only TextBox so
/// the owner can select and copy any part of it without leaving the player.
/// </summary>
public partial class FieldGuideWindow : ThemedWindow
{
    public FieldGuideWindow()
    {
        InitializeComponent();
        ContentBox.Text = BuildGuide();
    }

    private static string BuildGuide()
    {
        var builder = new StringBuilder();

        builder.AppendLine("一、次要信息占位符（用于“列与布局 → 次行格式”）");
        builder.AppendLine("在列的“次行格式”输入框里填占位符，列表行会在主行下方显示对应内容；留空或值本身为空时不显示该行。");
        builder.AppendLine("只要任一列填了格式，“列与布局”里的总开关会自动启用；封面 / 状态 / # 三列不显示次行。");
        builder.AppendLine();
        foreach (var token in SecondaryTextFormat.Tokens)
        {
            builder.Append("  {").Append(token.Key).Append('}');
            builder.Append(' ', Math.Max(1, 20 - token.Key.Length));
            builder.AppendLine(token.Label);
        }

        builder.AppendLine();
        builder.AppendLine("可以和其他文本混排，例如：{bitrate}/{sampleRate} → 320K/44.1K");
        builder.AppendLine("某个值为空时会自动省略并清理多余的分隔符（采样率缺失时显示 320K）；未知占位符按空处理；");
        builder.AppendLine("需要输出花括号本身时写 {{ 或 }}。");
        builder.AppendLine();

        builder.AppendLine("二、显示列（在“列与布局”中勾选显示；排序可以选用未显示的列）");
        builder.AppendLine();
        foreach (var column in ColumnCatalog.All)
        {
            builder.Append("  ").Append(column.Header).Append('（').Append(column.Key).Append('）');
            builder.AppendLine();
        }

        builder.AppendLine();
        builder.AppendLine("三、排序与分组");
        builder.AppendLine("按“专辑”或“专辑艺术家”排序时显示按专辑分组的列表，即使这两列没有显示：分组标题仍然展示专辑、专辑艺术家与年份。");
        builder.AppendLine("“列与布局 → 排序”里可以选用未显示的列；开启“按专辑分组”时，如果当前排序不是专辑方向，会自动切换为按专辑排序。");
        return builder.ToString();
    }

    private void OnCopyAll(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(ContentBox.Text);
            CopyHint.Text = "已复制到剪贴板";
        }
        catch (Exception)
        {
            CopyHint.Text = "复制失败，请手动选中复制";
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }
}
