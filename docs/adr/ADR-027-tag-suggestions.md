# ADR-027 标签编辑候选值（艺术家 / 专辑艺术家 / 作曲家 / 流派）

- 状态：Accepted（owner 批准：2026-11-17）
- 日期：2026-11-16
- 相关：ADR-013（作曲家列）、ADR-019（标签编辑页）、ADR-025（标签规范化）、ADR-026（本轮其它标签/快捷键改动）

## 1. 背景与问题

手动编辑歌曲标签时，艺术家、专辑艺术家、作曲家、流派这些字段的值通常在媒体库里已经出现过
（例如同一艺术家的多张专辑、同一批文件的流派）。目前只能纯手输，容易产生 `Demo Artist` /
`demo artist` / `Demo Artist ` 这类近重复值，既慢又不一致。

希望：**在手动填写这四个字段时，自动列出媒体库里其它歌曲使用过的同字段值作为候选项**；
其余字段（标题、专辑、年份、音轨号、碟片号、备注）保持现状，不提供候选。

约束与风险：

- 媒体库可能很大（数万首），候选查询不能在打开标签窗口时卡住 UI，也不能每敲一个键就查一次数据库；
- 文件是唯一真相，但候选只用于输入辅助，不需要实时精确到文件变化的那一秒；
- 不新增数据库索引（避免拖慢扫描写入），不新增第三方依赖。

## 2. 决策

### 2.1 候选值来源与查询（Infrastructure）

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
  在百毫秒级（见 2.5），且只在进程内首次需要时执行一次。
- 新增仓储方法：

  ```csharp
  Task<TagSuggestionValues> GetTagSuggestionValuesAsync(
      int limitPerField = TagSuggestionValues.DefaultLimit,
      CancellationToken cancellationToken = default);
  ```

  返回快照 `TagSuggestionValues`（字段键 → 候选值列表），键使用 `TagFieldKeys` 常量：
  `artist`、`albumArtist`、`composer`、`genre`。

### 2.2 Core 中的纯逻辑

- `TagSuggestionValues`：不可变快照，`Get(fieldKey)` 缺失时返回空列表。
- `TagSuggestionMatcher.Match(candidates, input, limit = 10)`：
  - 空白输入返回空（不主动弹候选）；
  - 先收集**前缀命中**，再收集**仅包含命中**，同类内保持候选原顺序（即频率顺序）；
  - 大小写不敏感去重，候选两侧空白先 trim；
  - 最多返回 `limit` 条。
  - 放在 Core 以便单测，UI 只做显示/键盘交互。

### 2.3 进程内缓存与失效

- 新增 `ITagSuggestionService`（Core/Abstractions）与 `TagSuggestionService`（Infrastructure）：
  - 首次 `GetAsync` 时查询一次并缓存；并发调用用 `SemaphoreSlim(1,1)` 合并为一次查询；
  - 缓存成功结果；查询失败记录 `Warn` 并返回空快照，**不缓存失败**，下次重试；
  - `Invalidate()` 递增 generation 并清空缓存。查询期间若发生 `Invalidate`，旧结果不写回缓存。
- 应用层在 `MainViewModel.ReloadSourcesAsync()` 末尾调用 `_host.TagSuggestions.Invalidate()`。
  扫描、标签保存、添加文件、文件监听刷新等所有会改变媒体库缓存的流程最终都会走到这里，
  因此候选快照的刷新时机与来源列表/统计信息一致。
- 代价：媒体库变化到下次失效前，候选可能短暂滞后；对“输入辅助”而言可接受。

### 2.4 UI（标签属性窗口）

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

### 2.5 性能实测（合成库，Release）

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

## 3. 备选方案

| 方案 | 放弃原因 |
| --- | --- |
| 每次输入都查 SQLite（带前缀 LIKE） | 按键频率下频繁 I/O 与锁竞争，大库会卡；且需要额外索引 |
| 给 Artist/AlbumArtist/Composer/Genre 加索引 | 提高写入/扫描成本，收益仅此输入辅助功能 |
| 从音频文件即时读取全库标签 | 太慢，违背“数据库只是缓存”的性能红线 |
| 使用 WPF 自绘 AutoCompleteBox / 第三方控件 | 无必要，共享 Popup + ListBox 已满足需求且不引依赖 |
| 用 `LIKE` 在 SQL 里做前缀过滤 | 仍要每键查询；且中文/大小写/白空格行为不如内存 `OrdinalIgnoreCase` 可控 |

## 4. 后果、风险与限制

- 仅覆盖四个字段；标题/专辑/年份等不提供候选（按需求刻意收窄）。
- 候选按 `TRIM` 分组，但 SQLite 的 `GROUP BY` 大小写敏感：`Rock` 与 `rock` 可能各占一条；
  UI 匹配层做大小写不敏感去重，显示时只保留先出现者，排序仍以频率为主。
- 每字段最多 2000 个候选（频率靠后的长尾值不会出现）；如需更大可在常量处调整。
- 候选快照可能滞后到下一次 `ReloadSourcesAsync` 失效为止（扫描完成 / 标签保存后即刷新）。
- 查询失败时静默降级为空候选（日志 Warn）；用户仍可手动输入，功能不阻塞。
- 不涉及 schema 变更、配置变更或新依赖。

## 5. 测试与验证

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

## 6. 状态

- 代码与测试已完成（2026-11-16），等待 owner 批准后归档到 `docs/adr/` 并更新 `docs/adr/ADR-index.md`。
