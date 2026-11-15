# KB-015（提案）：五套主题的令牌清单、迁移与验证手册

- 状态：**Accepted**（owner 批准：2026-11-16）
- 日期：2026-11-15
- 关联：ADR-024（提案）、KB-006（旧两主题笔记，本文件生效后建议在 KB-006 顶部标注“五主题版本见 KB-015”）

## 1. 主题文件与切换链路

- 5 个字典：`src/EchoGarage.App/Resources/Themes/{PureStudio,WarmPaper,NordicIce,MidnightVinyl,SlateStudio}Theme.xaml`。
- `ThemeManager.Apply(ThemeKind)`：pack URI 换 `Application.Resources.MergedDictionaries[0]`；
  `Current` / `IsDark` 供标题栏 DWM 属性（20，失败回退 19）与阴影强度使用。
- 切换入口：`视图 → 主题` 二级菜单、设置页「外观」下拉，两者都落到 `MainViewModel.ThemeIndex`
  → `Settings.Theme` → `QueueSettingsSave()`；状态栏提示“界面主题：<DisplayName>”。
- 菜单勾选由 `MainWindow.xaml.cs:UpdateThemeMenuChecks()` 在 `ThemeManager.ThemeApplied` 之后同步，
  `Tag` 存索引字符串，`ThemeCatalog.IndexOf(Current)` 比对；启动时（构造函数末尾）也同步一次。

## 2. 令牌分组（每个主题字典都必须包含全部 57 个键）

| 组 | 键 |
| --- | --- |
| 背景 | `WindowBackgroundBrush` `ToolbarBackgroundBrush` `SidebarBackgroundBrush` `MainBackgroundBrush` `PanelBackgroundBrush` `TransportBackgroundBrush` `HeaderBackgroundBrush` `PopupBackgroundBrush` `PanelAltBackgroundBrush` `SearchBackgroundBrush` `GroupHeaderBackgroundBrush` |
| 分隔/描边 | `BorderBrush` `RowSeparatorBrush` `BorderStrongBrush` `ControlHoverBorderBrush` `CoverStrokeBrush` |
| 文字/导航 | `TextPrimaryBrush` `TextSecondaryBrush` `TextMutedBrush` `NavIconBrush` `AlbumGroupForegroundBrush` |
| 强调/交互 | `AccentBrush` `AccentHoverBrush` `AccentPressedBrush` `AccentSoftBrush` `AccentContrastBrush` `CheckGlyphBrush` `RowHoverBrush` `RowSelectedBrush` `PlayingRowBrush` `PlayingLeftBarBrush` `PlayingTitleBrush` `SidebarHoverBrush` `SidebarSelectedBackgroundBrush` `SidebarSelectedForegroundBrush` |
| 控件 | `SliderTrackBrush` `ScrollBarThumbBrush` `ScrollBarThumbHoverBrush` `ProgressTrackBrush` `CoverPlaceholderBrush` `CoverBoxBrush` `EmptyGlyphBrush` `CoverNoteBrush` `DangerBrush` |
| 波形 | `WaveformLowBrush` `WaveformMidBrush` `WaveformHighBrush` `WaveformTrackBackgroundBrush` `WaveformPlayheadBrush` |
| 频谱 | `SpectrumBarBottomBrush` `SpectrumBarTopBrush` `SpectrumPeakBrush` `SpectrumGridBrush` `SpectrumLabelBrush` |
| 字体/阴影 | `UiFont` `MonoFont` `PopupShadow`（后三者用 `StaticResource`，其余均 `DynamicResource`） |

### 2.1 波形三色带配色（2026-11-16 owner 二次调整后）

原则：低频=高能量（暖色）、中频=过渡（金/绿）、高频=低能量（冷色）；三带色相分离、分段不透明，
不再使用高饱和纯色（原 `#F43F5E`/`#14B8A6`/`#2563EB` 观感过于极端）。

| 主题 | 低频（高能量） | 中频（中能量） | 高频（轻能量） | 播放指针 |
| --- | --- | --- | --- | --- |
| 纯净极简白 Pure Studio | `#C45C4B` 暖赤陶红 | `#E3AE52` 柔麦芽金 | `#3572DC` 沉稳蔚蓝 | `#0F172A` |
| 温润纸感白 Warm Paper | `#C45C4B` 暖赤陶红 | `#DFA648` 麦芽琥珀 | `#2B8A9C` 莫兰迪青蓝 | `#1C1917` |
| 极简冰青白 Nordic Ice | `#CF636C` 柔玫瑰红 | `#47A388` 薄荷鼠尾草 | `#2E74C7` 冰海蓝 | `#0F172A` |
| 午夜黑胶 Midnight Vinyl | `#DC5A5A` 柔暗红 | `#DDA03B` 爵士琥珀金 | `#56B4E9` 天青蓝 | `#FFFFFF` |
| 深海蓝灰 Slate Studio | `#E07A48` 暖陶橙 | `#4EB888` 柔翠绿 | `#4FA8E8` 晴空蓝 | `#F8FAFC` |

令牌：`WaveformLowBrush` / `WaveformMidBrush` / `WaveformHighBrush` / `WaveformPlayheadBrush`
（+ `WaveformTrackBackgroundBrush`）。配色表同时写在生成脚本 `.scratch/generate-themes.ps1`；
手工改配色时两处都要改，避免重新生成时回退。

### 2.2 主题显示名

界面显示中文名，内部/日志保留英文 id：

| 枚举 | 中文显示名 | 英文专有名称 |
| --- | --- | --- |
| PureStudio | 纯净极简白 | Pure Studio |
| WarmPaper | 温润纸感白 | Warm Paper |
| NordicIce | 极简冰青白 | Nordic Ice |
| MidnightVinyl | 午夜黑胶 | Midnight Vinyl |
| SlateStudio | 深海蓝灰 | Slate Studio |

菜单项 `Header` 用中文名、`ToolTip` 用中文说明（界面不出现英文主题名）；设置页下拉直接用 `ThemeCatalog.DisplayName`。`EnglishName` 只用于日志与文档。
`settings.json` 里写 canonical 枚举名（`PureStudio` 等），不写中文，避免重命名影响配置兼容。

约定：
- `AccentContrastBrush` = 主播放按钮/选中强调上的前景图标色。四套主题为白色，**Midnight Vinyl 为深色 `#121316`**
  （琥珀底上白字对比不足），因此 `PlayButton.Foreground` 与 `CheckGlyphBrush` 必须走令牌，不能写死白色。
- 弹层（菜单/ComboBox/ContextMenu/ToolTip）用 `PopupBackgroundBrush`；`PanelAltBackgroundBrush` 保留给对话框卡片。
- 搜索框（含设置页 TextBox）背景用 `SearchBackgroundBrush`，在浅色主题里比卡片底色略亮/纯白。
- 播放行左侧 3px 条用 `PlayingLeftBarBrush`，由 `DataGridRow` / `GroupedTrackItem` 模板内的
  `PlayingBar` Border + `DataTrigger Binding="{Binding IsPlaying}"` 控制 Visibility。
- 封面圆角 4–6px + 1px `CoverStrokeBrush`：列表缩略图 24px（圆角 4）、分组封面（圆角 6）、
  右栏大封面（圆角 6）；小尺寸不要用 6px，会吃掉内容。

## 3. 新增/修改令牌的检查清单

1. 5 个主题字典同时加键（缺一个主题会在运行时 `DynamicResource` 解析失败、控件透明）。
2. 运行令牌覆盖校验：扫描 `src/EchoGarage.App/**/*.xaml` 的 `{DynamicResource X}`，与 5 个字典的 `x:Key` 求差集，必须为空。
3. 明暗两套各截一张图（`--screenshot`），确认新令牌在浅色/深色下都有足够对比。
4. 若新令牌进入 `Controls.xaml` 样式，检查 `StaticResource`/`DynamicResource` 选择：主题会运行时替换字典，
   凡主题令牌都用 `DynamicResource`。

## 4. 旧配置迁移

- `ThemeKindJsonConverter` 必须在 `JsonDefaults.Converters` 中排在 `JsonStringEnumConverter` 之前
  （先匹配者胜）：`"Light"`→PureStudio、`"Dark"`→SlateStudio、canonical 名称大小写不敏感、数字 0–4、
  未知值→PureStudio。
- 不要指望 `JsonStringEnumConverter` 处理旧名；未知枚举名会抛 `JsonException`，
  而 `JsonSettingsService.Load` 捕获后会备份并重置全部设置为默认值。
- 回归测试在 `tests/EchoGarage.Tests/SettingsTests.cs`：旧名迁移、未知值兜底、5 主题往返、目录顺序。

## 5. 验证流程（无人工输入）

1. 复制 UAT 数据到 `.scratch/ui-verify/data-root`（只读复制，勿改 `D:\Temp\EchoGarage-UAT`），
   在副本 `config/settings.json` 里设置 `"theme"`、`"volume": 0`、`"autoScanOnStartup": false`。
2. `$env:ECHOGARAGE_DATA_DIR=<副本>` 后运行 Debug exe：
   - 主界面：`Start-Process -FilePath $exe -ArgumentList "--play-file `"<track>`" --screenshot `"<png>`""`
     —— 参数必须 **一个完整字符串** 传入，`-ArgumentList @(...)` 会把含空格路径拆散导致截图落错位置。
   - 波形：`--waveform <file> <png>`（会定位到 40% 后截图）。
3. 菜单切换用 UI Automation：`AutomationElement.FromHandle` → 找 `视图` MenuItem →
   `ExpandCollapsePattern.Expand` → 找 `主题` → Expand → 找目标主题名 → `InvokePattern.Invoke`；
   断言 `settings.json` 的 `theme` 与日志 `Theme switched to <Kind> (<DisplayName>)`。
   UIA 只展开菜单、不注入键鼠，不需要额外通知 owner。
4. 像素抽查：播放行左侧条应为 3px 宽、与行内容起始对齐；状态列 ▶ 应与行垂直居中。


