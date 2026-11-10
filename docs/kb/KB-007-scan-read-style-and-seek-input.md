# KB-007：扫描读取标签与进度条点击的两个 WPF / TagLib 坑

- 状态：已批准（Accepted，2026-11-11，owner）
- 日期：2026-11-10
- 关联：KB-005（TagLib# 使用细节）、ADR-009（WASAPI 播放管线）、ADR-010（缓存可用性失效）
- 环境：TagLibSharp 2.3.0、.NET 10 WPF

## 1. TagLibSharp 的 `ReadStyle.PictureLazy` 是标志位，必须与 `Average` 组合

- 枚举实际值：`None = 0`、`Average = 2`、`PictureLazy = 4`，且带 `[Flags]`。
- 只传 `PictureLazy` 时，标签能读出来，但**音频属性完全不解析**：`file.Properties` 为 `null`。
  随后访问 `properties.Duration` 会抛 `NullReferenceException`，扫描器把整首歌降级为“仅文件名”。
- 扫描阶段应使用：

  ```csharp
  TagLib.File.Create(path, TagLib.ReadStyle.Average | TagLib.ReadStyle.PictureLazy);
  ```

  这样既有 `Duration/Bitrate/SampleRate/Channels`，又不把内嵌封面字节读进内存。
- 需要封面字节时（标签属性对话框、封面缓存提取）继续用 `ReadStyle.Average`。
- 读取代码必须对 `file.Tag` / `file.Properties` 判空，音频文件损坏或格式不支持时也要能降级。
- 实测：2,825 首 M4A/MP3 用 `Average` 读取约 4.0 s（含把约 470 MB 封面复制到内存），
  改成 `Average | PictureLazy` 后整轮扫描（8 线程、含数据库写入）约 1.0 s。

## 2. WPF `Slider.IsMoveToPointEnabled=true` 会吞掉 XAML 的 Preview 鼠标事件

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
  5. 跳转不要重建 WASAPI 输出：`Pause → reader.CurrentTime = target → Play` 即可，
     实测 `Seek()` 调用 2–9 ms，之后位置正常前进；重建输出约需 1 s，会像重新开始播放。
- 调试日志（`[DEBUG]`）：`Seek pointer down`、`Seek begin`、`Seek point`、`Seek target (down/move/up)`、
  `Seek gesture end`、`Seek commit`、`Seek applied`；播放器侧还有 `Seek request`、
  `Seek reader repositioned`、`Seek resume`。排查用户反馈时先看这些行是否成对出现。

## 验证

- 单元测试：`TagAndCoverTests.ReadWithoutCover_StillReturnsTagsAndDuration`。
- 真实鼠标（UI Automation 定位 + `SendInput`）验证：单击 70% 处从 1 s 跳到 255 s（单击生效），
  按住从 20% 拖到 60% 后落在 219 s；两次都只产生一条 `Seeked to` 日志，位置继续前进。
