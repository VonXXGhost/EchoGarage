# ADR-022：Web 搜索标签（第一阶段：Apple Music 日本网页源）

- 状态：已批准并完成第一阶段实现（Accepted，2026-11-15，owner；2026-11-15 实现完成并发布验收版本）
- 日期：2026-11-15
- 相关：ADR-003（文件是唯一真相）、ADR-018（外部文件标签刷新）、ADR-019（标签编辑器与封面转换）、ADR-020（播放中文件写入）
- 前置需求：owner 2026-11-15 对话（m00537），要求先调研规划、讨论确认；owner 已批准并说“开始实现”

## owner 已确认的决策（2026-11-15）

1. **接入方式**：解析 Apple Music 网页（mp3tag 的实现方式），不是 iTunes API。
2. **字段范围**：标准标签字段 + **作曲**；作曲是核心需求。版权、发行商、iTunes catalog ID、iTunes advisory 不在第一阶段。
3. **代理**：HTTP/HTTPS 代理；本机 `http://127.0.0.1:7890`。不支持 SOCKS5。
4. **多专辑**：一次只处理一个专辑组，不做多专辑队列。
5. **源配置**：第一阶段只做内置源的启用/默认；用户自定义源留到后续。
6. **远程值**：窗口内只读预览 + 勾选应用，不直接在 Web 窗口编辑。
7. **封面**：沿用现有标签编辑页的 JPG / 最大边长转换规则。
8. **搜索兜底**：接受从单曲结果反推专辑候选（最多 5 个，标注“由单曲结果推导”）。
9. **作曲缺失**：显示“无数据”、跳过作曲、不清除本地已有值，不伪造。
10. **封面默认值**：沿用现有 JPG / 质量 90 / 最大边长 1000，窗口内可调。
11. **手动链接兜底**：支持直接粘贴 Apple Music 专辑链接/纯数字 albumId；放在搜索页低存在感位置，作为低频兜底，不占主流程。
12. **部分专辑匹配**：本地只选中专辑的一部分曲目时，优先用已有音轨号自动匹配；最终匹配界面必须支持把本地文件拖到目标远程曲目行来纠正匹配，不能按本地文件数量/顺序强配。
13. **第三步元数据预览**：按 mp3tag 的方式分成「歌曲级标签（远程）」和「专辑级标签（远程）」两个独立面板；选中远程曲目行时歌曲级面板同步切换；字段勾选放在预览面板中，远程列表每行另有行级写入勾选。

本文档已获 owner 批准，第一阶段实现已完成并发布验收版本。

## 背景

现有「属性（标签）…」窗口只编辑本地标签，无法从网络补全缺失或错误的专辑/歌曲信息。
owner 希望增加一个**完全独立**的 Web 搜索标签功能，流程参考 mp3tag 的三步：

搜索（自动填充艺术家/专辑）→ 选择搜索结果 → 匹配并编辑标签。

明确约束：

1. 新功能不得影响现有 Tag 编辑窗口的行为。
2. Web 搜索通常以**专辑**为单位，一次处理多首文件；UI 与流程要体现这一点，但可以优化。
3. Web 源可配置；后端要有模块化抽象。第一阶段接入 Apple Music（日本）。
4. 网络调用需要可选代理设置；本机调试使用 `http://127.0.0.1:7890`。
5. 返回的标签很多，用户只勾选其中一部分写入；程序要记住用户勾选的字段。
6. 标签可能分为专辑级和歌曲级；不是所有源都会这样分，设计要兼容。

本提案只覆盖第一阶段；不修改现有标签编辑窗口的 UI 与交互。

## 调研结论

### 1. Apple Music 网页源可行

搜索页、专辑页都在 HTML 中内嵌：

```html
<script type="application/json" id="serialized-server-data">...</script>
```

搜索页的 JSON 中 `data[].data.sections` 包含若干 section：

- `topSearchLockup`：混合结果（专辑、歌曲、艺术家等）；
- `squareLockup`：专辑卡片（`contentDescriptor.kind == "album"`）；
- `trackLockup`：歌曲结果；
- `bubbleLockup`：艺术家。

专辑页的 JSON 中 section 依次为：

- `containerDetailHeaderLockup`：专辑标题、艺术家、曲目数、封面、`quaternaryTitle`（如 `アニメ · 2022年`）；
- `trackLockup`：曲目列表（音轨号、碟号、时长、`artistName`、`composer`）；
- `containerDetailTracklistFooterLockup`：页脚 description（发行日期、曲目数、`℗` 版权）、`linkSection`；
- `more-by-artist` / `you-might-also-like` 等推荐区块（忽略）。

此外还有 `<script type="application/ld+json">`（`MusicAlbum`），可补 `datePublished`、`genre`、`byArtist`、`image`。

### 2. 作曲字段确实存在

Apple Music 专辑页的 `trackLockup` 曲目对象带可选的 `composer` 字段。脱敏样本专辑
`Demo Album A - EP`（`albumId=0000000001`）：

- `Demo Track 01` → `Demo Composer A`
- `Demo Track 02` → `Demo Composer B`
- `Demo Track 03 (Demo Version)` → `Demo Composer C`

说明网页源可以满足“作曲”这个核心需求。但 `composer` 不是每张专辑都有：
`Demo Artist B` 的 `Demo Album B` 曲目对象里没有该字段。没有数据时 UI 显示“无数据”，
不写空值、不清除文件已有作曲，不伪造。

### 3. 搜索的两种情况

- 关键词较完整时，搜索页直接有 `squareLockup` 专辑结果；例如
  `Demo Artist A Demo Track 02` 能定位到上述样本专辑。
- 部分精确关键词只返回歌曲结果，没有专辑 section；例如
  `Demo Artist B Demo Track 09` 实测只得到歌曲和无关专辑。处理方式：
  1. 先收集所有 `contentDescriptor.kind == "album"` 的结果；
  2. 没有专辑时，从歌曲结果的 URL 提取 album ID（`/album/<slug>/<id>` 或 `?i=` 前面的路径），
     去重后作为候选；
  3. 最多抓取前 5 个候选的专辑页来显示标题/艺术家/曲目数，避免一次请求过多；
  4. 仍无结果时提示用户缩短关键词或只保留专辑名。

这个兜底不能保证一定命中目标专辑；它只是把“网页搜索只返回单曲”的情况转成可选择的候选，
最终由用户判断。

### 4. 封面

专辑 header 的 `artwork.dictionary.url` 是模板，例如：

```
https://is1-ssl.mzstatic.com/image/thumb/.../xxx.jpg/{w}x{h}bb.{f}
```

把后缀换成 `/1200x1200bb.jpg` 即可得到大图（实测 200、`image/jpeg`、约 343 KB）。
第一阶段下载 1200 图，再走现有标签编辑页的封面转换控件/规则（默认勾选 JPG、质量 90、最大边长 1000），
最终写入转换后的字节；转换失败则提示用户，不静默写入原图。

### 5. 代理

- 本机经 `http://127.0.0.1:7890` 访问 `music.apple.com` 正常（HTTP 200）。
- .NET BCL 的 `WebProxy` 支持 HTTP/HTTPS 代理，**不原生支持 SOCKS5**；本机 7890 同时提供 HTTP 代理，
  因此开发/自测使用 `http://127.0.0.1:7890`。
- owner 已确认不支持 SOCKS5；不新增第三方依赖。

### 6. 现有代码集成点

> 本节按“符号 + 文件”定位，行号以归档时为准（随后续代码演进会漂移）。

- `src\EchoGarage.App\AppHost.cs`：手写组合根；新增 Web 源 / HTTP 工厂 / 服务在这里装配（`WebTags` 装配段）。启动路径不联网。
- `src\EchoGarage.App\Services\DialogService.cs`：现有对话框统一入口；`EditTags` 方法构造
  `TagPropertiesWindow`；Web 窗口走新的打开方法。
- `src\EchoGarage.App\ViewModels\MainViewModel.cs`：`EditTagsAsync` 已有“读取标签 → 弹窗 →
  逐文件写入 → 重新索引 → 失效封面缓存 → 刷新列表/正在播放”的完整闭环；Web 写入复用/抽取该闭环，
  不复制第二份写文件代码。
- `src\EchoGarage.Core\Models\TagBatchEdit.cs`：`TagFieldKeys` 已含 `composer`；`TagBatchEdit` /
  `TagBatchEditor` 可直接承载 Web 结果，不需要为作曲扩展模型。
- `src\EchoGarage.Core\Models\Audio.cs`：`AudioTagData` / `TagEditModel` 已有 `Composer`；
  `TagLibTagService` 已写入 `Composers`。因此“标准字段 + 作曲”不需要改标签数据模型。
- `src\EchoGarage.App\Views\SettingsWindow.xaml`：设置页分区块、逐项即时保存；新增“网络与 Web 标签”区块。
- `src\EchoGarage.App\Views\MainWindow.xaml`：歌曲右键菜单；新增「Web 搜索标签…」。
- 启动路径不能做网络请求；Web 源只在用户打开窗口并点击搜索后才联网。

## 总体设计

### 1. 分层与职责

严格保持 `App → Infrastructure → Core`：

**Core（纯模型 / 纯规则，不引用 WPF、HttpClient、JSON）**

- `WebTagSearchQuery`、`WebTagSearchItem`、`WebTagRelease`、`WebTagTrack`、`WebTagFieldDefinition`。
- `WebTagFieldKeys` / `WebTagFieldCatalog`：字段键、中文名、专辑级/歌曲级、默认勾选状态。
- `IWebTagSource`：源适配器接口（搜索 + 取专辑详情）。
- `IWebTagService`：App 使用的跨层服务（源注册表 + 搜索/详情/封面下载 + 连接测试）。
- `WebTagMatcher`：本地文件与远程曲目的匹配算法（纯函数）。
- `WebTagApplyPlanner`：按“用户勾选字段 + 匹配结果”生成每文件的 `TagBatchEdit`（纯函数）。
- `AppSettings.Network` / `AppSettings.WebTags` 的设置模型与归一化规则。

**Infrastructure**

- `WebTagHttpClientFactory`：按当前设置创建带代理、超时、UA、解压的 `HttpClient`。
- `AppleMusicHtmlParser`：提取 `serialized-server-data` / JSON-LD，解析搜索页、专辑页。
- `AppleMusicWebTagSource`：把解析结果映射为 Core 的搜索/详情模型，处理搜索兜底与封面 URL。
- `WebTagService`：源注册表、调用源、下载/校验封面、连接测试、错误包装。
- `JsonSettingsService.Normalize`：新增设置项的默认值与非法值归一化。

**App（WPF）**

- 新窗口 `WebTagSearchWindow` + `WebTagSearchViewModel`，独立于 `TagPropertiesWindow`。
- `DialogService` 新增打开 Web 搜索窗口的方法。
- `MainViewModel` 新增命令：取选中行 → 打开窗口 → 拿到写入计划 → 复用现有写入/刷新闭环。
- 右键菜单新增「Web 搜索标签…」。
- 设置页新增「网络与 Web 标签」区块。

### 2. 源接口（Core）

```csharp
public interface IWebTagSource
{
    string Id { get; }
    string DisplayName { get; }
    WebTagSourceCapabilities Capabilities { get; }

    Task<IReadOnlyList<WebTagSearchItem>> SearchAsync(
        WebTagSearchQuery query, CancellationToken cancellationToken);

    Task<WebTagRelease> GetReleaseAsync(
        string releaseId, CancellationToken cancellationToken);

    /// <summary>尝试把用户输入识别为专辑链接/ID；不匹配返回 false。</summary>
    bool TryParseReleaseId(string input, out string releaseId);
}
```

`WebTagSourceCapabilities` 至少包含：

- `SupportsAlbumFields`
- `SupportsTrackFields`
- `SupportsCover`
- `SupportsComposer`（第一阶段 Apple Music 为 true，但单张专辑可能无数据）

这样即使某个源不区分专辑/歌曲，或者没有封面/作曲，UI 也能按能力显示/禁用相应区块，
不会硬编码 Apple Music 的结构。

`IWebTagService` 面向 App，负责：

- 列出已启用源；
- 按 sourceId 调用搜索/详情；
- 下载并校验封面字节；
- 用当前代理设置做连接测试。

### 3. 数据结构

- `WebTagSearchQuery(string SourceId, string Term)`。
- `WebTagSearchItem`：`SourceId`、`ReleaseId`、`Title`、`Artist`、`TrackCount?`、`Year?`、
  `ArtworkUrl?`、`SourceUrl?`、`ResultKind`（`Album` / `DerivedAlbum`）。
- `WebTagRelease`：`SourceId`、`ReleaseId`、标题、艺术家、专辑级字段字典、`IReadOnlyList<WebTagTrack>`、
  封面 URL、来源 URL、`WebTagReleaseKind`（`Album` / `Single` / `Unspecified`）。
- `WebTagTrack`：`DiscNumber`、`TrackNumber`、字段字典、`DurationMs?`。
- 字段键统一定义在 `WebTagFieldKeys`；内置源 ID 常量放在 `WebTagSourceIds`（第一阶段 `apple-music-jp`）：
  - 专辑级：`album`、`albumArtist`、`year`、`genre`、`cover`
  - 歌曲级：`title`、`artist`、`composer`、`track`、`disc`
  - 没有值的字段不出现在字典里；UI 显示为“无数据”并禁用勾选。
- 源按字段键填值，Core 的字段目录负责中文标签/顺序/默认勾选；不同源只返回自己支持的键，UI 自动兼容。

### 4. 匹配与写入计划

`WebTagMatcher` 必须支持“本地只选了专辑中的一部分曲目”的场景（例如远程 10 首、本地 3 首）。
自动匹配按以下优先级：

1. 本地文件有正整数音轨号：
   - 本地碟号也有效时，优先按 `disc + track` 精确匹配；
   - 本地碟号缺失或为 0 时，先按 `track` 在整张专辑中查找，唯一才匹配；
   - 多个远程曲目同号（多碟）时用标题相似度/时长消歧，仍不唯一则不自动匹配，交给用户拖拽。
2. 没有可用音轨号，或音轨号未命中：标题归一化后唯一匹配；
3. 其余**不按列表顺序强行匹配**，避免部分曲目、隐藏曲目或 bonus track 错位；未匹配文件保留在本地文件列表。
4. 时长只用于打破平局，不作为主匹配条件。

标题归一化：`NormalizationForm.FormKC`、去首尾空白、折叠连续空白、`ToLowerInvariant`；
保留括号内容，不做过度清洗。

匹配结果模型：

```csharp
public enum WebTagMatchKind { None, TrackNumber, Title, Manual }

public sealed record WebTagMatch(
    Track LocalTrack,
    WebTagTrack? RemoteTrack,
    WebTagMatchKind Kind);
```

- 自动匹配只生成 `TrackNumber` / `Title` / `None`；用户拖拽或下拉选择后为 `Manual`。
- 一个本地文件最多绑定一个远程曲目，一个远程曲目最多绑定一个本地文件；
- 手动把文件拖到已被占用的远程曲目时，替换旧文件并把旧文件退回“未匹配”；
- 手动把文件拖到另一个远程曲目时，先解除它原来的绑定；
- “重新自动匹配”会丢弃所有手动绑定并重新计算；用户确认（或写入前）再执行。

`WebTagApplyPlanner` 生成：

```csharp
public sealed record WebTagApplyPlan(IReadOnlyList<WebTagFileEdit> Edits);

public sealed record WebTagFileEdit(Track Track, TagBatchEdit Edit);
```

规则：

- **专辑级字段**：写入本次所选专辑组中的所有本地文件（不管是否匹配到远程曲目）。
- **歌曲级字段**：只写入匹配成功的文件；未匹配文件不写歌曲字段。
- **远程多余曲目**：远程有、但本地没有对应文件的曲目只展示、不写入，这是正常情况，不报错。
- **封面**：勾选且下载/转换成功时写入所有本地文件。
- 只写用户勾选、且远程有非空值的字段；**不用空值清除已有标签**。
- `year` 必须能解析为合理年份，`track`/`disc` 必须为正整数，否则跳过并记日志。
- `composer` 可能为空；空值不写，不清除文件已有作曲。
- 用户未勾选的字段不进入 `TagBatchEdit`；最终仍调用 `TagBatchEditor.CreateEdit`，
  未选中字段保留当前文件值。
- 每个本地文件最多匹配一个远程曲目；手动选择重复文件时，自动解除另一行并提示。

### 5. 写入流程

- Web 窗口只负责“选择 + 生成计划”，**不直接写文件**；点「写入标签」后关闭窗口并返回
  `WebTagApplyPlan`。
- `MainViewModel` 复用/抽取 `EditTagsAsync` 的写入闭环：
  1. `Task.Run` 读取每个文件当前 `AudioTagData`；
  2. `TagBatchEditor.CreateEdit(current, plan.Edit)`；
  3. `ITagService.Write`；
  4. `Scanner.IndexFileAsync`；
  5. 更新 `TrackRowViewModel`、失效旧/新封面缓存、刷新正在播放面板；
  6. 最后 `ReloadSourcesAsync`，汇总成功/失败数。
- 正在播放文件使用现有 `WriteViaReplacement` 逻辑（ADR-020），不新增特殊路径。
- 失败不中断整批；聚合错误并用 `SetStatus` + `Logger.Error/Warn` 提示。
- 封面转换在 Web 窗口中完成（App 层 `CoverImageProcessor`），生成最终字节后放入
  `TagBatchEdit.NewCoverBytes/NewCoverMime`；写入链路不再重复转换。

## 交互设计

### 入口与范围

- 新窗口建议初版 `1100×760`、可调整大小、最小 `980×640`，使用现有主题令牌，不硬编码颜色。
- 歌曲列表右键菜单新增「Web 搜索标签…」，放在「属性（标签）…」之后。
- 仅对当前选中行（无选中则当前行）生效；至少需要 1 个文件；无可用行时菜单项禁用。
- 选中行可能属于多个专辑：窗口顶部提供「要搜索的专辑」下拉，按“专辑艺术家 + 专辑名”
  （无专辑时按目录）分组；一次只处理一个专辑组。**第一阶段不做多专辑排队**。

### 第一步：搜索

- 显示当前专辑组摘要：`专辑艺术家 — 专辑（N 首）`。
- 搜索源：下拉选择已启用源；第一阶段只有「Apple Music（日本）」。
- 搜索关键词：文本框，默认由「专辑艺术家 + 专辑名」组成；缺失时退回「艺术家 + 专辑名」，二级回退为「艺术家 + 歌曲名」；
  用户可手动修改。
- 支持直接粘贴 Apple Music 专辑链接或纯数字 albumId（如
  `https://music.apple.com/jp/album/xxx/0000000001` 或 `0000000001`）：识别后直接请求专辑详情，
  跳过搜索结果页进入第三步。该入口放在搜索框下方，用低存在感的弱化文字链（如
  “搜不到？直接打开专辑链接/ID”）折叠展开，不占主流程空间；这是低频兜底，
  用于搜索索引缺失、地区/出口差异导致搜不到目标专辑的情况。
- 不自动联网；点击「搜索」或按 Enter 才请求。打开窗口本身不产生网络请求。
- 关闭窗口或切换步骤时取消仍在进行的搜索/详情/封面请求，避免回调打到已关闭的窗口。
- 空结果提示：如果有网络报错，则弹框提示，如果是正常获得结果为空，则保持空输出即可不需要其他提示。

```
┌ Web 搜索标签 ──────────────────────────────────────────────┐
│  ① 搜索        ② 选择结果        ③ 匹配与写入              │
│  当前文件：Demo Artist — Demo Album A（7 首）              │
│  搜索源：[Apple Music（日本）▾]                             │
│  搜索关键词：[ Demo Artist Demo Album A             ] [搜索]│
│                                      [取消]  [下一步 →]     │
└───────────────────────────────────────────────────────────┘
```

### 第二步：选择结果

- 列表列：封面缩略图（可选）、专辑、艺术家、曲目数、年份。
- `DerivedAlbum`（从歌曲结果反推的候选）在列表中标注“由单曲结果推导”，并显示来源曲目数；
  用户仍可正常选择。
- 选中行右侧/下方显示封面预览和基本信息；双击或「下一步」加载专辑详情。
- 「上一步」返回搜索并保留关键词与结果。
- 加载详情失败时停在当前步，显示错误，可重试。

```
┌ Web 搜索标签 ──────────────────────────────────────────────┐
│  ① 搜索        ② 选择结果        ③ 匹配与写入              │
│  搜索“Demo Artist Demo Album A”的结果                       │
│  ┌─────────────────────────────────────────────────────┐  │
│  │ 专辑                    艺术家       曲目  年份       |  |
│  │ Demo Album A          Demo Artist    7    20XX      |  |
│  └─────────────────────────────────────────────────────┘  │
│  选中详情：封面 / 专辑名 / 艺术家 / 年份 / 曲目数           │
│                          [← 上一步] [取消] [下一步 →]      │
└───────────────────────────────────────────────────────────┘
```

### 第三步：匹配与写入

- 顶部固定显示「远程元数据预览 + 字段勾选」，参考 mp3tag 分成两个独立面板：
  - **歌曲级标签（远程）**：显示当前选中的远程曲目（如 `#03 曲目名`）的标题、艺术家、作曲、
    音轨号、碟号；每行 `☑ 字段名  远程值`。切换下方远程曲目行时，面板同步切换；
    没有选中行时提示“请在下方选择远程曲目”。
  - **专辑级标签（远程）**：显示整张专辑的专辑、专辑艺术家、年份、流派，以及封面缩略图；
    每行 `☑ 字段名  远程值`。这些字段对本次所有本地文件一致。
  - 远程无值的字段值显示“无数据”，checkbox 置灰；保留用户之前的选择记忆，但不写入。
  - **歌曲级字段的 checkbox 是全局写入策略**：勾选后，每个匹配文件写入“它自己对应的远程曲目”
    的该字段值，不是把上方预览的这一首值复制给所有文件；上方只负责预览当前选中行。
    远程列表每行另有行级“写入”checkbox。
  - 默认勾选全部字段（含作曲、封面）。
- 匹配工作台（下方左右两栏）：
  - **左侧「远程曲目」列表**：按专辑顺序显示全部远程曲目（例如 10 行），每行：
    行级写入勾选、远程序号、标题、艺术家、作曲、时长、已绑定本地文件、匹配方式
    （音轨号 / 标题 / 手动）。选中一行会更新上方歌曲级标签预览。
  - **右侧「本地文件」列表**：显示本次选中的全部本地文件（例如 3 行），每行：
    拖动把手、当前音轨号（没有则显示“无序号”）、标题/文件名、时长、匹配状态
    （`→ #03 曲目名` 或 `未匹配`）。
  - **拖拽匹配**：从右侧本地文件拖到左侧目标远程曲目行；目标行高亮，松手即绑定。
    这是“调整匹配”的主要操作，**不是只改变本地列表的显示顺序**，
    因此只选 3 首也能精确对应到远程第 3、5、7 首，不会因为数量不同而错位。
  - 已匹配文件仍可从右侧再次拖到另一行；拖到已占用行会替换，原文件退回未匹配。
  - 已匹配文件可从左侧行尾 `×` / 右键「解除匹配」取消绑定，或拖回右侧空白区域。
  - 每行另提供「本地文件」下拉作为键盘/无障碍替代方案；重复选择同一文件时自动解除旧绑定并提示。
  - 「重新自动匹配」只重置自动结果；「清空匹配」解除全部绑定（需确认）。
  - 未匹配的远程行不隐藏、不置灰到不可操作；未匹配的本地文件仍会写入专辑级字段，
    只是不写歌曲级字段。
- 摘要按部分专辑场景显示：
  `本地 3 首 / 远程 10 首：已匹配 3 首；远程 7 首未使用（正常）；0 个本地文件未匹配`。
  若有未匹配本地文件，追加 `1 个本地文件未匹配，仅写专辑级字段`。
- 封面转换区（与现有标签编辑页一致）：
  - ☑ 保存时转换为 JPG（默认勾选）
  - 质量（1–100，默认 90）
  - 最大边长（px，默认 1000；0 表示不限制）
  - 只缩小、不放大；已是 JPG 且小于目标边长时跳过；无需缩放时仅当转换后体积更小才采用。
  - 转换在下载完成后执行；失败显示错误，允许重试或取消勾选封面。
- 「写入标签」只在至少一个字段被勾选、且至少有一个可写本地文件时启用；专辑级字段不要求匹配，
  歌曲级字段只对匹配成功的文件生效。点击后先保存字段记忆，再关闭窗口交给主窗口执行。
- 关闭/取消不写任何文件；字段勾选变化在关闭时保存。

```
┌ Web 搜索标签 ──────────────────────────────────────────────────────────────┐
│  ① 搜索        ② 选择结果        ③ 匹配与写入              [← 返回结果]     │
│ ┌ 歌曲级标签（远程）──────────────────┐ ┌ 专辑级标签（远程）────────────────┐│
│ │ 当前曲目：#03 デモ曲03              │ │ ☑ 专辑       デモアルバムA       ││
│ │ ☑ 标题       デモ曲03              │ │ ☑ 专辑艺术家 デモアーティスト    ││
│ │ ☑ 艺术家     デモアーティスト      │ │ ☑ 年份       20XX               ││
│ │ ☑ 作曲       デモ作曲者C           │ │ ☑ 流派       デモジャンル        ││
│ │ ☑ 音轨号     03                   │ │ ☑ 封面       [封面缩略图]        ││
│ │ ☑ 碟号       1                    │ │                                 ││
│ └───────────────────────────────────┘ └─────────────────────────────────┘│
│ ┌ 远程曲目（10 首）─────────────────────┐ ┌ 本地文件（3 首）───────────────┐│
│ │▶☑ 03 デモ曲03 ... [A.flac] 音轨号      │ │☰ A.flac [03] → #03            ││
│ │ ☑ 04 デモ曲04 ... [未匹配]   —        │ │☰ B.flac [无序号] 未匹配       ││
│ │ ☑ 05 デモ曲05 ... [B.flac] 手动        │ │☰ C.flac [07] → #07            ││
│ │ ☑ 07 デモ曲07 ... [C.flac] 音轨号      │ │                                ││
│ │ ...（其余远程曲目正常显示）              │ │ 拖到左侧远程行进行绑定          ││
│ └────────────────────────────────────────┘ └────────────────────────────────┘│
│  本地 3 首 / 远程 10 首：已匹配 3 首；7 首远程未使用（正常）                │
│  ☑ JPG 质量 90  最大 1000                         [← 上一步] [取消] [写入标签]│
└────────────────────────────────────────────────────────────────────────────┘
```

- 上图中 `▶` 为当前选中的远程曲目，选中项同时决定上方「歌曲级标签（远程）」显示哪一首；
  上方两个面板里的 checkbox 是“要写哪些字段”，下方远程行左侧的 `☑` 是“这一行是否参与写入”。
- 上图的左右布局对齐 mp3tag：上方左侧歌曲级预览、右侧专辑级预览；下方左侧远程曲目、
  右侧本地文件。拖拽方向是“右侧本地文件 → 左侧远程曲目行”。

## Apple Music 网页源实现细节

### 请求与 HTTP

- 统一走 `WebTagHttpClientFactory` 创建的 `HttpClient`：
  - `SocketsHttpHandler`，`AutomaticDecompression = All`，`ConnectTimeout = 15s`，整体 `Timeout = 20s`，
    不保存 Cookie。
  - 代理模式：跟随系统 / 不使用 / 自定义 HTTP 代理；自定义 URL 只接受 `http://` 或 `https://`。
  - UA 使用常见浏览器 UA；`Accept-Language: ja-JP,ja;q=0.9`。
  - 搜索/专辑页响应限制 ≤ 20 MB；封面限制 ≤ 15 MB。
- 请求：
  - 搜索：`https://music.apple.com/jp/search?term=<Uri.EscapeDataString(term)>&l=ja-JP`
  - 专辑：`https://music.apple.com/jp/album/<albumId>?l=ja-JP`
- 不登录、不使用 API key、不保存 Cookie。

### 搜索页解析

1. 用不区分大小写的正则提取
   `<script ... id="serialized-server-data">...</script>` 的内容并 `JsonDocument.Parse`。
2. 在 `data[].data.sections` 中遍历：
   - 收集 `contentDescriptor.kind == "album"` 的 item 作为专辑结果；
   - 收集 `trackLockup` 的歌曲 item，保存标题、艺术家、`cdUrl`/`contentDescriptor.url`、封面。
3. 专辑 item 映射：
   - `ReleaseId`：从 `contentDescriptor.url` 末段或 `identifiers.storeAdamID` 提取；
   - 标题：`titleLinks[0].title`，否则 `title`；
   - 艺术家：`subtitleLinks[0].title`；
   - 曲目数：`trackCount`；
   - 封面：`artwork.dictionary.url` 模板，转 1200；
   - 年份/流派：搜索结果通常没有，列表显示为空或从详情补。
4. 没有专辑结果时：
   - 从歌曲 URL 提取 album ID：优先 `contentDescriptor.url`，其次 `cdUrl`；
   - 按出现顺序去重，最多取前 5 个；
   - 对每个 album ID 抓取专辑页，取标题/艺术家/曲目数/封面，标记 `ResultKind=DerivedAlbum`；
   - 任何候选抓取失败都跳过并记 `[WARN]`，不影响其余候选。
5. 搜索词不做自动改写；UI 空结果时提示用户缩短关键词。后续可增加“只搜专辑名”的重试按钮，但不进入第一阶段必做项。

### 专辑页解析

1. 提取 `serialized-server-data`，找到 `data[0].data.sections`。
2. `containerDetailHeaderLockup`：
   - `title`、`subtitleLinks[0].title`（艺术家）、`trackCount`、`artwork.dictionary.url`、
     `quaternaryTitle`（流派/年份展示用）、`contentDescriptor`（album ID）。
3. `trackLockup` 每个 item：
   - `title`、`trackNumber`、`discNumber`、`duration`（毫秒）、`artistName`、`composer`（可能 null）、
     `contentDescriptor.identifiers.storeAdamID`（可选）；
   - `artwork` 通常为空，用专辑 header 封面。
4. `containerDetailTracklistFooterLockup`：
   - `description` 可解析发行日期/曲目数/`℗` 版权；第一阶段只用于补年份/展示，不写入版权字段；
   - `linkSection` 可补发行商，第一阶段不写入。
5. JSON-LD：
   - 用 `MusicAlbum` 的 `datePublished` 补年份、`genre` 补流派、`byArtist` 兜底艺术家；
   - JSON-LD 解析失败不阻塞，优先使用 `serialized-server-data`。
6. 字段缺失/类型不符时不要抛整页失败，记录 `[DEBUG]`/`[WARN]`，该字段显示“无数据”。

### 字段映射

| 字段键 | Apple Music 网页来源 |
|---|---|
| `album` | header `title` |
| `albumArtist` | header `subtitleLinks[0].title` |
| `year` | JSON-LD `datePublished` 前 4 位，或 footer `description` 日期 |
| `genre` | JSON-LD `genre[0]`，或 header `quaternaryTitle` 的流派部分 |
| `cover` | header `artwork.dictionary.url` → 1200 |
| `title` | track `title` |
| `artist` | track `artistName`，无则 header 艺术家 |
| `composer` | track `composer`（可能为空） |
| `track` | track `trackNumber` |
| `disc` | track `discNumber` |

`duration` 仅用于匹配平局和 UI 显示，不写入标签。

### 封面下载

- 把模板中的 `{w}x{h}bb.{f}` 替换为 `1200x1200bb.jpg`；若 URL 不含模板，直接请求原 URL。
- 校验响应 `Content-Type` 为 `image/jpeg`/`image/png`，并检查文件头；非法则不采用。
- 下载后交给 App 层 `CoverImageProcessor.ConvertToJpeg`（沿用现有标签编辑页规则），
  得到最终字节与 MIME。
- 同一窗口内按 releaseId 缓存已下载封面，避免重复请求。

### 错误、缓存与频率

- 非 2xx、HTML 不含 JSON、JSON 解析失败、页面结构改变、封面非法 → 抛带中文消息的
  `WebTagSourceException`，记录 `[WARN]`/`[ERROR]`，不记录完整响应体。
- 403/429 提示“Apple Music 请求过于频繁或暂时不可用，请稍后再试”；不自动重试。
- 同一窗口内按 releaseId 缓存 `WebTagRelease`；不做持久化缓存，避免元数据过期。
- 一次操作通常 1 次搜索 + 1 次专辑页 + 1 次封面；搜索兜底最多额外 5 次专辑页。
  所有请求串行执行，避免突发并发。

## 设置与代理

### 新增设置模型

```csharp
public enum WebProxyMode { System, Direct, Custom }

public sealed class NetworkSettings
{
    public WebProxyMode ProxyMode { get; set; } = WebProxyMode.System;
    public string CustomProxyUrl { get; set; } = "";
}

public sealed class WebTagSettings
{
    public List<string> EnabledSourceIds { get; set; } = [];
    public string DefaultSourceId { get; set; } = "";
    public Dictionary<string, List<string>> SelectedFields { get; set; } = [];
}
```

- `AppSettings` 增加 `Network`、`WebTags` 两个属性；旧 JSON 缺失时用默认值。
- `JsonSettingsService.Normalize`：
  - 枚举非法 → `System`；
  - 自定义模式 URL 非法 → 回退 `System`（设置页同时给出错误提示）；
  - 源 ID 只保留已注册源；空列表回退 Apple Music 日本；默认源不在启用列表时取第一个；
  - `SelectedFields` 丢弃未知字段键；缺失源时使用字段目录默认值，显式空列表保持为空。

### 设置页「网络与 Web 标签」

- 代理模式：跟随系统 / 不使用代理 / 自定义代理。
- 自定义代理地址：`http://127.0.0.1:7890`，仅自定义模式可编辑；旁边「测试连接」。
- 测试连接：用当前控件值构造客户端，请求 Apple Music 搜索页（`limit` 无意义，可用固定关键词），
  显示成功/失败/超时；不写任何文件。
- Web 标签源列表：第一阶段只有「Apple Music（日本）」；提供启用勾选与默认源选择。
- 字段记忆不在设置页出现，由 Web 窗口的复选框维护；关闭窗口时保存。
- 设置即时保存（沿用现有 `SaveSettings()`）；代理变更后无需重启，下一次请求生效。

## 影响与风险

### 影响

- 新增 Core/Infrastructure/App 若干文件；`AppSettings` / `JsonSettingsService` / `AppHost` /
  `DialogService` / `MainViewModel` / `MainWindow` / `SettingsWindow` 有改动。
- 不需要为作曲扩展标签模型：`AudioTagData`、`TagEditModel`、`TagFieldKeys`、`TagBatchEdit`、
  `TagLibTagService` 已支持 `Composer`。
- 不新增第三方 NuGet 包；HTTP 与 JSON 均使用 BCL。
- 启动路径不联网；`WebTagService` 构造时不创建连接。
- 现有标签编辑窗口和写入逻辑保持行为不变；旧测试必须继续通过。

### 风险与缓解

- **Apple 页面改版**：解析器集中在 `AppleMusicHtmlParser`，用固定 HTML fixture 做回归；
  结构不匹配时给可读错误，不崩溃、不写半截数据。
- **搜索无专辑结果**：先直接专辑结果，再歌曲结果反推候选；仍无结果则由用户改关键词，
  或直接粘贴 Apple Music 专辑链接/ID。
- **搜索索引/地区差异**：Apple Music 搜索会受 storefront、出口地区与索引影响，同一关键词
  在不同网络下结果可能不同；因此提供可编辑关键词和“直接粘贴链接/ID”兜底，不承诺任意关键词都能搜到。
- **匹配错曲目**：按碟号/音轨号/标题匹配，歧义不自动匹配；本地只选部分曲目时绝不按数量
  或列表顺序强配；手动拖拽覆盖；未匹配文件只写专辑级字段。
- **部分专辑匹配混乱**：匹配区始终同时显示全部远程曲目和全部本地文件（远程在左、本地在右），
  拖拽为显式绑定；摘要分别统计“已匹配 / 本地未匹配 / 远程未使用”，避免把 3 首误写成前 3 首。
- **作曲缺失**：显示“无数据”，不写空值、不清除本地已有值；不伪造。
- **页面体积大**：搜索页可达数百 KB；只解析一次、限制 20 MB、不保留原始 HTML 到内存之外。
- **频率/反爬**：用户显式触发、请求串行、窗口内缓存、不做批量后台请求；403/429 可读提示。
- **代理协议**：只支持 HTTP/HTTPS；SOCKS5 不实现。
- **误清空标签**：空远程值不写；未勾选字段不动；不做“清除未选中字段”。
- **大封面**：下载 1200 图后按现有转换规则处理，限制 15 MB。
- **隐私**：只把搜索关键词/专辑 ID 发给 Apple，不上传本地库文件或路径；代理可选。
- **法务/条款**：网页解析属于非官方接口，可能受 Apple 使用条款限制；功能默认关闭、用户显式使用，
  不内置绕过登录/付费墙逻辑。若 owner 需要更稳妥的长期方案，后续可切换 API 或官方授权源。

## 测试计划

### 单元测试

- `AppleMusicHtmlParserTests`（固定 HTML fixture，不联网）：
  - 搜索页：专辑 section、歌曲 section、混合 `topSearchLockup`；
  - 专辑链接/纯数字 albumId 识别（含 `?i=`、无效链接、非 Apple Music 域名）；
  - 专辑页：header/track/footer/JSON-LD 映射；`composer` 有值/为 null；
  - 搜索兜底：无专辑 section 时从歌曲 URL 提取 album ID、去重、限 5 个；
  - HTML 不含 JSON、JSON 损坏、section 缺失、字段类型异常；
  - 封面 URL 1200 变换、无法变换时原 URL。
- `AppleMusicWebTagSourceTests`（Fake `HttpMessageHandler`）：
  - 搜索 → 结果映射（ID、标题、艺术家、曲目数、封面、`ResultKind`）；
  - 专辑 → 专辑字段 + 曲目字段（含 `composer`）；
  - 兜底候选抓取失败时跳过；空结果；非 2xx；429。
- `WebTagMatcherTests`：
  - 本地只选 3 首、远程 10 首时，按音轨号 3/5/7 精确匹配，其余远程曲目保持未使用；
  - 碟号缺失/为 0、多碟同号、音轨号重复时的歧义处理；
  - 标题全角/半角、大小写、空白归一化；
  - 重复标题不自动匹配；时长平局；未匹配。
- `WebTagApplyPlannerTests`：
  - 专辑级字段写所有文件，歌曲级只写匹配文件；
  - 部分专辑场景：未匹配本地文件只写专辑级字段；远程多余曲目不被写入；
  - 只写勾选且非空字段；不勾选字段不进 `TagBatchEdit`；
  - 年份/音轨号/碟号非法值跳过；`composer` 空值跳过；
  - 封面写所有文件；未匹配文件只专辑字段。
- `WebTagMatchEditorViewModelTests`（或等价的 App 层测试）：
  - 拖拽/手动绑定后匹配方式变为 `Manual`；重复绑定自动解除；
  - 拖到已占用远程行时替换并退回旧文件；“重新自动匹配”/“清空匹配”行为；
  - 切换选中的远程曲目时「歌曲级标签（远程）」预览同步更新；歌曲级字段勾选是全局策略、
    行级写入勾选只影响该行；无远程值字段置灰但保留勾选记忆。
- `WebTagHttpClientFactoryTests`：三种代理模式、URL 校验、超时设置。
- `JsonSettingsService` / `WebTagSettings`：旧 JSON 默认值、非法代理回退、源 ID 过滤、
  字段记忆往返、未知字段丢弃。
- `WebTagFieldCatalogTests`：字段顺序、专辑/歌曲 scope、默认勾选、未知键忽略。
- 现有标签测试不回归：`TagBatchEditTests`、`TagAndCoverTests`（尤其是 `Composer` 写入）。

### 实机 / 集成

- 经 `http://127.0.0.1:7890` 搜索测试专辑（文档中使用 dummy 值，真实名称不落盘）：
  - `Demo Artist B Demo Album B`（无作曲数据，验证“无数据”不写）；
  - `Demo Artist A Demo Track 02` 或 `Demo Album A`
    （有作曲数据，验证 `Demo Track 02 → Demo Composer B` 写入）。
- 在 `.scratch` 复制 FLAC/MP3/M4A 样本，走完整“选择 → 匹配 → 写入”，验证标签与封面写入、
  UI 刷新、数据库重新索引；检查作曲字段按文件格式正确写入。
- 部分专辑场景：本地只选 3 首（例如音轨号 2/5/7，另含一首无音轨号），验证自动匹配、
  拖拽纠正、远端多余曲目只展示不写入、未匹配文件只写专辑级字段。
- 应用两次：第二次不应改变未勾选字段；空值不清除旧值。
- 测试文件正在播放时写入（沿用 ADR-020 替换路径）。
- 设置代理为错误地址/超时，确认错误提示与可恢复。
- 启动时断网启动，确认主窗口正常、启动路径无网络请求。
- 现有测试：`dotnet build EchoGarage.sln`、现有 326 个测试 + Web 标签新增 98 个 = **424 个测试全部通过**。

## 分阶段与验收

### 第一阶段（本提案范围）

1. Core 模型/接口/字段目录/匹配器/计划器。
2. Infrastructure HTTP 工厂 + `AppleMusicHtmlParser` + `AppleMusicWebTagSource` + 源注册服务。
3. App 独立三步窗口、字段勾选记忆、搜索兜底候选、部分专辑音轨号匹配、
   mp3tag 式专辑级/歌曲级双面板预览、左右分栏拖拽匹配、封面转换、写入计划。
4. 右键入口、`DialogService`/`MainViewModel` 接入、写入与刷新闭环复用。
5. 设置页代理与源配置、连接测试。
6. 单元/集成测试、release 发布、实机验证。

### 验收标准

- 右键「Web 搜索标签…」打开独立窗口，不影响现有「属性（标签）…」。
- 能经代理搜索 Apple Music 日本、选择专辑、查看专辑/歌曲字段与封面。
- 第三步同时显示「专辑级标签（远程）」和所选远程曲目的「歌曲级标签（远程）」；切换远程曲目行时
  歌曲级预览同步更新；字段勾选与远程行的行级写入勾选含义清晰、互不混淆。
- 搜索正常但无结果时保持空列表，不弹额外提示；只有网络/解析错误才给出错误提示。
- 有作曲数据的专辑能把作曲写入匹配文件；无数据时不写、不报错、不伪造。
- 可勾选字段并记忆；关闭再打开保持选择。
- 自动匹配 + 手动拖拽修正后写入选中文件；部分专辑（本地 3 首 / 远程 10 首）能按音轨号
  正确匹配，远程多余曲目正常显示且不写入；封面按现有转换规则写入；UI/库状态刷新。
- 拖拽本地文件到远程行可绑定/替换/解除匹配；重复绑定会被阻止并提示。
- 未勾选字段、空远程值、未匹配文件的行为符合上述规则。
- 设置页代理测试可用；启动不联网。
- `dotnet build` 0 warning / 0 error；全部测试通过；发布版本可运行。

### 明确不做（第一阶段）

- 多个专辑排队处理；一次只处理一个专辑组。
- 按“本地文件列表顺序 → 远程前 N 首”的自动强配；拖拽只做显式绑定，不做纯排序式匹配。
- 用户自定义 Web 源脚本/配置文件；只做内置源注册表。
- 远程值在窗口内手工编辑（只做勾选应用；编辑仍走现有标签窗口）。
- 版权、发行商、iTunes catalog ID、iTunes advisory 写入。
- 总音轨数/总碟数写入。
- 搜索结果预览播放（previewUrl）。
- 自动搜索/自动应用；所有网络与写入都由用户显式触发。
- 用其他来源（MusicBrainz、Discogs 等）补作曲；如 Apple 无作曲数据，第一阶段只显示无数据。

## 备选方案

1. **iTunes Search/Lookup API**：结构化、稳定，但无作曲字段，无法满足核心需求；不采用。
2. **API 为主 + HTML 兜底**：实现量最大，且两条链路都要维护；第一阶段不采用。
3. **单窗口大表单而非三步**：开发少一步，但搜索结果、详情、匹配挤在一起，多文件场景更乱；不采用。
4. **直接在现有 TagPropertiesWindow 加“Web 搜索”按钮**：违反 owner“完全独立、不影响现有编辑”的要求；不采用。
5. **多专辑队列一次处理**：容易误写，违反“可控”原则；后续如需要再加。
6. **使用 MusicBrainz / Discogs 等其他源**：源抽象已预留，但 owner 明确第一阶段 Apple Music 日本；不采用。

## 已确认 / 待确认

已确认（2026-11-15）：见文首 13 条 owner 决策，包括搜索兜底、作曲缺失处理、封面默认值、
手动链接兜底、部分专辑的拖拽匹配和第三步双面板预览。

待确认：无阻塞项。后续如需调整字段清单、默认勾选或 UI 细节，可在实现前继续讨论。

## 审批后行动（已完成）

- 本提案已移入 `docs/adr/ADR-022-web-tag-search.md` 并更新 `docs/adr/ADR-index.md`；
- 按第一阶段清单实现，保持现有标签编辑器不回归，全部 424 个测试通过；
- 已完成实机验证（Apple Music 日本网页搜索、专辑详情、作曲缺失/存在、部分专辑匹配、封面转换、
  实际写入与回读），并编译 release 发布版本 `artifacts/publish/win-x64` 供验收（未打包压缩包）。

## 未做 / 后续

- iTunes API 源 / API + HTML 混合源。
- 版权、发行商、iTunes ID、advisory 等字段。
- 用户自定义源（mp3tag `.src` 风格或 JSON 配置）。
- 多专辑队列/批量匹配优化。
- 远程字段编辑、差异预览、撤销。
- 搜索结果预览播放、封面尺寸/质量选项。
- 其他 Web 源（MusicBrainz、Discogs 等）。
