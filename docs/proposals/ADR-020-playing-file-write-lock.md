# ADR-020：播放中文件的标签写入（共享句柄 + 临时副本原子替换）

- 状态：提案，待 owner 批准
- 日期：2026-11-14
- 相关：ADR-003（文件是唯一真相）、ADR-017（音频转换）、ADR-019（标签编辑与批量编辑）

## 背景

批量保存标签时出现过这样的失败：5 个文件中 4 个成功，正在播放的那 1 个失败，
错误为 `The process cannot access the file '...' because it is being used by another process`。

原因是播放器用 `NAudio.Wave.AudioFileReader` 打开音频文件后，在整段播放期间一直持有文件句柄；
TagLib 写入同一文件时无法获得写访问，于是标签保存失败。用户明确要求：

1. 程序要能自动处理“正在播放的文件被自己占用”的情况，在程序内修改文件内容时正常写入；
2. 方案确定后写入 ADR。

第一版实现采用“暂停播放 → 释放句柄 → 写标签 → 重新打开并恢复位置”的挂起/恢复方案。
验收反馈：

1. 保存标签时不应该停止播放；
2. 恢复播放时没有从停止的位置继续（实际会从头或位置漂移）。

因此本 ADR 改为**不中断播放**的方案。

## 决策

### 1. 播放器改用共享读取句柄

新增 `EchoGarage.Infrastructure.Audio.SharedAudioFileReader`，替代 `AudioFileReader`：

- 用 `FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)`
  打开文件，允许其他代码/进程同时读取，并允许替换或删除该文件；
- `.wav` 用 `WaveFileReader(stream)`，`.aiff/.aif` 用 `AiffFileReader(stream)`，
  其余格式（mp3 / m4a / aac / flac / wma 等）用 `StreamMediaFoundationReader(stream, settings)`；
- 外层用 `SampleChannel(reader, forceStereo: true)` 统一转成 32-bit float 立体声，
  对外提供 `WaveFormat` / `TotalTime` / `CurrentTime` / `Read(float[])`。

`WasapiAudioPlayer` 的 `_reader` 字段与 `StartOutput` 参数类型改为 `SharedAudioFileReader`，
播放、暂停、跳转、音量等逻辑不变。

### 2. 标签写入：独占锁检测 + 临时副本原子替换

`TagLibTagService.Write(path, edit)`：

1. 先用 `FileStream(path, Open, ReadWrite, FileShare.None)` 探测文件是否被独占占用；
   能打开说明没有其他句柄（未被播放），直接按原逻辑原地写入（`WriteCore`）；
2. 打不开（被播放器或第三方占用）时走 `WriteViaReplacement`：
   - 在同目录复制一份同扩展名的临时文件（`{name}.echogarage-{guid}{ext}`，保证 TagLib 能识别格式）；
   - 对临时文件调用 `WriteCore` 写标签；
   - `File.Replace(temp, path, destinationBackupFileName: null, ignoreMetadataErrors: true)`
     原子替换原文件；失败时回退 `File.Move(temp, path, overwrite: true)`；
   - `finally` 中删除临时文件。
3. 写入失败语义不变：逐文件汇总 `标签保存完成：成功 N 个，失败 M 个`，并列出前几个失败原因。

### 3. 移除挂起/恢复机制

删除上一版的以下内容：

- Core `PlaybackSnapshot` 记录与 `PlaybackFileWriteGuard`；
- `IAudioPlayer.SuspendForExternalWriteAsync()` / `RestoreAfterExternalWriteAsync()` 及
  `WasapiAudioPlayer` 中的实现与 `SafePosition`；
- `MainViewModel.EditTagsAsync` 中“写入前挂起、finally 恢复”的逻辑与相关测试。

### 4. 播放连续性

`File.Replace` 只替换目录项：播放器持有的旧文件对象仍然有效，解码继续、位置自然连续，
不会出现暂停、重新打开或位置重置。旧文件在播放器释放句柄后由系统自动删除。
标签保存完成后照常 `ReloadSourcesAsync()` 刷新列表与正在播放信息。

## 备选方案与取舍

- **挂起/恢复播放（上一版方案）**：实现简单，但会短暂中断播放；重新打开并重设位置时
  存在位置漂移/从头播放的问题，不满足用户要求。放弃。
- **整个文件读入内存播放**：播放时关闭磁盘句柄，写入天然不受占用；但每首播放要占用
  与文件等大的内存（大 FLAC 可达 40–60 MB），且首次播放前要多一次完整读取。放弃。
- **共享句柄下直接原地写入**：文件被播放器同时读取，TagLib 重写标签块（尤其是 FLAC/MP4
  需要重排元数据）时解码器可能读到撕裂的数据，出现杂音或崩溃。放弃。
- **写入失败后提示用户手动暂停**：体验差，且用户明确要求自动处理。放弃。
- **临时副本 + 原子替换（采用）**：播放完全不中断、位置连续；代价是写入正在播放的文件时
  多一次同目录复制（大小与文件相当）和一次替换操作。

## 影响

- 播放中保存标签不再中断播放，播放位置连续；暂停状态下保存标签也不会改变暂停状态。
- 写入正在播放的文件会多一次同目录复制与替换；未播放文件仍走原地写入，批量编辑速度不变。
- `File.Replace` 要求源/目标在同一卷且文件系统支持（NTFS 正常）；不支持时回退为移动覆盖。
- 若写入时文件被第三方程序（非本播放器）独占，仍会失败并照旧汇总提示。
- 不改变标签写入流程、数据库缓存结构；转换、扫描等其它写入路径不经过该机制。

## 验证

- 单元测试：`SharedAudioFileReaderTests`（读取器持有共享句柄时写标签：替换成功、标签已更新、
  读取位置继续前进）；移除原 `PlaybackFileWriteGuardTests` 5 项后全量 218 项通过。
- 机制验证（pwsh/.NET）：目标文件被 `FileStream(FileShare.ReadWrite | Delete)` 打开时，
  `File.Replace` 成功且旧句柄仍可继续读取旧内容。
- 生产类 harness（不发声）：
  - mp3 / m4a / flac：`SharedAudioFileReader` 打开并读取 → `TagLibTagService.Write` →
    继续读取成功，位置 0.500s → 0.510s，标题已更新；
  - 完整播放器（`Volume = 0`）：`WasapiAudioPlayer.LoadAsync(play)` 后 `State=Playing`、位置 2.170s
    → 写标签 → 1.5s 后 `State=Playing`、位置 3.790s，标题已更新（全程未停止）。
- 手动验证步骤：播放一首歌 → 右键属性 → 修改标题 → 保存 → 播放不中断、位置连续、标签生效；
  暂停状态下重复 → 保存后仍保持暂停。

## 风险与局限

- 同目录临时文件在进程崩溃时可能残留（文件名含 `.echogarage-` 前缀），需手动删除；
  正常路径与异常路径都会在 `finally` 中清理。
- `File.Replace` 失败回退 `File.Move` 仍失败时，会抛出原始写入错误并计入失败汇总。
- Media Foundation 流式读取需要 Windows 10+（与现有播放器要求一致）。
- 共享句柄方案要求所有播放路径都使用 `SharedAudioFileReader`；未来新增播放后端时需保持该约束。
