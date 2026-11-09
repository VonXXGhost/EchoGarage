# ADR-009：WASAPI 音频管线

- 状态：已批准（Accepted，2026-11-09，owner）
- 日期：2026-11-09

## 背景

需要“音频输出设备选择 + Windows 默认通道输出 / WASAPI 独占输出 + 进度条跳转”，并且开头要快。

## 决策

1. 解码：`AudioFileReader`（WAV/MP3/AIFF 原生，FLAC/M4A/AAC/WMA 走 Media Foundation），统一得到 `ISampleProvider`（float）。
2. 输出链路：
   `AudioFileReader → WdlResamplingSampleProvider（必要时）→ 声道转换 → VolumeSampleProvider → SampleToWaveProvider(16) → WasapiOut`
3. 共享模式：目标格式 = 设备的 `AudioClient.MixFormat`（当前机器为 48kHz/2ch float），避免系统混音器二次转换。
4. 独占模式：按候选格式依次尝试
   1. 源采样率 + float32；
   2. 48k / 44.1k / 96k / 32k + float32；
   3. 以上采样率 + 16-bit PCM。
   任一成功即停止；全部失败返回明确错误，由 UI 提示并跳到下一首。
5. 跳转：停止并重建输出链路（避免 WASAPI 已缓冲的旧音频继续播放），把解码器定位到目标时间后再启动。
   播放位置由解码器 `CurrentTime` 报告，UI 用 200ms 定时器刷新。
6. 音量：在采样域用 `VolumeSampleProvider`，共享/独占行为一致。
7. 预热：窗口显示后在后台线程调用 `MediaFoundationApi.Startup()`、取一次默认设备与 `MixFormat`、
   打开一次 SQLite 连接，降低首次播放 / 首次设备枚举的等待。
8. 设备枚举与切换都在后台或延迟执行，不在启动关键路径上；切换输出在下次播放（或下一次 Load）生效。

## 理由

- 共享模式必须匹配混音格式，否则 WASAPI 初始化失败；主动转换比依赖系统隐式转换可控。
- 独占模式的“格式阶梯”能兼容只支持 PCM16 的老设备 / 声卡驱动。
- 重建链路跳转虽然有几毫秒开销，但语义干净、不会出现“跳转后还听到旧片段”。

## 后果

- 独占模式下其他程序无法出声是预期行为，UI 已说明。
- 每次跳转会短暂中断（通常 < 100ms），无缝播放/gapless 需后续单独设计。
- 首次播放（尤其是 MP3 的 ACM 解码器）可能有约 1 秒初始化；预热能覆盖大部分场景，但新进程第一次 MP3 仍可能较慢。

## 备选方案

- 使用 `WasapiLoopbackCapture` / 直接 P/Invoke WASAPI：工作量与风险更大，NAudio 已覆盖需求。
- 独占模式固定 48k：会强制重采样所有 44.1k 音乐，音质与 CPU 都更差。
