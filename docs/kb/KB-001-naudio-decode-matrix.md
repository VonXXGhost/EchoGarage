# KB-001：音频解码、标签读取与交互行为笔记

- 状态：Accepted（原 KB-001、KB-005 于 2026-11-09，原 KB-007 于 2026-11-11 获 owner 批准；
  2026-11-19 合并原 KB-001/005/007，**原 KB-005、原 KB-007 已并入本文**）
- 日期：2026-11-09（解码矩阵 / TagLib#）/ 2026-11-10（扫描 ReadStyle / 进度条点击）/ 2026-11-19（合并）
- 关联：`docs/adr/ADR-001-platform-and-stack.md`、`docs/adr/ADR-009-wasapi-audio-pipeline.md`、
  `docs/adr/ADR-003-files-are-truth.md`、`docs/adr/ADR-010-metadata-cache-invalidation.md`、
  `docs/adr/ADR-020-playing-file-write-lock.md`
- 环境：Windows 10.0.26200（Windows 11 24H2 内核版本号）；.NET 10（2026-11-19 实测 runtime 10.0.15、
  SDK 10.0.404；本文最初记录时为 10.0.14）；NAudio 2.2.1；TagLibSharp 2.3.0

## 1. 解码矩阵（按当前 `SharedAudioFileReader`）

播放路径不再使用 `AudioFileReader`：`src/EchoGarage.Infrastructure/Audio/SharedAudioFileReader.cs`
以 `FileStream`（`FileShare.ReadWrite | FileShare.Delete`）打开文件，由扩展名分派：

| 扩展名 | 解码器 | 备注 |
| --- | --- | --- |
| `.wav` | `WaveFileReader`（NAudio 原生） | 不依赖 Media Foundation |
| `.aiff` / `.aif` | `AiffFileReader`（NAudio 原生） | 不依赖 Media Foundation |
| `.mp3` / `.m4a` / `.aac` / `.flac` / `.wma` / 其它 | `StreamMediaFoundationReader` | 统一走 Windows Media Foundation |

- 共享打开的收益：播放期间标签写入器可以把新内容写到临时文件后原子替换原文件，而不必中断播放
  （见 ADR-020）。旧文档中“mp3 走 `Mp3FileReader`/ACM、`AudioFileReader` 内部再选解码器”的描述
  已不适用于播放路径；`AudioFileReader` 目前只在波形分析器（`WaveformAnalyzer`）等离线读取处使用。
- 所有非 wav/aiff 格式都依赖 Windows Media Foundation：精简版系统缺少对应 MF 解码器时会失败，
  需要后续可选地引入第三方解码器（如 FlacBox / FFmpeg）。
- 解码后统一过 `SampleChannel`（float 输出），再由播放器按共享/独占模式重采样为设备格式。

## 2. 设备枚举与预热

- 设备枚举使用 `MMDeviceEnumerator`，读取 `AudioClient.MixFormat`。2026-11-09 实测默认设备
  MixFormat = IEEE float 32-bit / 48kHz / 2ch。
- `Warmup()`：`MediaFoundationApi.Startup()` + 枚举默认输出端点并读取 `MixFormat`，
  日志 `Audio warmup completed (<设备>).`；首次播放慢、之后快，预热很有价值（ADR-009）。
- 共享模式直接用设备 MixFormat；独占模式优先源采样率，再依次尝试 48k/44.1k/96k/32k。

## 3. 陷阱

- 在受限沙箱（不允许 COM / 命名管道）中，`MediaFoundationReader` / `StreamMediaFoundationReader`
  会抛 `UnauthorizedAccessException (0x80070005)`，而 TagLib 读取正常。这是环境限制，不是代码问题；
  正式运行（普通用户会话）正常。
- **不要把解码器构造放在 UI 线程**：`LoadAsync` 在 `Task.Run` 内 `new SharedAudioFileReader(path)`；
  大文件 / 首次 MF 初始化会卡住界面。
- **`WasapiOut` 会接管传入的 `MMDevice` 并在 `Dispose` 时释放它**。如果缓存了 `MMDevice` 以便
  “跳转时复用”，跳转（重建输出链路）后再次访问 `device.AudioClient` 会抛
  `InvalidCastException ... E_NOINTERFACE`。正确做法：销毁输出链路时同时丢弃缓存的设备，
  下次启动输出时重新获取。本项目实测跳转失败一次后按此修复。
- 跳转不要重建 WASAPI 输出：`Pause → reader.CurrentTime = target → Play` 即可，实测 `Seek()`
  调用 2–9 ms；重建输出约需 1 s，会像重新开始播放。
- 播放器日志：`Playback started on <设备> · 共享/独占 · 混音格式 48kHz/2ch for <path>`；
  跳转成功写 `Seeked to 00:06.000 (playing=True)`（`WasapiAudioPlayer`）。

## 4. TagLib# 使用细节

- 读取：`TagLib.File.Create(path, ReadStyle.Average)`；`file.Tag`、`file.Properties`。
  - `Properties.Duration`、`AudioBitrate`（kbps）、`AudioSampleRate`、`AudioChannels`。
  - `Tag.JoinedPerformers / FirstPerformer`、`JoinedAlbumArtists`、`JoinedGenres`、`Year`、`Track`、`Disc`。
  - 封面：`Tag.Pictures`（`IPicture.Data.Data`、`MimeType`、`Type`），优先 `PictureType.FrontCover`。
- 写入：`TagLib.File.Create(path, ReadStyle.None)` 后修改 `file.Tag` 并 `file.Save()`。
- **`file.TagTypes` 是只读的**，不能用 `file.TagTypes = ...` 创建标签块（编译错误）。
  文件原本没有标签时用：
  ```csharp
  var tag = file.GetTag(TagLib.TagTypes.Id3v2, create: true);
  ```
  不同扩展名对应：mp3/aiff → Id3v2；flac → Xiph；m4a/mp4 → Apple；wma → Asf；wav → Id3v2（RIFF 内嵌块）。
- 某些格式（如原始 ADTS `.aac`）不支持写入标签；此时应抛出明确异常而不是静默丢弃。

### 4.1 实测差异

- **WAV**：`GetTag(Id3v2, true)` + Save 可以写入标题/艺术家/专辑/年份/音轨/封面；写两次后时长仍正确。
- **FLAC**：Vorbis Comment + `METADATA_BLOCK_PICTURE` 正常工作；ffmpeg 生成的封面可被读取。
- **MP3**：带内嵌封面时，TagLib 读出的 Duration 可能明显大于实际音频（测试文件 24.1 s vs 播放器 12 s）。
  因此界面显示时长应以扫描值/播放器值为准，TagLib 只用于元数据；同一文件再次扫描会使用缓存值。
- **M4A**：`Tag.Year` 仍常为 0（iTunes 的 `©day` 存的是日期字符串，TagLib 未映射到 `Year`）。
  **本项目已实现年份原文读取**：`TagLibTagService.ReadYearText` 依次读 Apple `©day` → ID3v2 `TDRC`
  （其次 `TYER`）→ Xiph `DATE`（其次 `YEAR`）→ ASF `WM/Year`，写回时保持原字段格式
  （年份原文显示见 `docs/adr/ADR-016-search-hotkeys-playback-mode.md` 的“年份原文显示”节，
  原 ADR-026 已并入该文件；Apple 旧式音轨号自愈迁移见 `docs/adr/ADR-014-cache-repair-migrations.md`，
  原 ADR-029 已并入该文件）；界面用 `YearText` 展示，不要直接依赖 `Tag.Year`。

### 4.2 编码问题

- TagLib 对 ID3v2 使用 UTF-16/UTF-8 存储，中文示例数据（`Demo Track 01 / Demo Artist / Demo Album A`）
  往返无损。
- 写标签后必须重新 `Create` 读取，不能复用旧的 `File` 对象判断结果。

### 4.3 与数据库的关系

- 数据库中的 `DurationMs`、`Bitrate`、`SampleRate`、`Channels` 是扫描时的快照；
  标签编辑保存后会重新索引，保证界面与文件一致（见 ADR-003）。

## 5. 扫描读取标签：`ReadStyle.PictureLazy` 是标志位，必须与 `Average` 组合

- 枚举实际值：`None = 0`、`Average = 2`、`PictureLazy = 4`，且带 `[Flags]`。
- 只传 `PictureLazy` 时，标签能读出来，但**音频属性完全不解析**：`file.Properties` 为 `null`。
  随后访问 `properties.Duration` 会抛 `NullReferenceException`，扫描器把整首歌降级为“仅文件名”。
- 扫描阶段应使用：

  ```csharp
  TagLib.File.Create(path, TagLib.ReadStyle.Average | TagLib.ReadStyle.PictureLazy);
  ```

  这样既有 `Duration/Bitrate/SampleRate/Channels`，又不把内嵌封面字节读进内存
  （读取入口在 `TagLibTagService`：需要封面字节时用 `Average`，否则用 `Average | PictureLazy`）。
- 需要封面字节时（标签属性对话框、封面缓存提取）继续用 `ReadStyle.Average`。
- 读取代码必须对 `file.Tag` / `file.Properties` 判空，音频文件损坏或格式不支持时也要能降级。
- 实测（2026-11-10）：2,825 首 M4A/MP3 用 `Average` 读取约 4.0 s（含把约 470 MB 封面复制到内存），
  改成 `Average | PictureLazy` 后整轮扫描（8 线程、含数据库写入）约 1.0 s。

## 6. WPF 进度条点击/拖动跳转：`Slider.IsMoveToPointEnabled=true` 会吞掉 XAML 的 Preview 事件

- `Slider.OnPreviewMouseLeftButtonDown` 在 `IsMoveToPointEnabled=true` 且点击点在轨道上时，
  会自己把 Thumb 移动到点击位置，并设置 `e.Handled = true`。
- 结果：XAML 上挂的 `PreviewMouseLeftButtonDown="..."` **永远不会被调用**（注册时没有
  `handledEventsToo`）。双击时因为第一次点击已把 Thumb 移到光标下，第二次点击命中 Thumb、
  类处理器不再拦截，反而偶尔能跳——表现为“单击从不跳转、双击偶尔跳转”。
- 另外类处理器用 `Value = newValue` 直接赋值，会把 `Value` 的 OneWay 绑定顶掉，Thumb 从此不再跟随播放。
- 自定义点击 / 拖动跳转的可行做法：
  1. `IsMoveToPointEnabled="False"`，不让 Slider 自己处理；
  2. 用 `AddHandler(PreviewMouseLeftButtonDownEvent, ..., handledEventsToo: true)`（Move / Up 同理）
     注册处理器，并 `CaptureMouse()`，保证拖出控件也能收到 Up；
  3. 目标值用 `e.GetPosition(SeekSlider).X / ActualWidth` 计算，`SetCurrentValue` 写回，保留绑定；
  4. 一次手势只提交一次（MouseUp / LostCapture 共用 guard）；点在 Thumb 上没有位移时视为误触，不 seek；
  5. 跳转不要重建 WASAPI 输出（见第 3 节）。
- 调试日志（`[DEBUG]`）：`Seek pointer down`、`Seek begin`、`Seek point`、`Seek target (down/move/up)`、
  `Seek gesture end`、`Seek commit`、`Seek applied`；播放器侧还有 `Seek request`、
  `Seek reader repositioned`、`Seek resume`。排查用户反馈时先看这些行是否成对出现。

## 7. 验证

- 单元测试：`TagAndCoverTests.ReadWithoutCover_StillReturnsTagsAndDuration`。
- 真实鼠标（UI Automation 定位 + `SendInput`）验证：单击 70% 处从 1 s 跳到 255 s（单击生效），
  按住从 20% 拖到 60% 后落在 219 s；两次都只产生一条 `Seeked to` 日志，位置继续前进。
- 生成测试文件（ffmpeg 可用时）：

  ```powershell
  ffmpeg -f lavfi -i "sine=frequency=440:duration=12" -c:a flac out.flac
  ffmpeg -f lavfi -i "sine=frequency=440:duration=12" -c:a libmp3lame -b:a 192k out.mp3
  ffmpeg -f lavfi -i "sine=frequency=440:duration=12" -c:a aac -b:a 192k out.m4a
  ```

  播放器日志中应能看到：
  `Playback started on <设备> · 共享 · 混音格式 48kHz/2ch for <path>`。

## 8. 修订记录

- 2026-11-09：KB-001 初版（NAudio 解码矩阵 / 环境依赖）、KB-005 初版（TagLib# 各格式差异）。
- 2026-11-10：KB-007 初版（扫描 `ReadStyle.PictureLazy` 组合、进度条点击跳转）；2026-11-11 批准。
- 2026-11-19：owner 要求简化合并文档：原 KB-005、原 KB-007 全文并入本文
  （**原 KB-005、原 KB-007 已并入本文**），标题改为「音频解码、标签读取与交互行为笔记」；
  按 `SharedAudioFileReader` 重写解码矩阵（wav/aiff 原生，其余含 mp3 走 Media Foundation，
  MF 启动预热）；环境版本更新为 runtime 10.0.15；M4A 年份改为“已实现（读 Apple `©day`）”，
  删除“后续应读取”；中文测试示例改为 dummy 值；补充扫描 `Average | PictureLazy` 的代码位置。
