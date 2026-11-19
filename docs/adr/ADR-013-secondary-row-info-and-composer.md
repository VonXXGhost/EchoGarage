# ADR-013：列表行次要信息与作曲家标签

> 状态：已批准（Accepted，2026-11-14，owner）
> 日期：2026-11-13
> 相关：ADR-003（文件是唯一真相）、ADR-005（布局持久化）、ADR-010（缓存行失效）、KB-014（分组表头固定）

## 背景

owner 在 2026-11-13 提出（参考图：foobar2000 风格的双行行信息）：

- 列表行增加**次要信息**显示，并提供**可选开关**；
- 每个主列可单独配置次要内容，例如标题下显示流派、艺术家下显示作曲家、时间下显示码率；
- 次要内容支持**复合格式**，例如时间下显示 `码率/采样率`（示例输出 `402K/48Khz`）；
- 没有配置的列不显示次要行，且次要行与主行之间要有字号、色彩的层次差异。

示例中的“作曲家”在当前数据模型（`Track`、`AudioTagData`、`TagEditModel`、SQLite 缓存）中不存在，需要一并补齐，否则无法配置。

## 决策

### 1. Composer 作为一等标签字段（贯穿文件 ↔ 缓存）

- `Track.Composer`、`AudioTagData.Composer`、`TagEditModel.Composer` 新增；`TagLibTagService` 读 `JoinedComposers`（回退 `FirstComposer`）、写 `tag.Composers`。
- `LibraryScanner` 扫描时写入 `Composer`；`TagPropertiesWindow` 增加“作曲家”编辑行，保存仍直接写文件（ADR-003）。
- SQLite schema 升到 v2：`ALTER TABLE tracks ADD COLUMN Composer TEXT NOT NULL DEFAULT ''`，`PRAGMA user_version = 2`。`InitializeAsync` 改为顺序迁移（v1 建表 → v2 加列），旧库自动升级；两个 SQLite 仓储的列清单与 Upsert 同步。缓存列由文件扫描刷新，符合 ADR-010（后续 v3 / v4 缓存修复迁移见 ADR-014）。

### 2. 布局模型（Core）

- `PlaylistColumn.SecondaryFormat`（`string?`）+ `PlaylistViewLayout.ShowSecondaryInfo`（默认 `false`），随布局、预设、Clone 序列化。
- `ViewLayoutFactory.Normalize`：清洗格式（去换行/制表、折叠空白、截断 160 字符、全空置 null），隐藏列也保留格式（重新显示时恢复）。
- `SecondaryTextFormat`：纯函数格式引擎，token 表覆盖共 20 项：title/artist/album/albumArtist/composer/genre/year/trackNumber/discNumber/duration/bitrate/sampleRate/channels/codec/fileSize/addedAt/modifiedAt/path/fileName/comment；支持 `{token}`、`{{`/`}}` 转义、大小写不敏感；token 值为空时自动清理相邻分隔符（`{bitrate}/{sampleRate}` 在采样率缺失时输出 `320K` 而不是 `320K/`），全部为空返回空串。
- 评分 / 播放次数不进入次要信息 token：owner 2026-11-13 明确现阶段不做播放统计与歌曲评级等动态信息，因此 `rating` / `playCount` 从 token 表移除，`ColumnCatalog` 的“评分”列与相关显示/主题资源一并删除；数据库中的历史字段保留，不新增功能。

### 3. 渲染（App）

- `TrackRowViewModel.SecondaryTexts`：由当前布局的格式配置 + 轨道元数据计算，`ApplySecondaryFormats` 在布局重建/轨道更新时刷新；值缺失的列不产生条目。
- 除封面 / 状态 / # 外，每个 `Cell.*` 模板改为两行 `StackPanel`：主行保持原样式，次行 `FontSize=11`、`TextMutedBrush`、`TextTrimming=CharacterEllipsis`，`Text` 为空时 `Collapsed`（转换器 + 样式 Trigger）。
- `DataGridRow` 由固定 `Height=32` 改为 `MinHeight=32`，允许两行把行撑高；分组 ListView 行本身按内容自适应。
- 普通列表与分组列表复用同一批 Cell 模板与行视图模型，行为一致。

### 4. 配置入口

- “列与布局”对话框：总开关“列表行显示次要信息”放在左栏顶部、列表面板上方；每列在列名下方直接提供整行宽的“次行格式”输入框（水印提示示例，ToolTip 列出可用占位符与示例），封面 / 状态 / # 三列因单元格本身只有一行而不显示该输入框。
- 打开对话框时若已有任一列配置了格式，自动勾选总开关；输入格式时也自动勾选，避免“填了格式但忘记开总开关 → 确定后列表没有变化”的困惑。
- 同一次改动修复两个既有缺陷：编辑列布局时 `Pin` 丢失（会重置用户的左右固定设置）、排序下拉用列 key 匹配 `SortColumn`（其值为排序属性名）导致永不回显；`CaptureLayoutFromGrid` 同样保留 `Pin`/`SecondaryFormat`。
- 对话框右栏内容变高，加 `ScrollViewer` 避免小窗口裁切。

## 备选与取舍

- **模板硬编码每列次要字段**：新增列/改字段就要改 XAML，且无法支持复合格式 → 采用数据驱动 token。
- **仅分组视图支持**：普通列表同样需要两行 → 复用相同模板与行 VM。
- **不缓存 Composer、每次读文件**：违反 ADR-003/ADR-010 的缓存分层并拖慢列表加载 → 增加缓存列，由扫描器刷新。
- **次行用 `DataTrigger` 控制可见性**：需要为每个值写触发器 → 用转换器把缺失值转成空串，再由样式 Trigger 折叠。

## 影响

- 数据库 v1→v2 自动迁移（幂等）；旧行 Composer 为空串。
- 标签属性对话框多一个字段；列与布局对话框内容更高（可滚动）。
- 默认关闭次要信息，不改变现有用户界面；性能开销仅为行视图模型内一次小字典格式化，无额外 IO。
- `sampleRate` token 沿用现有 `DisplayFormatter.SampleRate`，输出 `48K` / `44.1K`；owner 示例中的 `48Khz` 若必须保持一致，可在格式中追加文本（`{sampleRate}Hz`）或后续单独调整 token 单位。

## 验证

- 单元测试：`SecondaryTextFormat` 10 项（复合格式、空值清理、未知 token、转义、归一化）；布局 roundtrip/Clone 4 项；TagLib WAV 写入→读取 Composer roundtrip；SQLite v1→v2 迁移（旧行默认空串）；Upsert Composer roundtrip。
- 构建 0 警告 0 错误；测试 106 项全部通过（含“隐藏列仍保留排序”用例）。
- 截图验证（隔离 `ECHOGARAGE_DATA_DIR`）：分组视图 5 首测试曲目，标题→流派、艺术家→作曲家、专辑→专辑艺术家、时间→`{bitrate}/{sampleRate}`，两行层次正确；普通列表同样显示两行；列与布局对话框显示顶部总开关（配置已有格式时自动勾选）与每列格式输入；标签属性对话框显示“作曲家”；字段说明页不再列出评分 / 播放次数。
