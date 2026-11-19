# KB-018：标签规范化 UI（设置页规则管理 / 右键批量应用 / 标签属性窗口快捷规范化）

- 状态：Accepted（2026-11-16 批准）
- 日期：2026-11-16
- 相关：ADR-025（标签规范化规则，见 `docs/adr/ADR-025-tag-normalization-rules.md`）、
  `src/EchoGarage.Core/Models/TagNormalization.cs`、标签写入闭环 `MainViewModel.ApplyTagEditsAsync`、
  文件操作目标文件夹（ADR-021）、五套主题令牌

## 1. 背景与需求

ADR-025 已确定“可配置正则快捷规范化”的 Core 规则模型与执行语义（规则列表、默认规则、通配字段、
1 秒正则超时、额外标签只改不删、批量同文件多规则合并为一次 `TagBatchEdit`）。但当时没有任何界面入口，
用户无法配置规则，也无法在选中歌曲上批量执行。

本文记录 UI 接线方案：设置页管理规则、主窗口右键菜单批量应用、标签属性窗口内快捷规范化。
**不改变** Core 模型、`settings.json` Schema 与 `library.db` Schema。

非目标：

- 不引入规则排序 / 上移下移（当前按列表顺序应用，顺序即设置页显示顺序）。
- 不引入后台进度条与取消（大批量时状态栏提示“正在检查标签…”，执行期间界面仍可响应）。
- 不做正则“测试”预览按钮（ADR-025 §2.5 列为可选，后续可加）。
- 不自动应用规则（只在用户显式点击菜单 / 按钮时执行）。

## 2. 与 ADR-025 的关系

| 层 | 归属 | 内容 |
| --- | --- | --- |
| Core 规则与语义 | ADR-025（已实现） | `TagNormalizationRule`、`TagNormalizationDefaults`、`TagNormalization.Apply/ApplyToTag`、`NormalizeRules`、上限 50 |
| 设置持久化 | ADR-025 + 既有 `JsonSettingsService` | `AppSettings.TagNormalizationRules`；`SaveAsync` 时 `NormalizeRules` 兜底；`null`→默认两条，空列表→保持空（用户删空不复活） |
| UI 接线与交互 | 本文（KB-018） | 设置页规则编辑、右键菜单批量应用、标签属性窗口快捷应用 |

UI 接线点与交互约定以本文为准；规则语义继续以 ADR-025 为准。

## 3. 设置页：规则管理

### 3.1 位置与布局

- 页面：设置 → **文件与转换**（`PageFiles`）。
- 位置：编码器段之后、文件操作目标文件夹之前；风格与页面其它段落一致
  （`SectionLabel` 标题 + `PanelBackgroundBrush` 边框圆角容器）。
- 段落标题：**标签规范化**。
- 说明文案（只读）：
  - “对选中歌曲按正则批量规范化标签；规则保存在 settings.json。”
  - “非法规则不会被执行，请按行内提示修正；示例：把「Demo Album A - EP」规范化为「Demo Album A」。”
- 空列表时显示提示：“还没有规则。点击「添加规则」新建，或「恢复默认规则」载入内置规则。”
- 规则列表：`ListBox`（无边框、透明背景、禁用自身滚动，避免与页面 ScrollViewer 双滚动），
  每行一个编辑块：

| 控件 | 绑定 / 行为 |
| --- | --- |
| 「启用」`CheckBox` | `Enabled`；取消勾选后该规则不参与批量应用与快捷规范化 |
| 名称 `TextBox` | `Name`；为空时 Core 归一化为“规范化规则 N” |
| 目标字段 `ComboBox` | `TargetField`；第一项为“全部可文本字段”（`*`），其后按 `TagNormalization.SupportedTargetFields` 顺序显示中文名：标题 / 艺术家 / 专辑 / 专辑艺术家 / 作曲家 / 流派 / 年份 / 音轨号 / 碟片号 / 备注 |
| 正则 `TextBox` | `Pattern`；等宽字体（`MonoFont`），提示示例 `\s*-\s*(EP|Single)$` |
| 替换为 `TextBox` | `Replacement`；允许空串（清空匹配文本） |
| 「删除」按钮 | 从列表移除该行并立即保存 |
| 行内错误文本 | 非法正则时显示 `DangerBrush` 红色提示（来自 `TagNormalization.IsValidPattern`），该行不参与应用 |

- 列表底部按钮：「添加规则」「恢复默认规则」；右侧状态文字显示“已启用 N 条规则”“N 条规则正则无效，
  不参与应用”“最多 50 条规则，已达到上限”等。
- 达到 `TagNormalization.MaxRuleCount`（50）时，“添加规则”禁用，并以状态文字 / ToolTip 提示上限。

### 3.2 交互与保存

- 所有编辑（启用、名称、目标字段、正则、替换）在控件事件中写回后台行对象，随后调用
  `ApplyTagNormalizationRules()`：
  1. 用行数据组装 `List<TagNormalizationRule>`；
  2. `_host.Settings.Current.TagNormalizationRules = TagNormalization.NormalizeRules(rules)`（不传 logger，
     避免逐键校验产生 WARN 噪音）；
  3. 调用设置窗口既有的 `SaveSettings()`（`JsonSettingsService.SaveAsync` 异步写 `settings.json`）；
  4. 逐行调用 `IsValidPattern` 刷新错误提示与计数。
- 该行为与现有快捷键编辑（每次 `TextChanged` → `SaveHotkeys()` → `SaveSettings()`）一致；写文件为异步 IO，
  不在 UI 线程同步等待。
- “添加规则”默认值：新 `Guid` Id、名称空、目标字段“专辑”、正则/替换为空（空正则会立即显示行内错误，
  引导填写，不会被应用）。
- “恢复默认规则”等价于 `TagNormalizationDefaults.Create()`（移除专辑名 EP / Single 后缀、日期时间只保留日期
  两条），覆盖当前列表并保存。
- 重新打开设置页：`PopulateTagNormalizationRules()` 从 `settings.TagNormalizationRules` 重建行，
  状态保持；用户把列表删空时保持空列表（不复活默认规则）。

## 4. 主窗口右键菜单：批量应用

- 菜单：曲目右键菜单 `TrackMenu`，在“Web 搜索标签…”之后新增“标签规范化”子菜单。
- 构建时机：与“转换”“文件操作”子菜单一致，在 `ContextMenuOpening` 中一次性构建（WPF 子菜单若在
  菜单打开后再动态添加会出现空子项 / 塌陷，见 `MainWindow.xaml` 资源注释）。
- 子项内容：
  - 列出 `Settings.TagNormalizationRules` 中 `Enabled == true` 的规则：Header = 规则名称（空名显示“未命名规则”），
    点击后执行 `ApplyTagNormalizationAsync(rule.Id)`；
  - 分隔线；
  - “应用全部启用规则”：按列表顺序应用全部启用规则（`ApplyTagNormalizationAsync(null)`）。
- 无启用规则时：显示禁用项“（没有启用的规则）”，“应用全部启用规则”禁用。
- 没有选中歌曲（`SelectedRows` 为空且 `SelectedRow` 为 null）时：整个“标签规范化”子菜单禁用。
- 不改变“转换”“文件操作”子菜单现有的重建 / 恒禁用逻辑。

## 5. 批量应用执行流程（MainViewModel）

`public async Task ApplyTagNormalizationAsync(string? ruleId)`：

1. 目标行：优先多选 `Tracks.Where(IsRowSelected)`；没有多选时退化为 `SelectedRow`；为空则
   `SetStatus("请先选择要规范化的歌曲")` 并返回。
2. 规则选择：
   - `ruleId == null`：全部启用规则（复制后使用），标签为“全部启用规则”；
   - 指定 Id：找不到 → 状态提示“找不到该规范化规则，可能已在设置页删除”；已停用 → “规则「…」已停用”；
   - 过滤掉正则非法的规则（`IsValidPattern`），全部非法时提示并返回。
3. `SetStatus("正在检查标签…")`，随后 `Task.Run` 在后台逐个读取 `TagService.Read(path)`：
   - 单文件读取失败 → `Logger.Warn` 并计入跳过，不中断整批；
   - `TagNormalization.ApplyToTag(rules, tag)` 返回 `null` / `HasChanges == false` → 计入跳过；
   - 有变化：`new TagBatchEdit()` + `result.ApplyTo(edit)`，收集（行、读取到的标签、编辑），
     `changes += result.Count`；`Task.Run` 内只读 ViewModel 行对象，不触碰 UI 集合。
4. `Targets.Count == 0`：`SetStatus("没有需要规范化的标签（检查了 N 首歌曲）")`。
5. 复用现有标签写入闭环：`await ApplyTagEditsAsync(rows, tags, edits, "标签规范化")` ——
   该方法负责逐文件写入、`IndexFileAsync` 重新索引、封面缓存失效、行数据与“正在播放”刷新、最终
   `ReloadSourcesAsync`，以及失败聚合提示；本次只把返回类型改为 `Task<int>`（返回成功数），原有两个调用方
   的 `await` 语句无需修改。
6. 成功后状态栏：`已规范化 N 首歌曲的标签（M 处修改）`；`Logger.Info` 记录规则 Id / 名称、目标文件数、
   修改数、跳过数。
7. 异常：`Logger.Error("Tag normalization failed", ex)` + `_dialogs.Error("标签规范化失败：…")`，不吞异常。

## 6. 标签属性窗口：快捷规范化

- 位置：标签属性窗口左侧“标签信息”标题行右侧新增「快捷规范化」按钮（`FlatButton`），点击弹出
  `ContextMenu` 列出设置页启用的规则（无启用规则时显示禁用项“（没有启用的规则）”）。
- 规则来源：`DialogService.EditTags` 调用 `new TagPropertiesWindow(tracks, tags, _host.Settings.Current.TagNormalizationRules)`；
  构造函数第三参可选，`Diagnostics` 的既有两参调用保持编译，`null` 时使用内置默认规则。
  窗口内再经 `TagNormalization.NormalizeRules` 归一化，使用设置页最新列表。
- 选中规则后：
  - 遍历 `_editors`（10 个常用字段）：目标字段为 `*` 或与编辑器 Key 相同才处理；当前文本为多值占位
    `<多值>` 时不自动改写，若该字段的候选值有任一匹配则计数；否则 `TagNormalization.Apply(rule, text)` 非 null
    时写回 `TextBox.Text`（不改 `InitialText`，因此保存时 `BuildResult` 会把它作为本次修改提交）。
  - 目标字段为 `*` 时额外遍历 `_additionalTagEditors`，对 `Value` 应用规则并写回（`AdditionalTagEditor` 实现
    `INotifyPropertyChanged` 以刷新绑定）；空替换在额外标签上保留原值（只改不删，与 ADR-025 一致）。
- 结果提示（标题下方状态文字，`TextSecondaryBrush`）：
  - 有改动：`规则「名称」：修改 N 个字段[，已跳过 M 个多值字段]。`
  - 无匹配且无多值：`当前标签没有匹配项`
  - 规则正则非法：直接显示 `IsValidPattern` 的错误文本，不应用。
- 不影响封面选择、其他标签删除、保存 / 取消流程；只有用户点击“保存”才会写入文件。

## 7. 主题与可用性

- 五套主题（PureStudio / WarmPaper / NordicIce / MidnightVinyl / SlateStudio）下均需可用。
- 颜色只用 `DynamicResource` 主题令牌：`PanelBackgroundBrush`、`PanelAltBackgroundBrush`、`BorderBrush`、
  `TextPrimaryBrush`、`TextSecondaryBrush`、`TextMutedBrush`、`AccentBrush`、`RowHoverBrush`、`AccentSoftBrush`、
  错误提示用 `DangerBrush`；等宽正则框用 `MonoFont`。
- 复用 `Controls.xaml` 既有样式：`SectionLabel`、`FlatButton`、`AccentButton`、`MutedText`。
- 所有新增文案为中文；无第三方依赖；设置页编辑只写异步 `settings.json`，不新增启动路径同步 IO。
- 文档与占位示例使用 dummy 名称（`Demo Album A`、`Demo Artist`、`Demo Track 01`），不出现真实歌曲 / 专辑名。

## 8. 涉及文件与接线点

| 文件 | 改动 |
| --- | --- |
| `src/EchoGarage.App/Views/SettingsWindow.xaml` | `PageFiles` 内新增“标签规范化”段：说明、规则列表模板、添加 / 恢复默认按钮、状态文字 |
| `src/EchoGarage.App/Views/SettingsWindow.xaml.cs` | `PopulateTagNormalizationRules`、`ApplyTagNormalizationRules`、`UpdateTagNormalizationRuleState`、增删 / 恢复默认 / 行编辑事件处理器、`TagNormalizationEditRow` |
| `src/EchoGarage.App/Views/MainWindow.xaml` | `TrackMenu` 新增“标签规范化”子菜单占位 |
| `src/EchoGarage.App/Views/MainWindow.xaml.cs` | `OnTrackMenuOpening` 新增分支 + `RebuildTagNormalizationMenu` |
| `src/EchoGarage.App/ViewModels/MainViewModel.cs` | 新增 `ApplyTagNormalizationAsync`；`ApplyTagEditsAsync` 返回 `Task<int>` |
| `src/EchoGarage.App/Views/TagPropertiesWindow.xaml` | “标签信息”标题行右侧“快捷规范化”按钮 + 结果状态文字 |
| `src/EchoGarage.App/Views/TagPropertiesWindow.xaml.cs` | 可选第三构造参数、`OnQuickNormalizeClick`、`ApplyQuickNormalization`、`AdditionalTagEditor` 支持通知 |
| `src/EchoGarage.App/Services/DialogService.cs` | 传入设置页最新规则列表 |

## 9. 验证步骤

自动：

1. Debug 构建 + 全量测试（`scripts/test.ps1`），基线 584 项通过（UI 工程不被测试工程引用，UI 改动由编译覆盖）。
2. Release 构建（`scripts/build.ps1 -Configuration Release`）0 错误。

手工（使用副本数据目录，如 `ECHOGARAGE_DATA_DIR`；不要修改 UAT 目录）：

1. 设置 → 文件与转换：段落在编码器之后、目标文件夹之前；默认两条规则显示“启用”；空正则行显示红色错误提示；
   输入非法正则 `[` 显示错误、该行不参与应用；名称 / 目标字段 / 正则 / 替换编辑后关闭再打开设置页，状态保持；
   检查 `settings.json` 中 `TagNormalizationRules` 已保存。
2. 勾掉全部规则或删除全部规则 → 重开设置页保持空；主窗口右键“标签规范化”显示“（没有启用的规则）”禁用项；
   点击“恢复默认规则”后两条规则回来。
3. 添加到达 50 条时“添加规则”禁用并提示上限。
4. 主窗口选中 1 首与多首歌曲（Ctrl 多选）→ 右键“标签规范化”→ 单条规则 / “应用全部启用规则”：
   状态栏先显示“正在检查标签…”，完成后显示“已规范化 N 首歌曲的标签（M 处修改）”；
   列表行、正在播放面板、封面（若专辑变化）同步刷新；无选中歌曲时子菜单禁用。
5. 使用 dummy 测试文件（例如 `Demo Artist - Demo Track 01.flac`，专辑 `Demo Album A - EP`）验证
   “移除专辑名 EP / Single 后缀” 与 “日期时间只保留日期” 的实际写入；检查额外标签未被删除。
6. 打开标签属性窗口 → 「快捷规范化」→ 选择规则：标题行右侧状态文字显示修改数量；多值字段显示
   “已跳过 N 个多值字段”；点击“保存”后歌曲行与文件标签更新；取消则不写入。
7. 五套主题下切换查看：边框 / 背景 / 文字 / 红色错误 / 按钮悬停均为主题色，无硬编码颜色。

## 10. 风险与限制

- **大批量耗时**：批量应用在后台逐首读取标签（每首一次文件 IO），数百首时“正在检查标签…”会持续一段时间；
  当前无进度条 / 取消，用户可在完成后重试；后续可加取消令牌与分批进度。
- **规则叠加顺序**：多条规则按设置页顺序依次应用（同一次 `ApplyToTag` 内顺序叠加），结果依赖顺序；
  当前不提供上移下移，顺序 = 列表顺序，编辑后即时保存。
- **额外标签只改不删**：通配规则对额外标签的空替换保留原值（ADR-025 语义），用户若希望清空需在标签属性窗口
  删除该额外标签。
- **数字字段校验**：年份 / 音轨号 / 碟片号只接受非负整数结果（0 = 清空），正则替换出非数字时该字段不写入；
  用户可能误以为规则未生效，需在文档 / 提示中说明。
- **非法正则**：Core 在应用时跳过非法规则；UI 行内提示错误，但不会阻止保存（与 ADR-025 一致）。
- **设置页即时保存**：每次编辑触发异步 `settings.json` 写入（沿用现有快捷键编辑行为）；若未来需要防抖，
  可在设置窗口统一加节流，不影响本文描述的 UI 结构。
- **右键菜单快照**：子菜单在 `ContextMenuOpening` 时构建，菜单打开期间在设置页改规则不会反映到当前菜单；
  关闭重开菜单即可。
- **多值字段不自动改写**：快捷规范化对 `<多值>` 字段只提示跳过，避免静默丢值；需要用户手动选择/输入。
