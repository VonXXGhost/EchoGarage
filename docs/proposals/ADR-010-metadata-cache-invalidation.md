# ADR-010：媒体库缓存的“可用性”失效条件

- 状态：提案（待 owner 批准）
- 日期：2026-11-10
- 关联：ADR-003（文件是唯一真相）、ADR-007（文件变更跟踪）
- 适用范围：`LibraryScanner` 的增量判断、`SqliteLibraryRepository` 的缓存行查询

## 背景

一次读取标签的缺陷（TagLib `ReadStyle.PictureLazy` 单独使用时同时跳过了音频属性，
`file.Properties` 为 null 并触发 `NullReferenceException`）导致 2,825 首歌在扫描时全部
回退为“仅文件名”，缓存行 `Title=文件名、Artist/Album 为空、DurationMs=0`。

增量扫描只比较 `FileSize` + `ModifiedAtUtc`。这些文件本身没有变化，所以修复读取逻辑后
扫描仍会把这些无效行当成“未变化”跳过，错误信息永远不会自愈；用户只能删除媒体库重新导入。

## 决定

1. 缓存行的有效性除了“文件大小 + 修改时间未变”，还要求该行具备可用的音频属性：
   `DurationMs > 0`。缺少时长的行一律视为**失效缓存**，下次扫描必须重新读标签重建。
2. 扫描器的 `IsUnchanged` 增加 `state.DurationMs > 0` 条件；`TrackState` 从数据库多读
   `DurationMs` 字段。
3. 启动时查询“库里存在多少条 `DurationMs <= 0` 的缓存行”（`GetIncompleteTrackCountAsync`）。
   若数量大于 0，即使 `autoScanOnStartup=false` 也自动在后台跑一次静默扫描修复；
   修复完成刷新列表与正在播放面板。无需用户手动删库。
4. 不修改数据库 schema，也不提升 `PRAGMA user_version`：这是缓存语义的变化，不是结构变化。

## 理由

- 符合 ADR-003：数据库只是缓存，缓存不可用时应从文件重建，而不是把错误当成事实。
- 用“时长是否可用”作为判据简单、可从文件重新推导，且对所有支持格式都成立
  （WAV/FLAC/MP3/M4A/AAC 的正常文件时长必然大于 0）。
- 修复成本低：本机 2,825 首实测一次规则失效重建约 1.0 s（8 线程并行读标签、不读封面）。
- 修复一次后该条件不再触发；只有真正损坏、读不出时长的文件会在每次扫描时重试（数量应极少）。

## 备选方案

- **删除 `library.db` 让用户重新导入**：丢失播放计数、添加时间、播放列表条目，不可接受。
- **提升 `user_version` 并在迁移里清空 tracks**：同样破坏播放列表引用，且 schema 未变。
- **每次启动全量重扫**：违反启动性能红线（见 AGENTS §6），且 2,825 首虽只要 1 s，
  数万首时不可接受。
- **只在设置里记一个“元数据版本号”**：需要额外的配置迁移，且配置与媒体库分离时
  可能不同步；用数据自身判断更直接。

## 后果

- 旧版本留下的无效缓存会在升级后的首次启动自动修复，用户无感（约 1 s 后台扫描）。
- 极端情况下（文件损坏、读不出时长）每次扫描会重试该文件；可接受，必要时后续加“损坏”标记。
- ADR-007 描述“增量比对大小与修改时间”，若本提案批准，应同步在 ADR-007 里补一句
  “缓存行还必须具备可用时长”，保持文档一致。

## 验证

- 单元测试：`ScannerTests.Rescan_RepairsCachedRowsWithoutDuration`（把行的 `DurationMs` 改为 0
  后重扫，必须重建并恢复时长）；`TagAndCoverTests.ReadWithoutCover_StillReturnsTagsAndDuration`。
- 实测：复制用户 `library.db`（2,825 行全部 `DurationMs=0`），扫描后 `Incomplete=0`，
  标题 / 艺术家 / 专辑 / 时长全部恢复；再次扫描 2,825 行全部跳过（0.25 s）。
