# ADR-018 播放列表外部文件的标签缓存刷新策略

- 状态：已批准（Accepted，2026-11-14，owner）
- 日期：2026-11-14
- 相关：ADR-003（文件是唯一真相）、ADR-010（缓存行时长缺失视为失效）、ADR-013（Composer 字段）、ADR-017（音频转换）

## 背景

播放列表允许加入媒体库目录之外的文件（“外部文件”）。加入时这些文件会以
`inLibrary: false` 写入 `tracks` 表作为缓存行，播放列表查询优先读取缓存列
（标题 / 艺术家 / 专辑 / 时长 / 封面键等）。

问题：媒体库扫描只遍历 `library_folders`，`FileSystemWatcher` 也只监听媒体库目录，
因此**外部文件没有任何重新索引的路径**。用户在文件管理器里修改标签后，即使重启软件，
列表仍显示旧标签；只有属性对话框（直接读文件）能看到新内容。

UAT 数据实证：默认播放列表 7 首外部文件（`E:\Other\...`）的缓存行
`ModifiedAtUtc` 为 2026-11-14 04:41:49，而文件实际 mtime 为 05:21:14；
媒体库扫描日志只覆盖 `D:\Temp\POP`（unchanged=2769），不会触及这些文件。

## 决策

1. 在 Core 增加纯函数 `TrackRefresh.NeedsRefresh(track, fileSize, modifiedAtUtc)`：
   当缓存行 `DurationMs <= 0`、或文件大小不同、或修改时间不同时返回 true。
2. `MainViewModel.RefreshStaleTracksAsync(bool force)`：
   - 只在有当前来源、且未在刷新时执行；非 `force` 调用有 2 秒节流。
   - 在后台线程对当前来源的每一行做 `FileInfo` 比对；需要刷新时调用
     `ILibraryScanner.IndexFileAsync(path, track.IsInLibrary, track.FolderId)`
     重新读取标签并更新缓存行（保留播放次数 / 添加时间等现有字段）。
   - 有变化且当前来源未变时，`RefreshCurrentSourceAsync(preserveSelection: true)`
     刷新列表并记录日志与状态栏提示。
3. 触发时机：
   - 切换来源（`SelectSourceAsync` 末尾，`force: true`）。
   - 主窗口重新激活（`Activated` 事件，节流）。
4. 不改变数据模型与数据库 schema；不删除、不新增缓存行；文件缺失仍由媒体库扫描 /
   文件监听按既有逻辑标记，不在本刷新中处理。

## 备选方案与取舍

- **播放列表加载时逐条读标签**：能保证最新，但每次切列表都读磁盘标签，播放列表加载
  性能红线（几十毫秒）无法满足；放弃。
- **后台定时轮询全部播放列表**：持续 IO 开销、且播放列表数量多时无法负担；放弃。
- **把文件监听扩展到外部文件所在目录**：外部文件目录不可预知、可能数量庞大，
  监听成本与可靠性都不可控；放弃。
- **只在右键“属性”保存后刷新**：用户可能在软件外改标签，覆盖不到；放弃。

## 影响

- 启动后首次激活会多一次“文件元数据比对”（只 `stat`，不读标签）；实测 7 首外部文件
  刷新耗时约 1 秒（含 7 个 FLAC 标签重读），数千首媒体库列表的 stat 循环在后台线程执行，
  不阻塞 UI。
- 标签变化最多延迟到下一次窗口激活 / 切换来源，不是实时监听；符合“文件是唯一真相、
  缓存可重建”的原则。
- 若文件内容变化但大小与 mtime 均未变（极罕见），不会触发刷新；可接受。

## 验证

- `TrackRefreshTests`：尺寸 / 时间戳匹配、尺寸变化、时间戳变化、时长缺失 4 个用例。
- `PlaylistMetadataRefreshTests`：外部 WAV 以 `inLibrary:false` 索引 → 加入播放列表 →
  用 TagLib 写入新标签 → 再次索引后 `GetPlaylistTracksAsync` 返回新标题 / 艺术家 / 专辑。
- UAT 数据副本实测：7 首外部文件在启动后自动刷新，DB 副本 `ModifiedAtUtc` 更新为文件
  mtime，列表显示新日文标签。
- 全量测试：183 项通过。
