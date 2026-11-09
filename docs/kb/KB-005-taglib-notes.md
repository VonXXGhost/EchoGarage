# KB-005：TagLib# 使用细节与各格式差异

- 状态：已批准（Accepted，2026-11-09，owner）
- 日期：2026-11-09
- 环境：TagLibSharp 2.3.0

## API 要点

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

## 实测差异

- **WAV**：`GetTag(Id3v2, true)` + Save 可以写入标题/艺术家/专辑/年份/音轨/封面；写两次后时长仍正确。
- **FLAC**：Vorbis Comment + `METADATA_BLOCK_PICTURE` 正常工作；ffmpeg 生成的封面可被读取。
- **MP3**：带内嵌封面时，TagLib 读出的 Duration 可能明显大于实际音频（测试文件 24.1 s vs 播放器 12 s）。
  因此界面显示时长应以扫描值/播放器值为准，TagLib 只用于元数据；同一文件再次扫描会使用缓存值。
- **M4A**：`Year` 常为 0（iTunes 的 `©day` 存的是日期字符串，TagLib 未映射到 `Year`）。
  如需年份，后续应读取 `TagLib.Mpeg4.AppleTag` 的自定义字段。

## 编码问题

- TagLib 对 ID3v2 使用 UTF-16/UTF-8 存储，中文测试（“第一首 / 测试艺术家 / 测试专辑”）往返无损。
- 写标签后必须重新 `Create` 读取，不能复用旧的 `File` 对象判断结果。

## 与数据库的关系

- 数据库中的 `DurationMs`、`Bitrate`、`SampleRate`、`Channels` 是扫描时的快照；
  标签编辑保存后会重新索引，保证界面与文件一致（见 ADR-003）。
