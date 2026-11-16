# ADR-015-A2（提案）：波形高度窗口按“最小显示动态范围”向下放宽下界

- 状态：**Accepted**（owner 批准：2026-11-16；实现已随本提案提交）
- 日期：2026-11-16
- 关联：ADR-015（波形进度条，已生效；本文件为其修订）、ADR-015-A1（已播放区不透明度，已生效）

## 1. 背景与现象

owner 反馈：部分以简单乐器 / 持续音符为主的歌曲（示例：`Demo Artist` 的 `Demo Track 01`）播放正常、
右栏可视化有声音，但**波形进度条显示为空白**；同一批里鼓点密集、动态较大的曲目（`Demo Album A` 中
其他音轨）波形正常。

排查范围：`WaveformMath.StretchWindow`（最终高度窗口）、`WaveformAnalyzer` 最终快照、
`FileWaveformCache` 版本校验、以及 App 层 `WaveformSeekBar` 的绘制映射（只读检查）。

## 2. 根因

ADR-015 §6.6（当前归档文本第 307–308 行）规定：

1. `lo = p5`、`hi = p99.5`；
2. `hi = max(hi, lo + 8.0)`（最小动态范围 8 dB）。

第 2 步是**抬高上限**。对恒定电平 / 窄动态素材，`p5 ≈ p99.5`（例如整曲稳定在 −20 dB 附近、只有
±1 dB 起伏）：`lo ≈ hi`，`hi` 被抬到 `lo + 8`，而所有列的 dB 都约等于 `lo`，于是

```
v = (dB − lo) / (hi − lo) ≈ 0 / 8 ≈ 0
```

`WaveformMath.StretchHeight` 对 `v == 0` 返回 0；`WaveformSeekBar` 绘制时跳过 `heightByte == 0` 的列
（且没有按 `FloorDb/CeilingDb` 的二次归一化），最终整条波形看起来是空的。

与 owner 描述吻合的另一个证据：**分析过程中反而正常**。渐进快照使用固定 −48…0 dB 窗口
（ADR-015 §6.10），同一首 −20 dB 的恒定素材在分析中高度约 `(28/48)·255 ≈ 149`；分析完成、切换成
全曲 p5/p99.5 最终快照的瞬间，整条波形归 0。owner 看到的“分析时有、完成后空”即此。

复现方法（测试中已固化）：3 s、440 Hz、振幅恒定的单声道 WAV，旧窗口 `lo ≈ hi ≈ −9.03 dB`，
稳定段所有列 `height = 0`。

## 3. 决策（建议）

### 3.1 窗口算法：只向下放宽下界

```text
lo = p5
hi = p99.5
lo = max(SilenceDb, min(lo, hi − MinimumStretchDb))   // 新增
```

- 不再抬高 `hi`，`hi` 始终贴近素材的实际响度上限；
- 动态范围 ≥ `MinimumStretchDb` 的素材：`min(lo, hi − 30) = lo`，窗口**逐位不变**；
- 动态范围不足 30 dB 时：`lo` 向下扩展，保证至少 30 dB 的显示量程；
- `lo` 钳制在 `SilenceDb`（−100 dB），极端安静素材的窗口不会落到数字静音以下；钳制后窗口仍非退化，
  非静音列高度接近满高。

### 3.2 常量

`WaveformMath.MinimumStretchDb`：`8.0` → `30.0`（名称与可见性不变，语义改为“最小**显示**动态范围”）。
`StretchHeight`、三频段份额、`WaveformPeaks` 结构与 EGWV 字节布局均不改。

### 3.3 StreamingWindow（−48…0 dB）不调整

渐进分析使用固定 48 dB 量程，恒定电平 / 窄动态素材在分析阶段本来就有可见高度（见 §2），
“空图”只发生在最终快照。改动 `StreamingWindow` 会破坏 ADR-015 §6.10 的“分析过程零重排”语义，
并让既有渐进窗口测试失去意义，收益为零，因此**保持不变**。

### 3.4 分析器版本

`WaveformAnalyzer.AnalyzerVersion`：`1` → `2`。缓存格式 `FormatVersion` 仍为 `1`（72 字节头、
每列 4 字节布局未变）。读取时 `FileWaveformCache.Parse` 校验头部 analyzer version，旧 v1 缓存被判定
无效、删除并返回 null，`WaveformService` 随即重新分析并写回 v2。

## 4. 阈值依据与取舍

- **30 dB 的量级**：常见“简单乐器 / 持续音符”素材的列间起伏约 ±1–3 dB。显示量程固定为 30 dB 时，
  稳定列高度为 `(30 − 3)/30 ≈ 90%` 满高以上，肉眼明显可见；同时 2–3 dB 的内部起伏只占约 17–25 px，
  **不会**被放大成“假层次”——这恰好缓解了 ADR-015 原第 3 步担心的“整曲只有 2 dB 动态时把噪声
  放大”的问题（旧做法把 8 dB 量程拉满，2 dB 起伏会占 25% 高度；新做法只占约 7%）。
- **宽动态素材零回归**：p5–p99.5 跨度 ≥ 30 dB 的素材（鼓组 / 动态大的流行、古典）窗口与旧实现
  完全一致，缓存中的 `FloorDb/CeilingDb` 与高度字节都不变（单测锁定 85 dB 示例的精确分位值）。
- **上限不抬高**：`hi = p99.5` 仍避免单个爆音拉高窗口；同时保证素材最响的列接近满高，不会出现
  “整曲最高只有 80% 高度”的观感。
- **可预期的视觉代价**：窄动态素材的内部对比被压缩到顶部附近（更接近“实心带”），这是
  “有声音必须可见”优先于“细粒度层次”的取舍；需要细节时可后续增加用户可选的对比度设置，
  但不在本次范围。

## 5. App 层复核结论（只读，未改 App 文件）

`src/EchoGarage.App/Views/Controls/WaveformSeekBar.cs`：

- 几何缓存键只有列数、`FloorDb/CeilingDb`（容差 0.01）、设备像素宽高与 DPI；
- `AppendGeometry` 直接把 `heightByte` 映射为 `heightPx = round(heightByte / 255 · deviceHeight)`
  （钳制到 `[1, deviceHeight]`），`heightByte == 0` 时跳过该列；
- 段内聚合用控件本地的 p80 高度与份额加权，**没有**再按 `FloorDb/CeilingDb` 做二次归一化。

因此空图的唯一来源是分析器最终快照输出 `height ≈ 0`；本次只需修分析器窗口，App 层不需要改动。
（若将来要修“`heightByte == 0` 不画”的观感问题，应另立提案，避免越权改 App 层。）

## 6. 缓存版本与兼容影响

- **旧缓存**：v1 `.wfm` 文件头部 analyzer version 与当前 2 不符 → 读时校验失败 → 删除文件 →
  重新分析（每首约 1 s 量程，后台 `BelowNormal` 低优先级，ADR-015 §6.10 / §7.3 原有机制）。
  无需迁移脚本，也不需要用户手动清缓存。
- **回滚**：把 `MinimumStretchDb` 改回 8、`StretchWindow` 恢复抬高上限、`AnalyzerVersion` 改回 1 即可；
  届时 v2 缓存同样因版本不符失效重算，不需要清理文件。
- **格式兼容**：EGWV `FormatVersion = 1` 不变；外部工具（若有）按 72 字节头解析不受影响。
  只是 `FloorDb/CeilingDb` 的数值语义随分析器版本变化，缓存本身已用 analyzer version 区分。
- **配置 / 数据库**：无影响（不涉及 schema、settings.json、library.db）。

## 7. 验证方式

### 7.1 单元测试（随实现提交）

`WaveformMathTests`：

- 恒定电平 `[-70, -70, -70]`：`ceiling = −70`、`floor = SilenceDb`、量程 30 dB，
  −70 dB 列映射为满高 255（旧实现为 0）；
- 窄动态 ±0.5 dB：稳定列高度 > 200 且保留可见高度差；
- 宽动态 85.05 dB（101 列精确分位）：`floor = −90.5`、`ceiling = −5.45`，与旧实现逐位一致；
- 极安静窄动态（−99 dB）：`floor` 被钳制到 `SilenceDb`，窗口非退化、列高 255；
- 空输入 / 全静音 / `floor == ceiling` 防御路径维持原语义。

`WaveformAnalyzerTests`：

- 3 s 恒定正弦：最终快照稳定段 `height > 200`，`ceiling − floor == 30 dB`；
- 渐进快照窗口仍恒为 −48…0 dB（ADR-015 §6.10 不变）。

`WaveformCacheTests`：

- 头部字段：`FormatVersion = 1`、`analyzer version = 当前 AnalyzerVersion`；
- 旧 AnalyzerVersion 缓存被拒绝并删除；
- 集成：旧缓存 → `WaveformService` 真实重算 → 恒定素材稳定段有高度 → 按 v2 写回并可命中缓存。

### 7.2 构建与全量测试

- Debug：`scripts/test.ps1`（全量构建 + xUnit v3 进程内测试）；
- Release：`scripts/build.ps1 -Configuration Release`。

### 7.3 手工验证建议（不修改验收目录）

- 用副本目录 + `ECHOGARAGE_DATA_DIR` 指向临时目录，运行
  `--waveform <file> <png>` 对 dummy 持续正弦 / 简单乐器片段生成截图，确认由空白变为有高度；
- 抽查一首宽动态曲目（`Demo Album A` 中鼓点密集音轨）与旧截图对比，宽动态行为应一致；
- owner 验收目录 `D:\Temp\EchoGarage-UAT` 只读检查，不修改任何内容。

## 8. 备选方案

1. **只把 8 dB 改成 30 dB，仍抬高上限**：恒定电平素材 `p5 ≈ p99.5` 时所有列仍落在下界、高度仍为 0，
   根因未解决，否决。
2. **固定全局窗口（如 −60…0 dB）**：宽动态素材会变平，违背 ADR-015 §6.6 的逐曲归一目标，否决。
3. **对非静音 0 高度列统一给 1 px**：得到一条贴底虚线，无法判断内容，且破坏 p80 聚合，否决。
4. **改用 p1–p99 等更宽分位**：恒定素材 p1 ≈ p99 仍退化；且改变宽动态行为，否决。
5. **App 层二次归一化**：控件没有列 dB，只能放大字节（等同放大噪声），并引入跨层耦合，否决。

## 9. 回滚

见 §6“回滚”。改动仅限 `WaveformMath` 窗口算法、`MinimumStretchDb`、`WaveformAnalyzer.AnalyzerVersion`
与对应测试；无数据迁移、无配置变更、无 App 层改动。

## 10. 归档时需同步修订的文档

owner 批准后，移入 `docs/adr/` 并更新 `docs/adr/ADR-index.md`；同时修订 ADR-015：

- §0.3 / §6.6：最小动态范围 8 dB → **30 dB**，并说明改为“向下扩展下界、上限不抬高”；
- §6.10 表格“完成”行的“最小 8 dB”同步更新，并注明渐进窗口不变；
- §7.2 / §7.3：analyzer version 2 及旧缓存自动失效说明；
- §12.1 测试清单：补充窄动态可见、宽动态不变、下界钳制、旧版本缓存重算；
- §17 修订记录：新增 2026-11-16 条目（本修订）。
