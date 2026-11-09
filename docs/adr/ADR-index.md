# 架构决策记录索引（ADR Index）

EchoGarage 的每个重要取舍都记录在这里。ADR 一经批准即生效；实现与 ADR 冲突时先讨论，不默默违背。

- 状态说明：`Accepted` = owner 已批准；`Proposed` = 待 owner 批准（放在 `docs/proposals/`，不进入本目录）。
- 变更流程：新增 / 修改 ADR 时先写提案 → owner 同意 → 移入 `docs/adr/` 并更新本索引。

| 编号 | 标题 | 状态 | 日期 | 一句话结论 |
| --- | --- | --- | --- | --- |
| [ADR-001](ADR-001-platform-and-stack.md) | 平台与技术栈 | Accepted | 2026-11-09 | .NET 10 + WPF + NAudio + TagLib# + SQLite；支持多主题，v0.1 为白色基底浅色（默认）+ 深色 |
| [ADR-002](ADR-002-layered-architecture.md) | 分层架构 | Accepted | 2026-11-09 | Core / Infrastructure / App 单向依赖，能力经接口暴露，AppHost 统一装配 |
| [ADR-003](ADR-003-files-are-truth.md) | 文件是唯一真相 | Accepted | 2026-11-09 | 标签 / 时长 / 封面从文件读取，数据库只做缓存；播放计数等软件状态除外 |
| [ADR-004](ADR-004-playlist-model.md) | 播放列表模型 | Accepted | 2026-11-09 | 普通列表存显式条目，智能列表存条件；默认列表由外部打开覆盖 |
| [ADR-005](ADR-005-playlist-view-layouts.md) | 播放列表显示格式 | Accepted | 2026-11-09 | 每个播放列表独立记忆列显示 / 宽度 / 顺序 / 排序，全局预设可复用 |
| [ADR-006](ADR-006-settings-vs-library.md) | 配置与媒体库分离 | Accepted | 2026-11-09 | `settings.json` 与 `library.db` 分离；导出配置不含媒体库内容 |
| [ADR-007](ADR-007-file-watching.md) | 文件变更跟踪 | Accepted | 2026-11-09 | FileSystemWatcher + 去抖，增量比对大小与修改时间，失败可回退全量扫描 |
| [ADR-008](ADR-008-single-instance-default-player.md) | 单实例与默认播放器 | Accepted | 2026-11-09 | 命名互斥体 + 管道转发；文件关联是可选、手动、按格式勾选的操作，启动不自动注册 |
| [ADR-009](ADR-009-wasapi-audio-pipeline.md) | WASAPI 播放管线 | Accepted | 2026-11-09 | 共享模式走混音格式；独占模式按设备格式阶梯重采样，失败回退共享 |

## 相关长期知识（KB）

实现细节与踩坑记录在 `docs/kb/`（同样需要 owner 批准后写入）：

- `KB-001` NAudio 各格式解码矩阵
- `KB-002` 受限 / 沙箱开发环境
- `KB-003` WPF 深色主题与控件坑
- `KB-004` 性能基线
- `KB-005` TagLib# 使用笔记

> 2026-11-09：owner 批准全部 ADR / KB 提案，并要求：
> 1）ADR-001 增加“多套主题、先做白色基底亮色主题”；
> 2）ADR-008 的文件关联注册必须是可选项、不得自动注册，注册时可以选择后缀。
> 两份 ADR 已按此修改后归档。
