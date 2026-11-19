# ADR-024：五套主题体系与单配置切换

- 状态：**Accepted**（owner 批准：2026-11-16）
- 日期：2026-11-15
- 关联：ADR-001（平台与栈，其“两套主题”的描述已同步修订）、KB-006（多主题实现笔记）、KB-003（WPF 主题坑）、KB-006（五主题令牌手册）

## 1. 背景

当前只有 `LightTheme.xaml` / `DarkTheme.xaml` 两套字典，`ThemeKind` 只有 `Light` / `Dark` 两个值；
`视图` 菜单里是两个平铺项（使用浅色主题 / 使用深色主题），设置页下拉只有“浅色 / 深色”。

Owner 要求（2026-11-15）：重新设计软件配色，落地 5 套主题（Pure Studio、Warm Paper、Nordic Ice、
Midnight Vinyl、Slate Studio），通过**单一配置项**切换；`视图` 菜单把两个主题项合并为一个「主题」
二级子菜单；同时按新配色补齐波形三色带、封面圆角/描边、播放行左侧强调条、浅色主题细分割线等视觉规范。

## 2. 决策

### 2.1 领域模型（Core）

- `ThemeKind`：`PureStudio = 0`（默认）、`WarmPaper = 1`、`NordicIce = 2`、`MidnightVinyl = 3`、`SlateStudio = 4`。
  枚举顺序即菜单/下拉顺序，持久化到 `settings.json` 的 `theme` 字段（字符串，canonical 名称）。
- 新增 `ThemeCatalog`（`src/EchoGarage.Core/Models/ThemeCatalog.cs`）：唯一来源，提供
  `ThemeInfo(Kind, DisplayName, EnglishName, Description, IsDark)` 列表、`ByIndex`、`Get`、`IsDark`、`DisplayName`、`IndexOf`。`DisplayName` 是**中文界面名**（纯净极简白 / 温润纸感白 / 极简冰青白 / 午夜黑胶 / 深海蓝灰），`EnglishName` 保留英文专有名称用于日志与文档。
  界面层不硬编码主题名称与明暗属性。
- 旧配置兼容：新增 `ThemeKindJsonConverter`（`JsonConverter<ThemeKind>`），在 `JsonDefaults.Converters`
  中注册在 `JsonStringEnumConverter` **之前**。读取规则：`"Light"`→PureStudio（旧默认浅色），
  `"Dark"`→SlateStudio（旧深色最近邻），canonical 名称大小写不敏感，数字 0–4，其它一律 PureStudio。
  目的：旧 `settings.json` 不能因未知枚举名抛 `JsonException`——该异常会让 `JsonSettingsService`
  备份并重置**全部**设置。
- `JsonSettingsService.Normalize` 的兜底值改为 `ThemeKind.PureStudio`；`schemaVersion` 不变（1），
  因为只是枚举取值变化 + 读时迁移，无表结构变化。

### 2.2 主题字典与令牌

- `ThemeManager`：`Dictionary<ThemeKind, Uri>` 映射 5 个 pack URI
  `Resources/Themes/{PureStudio,WarmPaper,NordicIce,MidnightVinyl,SlateStudio}Theme.xaml`，
  `Apply` 语义不变（替换 `Application.Resources.MergedDictionaries[0]`）；`IsDark` 由 `ThemeCatalog` 决定。
- `App.xaml` 的 `MergedDictionaries[0]` 指向 `PureStudioTheme.xaml`（启动即默认主题）。
- 每个主题字典一份，**只定义 Brush（不要求 Color 中间层）**，共 57 个键，分 8 组：
  背景（窗口/工具栏/侧栏/主区/右栏/传输条/表头/弹层/搜索框/分组头）、分隔线与描边、文字与导航、
  强调与交互（含播放行背景/左侧条/播放行标题、侧栏三态）、控件占位与滚动条、波形三色带+轨道+播放头、
  频谱 5 色、`UiFont` / `MonoFont` / `PopupShadow`。
- 所有控件/视图继续只通过 `DynamicResource` 取色（AGENTS.md §4 不变）；新增主题 = 新增一个字典文件，
  并补齐同一组键。`WaveformPlayedOverlayBrush` 按 ADR-015 修订提案删除。
- 明暗主题的边界：工具栏/侧栏/传输条允许与主区不同色（Pure Studio 的白色主区 vs 浅灰工具栏），
  因此新增 `ToolbarBackgroundBrush`、`SidebarBackgroundBrush`、`MainBackgroundBrush`、
  `TransportBackgroundBrush`、`HeaderBackgroundBrush` 等独立令牌，不再复用 `PanelBackgroundBrush`。

### 2.3 界面接入

- `视图` 菜单：两项主题开关合并为 `主题` 子菜单，第二级列出 5 个可勾选项
  （`Header` 用中文主题名，`ToolTip` 为中文说明，`Tag` 为索引，`Command=SetThemeCommand`，`CommandParameter` 为索引字符串；界面不展示英文主题名）。
  勾选状态由 `ThemeManager.ThemeApplied` 事件驱动，代码隐藏里按 `ThemeCatalog.IndexOf(Current)` 同步，
  避免只依赖菜单点击路径。
- 设置页「外观」卡片：下拉 `ItemsSource` 改为 `ThemeCatalog.All.DisplayName`（5 个中文名），
  `SelectedIndex` 绑定 `ThemeIndex`（= 枚举值）；帮助文案说明默认浅色与两套深色主题。
- 主题切换仍是即时生效 + 持久化 + 状态栏提示；窗口标题栏 DWM 属性继续按 `IsDark` 走 20/19 号属性。

## 3. 后果

- 好处：配色与语义解耦；5 套主题与控件样式解耦；新增主题成本 = 1 个字典 + 1 个枚举值 + 1 条目录记录；
  旧配置平滑迁移不丢设置。
- 代价：主题字典键从 ~30 个增加到 57 个，新增控件令牌需要在 5 个文件同时补键。
  为此增加**令牌覆盖校验**（脚本/测试）：扫描 `src/EchoGarage.App/**/*.xaml` 中的
  `{DynamicResource X}`，断言每个键在 5 个主题字典中都存在。当前校验结果：54 个被引用键、每个主题 57 个键、
  0 缺失（`UiFont`/`MonoFont`/`PopupShadow` 用 `StaticResource`）。
- 删除 `LightTheme.xaml` / `DarkTheme.xaml`；文档层旧引用（ADR-001、ADR-015、KB-006、README 截图说明）
  由对应提案/后续文档更新处理，不在本提案里直接改 ADR。

## 4. 备选方案

1. 保留 Light/Dark 再叠加 3 套：语义不成立（新旧主题名字对不上），且菜单/下拉仍要特判，否决。
2. 主题 JSON/CSS 外部化 + 运行时解析：启动成本与校验成本高，且无法在编译期保证控件令牌完整性，否决。
3. 令牌继承（base + override 字典）：5 套差异较大，继承带来的隐式覆盖更难审查，当前规模不值得，否决。

## 5. 验证

- 构建：`dotnet build EchoGarage.sln -nodeReuse:false -p:UseSharedCompilation=false` → 0 warning / 0 error。
- 测试：476 项全部通过；新增 4 项：旧主题名迁移（Light/Dark）、未知主题兜底、5 主题保存/读取往返、
  目录顺序与 `IsDark` 断言。
- 界面：5 套主题各截主界面 + 波形模式截图（1696x968），逐套核对配色；设置页与深/浅色对话框各截一组。
- 交互：UI Automation 展开 `视图 → 主题 → 午夜黑胶` 并 Invoke，`settings.json` 写入
  `"theme": "MidnightVinyl"`，日志出现 `Theme switched to MidnightVinyl (午夜黑胶)`。
- 波形：5 套主题截图确认三色带色相分离、已播放区约 26% 不透明度、未播放区 100%。
- 波形配色在 2026-11-16 按 owner 反馈二次调整（原纯度较高、观感极端），改为低饱和赤陶/麦金/蔚蓝体系，
  具体色值见 KB-006 §2.1。




