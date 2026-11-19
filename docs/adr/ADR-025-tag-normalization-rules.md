# ADR-025：标签编辑辅助：规范化正则规则与候选值

- 状态：Accepted（2026-11-16 批准；2026-11-19 并入原 ADR-027，该 ADR 于 2026-11-17 批准）
- 日期：2026-11-16（2026-11-19 并入原 ADR-027）
- 关联：ADR-013（作曲家列）、ADR-019（标签编辑页与额外标签展示）、AGENTS.md §5（文件是唯一真相）、
  §7（测试要求）
- 影响范围：`EchoGarage.Core` 领域模型与纯逻辑 + `EchoGarage.Infrastructure` 设置持久化、候选值仓储 +
  `EchoGarage.App` 界面接线（已实现）+ 单元测试；不新增第三方依赖，不修改 SQLite schema

## 1. 背景

标签编辑页目前只能逐字段手工修改。用户经常遇到两类重复劳动：

1. 专辑名带数字发行版后缀，例如 `Demo Album A - EP`、`Demo Album A – Single`；
   后缀的破折号字符（`-`、`–`、`—`、`－`、`−` 等）与大小写不统一。
2. 某些文件把日期写成完整时间戳，例如 `2026-01-01T00:00:00`、`2026-01-01 00:00:00.000Z`、
   `2026-01-01T00:00:00+08:00`，而用户只需要 `2026-01-01`。

Owner 需求（2026-11-16）：**在配置里保存自定义正则“快捷规范化”规则，对歌曲特定 tag 规范化**；
默认提供两条规则（专辑名 EP / Single 后缀、日期时间只保留日期），并允许用户自行增删改。
本 ADR 首版（原 ADR-025）落地 Core / Infrastructure + 测试与设置持久化；设置页规则编辑、
标签编辑页“快捷规范化”入口等界面接线随后的任务完成，现已全部落地（见 2.8，实现笔记 KB-018）。

同时，手动编辑歌曲标签时，艺术家、专辑艺术家、作曲家、流派这些字段的值通常在媒体库里已经出现过
（例如同一艺术家的多张专辑、同一批文件的流派）。此前只能纯手输，容易产生 `Demo Artist` /
`demo artist` / `Demo Artist ` 这类近重复值，既慢又不一致。
希望：**在手动填写这四个字段时，自动列出媒体库里其它歌曲使用过的同字段值作为候选项**；
其余字段（标题、专辑、年份、音轨号、碟片号、备注）保持现状，不提供候选。

候选值部分的约束与风险：

- 媒体库可能很大（数万首），候选查询不能在打开标签窗口时卡住 UI，也不能每敲一个键就查一次数据库；
- 文件是唯一真相，但候选只用于输入辅助，不需要实时精确到文件变化的那一秒；
- 不新增数据库索引（避免拖慢扫描写入），不新增第三方依赖。

## 2. 决策

### 2.1 规范化规则领域模型（Core）

新增文件 `src/EchoGarage.Core/Models/TagNormalization.cs`（namespace `EchoGarage.Core.Models`），
包含四个公开类型，均为纯逻辑、不依赖 WPF / TagLib / SQLite：

- `TagNormalizationRule`（sealed class，可 JSON 序列化）：
  - `string Id`：稳定标识（默认 `Guid.NewGuid().ToString("N")`），用于 UI 列表身份与去重；
  - `string Name`：显示名称；归一化时为空会补 `规范化规则 N`；
  - `string TargetField`：`TagFieldKeys` 之一（`title` / `artist` / `album` / `albumArtist` /
    `composer` / `genre` / `year` / `track` / `disc` / `comment`），或 `TagNormalization.AnyField`（`"*"`）；
  - `string Pattern`：.NET 正则，首尾空白在归一化时去除；
  - `string Replacement`：替换文本，空字符串表示删除匹配内容，支持 `$1`、`${name}` 反向引用；
  - `bool Enabled`：禁用规则保留配置但不参与应用；
  - `TagNormalizationRule Clone()`：深拷贝（规则无嵌套可变对象）。
- `TagNormalizationDefaults`（static）：
  - `string AlbumEpSingleSuffixRuleId` / `IsoDateTimeRuleId`：内置规则 id；
  - `List<TagNormalizationRule> Create()`：每次调用返回**全新实例**的默认规则列表，
    不存在共享可变静态集合。
- `TagNormalizationResult`（sealed class）：一次 `ApplyToTag` 的实际变化集合
  - `IReadOnlyDictionary<string,string> Fields`：常用字段新值（key 为 `TagFieldKeys`）；
  - `IReadOnlyList<AudioTagChange> AdditionalTagChanges`：额外标签变更（`Value` 必非空）；
  - `bool HasChanges` / `int Count`；
  - `void ApplyTo(TagBatchEdit edit)`：把变化合并进现有批量编辑模型
    （`edit.Fields[...] = value` + `edit.AdditionalTagChanges.AddRange(...)`），
    可直接复用 ADR-019 的标签保存链路。
- `TagNormalization`（static）：纯函数服务
  - `const string AnyField = "*"`、`const int MaxRuleCount = 50`；
  - `TimeSpan PatternMatchTimeout`（1 秒）；`IReadOnlyList<string> SupportedTargetFields`（UI 下拉数据源）；
  - `bool IsAnyField(string?)`、`bool IsKnownField(string?)`、`string CanonicalizeTargetField(string?)`；
  - `bool IsValidPattern(string?, out string? error)`；
  - `string? Apply(TagNormalizationRule?, string?)`：**返回 null 表示无需改动**
    （规则为空 / 禁用、pattern 空或非法、正则超时、无匹配、结果与输入相同）；
    返回非 null 为替换后文本，可能是空字符串（清空）；替换串非法时同样返回 null，不抛异常。
  - `TagNormalizationResult? ApplyToTag(TagNormalizationRule?, AudioTagData)`；
  - `TagNormalizationResult? ApplyToTag(IEnumerable<TagNormalizationRule>?, AudioTagData)`：
    按列表顺序叠加（后一条看到前一条的结果），只返回相对原始标签的真实变化，无变化返回 null；
    禁用 / 非法 pattern / 未知目标字段的规则被跳过。
  - `List<TagNormalizationRule> NormalizeRules(IEnumerable<TagNormalizationRule>?, IAppLogger? = null)`：
    设置归一化（见 2.4），放在 Core 以便单元测试，Infrastructure 只负责调用。

### 2.2 匹配与应用语义

- **正则引擎**：`System.Text.RegularExpressions.Regex`，每 pattern 进程内缓存（`ConcurrentDictionary`），
  统一 1 秒 `matchTimeout`，防止用户写出灾难性回溯 pattern 卡死 UI 线程。
- **大小写**：规则 schema 不含 `IgnoreCase` 字段，默认规则在 pattern 内联 `(?i)`；
  自定义规则可在正则里写 `(?i)`。
- **通配目标字段 `"*"`**：规则依次作用于 10 个常用字段 + `AudioTagData.AdditionalTags` 的文本值；
  未知的 `TargetField`（非字段、非 `"*"`）跳过，配置保留供 UI 提示。
- **数字字段（`year` / `track` / `disc`）**：只接受可解析的非负整数结果；
  解析失败或负数保留原值。`0` 表示未设置，会作为“清空”写入（与 `TagBatchEditor.CreateEdit`
  的 `ParseInt` 语义一致）。
- **额外标签**：文本值参与规范化；但替换结果为空 / 全空白时**保留原值、不记录删除**——
  “删除 EP 后缀”之类规则不应把整条额外标签删掉。额外标签的 `Kind` / `Key` 原样保留。
- **只做显式操作**：规则只在用户点击“快捷规范化”时应用，**不会**在保存标签、扫描或播放时自动改写文件，
  符合“文件是唯一真相”与不静默改动用户数据的原则。
- **批量规则叠加**：同一个文件按规则列表顺序依次应用，所有变化合并为一次 `TagBatchEdit`，
  一次写文件，避免同一文件多次重写与封面缓存反复失效。

### 2.3 默认规则

| id | 名称 | 目标字段 | Pattern | Replacement |
| --- | --- | --- | --- | --- |
| `builtin-album-ep-single-suffix` | 移除专辑名 EP / Single 后缀 | `album` | `(?i)\s*[-\u2010\u2011\u2012\u2013\u2014\u2015\uFF0D\u2212]\s*(?:EP\|Single)\s*$` | 空 |
| `builtin-iso-datetime-to-date` | 日期时间只保留日期 | `*` | `(?i)^\s*(\d{4}-\d{2}-\d{2})[T ]\d{2}:\d{2}(?::\d{2}(?:\.\d{1,7})?)?(?:\s*(?:Z\|[+-]\d{2}:?\d{2}))?\s*$` | `$1` |

- 破折号字符集覆盖 `-`、`‐`(U+2010)、`‑`(U+2011)、`‒`(U+2012)、`–`(U+2013)、`—`(U+2014)、
  `―`(U+2015)、`－`(U+FF0D)、`−`(U+2212)；只删除**末尾**的后缀，
  `Demo Album A (EP)`、`EP Demo Album A`、`Demo Album A - EP Edition` 保持不变。
- 日期规则要求整个文本是“日期 + 时间”形态（首尾可有空白）：`2026-01-01T00:00:00`、
  `2026-01-01 00:00:00`、缺失秒、毫秒 1–7 位、`Z`、`+08:00` / `-0500` 均可；
  纯日期 `2026-01-01`、`2026/01/01 00:00:00`、前后夹带其他文字的值保持不变。
- 日期规则目标为 `*`：`AudioTagData.Year` 是 `int?`，日期文本实际存放于 comment /
  额外标签（如自定义日期字段）等文本位置；通配让规则在常用字段与额外标签上都能生效，
  同时不会命中纯数字的 `year` 文本。
- 两条默认规则同时存在于默认配置中，用户可单独禁用；日期规则只做“截断”，不做时区换算。

### 2.4 设置持久化与归一化

`AppSettings` 新增：

```csharp
/// <summary>用户配置的标签“快捷规范化”正则规则，顺序即叠加应用顺序。</summary>
public List<TagNormalizationRule> TagNormalizationRules { get; set; } = TagNormalizationDefaults.Create();
```

- **null 与空列表语义不同**：`null`（旧配置缺字段、手工写入 `null`）在加载时补默认规则；
  空列表（用户在设置页删除全部规则后保存）保持为空，**不会复活默认规则**。
  `AppSettings` 属性初始化为 `Create()`，旧配置反序列化后自然得到默认规则，无需迁移代码。
- `JsonSettingsService.Normalize` 增加一行调用（紧随 `Hotkeys` 归一化之后）：

```csharp
settings.TagNormalizationRules = TagNormalization.NormalizeRules(settings.TagNormalizationRules, _logger);
```

`NormalizeRules` 规则：

1. `null` → `TagNormalizationDefaults.Create()`；空列表 → 空列表；
2. 跳过 `null` 元素，逐条 `Clone()`（不修改调用方实例）；
3. `Name` / `Id` 去首尾空白；`Name` 空 → `规范化规则 N`（N 为当前序号）；
   `Id` 空或与前面重复 → `Guid.NewGuid().ToString("N")`；
4. `TargetField` 规范大小写（`" Album "` → `album`、`"ALBUMARTIST"` → `albumArtist`）；
   未知字段只 Trim 保留，并 `Warn` 日志；
5. `Pattern` Trim、`Replacement` `null` → 空字符串；
6. `Pattern` 非法的规则**保留**（供 UI 红框提示 / 禁用标记），只 `Warn` 日志，
   应用时 `Apply` 返回 null 自动跳过；
7. 数量上限 `MaxRuleCount = 50`，超出部分丢弃并 `Warn`；
8. 顺序保持不变。

- **SchemaVersion 不变（1）**：只是配置新增可忽略属性，无 SQLite 表结构变化、无数据修复需求；
  `JsonDefaults.Options` 不启用 `UnmappedMemberHandling.Disallow`，旧版本读新配置会忽略该字段，
  新版本读旧配置走属性默认值 → 前后兼容。
- 导出 / 导入配置自动包含规则（都经过 `Normalize`）；媒体库 `library.db` 不受影响。

### 2.5 候选值来源与查询（Infrastructure）

- 只读 `library.db` 的 `tracks` 表，只统计 **`IsInLibrary = 1 AND IsMissing = 0`** 的行；
  不读音频文件、不扫描目录。
- 每个字段一条聚合查询（四个字段共四次全表扫描，单连接内顺序执行）：

  ```sql
  SELECT TRIM(Artist) AS Value
  FROM tracks
  WHERE IsInLibrary = 1 AND IsMissing = 0
    AND Artist IS NOT NULL AND TRIM(Artist) <> ''
  GROUP BY TRIM(Artist)
  ORDER BY COUNT(*) DESC, TRIM(Artist) COLLATE NOCASE
  LIMIT @limit;
  ```

  四个字段的列名分别是 `Artist`、`AlbumArtist`、`Composer`、`Genre`。
- `LIMIT` 每字段默认 **2000**（`TagSuggestionValues.DefaultLimit`），上限钳制到 10000；
  按出现频率降序返回，常用值优先出现在候选框顶部，同时把内存/传输限制在常数级。
- 不新增索引：四列都在扫描时写入，加索引会拖慢 `UpsertTracksAsync`；四万行级别的四次聚合实测
  在百毫秒级（见 2.10），且只在进程内首次需要时执行一次。
- 新增仓储方法：

  ```csharp
  Task<TagSuggestionValues> GetTagSuggestionValuesAsync(
      int limitPerField = TagSuggestionValues.DefaultLimit,
      CancellationToken cancellationToken = default);
  ```

  返回快照 `TagSuggestionValues`（字段键 → 候选值列表），键使用 `TagFieldKeys` 常量：
  `artist`、`albumArtist`、`composer`、`genre`。

### 2.6 候选值匹配（Core 纯逻辑）

- `TagSuggestionValues`：不可变快照，`Get(fieldKey)` 缺失时返回空列表。
- `TagSuggestionMatcher.Match(candidates, input, limit = 10)`：
  - 空白输入返回空（不主动弹候选）；
  - 先收集**前缀命中**，再收集**仅包含命中**，同类内保持候选原顺序（即频率顺序）；
  - 大小写不敏感去重，候选两侧空白先 trim；
  - 最多返回 `limit` 条。
  - 放在 Core 以便单测，UI 只做显示/键盘交互。

### 2.7 候选值进程内缓存与失效

- 新增 `ITagSuggestionService`（Core/Abstractions）与 `TagSuggestionService`（Infrastructure）：
  - 首次 `GetAsync` 时查询一次并缓存；并发调用用 `SemaphoreSlim(1,1)` 合并为一次查询；
  - 缓存成功结果；查询失败记录 `Warn` 并返回空快照，**不缓存失败**，下次重试；
  - `Invalidate()` 递增 generation 并清空缓存。查询期间若发生 `Invalidate`，旧结果不写回缓存。
- 应用层在 `MainViewModel.ReloadSourcesAsync()` 末尾调用 `_host.TagSuggestions.Invalidate()`。
  扫描、标签保存、添加文件、文件监听刷新等所有会改变媒体库缓存的流程最终都会走到这里，
  因此候选快照的刷新时机与来源列表/统计信息一致。
- 代价：媒体库变化到下次失效前，候选可能短暂滞后；对“输入辅助”而言可接受。

### 2.8 界面接线（已实现）

原 ADR-025 把设置页规则编辑、标签编辑页“快捷规范化”入口列为后续接线项；这些界面与候选值补全
均已落地。实现细节见 `docs/kb/KB-018-tag-normalization-ui.md`；不改变 Core 模型、
`settings.json` Schema 与 `library.db` Schema。

#### 2.8.1 设置页规则管理

- 位置：设置 → **文件与转换**（`PageFiles`），编码器段之后、文件操作目标文件夹之前。
- 规则列表绑定 `AppSettings.TagNormalizationRules`，每行提供：启用复选框、名称、目标字段
  `ComboBox`（`TagNormalization.SupportedTargetFields`，含“全部可文本字段” `*` 与中文字段名）、
  正则、替换文本、删除；正则输入实时用 `TagNormalization.IsValidPattern` 校验，
  非法时 `DangerBrush` 红框 + 行内提示、**不阻止保存、不参与应用**。
- 底部「添加规则」「恢复默认规则」（等价于 `TagNormalizationDefaults.Create()`）与状态文字
  （已启用数量 / 非法数量 / 达到上限提示）；达到 `MaxRuleCount = 50` 时禁用“添加规则”。
- 编辑即写回后台行对象并异步保存 `settings.json`（不在 UI 线程同步等待）；重新打开设置页从
  `settings.TagNormalizationRules` 重建；用户把列表删空时保持空列表（不复活默认规则）。
- 规则顺序即应用顺序：顺序 = 设置页显示顺序，编辑后即时保存；本次实现未提供规则上移 / 下移，
  原计划的正则“测试”预览按钮也未实现（见 KB-018 非目标与风险与限制）。
- 示例文案使用 dummy 值（如 `Demo Album A - EP`）；主题令牌与既有 `SectionLabel` / `FlatButton` 样式复用。

#### 2.8.2 主窗口右键批量应用

- 曲目右键菜单 `TrackMenu` 在 `ContextMenuOpening` 中构建“标签规范化”子菜单：列出启用规则
  （点击执行单条规则）、分隔线、“应用全部启用规则”；无启用规则时显示禁用项
  “（没有启用的规则）”；没有选中歌曲时整个子菜单禁用。
- 执行 `MainViewModel.ApplyTagNormalizationAsync(string? ruleId)`：优先多选行、否则当前行；
  无选择 / 规则不存在 / 规则已停用 / 全部规则非法时用状态栏提示并返回；后台 `Task.Run` 逐首
  `TagService.Read(path)` 后调用 `TagNormalization.ApplyToTag`，单文件失败记 `Warn` 并跳过，
  无变化的文件计入跳过；有变化则组装 `TagBatchEdit`（`result.ApplyTo(edit)`）。
- 写入复用既有标签保存闭环 `ApplyTagEditsAsync`（重新索引 → 失效封面缓存 → 刷新行与正在播放面板），
  成功后状态栏显示“已规范化 N 首歌曲的标签（M 处修改）”；异常 `Logger.Error` + 用户可见错误提示。

#### 2.8.3 标签属性窗口快捷规范化

- 标签属性窗口“标签信息”标题行右侧新增「快捷规范化」按钮，弹出 `ContextMenu` 列出设置页启用规则
  （无启用规则时显示禁用项）；规则经 `TagNormalization.NormalizeRules` 归一化后使用设置页最新列表。
- `DialogService.EditTags` 把 `_host.Settings.Current.TagNormalizationRules` 作为可选第三参传入；
  诊断 / 截图的两参调用保持编译，`null` 时使用内置默认规则。
- 应用规则时只改当前编辑框文本（不改 `InitialText`，保存时仍走“只提交被修改字段”的既有语义）：
  目标字段为 `*` 或与编辑器字段相同才处理；`<多值>` 占位字段不自动改写，计数为“已跳过”；
  目标字段为 `*` 时额外遍历额外标签的文本值，空替换在额外标签上保留原值（只改不删）。
- 标题下方状态文字显示修改字段数 / 跳过的多值字段数 / 无匹配 / 非法正则错误；不影响封面选择、
  其他标签删除与保存 / 取消流程，只有用户点击“保存”才写入文件。

#### 2.8.4 标签属性窗口候选值补全

- `TagPropertiesWindow` 构造函数新增可选参数：

  ```csharp
  public TagPropertiesWindow(
      IReadOnlyList<Track> tracks,
      IReadOnlyList<AudioTagData> tags,
      IReadOnlyList<TagNormalizationRule>? normalizationRules = null,
      ITagSuggestionService? tagSuggestions = null)
  ```

  `DialogService.EditTags` 传入 `_host.TagSuggestions`；截图/诊断等两参、三参调用不受影响
  （没有服务时不装补全，行为与旧版一致）。
- 只给 `artist` / `albumArtist` / `composer` / `genre` 四个输入框挂补全；窗口 `Loaded` 后异步
  `GetAsync()`，不阻塞窗口打开。
- 共享一个 `Popup` + `ListBox`（不每字段各建一个）：
  - 输入变化时在内存中 `Match` 当前候选，最多显示 10 条；
  - `StaysOpen=True` + 窗口级 `PreviewMouseDown`/`LostKeyboardFocus`/`OnDeactivated` 负责关闭，
    避免 `StaysOpen=False` 时第一次点击被浮层吞掉；窗口 `PreviewMouseDown` 会先于浮层内部控件收到事件，
    因此命中浮层内部时必须放行；焦点/失活关闭延迟到 `DispatcherPriority.Background`，
    保证候选项的鼠标按下事件先被处理；
  - `↓/↑` 移动选择、`Enter`/`Tab` 采用、鼠标点击采用、`Esc` 只关候选框（不关标签窗口）；
    鼠标点击候选项与键盘采用走同一条 `ApplySuggestion` 路径；
  - 补全只改输入框文本，不改变“未修改字段不提交”和“多值保持不变”的既有语义；
    应用候选值后同样只是普通输入，用户仍可继续编辑。
- 颜色全部使用主题令牌（`PanelBackgroundBrush` / `BorderBrush` / `RowHoverBrush` /
  `AccentSoftBrush` / `TextPrimaryBrush`），已确认 5 套主题均存在。

### 2.9 安全与性能

- 纯内存计算，无额外启动 IO；不对启动路径增加任何同步读写（AGENTS.md §6）。
- 正则按 pattern 缓存，`matchTimeout` 1 秒；规则数量上限 50，批量应用复杂度为
  `文件数 × 规则数 × 字段数`，单文件微秒级，可在后台线程执行。
- 规则只改文本，不触碰文件路径、封面、播放统计；所有改动必须经用户显式触发并保存。
- 非法 / 空 pattern、非法替换串、未知目标字段、正则超时都返回 null（跳过），
  不会让设置加载失败或批量编辑整体失败。
- 文本比较使用 `StringComparison.Ordinal`，数字解析使用 `InvariantCulture`，不受系统区域设置影响。

### 2.10 候选值性能实测（合成库，Release）

探针：`.scratch/perf-probe`（不入库），50,000 首合成曲目、数据库 13.8 MB：

| 指标 | 结果 |
| --- | --- |
| 仓储 `GetTagSuggestionValuesAsync()` 冷查询（4 字段 GROUP BY） | **174 ms** |
| `TagSuggestionService.GetAsync()` 冷启动 | **183 ms** |
| `TagSuggestionService.GetAsync()` 缓存命中 | **0 ms** |
| 返回规模（默认 limit 2000） | artist 2000 / albumArtist 200 / composer 500 / genre 30 |

- 冷查询在后台线程执行，不阻塞 UI；同一进程内只执行一次，后续打开窗口/按键均为内存过滤（微秒级）。
- 每按键过滤的候选上限 2000 条/字段，`TagSuggestionMatcher` 线性扫描且提前达到 10 条即返回。
- 没有新增周期性 I/O；失效仅是清引用。

### 2.11 回滚

- 回滚到旧版本：旧程序读含 `tagNormalizationRules` 的 `settings.json` 会忽略该字段
  （JSON 反序列化默认容忍未知属性），不会触发“损坏 → 全部重置”。
- 功能回滚（保留配置）：删除 `AppSettings` 属性与 `Normalize` 调用即可；
  已保存的规则数据仍在 JSON 中，再次升级可恢复。
- 用户级回滚：把规则列表清空并保存，或删除 `settings.json` 中的对应字段后重启。
- 候选值功能无持久化状态：删除服务接线即完全回退，不影响 `library.db` 与 `settings.json`。

## 3. 备选方案

### 3.1 规范化规则

1. **硬编码两条规则**：实现最省，但 owner 明确要求“配置里保存自定义规则”，否决。
2. **独立 `tag-rules.json` 文件**：与 `settings.json` 分离、可单独分享；但多一次 IO / 文件生命周期，
   且导出配置语义分裂（导出配置不含规则或需两个文件），否决；规则体量小，放 `settings.json` 足够。
3. **复用“转换预设”管道（ConversionPreset）**：那是编码器 / 文件名模板，语义与标签字段规范化不同，
   强行复用会让两个功能互相牵连，否决。
4. **在扫描 / 播放时自动应用默认规则**：会造成静默改文件与性能负担，且违反“文件是唯一真相”的
   显式编辑原则，否决；只保留用户主动触发。

### 3.2 候选值

| 方案 | 放弃原因 |
| --- | --- |
| 每次输入都查 SQLite（带前缀 LIKE） | 按键频率下频繁 I/O 与锁竞争，大库会卡；且需要额外索引 |
| 给 Artist/AlbumArtist/Composer/Genre 加索引 | 提高写入/扫描成本，收益仅此输入辅助功能 |
| 从音频文件即时读取全库标签 | 太慢，违背“数据库只是缓存”的性能红线 |
| 使用 WPF 自绘 AutoCompleteBox / 第三方控件 | 无必要，共享 Popup + ListBox 已满足需求且不引依赖 |
| 用 `LIKE` 在 SQL 里做前缀过滤 | 仍要每键查询；且中文/大小写/白空格行为不如内存 `OrdinalIgnoreCase` 可控 |

## 4. 后果

### 4.1 规范化规则

正面：

- 两类高频重复编辑（EP / Single 后缀、日期时间截断）一次操作完成；
- 规则用户可配置、可禁用，默认值透明（都有稳定 id 与名称）；
- Core 纯逻辑可单测，UI 与持久化解耦，后续可扩展“转换预设”式的高级规则。

负面 / 代价：

- 正则对普通用户有学习成本 → 设置页提供行内非法提示、dummy 示例；
- 规则由用户维护，可能写出过宽 pattern（如 `.*`）→ 通配目标 + 空替换会清空字段：
  已通过“额外标签空结果不删除”、显式确认与应用预览降低风险；默认规则均较保守。
- 多一层配置字段与测试维护成本。

### 4.2 候选值

- 仅覆盖四个字段；标题/专辑/年份等不提供候选（按需求刻意收窄）。
- 候选按 `TRIM` 分组，但 SQLite 的 `GROUP BY` 大小写敏感：`Rock` 与 `rock` 可能各占一条；
  UI 匹配层做大小写不敏感去重，显示时只保留先出现者，排序仍以频率为主。
- 每字段最多 2000 个候选（频率靠后的长尾值不会出现）；如需更大可在常量处调整。
- 候选快照可能滞后到下一次 `ReloadSourcesAsync` 失效为止（扫描完成 / 标签保存后即刷新）。
- 查询失败时静默降级为空候选（日志 Warn）；用户仍可手动输入，功能不阻塞。
- 不涉及 schema 变更、配置变更或新依赖。

## 5. 测试与验证

### 5.1 规范化规则（已实现）

- `tests/EchoGarage.Tests/TagNormalizerTests.cs`：默认规则内容 / 每次返回新实例；EP、Single、
  破折号、大小写、无后缀不变例；日期多格式与不变例；`IsValidPattern`、`Apply` 的
  null / 禁用 / 空 pattern / 非法 pattern / 反向引用 / 清空 / 结果相同语义；
  `ApplyToTag` 常用字段、额外标签、空替换不删除额外标签、Year 不被日期规则误改、
  数字字段非负校验、0 表示清空、未知目标字段与禁用规则跳过、多规则顺序叠加、
  `TagNormalizationResult.ApplyTo` 合并、规则 `Clone`；
  `NormalizeRules` 的 null → 默认、空列表保持空、Trim / 补全 / id 去重、非法 pattern 保留、
  数量上限、不修改输入实例。
- `tests/EchoGarage.Tests/SettingsTests.cs`：加载时生成默认规则并写入 JSON；
  保存空列表后重载仍为空；缺字段 / `null` 补默认；保存时归一化（Trim、id 去重、非法 pattern 保留）；
  导出 / 导入往返；`AppSettings.Clone` 深拷贝规则列表。
- 界面接线验证：KB-018 §9（设置页规则管理、右键批量应用、标签属性窗口快捷规范化；Debug 构建 +
  全量测试基线 584 项通过，Release 构建 0 错误，另有副本数据目录下的手工验收步骤）。

### 5.2 候选值（已实现）

- 新增单元测试（8 项，测试总数 656，全部通过）：
  - `TagSuggestionMatcherTests`：空输入、前缀优先于包含、大小写不敏感去重、trim/空候选、limit；
  - `RepositoryTests`：频率排序 + TRIM 合并 + 四字段返回；排除非媒体库 / 缺失 / 空白行，`limitPerField` 生效；
    `TagSuggestionService` 缓存命中、`Invalidate` 后读到新值。
- 完整构建 0 错误；既有 `TagNormalizerTests.cs(54,6)` xUnit1025 警告为改动前已存在。
- UI 冒烟与自动交互验证（2026-11-16，发布版 `artifacts/publish/win-x64`，隔离数据目录）：
  - `--screenshot-all` 生成的 `dialog-tags.png` / `dialog-tags-multi.png` 正常加载窗口；应用日志出现
    `Tag suggestions loaded: artist=6, albumArtist=2, composer=3, genre=4.`，说明真实进程内四个字段的候选均已就绪。
  - 自动按键测试（测试前按规范发送系统通知，结束后恢复前台窗口与光标）：先经 UI Automation 选中歌曲并由
    物理点击把焦点放到数据网格，注入 `Alt+Enter` 打开“标签属性”窗口；定位到 4 个带候选提示的输入框；
    在“艺术家”输入框填入前缀 `Demo Artist ` 后弹出候选
    `Demo Artist Alpha / Beta / Gamma / Delta / Epsilon`（大小写变体被匹配层去重）；注入 `↓` + `Enter`
    采用第一项后输入框值变为 `Demo Artist Alpha` 且浮层关闭；最后 `Esc` 关闭窗口。
    结果：`pass=true`，`errors=[]`，应用保持运行，前台窗口与光标均恢复。
  - 鼠标点击验证（同一自动测试脚本 `-Mode mouse`，同样先发系统通知）：把鼠标移到候选
    `Demo Artist Alpha` 上物理点击后，输入框值变为 `Demo Artist Alpha`、浮层关闭；
    `applyViaMouse=true`、`pass=true`，前台窗口与光标均恢复。

## 6. 已确认事项与后续增强

原 ADR-025 的“待 owner 确认”项均已随实现落地并确认：

1. 默认规则保持“可选、不自动应用”：**已实现**——只在“快捷规范化”动作与右键批量应用中执行（见 2.2 / 2.8）。
2. 默认日期规则目标字段使用通配 `*`：**已实现**（见 2.3；通配覆盖常用字段与额外标签，不命中纯数字 `year`）。
3. 独立“规则导入 / 导出”（分享规则集）不属本期范围：**未实现**；当前规则随 `settings.json`
   整体导出 / 导入自动包含（见 2.4），规则集单独分享仍属后续增强。

## 7. 修订记录

- 2026-11-16：创建 `docs/adr/ADR-025-tag-normalization-rules.md`（本文件）：标签「快捷规范化」
  正则规则（Core 模型、匹配与应用语义、默认规则、设置持久化与归一化、备选方案与回滚）；owner 批准。
- 2026-11-16：原 ADR-027 创建（`docs/adr/ADR-027-tag-suggestions.md`，已并入本文）：标签编辑候选值
  （`library.db` 聚合、输入前缀 / 包含匹配、进程内快照缓存、标签属性窗口补全）。
- 2026-11-17：原 ADR-027（已并入本文）经 owner 批准（Accepted）。
- 2026-11-19：合并：原 ADR-027（已并入本文）并入本 ADR，保留 ADR-025 编号与文件名；
  标题更新为「标签编辑辅助：规范化正则规则与候选值」；决策重组为规范化规则（2.1–2.4）与候选值
  （2.5–2.7）两部分，界面接线统一为「已实现」（2.8，实现笔记 KB-018）；补充候选值备选方案、
  后果、性能实测与测试；「task-7 后续接线」「待 owner 确认」等过期表述更新为已实现；
  原 ADR-027 文件删除（内容已并入本文）。
