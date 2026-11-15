# KB-006：多主题与浅色主题实现笔记

- 状态：已批准（Accepted，2026-11-11，owner）
- 日期：2026-11-09
- 适用范围：EchoGarage.App 的 WPF 主题

> **2026-11-16 更新**：主题体系已升级为五套（见 `docs/adr/ADR-024-multi-theme-system.md`），
> 本文件保留为“两套主题时代”的历史笔记；当前令牌清单、旧配置迁移与无人工输入验证流程见
> `docs/kb/KB-015-theme-token-map.md`。

## 结构

```
src/EchoGarage.App/Resources/
  Themes/*Theme.xaml       五套主题（2026-11-16 起；旧 Dark/Light 两字典已移除）
  Controls.xaml            控件样式，只引用颜色令牌
```

- 每个主题字典定义同一组 key：`WindowBackgroundBrush`、`PanelBackgroundBrush`、`TextPrimaryBrush`、
  `AccentBrush`、`RowSelectedBrush`、`CoverPlaceholderBrush` 等（约 30 个）。
- `App.xaml` 的 `MergedDictionaries[0]` 固定是主题字典，`[1]` 是 `Controls.xaml`。
- `ThemeManager.Apply(ThemeKind)` 直接替换第 0 个字典；`AppSettings.Theme` 持久化选择。
- 主窗口与对话框标题栏用 DWM 属性 20 / 19 跟随主题；`ThemedWindow` 在 `SourceInitialized` 时应用，
  并订阅 `ThemeManager.ThemeApplied` 以便运行中切换后更新标题栏。

## 关键点

1. **必须用 `DynamicResource` 引用主题令牌**。用 `StaticResource` 时，切换字典后控件仍持有旧 Brush，
   界面只变一半。字体、DataTemplate、样式本身可以继续用 `StaticResource`。
2. 控件样式里不要硬编码颜色（`#232A34` 之类）；所有颜色进主题字典，浅色主题才有地方覆盖。
3. 窗口 `Icon` 要用 pack URI（`/EchoGarage;component/Resources/echogarage.ico`）。
   直接写 `Resources/xxx.ico` 会按 XAML 所在目录解析，运行时抛 `IOException: 找不到资源`。
4. 运行时切换会触发大量 `DynamicResource` 重新解析；实测 9 个来源、20 列定义下无卡顿，
   但不要在 `ThemeApplied` 处理里做重建列表之类的重活。
5. 无边框 / 自绘标题栏的控件（关闭按钮等）在浅色下需要对比度检查；当前用主题令牌控制前景色。

## 验证

- 深色 / 浅色启动截图（`--screenshot`、`--screenshot-all`）。
- UI Automation：`视图 → 使用深色主题`，检查日志 `Theme switched to Dark`、`settings.json` 中
  `"theme": "Dark"`、窗口保持响应。
- 浅色下检查主窗口、设置、标签属性、文件关联、列与布局、智能播放列表对话框。
