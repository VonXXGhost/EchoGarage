# 架构决策记录索引（ADR Index）

EchoGarage 的每个重要取舍都记录在这里。ADR 一经批准即生效；实现与 ADR 冲突时先讨论，不默默违背。

- 状态说明：`Accepted` = owner 已批准；`Proposed` = 待 owner 批准（放在 `docs/proposals/`，不进入本目录）。
- 变更流程：新增 / 修改 ADR 时先写提案 → owner 同意 → 移入 `docs/adr/` 并更新本索引。
- 编号说明：ADR-011 为已撤回的历史提案编号，未复用，因此编号从 012 顺延；该编号不对应任何文件。
- 2026-11-19 合并整理（owner 指示）：内容相近的修订案与主 ADR 合并，只保留主编号文件：
  ADR-015-A1 / A2 → ADR-015；ADR-021-A1 → ADR-021；ADR-023-A1 → ADR-023；ADR-026 → ADR-016；
  ADR-027 → ADR-025；ADR-029 → ADR-014。被合并编号不再单独维护，仅在新文件“修订记录”中保留追溯说明。

| 编号 | 标题 | 状态 | 日期 | 一句话结论 |
| --- | --- | --- | --- | --- |
| [ADR-001](ADR-001-platform-and-stack.md) | 平台与技术栈 | Accepted | 2026-11-09（2026-11-16 修订主题条目） | .NET 10 + WPF + NAudio + TagLib# + SQLite；支持多主题，v0.1 为五套（ADR-024）：纯净极简白（默认）、温润纸感白、极简冰青白、午夜黑胶、深海蓝灰 |
| [ADR-002](ADR-002-layered-architecture.md) | 分层架构与组合根 | Accepted | 2026-11-09 | Core / Infrastructure / App 单向依赖，能力经接口暴露，AppHost 统一装配 |
| [ADR-003](ADR-003-files-are-truth.md) | 文件即真相——标签、封面与元数据缓存 | Accepted | 2026-11-09（2026-11-21 补充容量上限） | 标签 / 时长 / 封面从文件读取，数据库只做缓存；播放计数等软件状态除外；封面缓存键由 `CoverKeyFactory` 生成；封面缓存内存上限 1 GB（字节 LRU）+ 磁盘上限默认 2 GB，解码尺寸随 DPI |
| [ADR-004](ADR-004-playlist-model.md) | 播放列表模型（普通 / 智能 / 默认） | Accepted | 2026-11-09 | 普通列表存显式条目，智能列表存条件；默认列表由外部打开覆盖；媒体库外条目由 ADR-018 刷新 |
| [ADR-005](ADR-005-playlist-view-layouts.md) | 每个播放列表独立的显示布局与预设 | Accepted | 2026-11-09 | 每个播放列表独立记忆列显示 / 宽度 / 顺序 / 排序，全局预设可复用；排序列只需能映射到 `ColumnCatalog`，允许隐藏列排序 |
| [ADR-006](ADR-006-settings-vs-library.md) | 配置与媒体库分离、缓存目录可配置 | Accepted | 2026-11-09 | `settings.json` 与 `library.db` 分离；默认便携到 exe 旁 `EchoGarageData`；导出配置不含媒体库内容 |
| [ADR-007](ADR-007-file-watching.md) | 文件监听与缓存失效 | Accepted | 2026-11-09 | FileSystemWatcher + 去抖，增量比对大小与修改时间；封面失效规则见 ADR-003 |
| [ADR-008](ADR-008-single-instance-default-player.md) | 单实例与默认播放器行为 | Accepted | 2026-11-09 | 命名互斥体 + 管道转发；文件关联是可选、手动、按格式勾选的操作，只写打开方式候选，不覆盖默认处理程序 |
| [ADR-009](ADR-009-wasapi-audio-pipeline.md) | WASAPI 音频管线 | Accepted | 2026-11-09 | 共享模式走混音格式；独占模式按设备格式阶梯重采样，失败回退共享；解码器为共享句柄的 `SharedAudioFileReader`（ADR-020） |
| [ADR-010](ADR-010-metadata-cache-invalidation.md) | 媒体库缓存的“可用性”失效条件 | Accepted | 2026-11-11 | 缓存行缺少时长（`DurationMs <= 0`）视为失效并在扫描时重建；启动时自动修复旧缓存 |
| [ADR-012](ADR-012-test-playback-volume.md) | 测试运行时的输出音量 | Accepted | 2026-11-12 | 实际运行并播放音频的测试必须先静音 / 调到最低音量（音量相关测试除外），并使用独立数据目录副本 |
| [ADR-013](ADR-013-secondary-row-info-and-composer.md) | 列表行次要信息与作曲家标签 | Accepted | 2026-11-14 | 每列可选次行复合格式（20 个占位符，默认关闭、填格式自动启用）；Composer 标签贯通文件 / 缓存 / 编辑；SQLite schema v2（v3 / v4 见 ADR-014）；两行渲染 |
| [ADR-014](ADR-014-cache-repair-migrations.md) | 缓存修复迁移：schema v3 回填作曲家 / schema v4 Apple 音轨号自愈 | Accepted | 2026-11-14（2026-11-18 并入 schema v4，原 ADR-029） | 统一以 `DurationMs <= 0` = 缓存失效：schema v3 把 `FolderId IS NOT NULL` 的旧行清零回填 Composer；schema v4 把 `.m4a/.mp4/.m4b` 且 `TrackNumber = 0` 的行清零，重读旧式 Apple 音轨号 |
| [ADR-015](ADR-015-waveform-seekbar.md) | 播放进度条的波形视图（Waveform Seek Bar） | Accepted | 2026-11-14（2026-11-16 并入原 ADR-015-A1 / A2；2026-11-21 修订几何与已播放区绘制） | 进度条可切整曲三频段波形：三层堆叠、动态范围窗口只向下放宽（`MinimumStretchDb` 30 dB）、已播放区 26% 观感（单遍绘制 + 轨道底色覆盖）、每频段一条阶梯折线 `StreamGeometry`、50 列/秒 EGWV 缓存 + LRU、点击 / 拖动跳转 |
| [ADR-016](ADR-016-search-hotkeys-playback-mode.md) | 列表搜索、可配置与全局快捷键、播放模式、年份原文显示、媒体键、播放列表管理与单曲循环 | Accepted | 2026-11-14（2026-11-19 并入原 ADR-026） | 列宽拖拽即时落盘；内联搜索 + Ctrl+F 高级搜索；快捷键可配置且可选全局注册；播放模式三态（顺序 / 随机 / 单曲循环）；年份保留原文 `YearText`（不落库）；媒体键默认控制播放；媒体库 / 播放列表导航右键管理 |
| [ADR-017](ADR-017-audio-conversion.md) | 音频转换（编码转换） | Accepted | 2026-11-14（2026-11-19 由 ADR-028 修订） | 预设驱动的转换器：格式 / 输出位置 / 文件名模板 / 高质量重采样 / 标签复制，右键与快捷键触发；响度增益与 SSRC 延迟补偿见 ADR-028 |
| [ADR-018](ADR-018-track-metadata-refresh.md) | 播放列表外部文件的标签缓存刷新策略 | Accepted | 2026-11-14 | 外部文件缓存行按 `FileSize` + `ModifiedAtUtc` 比对，窗口激活 / 切换来源时后台刷新标签，不阻塞列表加载 |
| [ADR-019](ADR-019-tag-editor-and-cover-conversion.md) | 标签编辑页重设计、额外标签展示、封面 JPG 转换与多文件批量编辑 | Accepted | 2026-11-14（2026-11-15 修订） | `#` 列显示「碟片号.音轨号」；标签编辑改两列表格 + 其他标签可编辑；封面可转 JPG 并限制最大边长；支持多文件批量编辑 |
| [ADR-020](ADR-020-playing-file-write-lock.md) | 播放中文件的标签写入（共享句柄 + 临时副本原子替换） | Accepted | 2026-11-14 | 播放器共享句柄 + 写标签时临时副本 `File.Replace` 原子替换，播放中改标签不再中断播放 |
| [ADR-021](ADR-021-file-operations.md) | 歌曲右键菜单「文件操作」（复制到 / 移动到 / 删除到回收站） | Accepted | 2026-11-15（2026-11-16 并入原 ADR-021-A1） | 级联目标目录（设置页记忆、最多 20 个）+ 同目录重名递增不覆盖 + 回收站删除；移动 / 删除后同步 `tracks` 与 `playlist_items.TrackPath`，删除成功同时清理所有普通播放列表条目 |
| [ADR-022](ADR-022-web-tag-search.md) | Web 搜索标签（第一阶段：Apple Music 日本网页源） | Accepted | 2026-11-15 | 独立三步窗口：Apple Music 网页搜索 / 专辑详情 → 专辑级 + 歌曲级预览与字段勾选 → 音轨号自动匹配 + 拖拽纠正；可配置代理与字段记忆 |
| [ADR-023](ADR-023-playback-visualizer.md) | 右栏「播放可视化」（分段频谱图 + 渲染帧率上限） | Accepted | 2026-11-15（2026-11-16 并入原 ADR-023-A1） | 右栏只留封面 + 基础信息；下方可切换可视化，第一阶段分段频谱：无锁环形缓冲抽头 + 渲染帧拉取 FFT、几何频段、dBFS 标定 + 斜率补偿 + 弹道、参数全可配置持久化、帧率上限 30/60/120/144/180/不限 |
| [ADR-024](ADR-024-multi-theme-system.md) | 五套主题体系与单配置切换 | Accepted | 2026-11-16 | ThemeKind 五值（PureStudio 默认）+ ThemeCatalog 单一来源；5 个主题字典各 57 个令牌；`视图 → 主题` 中文二级菜单 + 设置页下拉；ThemeKindJsonConverter 兼容旧 Light/Dark；移除旧两主题字典 |
| [ADR-025](ADR-025-tag-normalization-rules.md) | 标签编辑辅助：规范化正则规则与候选值 | Accepted | 2026-11-16（2026-11-19 并入原 ADR-027） | 可配置正则规则批量规范化标签（默认「移除专辑名 EP/Single 后缀」「ISO 日期时间 → 日期」，1 s 超时）；艺术家 / 专辑艺术家 / 作曲家 / 流派候选值从 `library.db` 聚合，前缀 / 包含匹配，不读文件 |
| [ADR-028](ADR-028-loudness-analysis-adjustment.md) | 响度计算与响度调整（LUFS / LRA / True Peak） | Accepted | 2026-11-18（2026-11-19 批准） | 自研纯 C# 流式响度引擎（BS.1770-5 / EBU R128）：右键多选「响度计算」扫描窗口（不缓存、不写 tag）；单文件「响度调整」只做线性增益输出新文件，目标默认 -10.0 LUFS（设置页可配置）、无损 -1.0 / 有损 -2.0 dBTP 天花板、16-bit TPDF dither；修订 ADR-017 并连带修复 SSRC 重采样前导延迟 |
| [ADR-030](ADR-030-file-association-tolerant-registration.md) | 文件关联注册的容错与 Windows 11 候选来源 | Accepted | 2026-11-21（修订 ADR-008 §3） | 逐个扩展名容错注册（单条失败不再中止整批）、按格式回报结果、注册表异常写日志；候选来源补充 Windows 11 的 `UserChoice` / `OpenWithProgids` 判定 |
| [ADR-031](ADR-031-now-playing-page-and-cover-glow.md) | 「正在播放」页与封面柔光取色 | Accepted | 2026-11-22 | 顶栏 TAB 切换的平行「正在播放」页：左侧封面 + 元数据 + 标签卡片 + 频谱，右侧播放列表按当前播放顺序快照（不分组、不可排序）；封面柔光用 OKLCH 中位切分取色，无彩/失败回退主题强调色，透明度由主题令牌控制，切歌交叉淡入 |

## 相关长期知识（KB）

实现细节与踩坑记录在 `docs/kb/`（同样需要 owner 批准后写入）。2026-11-19 同样做了合并：只保留以下编号文件，被合并编号的内容已并入并在文末“修订记录”中注明来源。

- [KB-001](../kb/KB-001-naudio-decode-matrix.md) 音频解码、标签读取与交互行为笔记（并入原 KB-005 TagLib# 笔记、KB-007 扫描读取标签与进度条点击）
- [KB-002](../kb/KB-002-sandbox-dev-environment.md) 受限开发环境（DSH 沙箱）下的构建与测试
- [KB-003](../kb/KB-003-wpf-dark-theme-pitfalls.md) WPF 深色主题实现中的坑
- [KB-004](../kb/KB-004-performance-baseline.md) 性能基线与列表切换优化（并入原 KB-016 列表切换增量渲染）
- [KB-006](../kb/KB-006-theme-system.md) 主题系统与五主题令牌手册（附两主题历史；并入原 KB-015 令牌清单）
- [KB-008](../kb/KB-008-grouped-cover-and-layout-persistence.md) 分组列表布局与封面 / 滚动交互（并入原 KB-009、KB-010、KB-011、KB-012；2026-11-21 补充 §3.5 分组封面可见窗口上限、§4.5 按 DPI 解码）
- [KB-014](../kb/KB-014-header-pinning-and-toolbar-refresh.md) 表头固定、来源布局记忆与顶栏 / 图标（并入原 KB-013）
- [KB-017](../kb/KB-017-cover-viewer.md) 封面大图浏览窗口（双击「正在播放」封面放大）
- [KB-018](../kb/KB-018-tag-normalization-ui.md) 标签规范化 UI（设置页规则管理 / 右键批量 / 标签属性窗口快捷处理）
- [KB-019](../kb/KB-019-loudness-measurement.md) 响度测量算法（LUFS / LRA / True Peak）：K 加权系数、门限、真峰值过采样、实现与验证笔记
- [KB-020](../kb/KB-020-now-playing-cover-crossfade.md) “正在播放”封面交叉淡入过渡（保留旧图 + 200ms 交叉淡入；替代 KB-008 §5 原加载态描述）

## 批准记录（摘要）

> 完整决策内容以各 ADR / KB 正文为准，这里只保留时间线与关键变更。

- 2026-11-09：owner 批准首批 ADR / KB，并要求 ADR-001 增加“多套主题、先做白色基底亮色主题”、ADR-008 文件关联必须可选且不自动注册。
- 2026-11-11：批准 ADR-010、KB-006、KB-007；ADR-011 撤回，分组封面尺寸改以 KB-008 记录（不做 ADR）。
- 2026-11-12：新增 ADR-012（测试输出音量与独立数据目录）；同日修复分组列表滚动条卡顿并归档 KB-011 / KB-012。
- 2026-11-14：批准 ADR-013 / ADR-014（schema v3）/ ADR-015 / ADR-016 / ADR-017 / ADR-018 / ADR-019 / ADR-020 与 KB-013 / KB-014；确定界面不再展示评分与播放次数等动态统计。
- 2026-11-15：批准 ADR-021（文件操作）与 ADR-023（播放可视化）；同日实现完成并发布验收版本。
- 2026-11-16：批准 ADR-024（五主题）、ADR-015-A1 / A2、ADR-021-A1、ADR-023-A1、ADR-025 与 KB-015 / KB-016 / KB-017 / KB-018；主题名称中文化，五套波形配色改为低饱和体系。
- 2026-11-17：批准 ADR-026（年份原文 / 快捷键捕获与媒体键 / 播放列表管理 / 单曲循环）与 ADR-027（标签候选值）；新增「文件修改时间」排序与列。
- 2026-11-18：批准 ADR-029（schema v4 Apple 旧式音轨号缓存自愈）。
- 2026-11-19：批准 ADR-028（响度计算与调整）与 KB-019；同日按 owner 要求做文档整理：合并修订案（见页首说明）、精简提案区、更新 README / AGENTS 与界面截图。
- 2026-11-20：批准 KB-020（“正在播放”封面交叉淡入过渡），并据此修订 KB-008 §5。
- 2026-11-21：批准 ADR-030（文件关联容错注册，修订 ADR-008 §3）、ADR-003 补充（封面缓存内存 1 GB / 磁盘 2 GB 上限）与 ADR-015 修订（4K 波形几何与已播放区单遍绘制），以及 KB-008 §3.5 / §4.5 补充；同日完成 4K/160 Hz 波形帧率修复（10.0 → 148.7 fps）与分组滚动内存回归修复（3,011 → 341 MB）。
- 2026-11-22：批准 ADR-031（「正在播放」页与封面柔光取色：OKLCH 中位切分取色、主题令牌化柔光、右侧播放列表同序快照）。
