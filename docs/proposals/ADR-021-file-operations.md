# ADR-021：歌曲右键菜单「文件操作」（复制到 / 移动到 / 删除到回收站）

- 状态：待批准（Proposed，2026-11-14）
- 日期：2026-11-14
- 相关：ADR-003（文件是唯一真相）、ADR-018（播放列表外部文件标签刷新）、ADR-020（播放中文件写入）

## 背景

目前从歌曲列表里操作文件只能「在资源管理器中显示」，要复制 / 移动 / 删除还得手动去
资源管理器操作，然后回到程序里重新扫描。owner 要求在歌曲右键菜单里增加「文件操作」，
菜单形态参考资源管理器的级联菜单：

```
文件操作 ▶ ┌ 复制 到 ▶ ┌ <记忆的目标文件夹 1>
           │            ├ <记忆的目标文件夹 2>
           │            └ 选择文件夹…
           ├ 移动 到 ▶  （同上）
           ├ 重命名 到…（本 ADR 暂不实现）
           ├ ─────────────
           └ 删除文件
```

要求：

1. 复制 / 移动的目标目录可在设置里配置并记忆，右键菜单按记忆顺序列出；
2. 删除文件走回收站（可还原），不做永久删除；
3. 「重命名 到…」本次忽略（菜单项置灰占位）。

## 决策

### 1. 分层职责

- `EchoGarage.Core.Services.FileDestinations`：目标目录列表的纯数据整理
  （去空白 / 去「复制为路径」带上的成对引号、限绝对路径、忽略大小写去重、保留顺序、上限 20 个）。
- `EchoGarage.Core.Services.FileOperationPlanner`：目标路径规划——同目录判定与重名规避
  （`song.flac` → `song (2).flac` → `song (3).flac`…，最多 999 次），**绝不覆盖**已有文件。
- `EchoGarage.Core.Abstractions.IFileOperationService`：跨层接口，返回逐条结果
  （`FileOperationItem(SourcePath, TargetPath, Error)` + 汇总 `FileOperationResult`），
  失败不抛异常而是逐条记录，UI 可以聚合提示「3 个成功，1 个失败」。
- `EchoGarage.Infrastructure.Files.FileOperationService`：后台线程执行复制 / 移动；
  目标目录不存在时创建；跨卷移动退化为「复制 + 删除源文件」。
- `EchoGarage.Infrastructure.Windows.ShellRecycleBin`：通过 `SHFileOperation`
  （`FO_DELETE` + `FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI`）
  把文件送进回收站，避免自己实现回收站元数据。

### 2. 数据库与界面同步

文件被移动 / 复制 / 删除后，程序里的列表与播放列表必须跟着变，因此：

- 移动：调用新增的 `ILibraryRepository.UpdateTrackPathAsync(oldPath, newPath, folderId)`，
  在**一个事务**里更新 `tracks.Path / FolderId / IsInLibrary / IsMissing`
  以及 `playlist_items.TrackPath`（两处都必须改，播放列表按路径关联）；
  若目标路径已存在一行旧缓存，先删除再改，避免撞 `Path` 唯一约束。
- 复制到媒体库内：用 `ILibraryScanner.IndexFileAsync` 让新文件进入库；复制到库外则不索引。
- 删除：`MarkTracksMissingAsync` 把行标记为缺失（保留播放列表条目，可从回收站还原后刷新回来）。
- 以上操作完成后统一 `ReloadSourcesAsync` / `RefreshCurrentSourceAsync(preserveSelection: true)`
  （界面绑定的是当前选中列表，不能无脑 Reload，必须刷新「当前来源」），
  并失效对应路径的波形缓存 `IWaveformService.Invalidate`。
- 若被操作的文件正在播放（`IAudioPlayer.CurrentTrackPath`），先停止播放再改库并提示。

### 3. 双击 / 确认与错误处理

- 删除前弹确认框：单个文件显示文件名，多个显示数量，并注明「可以随时从回收站还原」。
- **移动前同样弹确认框**（owner 要求）：显示目标目录与「原位置的文件会被移除」；
  单个文件显示文件名，多个显示数量。复制不弹确认（不改变原文件）。
- 复制 / 移动的目标目录如果来自「选择文件夹…」，该目录自动加入记忆列表。
- 失败项聚合为一个错误对话框（最多列出前若干条），同时写 `[WARN]` 日志；不静默失败。

### 4. 设置与菜单

- `AppSettings.FileDestinations: List<string>`，加载时经 `FileDestinations.Normalize` 归一化；
  设置页新增「文件操作目标文件夹」区块（列表 + 添加 / 移除 / 上移 / 下移，最多 20 个）。
- 右键菜单在 `ContextMenu.Opened` 与 `MenuItem.SubmenuOpened` 时重建
  （WPF 中 `MenuItem` 没有子项就不会弹出二级菜单，与「转换」菜单同一模式）：
  二级 `复制 到 / 移动 到` 各自再填充三级目标目录；末尾固定追加
  `选择文件夹…`（临时目标，不入库）与 `管理目标文件夹…`（打开设置页）。
- 多选时作用于所有选中行，未多选时作用于当前行；没有选中项时整个「文件操作」菜单禁用。

## 备选方案

1. **调用 `IFileOperation`（Windows Shell COM）做进度与撤销**：体验最好，但需要 COM 互操作与
   更长的实现周期，且与当前「简单聚合结果」的 UI 风格不匹配；留作后续优化。
2. **直接 `File.Delete`**：不符合 owner「删除到回收站」的要求。
3. **用资源管理器打开并让用户手动操作**：现有「在资源管理器中显示」已能做，但用户仍需手动同步库，
   不满足需求。

## 影响

- 新增 Core 3 个文件、Infrastructure 2 个文件、1 个接口文件；`AppSettings` / `JsonSettingsService` 各加 1 处；
  `ILibraryRepository` 与 `SqliteLibraryRepository` 各加 1 个方法。
- 移动 / 删除会改动用户的真实文件，因此所有操作都有确认（删除）或明确的目标目录选择（移动），
  不提供「静默批量转移」入口。
- 播放中删除 / 移动文件在共享句柄（ADR-020）下通常能成功，但当前实现选择先停止播放，避免边播边删的歧义。

## 测试

- `FileDestinations.Normalize/Add/Remove`：去引号、去重（忽略大小写）、保持顺序、上限与相对路径拒绝。
- `FileOperationPlanner`：同目录判定、重名递增后缀、不覆盖。
- `FileOperationService`：复制到不存在目录、同名不覆盖、移动删除源文件、同目录拒绝、源文件缺失逐条报错、
  删除走回收站接口（用 FakeRecycleBin 验证成功 / 拒绝两条路径）。
- `SqliteLibraryRepository.UpdateTrackPathAsync`：同时更新 `tracks` 与 `playlist_items`；
  目标路径已有旧缓存行时不撞唯一约束。

## 未做 / 后续

- 「重命名 到…」菜单项保留置灰，未实现。
- 复制 / 移动的大批量进度条与取消：当前逐文件串行、无进度 UI（数量通常是个位数）。
- 跨卷移动为大文件时是「复制 + 删除」，没有断点续传；失败时源文件保留。
- 列表行迷你波形（ADR-015 §15.2）与本 ADR 无关，未改动。
