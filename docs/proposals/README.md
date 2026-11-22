# 提案区（Pending proposals）

本目录只放**尚未获得 owner 批准**的 ADR / KB 提案；已批准文档在 `docs/adr/`、`docs/kb/`。

## 流程

1. 在本目录新建 `ADR-xxx-*.md` 或 `KB-xxx-*.md`：写清背景、决策、理由、后果与备选方案。
2. 在会话里向 owner 说明变更点与理由。
3. owner 明确同意后，把文件移动到 `docs/adr/`（并更新 `docs/adr/ADR-index.md`）或 `docs/kb/`；提案文件不保留副本。
4. 未经同意，不得直接修改 `docs/adr/` 与 `docs/kb/` 中已生效的文档。

## 当前状态

- 当前没有待批准的提案。

2026-11-22 批准的 ADR-023 修订（独占模式输出缓冲区、格式协商与可视化回调节奏）已按页首合并规则
并入 `docs/adr/ADR-023-playback-visualizer.md`（§18），提案文件已删除。
2026-11-22 批准的 ADR-032（输出设备热插拔检测与“上次使用的设备”记忆）已移入
`docs/adr/ADR-032-audio-output-device-hotplug.md`；同日批准的 ADR-033（按输出设备与模式记忆音量、
按模式设置音量上限）已移入 `docs/adr/ADR-033-output-volume-memory-and-ceiling.md`。
2026-11-21 批准的提案已分别并入 `docs/adr/ADR-030-…`、`docs/adr/ADR-003-files-are-truth.md`、
`docs/adr/ADR-015-waveform-seekbar.md` 与 `docs/kb/KB-008-grouped-cover-and-layout-persistence.md`。

历史提案的批准 / 撤回记录见 `docs/adr/ADR-index.md` 文末的“批准记录”，本文件不再逐条重复。
