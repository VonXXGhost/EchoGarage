# KB-001：NAudio 解码矩阵与环境依赖

- 状态：已批准（Accepted，2026-11-09，owner）
- 日期：2026-11-09
- 关联：ADR-001、ADR-009
- 环境：Windows 10.0.26200（Windows 11 24H2 内核版本号）、.NET 10.0.14、NAudio 2.2.1

## 结论（实测）

对同一批测试文件（ffmpeg 生成：FLAC、MP3、M4A/AAC、WAV）：

| 格式 | `AudioFileReader` | `MediaFoundationReader` | 备注 |
| --- | --- | --- | --- |
| .flac | OK（内部走 MediaFoundationReader） | OK，16-bit PCM 44.1kHz | 12s 立体声，时长正确 |
| .mp3 | OK（Mp3FileReader/ACM） | OK | MP3 带内嵌封面时 TagLib 报 24s，解码器报 12s，以播放器为准 |
| .m4a | OK（走 MF） | OK | AAC-LC |
| .wav | OK | OK | 单声道 9s 正常 |

## 重要前提

- FLAC / AAC / M4A 全部依赖 **Windows Media Foundation**。若系统是精简版（缺少 MF 解码器），这些格式会失败，
  需要后续可选地引入第三方解码器（如 FlacBox / FFmpeg）。
- 设备枚举使用 `MMDeviceEnumerator`，读取 `AudioClient.MixFormat`。实测默认设备为
  `扬声器 (3- Pebble V3)`，MixFormat = IEEE float 32-bit / 48kHz / 2ch。

## 陷阱

- 在受限沙箱（不允许 COM / 命名管道）中，`MediaFoundationReader`、`Mp3FileReader` 会抛
  `UnauthorizedAccessException (0x80070005)`，而 TagLib 读取正常。这是环境限制，不是代码问题；
  正式运行（普通用户会话）正常。
- `WasapiOut` 的共享 / 独占初始化是“第一次慢、之后快”，因此启动预热（ADR-009）很有价值。
- 不要在 UI 线程同步构造 `AudioFileReader`：大文件 / 首次 MF 初始化会卡住界面；播放器实现里用 `Task.Run` 包住 Load。
- **`WasapiOut` 会接管传入的 `MMDevice` 并在 `Dispose` 时释放它**。如果缓存了 `MMDevice` 以便“跳转时复用”，
  跳转（重建输出链路）后再次访问 `device.AudioClient` 会抛
  `InvalidCastException ... E_NOINTERFACE`。正确做法：销毁输出链路时同时丢弃缓存的设备，下次启动输出时重新获取。
  本项目实测跳转失败一次后按此修复，日志出现 `Seeked to 00:06.000 (playing=True)` 即正常。

## 复现方式

```powershell
# 生成测试文件（ffmpeg 可用时）
ffmpeg -f lavfi -i "sine=frequency=440:duration=12" -c:a flac out.flac
ffmpeg -f lavfi -i "sine=frequency=440:duration=12" -c:a libmp3lame -b:a 192k out.mp3
ffmpeg -f lavfi -i "sine=frequency=440:duration=12" -c:a aac -b:a 192k out.m4a
```

播放器日志中应能看到：
`Playback started on <设备> · 共享 · 混音格式 48kHz/2ch for <path>`。
