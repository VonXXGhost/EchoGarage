# ADR-025（提案）：标签「快捷规范化」正则规则

- 状态：**Accepted**（owner 批准：2026-11-16）
- 日期：2026-11-16
- 关联：ADR-019（标签编辑页与额外标签展示）、AGENTS.md §5（文件是唯一真相）、§7（测试要求）
- 影响范围：`EchoGarage.Core` 领域模型 + `EchoGarage.Infrastructure` 设置持久化 + 单元测试；
  界面接线由后续任务（task-7）完成，不在本提案实施范围内

## 1. 背景

标签编辑页目前只能逐字段手工修改。用户经常遇到两类重复劳动：

1. 专辑名带数字发行版后缀，例如 `Demo Album A - EP`、`Demo Album A – Single`；
   后缀的破折号字符（`-`、`–`、`—`、`－`、`−` 等）与大小写不统一。
2. 某些文件把日期写成完整时间戳，例如 `2026-01-01T00:00:00`、`2026-01-01 00:00:00.000Z`、
   `2026-01-01T00:00:00+08:00`，而用户只需要 `2026-01-01`。

Owner 需求（2026-11-16）：**在配置里保存自定义正则“快捷规范化”规则，对歌曲特定 tag 规范化**；
默认提供两条规则（专辑名 EP / Single 后缀、日期时间只保留日期），并允许用户自行增删改。
本提案只落地 **Core / Infrastructure + 测试**与设置持久化，界面（设置页规则编辑、标签编辑页
“快捷规范化”入口）由 task-7 接入。

## 2. 决策

### 2.1 领域模型（Core）

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

### 2.5 界面接线点（task-7）

1. **设置页**新增“标签规范化”卡片：
   - `DataGrid`/`ListView` 绑定 `AppSettings.TagNormalizationRules`（启用复选框、名称、目标字段
     `ComboBox`（`TagNormalization.SupportedTargetFields`，显示名需中文映射）、正则、替换文本、删除）；
   - 正则输入实时 `TagNormalization.IsValidPattern` 校验，非法时红框 + 提示，不阻止保存；
   - “测试”按钮用 `Apply` 对示例文本预览（示例用 `Demo Album A - EP` 等 dummy 值）；
   - 保存时 `SaveAsync` 自动归一化；列表清空后保存 → 空列表（不回填默认）。
2. **标签编辑页（批量）**新增“快捷规范化”动作：
   - 对每个选中文件的 `AudioTagData` 调 `TagNormalization.ApplyToTag(enabledRules, tag)`；
   - 非 null 时 `result.ApplyTo(批量, edit)`，走现有保存链路（重新索引 → 失效封面缓存 → 刷新列表）；
   - 应用前需确认（显示将修改的文件数 / 规则数），失败文件按现有错误处理 `SetStatus` + `Logger.Error`；
   - 预览可复用 `Apply` 对字段文本逐条展示。
3. 规则顺序即应用顺序；UI 提供上移 / 下移（或拖拽）并与持久化列表顺序一致。

### 2.6 安全与性能

- 纯内存计算，无额外启动 IO；不对启动路径增加任何同步读写（AGENTS.md §6）。
- 正则按 pattern 缓存，`matchTimeout` 1 秒；规则数量上限 50，批量应用复杂度为
  `文件数 × 规则数 × 字段数`，单文件微秒级，可在后台线程执行。
- 规则只改文本，不触碰文件路径、封面、播放统计；所有改动必须经用户显式触发并保存。
- 非法 / 空 pattern、非法替换串、未知目标字段、正则超时都返回 null（跳过），
  不会让设置加载失败或批量编辑整体失败。
- 文本比较使用 `StringComparison.Ordinal`，数字解析使用 `InvariantCulture`，不受系统区域设置影响。

### 2.7 回滚

- 回滚到旧版本：旧程序读含 `tagNormalizationRules` 的 `settings.json` 会忽略该字段
  （JSON 反序列化默认容忍未知属性），不会触发“损坏 → 全部重置”。
- 功能回滚（保留配置）：删除 `AppSettings` 属性与 `Normalize` 调用即可；
  已保存的规则数据仍在 JSON 中，再次升级可恢复。
- 用户级回滚：把规则列表清空并保存，或删除 `settings.json` 中的对应字段后重启。

## 3. 后果

正面：

- 两类高频重复编辑（EP / Single 后缀、日期时间截断）一次操作完成；
- 规则用户可配置、可禁用，默认值透明（都有稳定 id 与名称）；
- Core 纯逻辑可单测，UI 与持久化解耦，后续可扩展“转换预设”式的高级规则。

负面 / 代价：

- 正则对普通用户有学习成本 → 设置页提供测试按钮、非法提示、dummy 示例；
- 规则由用户维护，可能写出过宽 pattern（如 `.*`）→ 通配目标 + 空替换会清空字段：
  已通过“额外标签空结果不删除”、显式确认与应用预览降低风险；默认规则均较保守。
- 多一层配置字段与测试维护成本。

## 4. 测试覆盖（已实现）

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

## 5. 备选方案

1. **硬编码两条规则**：实现最省，但 owner 明确要求“配置里保存自定义规则”，否决。
2. **独立 `tag-rules.json` 文件**：与 `settings.json` 分离、可单独分享；但多一次 IO / 文件生命周期，
   且导出配置语义分裂（导出配置不含规则或需两个文件），否决；规则体量小，放 `settings.json` 足够。
3. **复用“转换预设”管道（ConversionPreset）**：那是编码器 / 文件名模板，语义与标签字段规范化不同，
   强行复用会让两个功能互相牵连，否决。
4. **在扫描 / 播放时自动应用默认规则**：会造成静默改文件与性能负担，且违反“文件是唯一真相”的
   显式编辑原则，否决；只保留用户主动触发。

## 6. 待 owner 确认

1. 默认规则是否保持“可选、不自动应用”（本提案：不自动应用，只在“快捷规范化”动作中执行）；
2. 默认日期规则目标字段用通配 `*` 是否可接受，还是应限定为 `comment` / 指定额外标签；
3. 是否需要“规则导入 / 导出”（分享规则集）作为后续增强，而非本期范围。
