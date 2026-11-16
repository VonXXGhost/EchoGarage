# ADR-023-A1（提案）：播放可视化增加渲染帧率上限

- 状态：**Accepted**（owner 批准：2026-11-16；实现已随本提案提交）
- 日期：2026-11-16
- 关联：ADR-023（播放可视化，已生效；本文件为其补充，不改变其算法与口径）

## 1. 背景与动机

ADR-023 采用**拉取式渲染**：`SpectrumView` 订阅 `CompositionTarget.Rendering`，每个 WPF 渲染 tick
都调用一次 `ISpectrumSource.NextFrame(dt)` 并 `InvalidateVisual()`（ADR-023 §6.1、§8）。
在 60 Hz 显示器上实测「渲染 tick == 实际绘制」为 139–142 FPS（ADR-023 §9，120/144 Hz 面板上按面板刷新率
1:1 出帧），每帧 OnRender 0.17–0.21 ms、分析 0.15–0.28 ms。

该模型满足 ADR-023 §2.1「稳定 ≥60 FPS」的目标，但代价是**只要有可视化就持续满载渲染**：

- 120 / 144 / 180 Hz 显示器上，每秒做 120–180 次 FFT + 绘制，比 60 FPS 多 2–3 倍；
- 频谱分析层本身不需要这么高的刷新率（FFT 4096 约 55 µs，但视觉上 60 FPS 已足够流畅）；
- 笔记本 / 多屏 / 远程桌面等场景下，持续渲染无谓消耗 CPU / GPU 与电量；
- 用户没有“开着可视化但想让机器安静一点”的选项（关掉可视化又会失去画面）。

owner 需求：为播放可视化增加 **FPS 限制选项 30 / 60 / 120 / 144 / 180 / 不限制**，在设置页可改、
即时生效、随配置持久化；**默认“不限制”**，即保持 ADR-023 现状行为。

## 2. 决策

### 2.1 数据模型（Core）

`VisualizationSettings` 增加：

```csharp
public enum VisualizerFrameRate
{
    Unlimited = 0,
    Fps30 = 30,
    Fps60 = 60,
    Fps120 = 120,
    Fps144 = 144,
    Fps180 = 180
}

public VisualizerFrameRate FrameRateLimit { get; set; } = VisualizerFrameRate.Unlimited;
```

- **枚举数值即 FPS**（`Unlimited = 0`），渲染层只需要一个整数上限，绑定与节流判定都不用再做映射表；
- 挂在 `VisualizationSettings` 而不是 `SpectrumSettings`：限制的是**渲染循环**，不是频谱分析参数；
  ADR-023 §14 阶段二的其它可视化形态（LED 分段柱、波形 / 示波器）可以复用同一个上限；
- `Normalize`：`!Enum.IsDefined(FrameRateLimit)` 回退 `Unlimited`（与 `Kind`、`Summation` 的兜底语义一致，
  ADR-023 §5）。手改 settings.json 写成 `"frameRateLimit": 42` 不会产生“负间隔 / 不节流”的异常行为；
- `Clone()` 拷贝枚举值（值类型天然深拷贝）；
- 绑定层换算：`VisualizerFrameRateExtensions.ToFps()`，未定义值返回 0（层间防御，与 `Normalize` 双保险）。

**序列化兼容**：`JsonDefaults.Options` 注册了 `JsonStringEnumConverter`，该字段落盘为
`"frameRateLimit": "Fps60"`（枚举名，与其它枚举字段一致）。旧版 `settings.json` 没有该字段 →
反序列化为默认值 `Unlimited`；旧版程序读到该字段 → System.Text.Json 默认忽略未知属性，直接回滚无迁移成本。

### 2.2 节流位置与公式（App 层）

`SpectrumView` 增加依赖属性 `FrameRateLimit`（`int`，0 = 不限制），由 `MainWindow.xaml` 绑定
`VisualizerFrameRateLimit`。节流放在 `OnRendering` 的**真实分析帧之前**，顺序为：

```text
可见性 / Source 检查
→ 同帧去重（RenderingTime == _lastRenderingTime 直接返回，ADR-023 原逻辑）
→ Interlocked.Increment(_renderingTicks) / 记录 _firstRenderingTime
→ 【节流判定】未到间隔 → return（不分析、不重绘、不推进计时）
→ dt = RenderingTime − _lastRenderingTime（首次 1/60）
→ _lastRenderingTime = RenderingTime
→ _frame = Source.NextFrame(dt)
→ InvalidateVisual()
```

判定公式：

```csharp
var frameRateLimit = FrameRateLimit;
if (frameRateLimit > 0 && _lastRenderingTime != TimeSpan.Zero &&
    (rendering.RenderingTime - _lastRenderingTime).TotalSeconds < FrameRateTolerance / frameRateLimit)
{
    return;
}
```

- `FrameRateTolerance = 0.95`：即“距上次分析帧不足 `0.95 / 上限` 秒就跳过”。见 §2.5 的取整分析；
- **跳过时绝不更新 `_lastRenderingTime`**：下一个真实分析帧的 `dt` 仍是真实经过时间，
  这正是弹道 / 峰值保持正确的前提（§2.3）；
- 跳过时也不调用 `NextFrame`、不 `InvalidateVisual`：`_renderingTicks` 仍在增长，
  但 `_renderedFrames` 只统计实际绘制帧，诊断口径见 §2.4；
- 窗口被遮挡 / 最小化时 `CompositionTarget.Rendering` 本身不出帧，节流不改变该行为（ADR-023 §9）。

**计时状态重置**：`Source`、`Options`（分析参数快照）、`FrameRateLimit` 变化时调用
`ResetFrameTiming()`（`_lastRenderingTime = _firstRenderingTime = TimeSpan.Zero`），
保证切换上限 / 参数后下一个可绘制 tick 立即出帧，不会因为旧计时状态“等间隔”而最多 1/30–1/60 秒不出帧；
`Hook()` / `OnLoaded()` 沿原有语义重置。

### 2.3 dt 与弹道正确性（关键约束）

ADR-023 §6.5：`level = max(measured, level − release·dt)`，峰值保持与下落也按 `dt` 推进，
且“速度用 dB/s × dt，与帧率无关”。因此节流必须满足：

- **dt 必须是真实经过时间**，不能固定成 `1 / 上限`。若固定，30 FPS 下释放速度会变成原来的
  一半（45 dB/s → 22.5 视觉 dB/s），峰值保持时长也会翻倍；
- 也不能把 `_lastRenderingTime` 推进到被跳过的 tick：那会让下一帧的 dt 偏小，产生同样的弹道变慢；
- 唯一正确的做法是**整帧跳过**：分析帧仍按真实 `RenderingTime` 差值推进弹道，
  只是分析帧变稀疏。弹道轨迹与“不限制时每 tick 分析”的连续轨迹一致（采样点变少，但落点相同）；
- `SpectrumAnalyzer.NextFrame` 内部把 `dt` 钳制在 `[0.001, 0.5]` 秒，即使极端情况下（窗口卡顿、
  恢复渲染）跳过的间隔很大，也不会把弹道推进到异常状态；
- 静音 / 暂停判定用 `_staleSeconds += dt`（150 ms 无新样本按静音喂弹道，ADR-023 §6.1），
  按真实 dt 累计，节流不改变“暂停后柱体自然落下去”的时间语义；
- FFT 仍取“最新 FFT 长度样本”（ADR-023 §6.1），只是取样本的时间点变稀疏，不累积延迟。

### 2.4 与 `--visualizer` 诊断的关系

ADR-023 §12 的诊断行：

```text
Visualizer diagnostic: file=… sampled=6.0s measured=5.99s renderFrames=838 fps=139.8
ticks=838 tickFps=139.8 renderAvg=0.213 ms renderMax=2.049 ms analyzerFrames=944 …
```

- `renderFrames` / `fps` 来自实际 `OnRender`，节流后**自然反映节流后的实际帧率**（如 60 Hz 面板选 30 →
  约 30 FPS），`renderAvg` / `renderMax` 只统计真正绘制的帧；
- `ticks` / `tickFps` 仍是 WPF 渲染 tick 速率（`_renderingTicks` 包含被跳过的 tick），所以
  `tickFps > fps` 就是“节流生效”的直接证据；
- `analyzerFrames` 来自 `SpectrumStats`，节流后等于实际分析帧数；
- `measured` 由首帧到最近分析帧的 `RenderingTime` 差算出，`fps` 的口径不变，
  可直接与 ADR-023 §9 的 139–142 FPS 基线对照。

**本提案未改 `src/EchoGarage.App/App.xaml.cs` 的诊断行**（该文件不在本次改动范围）。建议后续在其
`Visualizer diagnostic:` 行追加 `frameRateLimit=<SpectrumView.FrameRateLimit>` 字段（一行字符串拼接），
便于自动化验收直接断言“上限已生效”；这不影响节流正确性。

### 2.5 默认值与高刷屏取整（取舍）

**默认 `Unlimited`**：

- 保持 ADR-023 原有行为与性能基线，升级后画面与诊断日志逐位不变，不给既有用户带来观感回退；
- 节流是显式的省资源选项，144 / 180 Hz 用户默认仍享受满刷新率；
- 若默认 60，会把高刷屏用户的实际帧率、`--visualizer` 基线、截图对照全部改变，且违背
  “默认值应保持现状”的既有约定（ADR-023 §5 的默认参数同理）。

**实际帧率取整**：WPF 只能在显示器刷新 tick 上出帧（周期 `T = 1/R`）。节流判定“距上次分析 ≥ 0.95/L”，
所以实际每 `m` 个 tick 分析一次：

```text
m = max(1, ceil(0.95 · R / L))
实际帧率 = R / m
```

其中 `R` 是显示器刷新率、`L` 是设置的上限。60 Hz 与常见高刷屏下的实际帧率：

| 显示器 | 30 档 | 60 档 | 120 档 | 144 档 | 180 档 |
| --- | --- | --- | --- | --- | --- |
| 60 Hz | 30 | 60 | 60（≤ 刷新率） | 60 | 60 |
| 120 Hz | 30 | 60 | 120 | 120 | 120 |
| 144 Hz | 28.8 | **48** | 72 | 144 | 144 |
| 165 Hz | 27.5 | 55 | 82.5 | 82.5 | 165 |
| 180 Hz | 30 | 60 | 90 | 90 | 180 |
| 240 Hz | 30 | 60 | 120 | 120 | 120 |

- 上限是**天花板**，不是精确帧率：`L` 与 `R` 不成整数分频时实际帧率会低于 `L`（144 Hz 屏选 60 →
  48 FPS，选 120 → 72 FPS）；这来自“只在合成 tick 上出帧”的物理约束，任何方案（含动画计时器）都无法
  在同一显示时钟上精确产生 60 Hz；
- **0.95 容差的作用**：让 `L` 恰为 `R` 的整数分频（含 `L = R`）时能整除。若容差取 1.0，
  144 Hz 面板的 `RenderingTime` 抖动（如 6.9 ms vs 6.94 ms）会让“144 档”每隔一帧才满足条件，
  实际掉到 72 FPS；0.95 留 5% 余量，144 Hz 选 144 稳定 144 FPS、60 Hz 选 60 稳定 60 FPS；
  代价是理论实际帧率最多可比 `L` 高约 5%，但会被刷新率本身截断，观察不到；
- 若取更小容差（如 0.8），60 Hz 屏选 60 也能整除，但 144 Hz 屏选 120 会掉到 72（0.95 时是 72，
  因为 1.14 → 2；0.8 时 0.8·144/120 = 0.96 → 仍 1，实际 144，但会“超过上限 20%”），语义变混乱；
  0.95 是“尽量贴近所选上限、又不在刷新率整数分频上掉帧”的折中；
- 设置页提示文案明确“实际帧率还受显示器刷新率限制”，避免用户把 144 Hz 选 60 看到 48 当成 bug。

### 2.6 设置链路（即时生效与持久化）

```text
SettingsWindow「帧率上限」ComboBox（Tag = 枚举名）
→ ApplyVisualizer()：读取 Tag → Settings.Visualization.FrameRateLimit
→ VisualizationSettings.Normalize(...)（非法值回退不限制）
→ PopulateVisualizer()（回填控件）→ MainViewModel.RefreshVisualizer()
→ MainWindow 绑定 VisualizerFrameRateLimit 更新 → SpectrumView.FrameRateLimit DP
→ OnFrameRateLimitChanged 重置计时 → 下一 tick 按新上限出帧
→ SaveSettings()（既有 800 ms 防抖写盘，ADR-023 §11）
```

启动时 `RefreshVisualizer()` 在 `InitializeAsync` 中已调用，绑定初值即为磁盘上的设置；
“恢复默认参数”按钮走 `OnResetVisualizerSettings` 重建 `new VisualizationSettings()`，上限回到“不限制”。

### 2.7 对分析精度的影响（可接受代价）

节流只降低 FFT 采样频率，不改变 FFT 点数、频段划分、斜率与弹道速度：

- 30 FPS 时两次分析间隔约 33 ms，极短的瞬态峰值（< 33 ms）可能落在两次分析之间而不被绘制到；
  峰值保持线同样只基于已分析帧。这是“省资源”的预期代价，与频谱参数无关；
- 6.1 的“取最新样本”语义保证不累积延迟：低频柱体只按真实时间缓慢变化；
- 默认 `Unlimited` 时上述影响不存在。

## 3. 验证

### 3.1 单元测试（随实现提交）

`tests/EchoGarage.Tests/VisualizationSettingsTests.cs` 扩充（现有测试风格）：

- `Defaults_MatchDocumentedPreset`：默认 `FrameRateLimit == Unlimited`、`ToFps() == 0`；
- `Normalize_UnknownFrameRateFallsBackToUnlimited`：`(VisualizerFrameRate)42` → `Unlimited`；
- `FrameRateLimit_ToFpsMapsEnumValues`：六个合法枚举逐一映射 0/30/60/120/144/180，未知值 → 0；
- `Clone_IsDeepEnoughForSettingsPage`：`Fps144` 深拷贝，克隆体改 `Fps60` 不影响原对象；
- `SettingsFile_RoundTripsVisualizationSection`：`Fps144` 经 `settings.json` 往返仍为 `Fps144`；
- `Load_RepairsOutOfRangeVisualizationValues`：JSON 里 `"frameRateLimit": 42` 在 Load 时被修复为 `Unlimited`。

WPF 渲染层节流不做自动化单测（需要真实合成时钟），以下手工验收覆盖。

2026-11-16 实施结果：上述断言全部通过；Debug 全量构建 + xUnit v3 进程内测试 **584 项通过、0 失败**
（其中本提案新增 2 个测试方法，另在既有 4 个测试中补充断言）；Release 构建成功（0 错误）。
（既有 `TagNormalizerTests` 的 xUnit1025 重复 InlineData 警告来自其它改动，与本提案无关。）

### 3.2 构建与全量测试

- Debug：`scripts/test.ps1`（全量构建 + xUnit v3 进程内测试）；
- Release：`scripts/build.ps1 -Configuration Release`。

### 3.3 手工验收建议（不修改验收目录）

1. 60 Hz 显示器：`--visualizer <dummy 音频> <png> 3`，分别选“不限制 / 60 / 30”，
   日志 `fps` 应约为 60 / 60 / 30，`tickFps` 仍约 60，`renderFrames < ticks` 的比例与档位一致；
2. 144 Hz 显示器（如可用）：选 60 → 日志 `fps` 约 48、`tickFps` 约 144；选 144 → `fps` 约 144；
3. 设置页改档位应立即生效（不需要重启），播放中的画面卡顿感随之变化；重启后档位保留；
4. “恢复默认参数”后回到“不限制”，帧率恢复与 ADR-023 §9 基线一致；
5. 最小化 / 被其它窗口完全遮挡时不再出帧（既有行为），`renderFrames` 停止增长；
6. 低档位（30）下暂停播放：柱体应在约 `(当前电平 − floor) / release` 秒内落到底部，
   与“不限制”时耗时一致（验证 dt 口径），峰值保持时长也一致。

## 4. 风险与取舍

| 风险 | 应对 |
| --- | --- |
| 高刷屏档位取整，实际帧率明显低于所选值（144 Hz 选 60 → 48） | 上限语义 = “天花板”；设置页提示 + 本文档表格说明，避免验收误判 |
| 30 FPS 下瞬态峰值可能被跳过 | 视觉可接受；默认不限制不受影响；后续若要更灵敏可提高 FFT 频率而不改渲染（另立提案） |
| 节流后诊断 `fps` 变小被误读为性能问题 | `tickFps` 仍在，两者之差即节流；建议后续在诊断行加 `frameRateLimit` 字段 |
| 远程桌面 / 显卡驱动刷新率低于设定时，实际帧率低于档位 | 同“取整”语义；上限不是保证值 |
| 错误 / 未知枚举值 | 三层兜底：`Normalize` 回退、`ToFps()` 回退 0、DP 默认 0 |
| 设置页控件禁用 / 枚举扩展遗漏 | ComboBox 以枚举名 Tag 驱动（`ReadEnumTag`），新增档位需同时加枚举与选项（与现有参数一致的维护点） |

**回滚**：删除 `VisualizerFrameRate` / `FrameRateLimit` / `ToFps()` / `FrameRateTolerance` / DP 与绑定 /
设置行与读取代码，以及对应测试即可；`settings.json` 中的 `frameRateLimit` 字段会被忽略（旧代码不认识
未知属性），不需要迁移或清理配置文件。

## 5. 已否决的备选方案

1. **只提供“性能模式”布尔（固定 30 FPS）**：无法满足 60 / 120 / 144 档位需求，否决；
2. **用 `DispatcherTimer` 独立驱动分析 / 绘制**：计时器与合成时钟不同步，帧间抖动、额外延迟，
   且仍需 `InvalidateVisual`，否决；
3. **在分析器内部“每 N 帧才 FFT”**：绘制仍每 tick 发生，省不了绘制开销，还会让诊断口径混乱，否决；
4. **把 dt 固定成 1 / 上限**：破坏 ADR-023 §6.5 的“速度 = dB/s × dt、与帧率无关”，释放与峰值保持变慢，否决；
5. **用 `RenderOptions.ProcessRenderMode` 降级软件渲染**：画面质量受损且不解决持续渲染，否决；
6. **依赖“关闭可视化”（`VisualizerKind.None`）**：已有选项，但用户想要“开着但省资源”，否决。

## 6. 归档时需同步修订的文档

owner 批准后移入 `docs/adr/` 并更新 `docs/adr/ADR-index.md`，同时修订 ADR-023：

- §0 决策摘要：增加“渲染帧率上限（30/60/120/144/180/不限制，默认不限制）”；
- §5 数据模型：`VisualizationSettings` 树增加 `FrameRateLimit` 与枚举；
- §6.1 流水线：渲染帧入口增加“帧率上限节流（整帧跳过、dt 保持真实）”；
- §6.5 弹道：注明节流不改变 dt 口径与帧率无关性；
- §9 性能实测：补充节流档位的实测帧率（至少 60 / 30 两档）；
- §11 设置页：新增“帧率上限”下拉；
- §12 诊断：记录 `tickFps > fps` 即节流生效；若后续追加 `frameRateLimit` 字段在此登记；
- §15 风险与取舍：高刷屏取整表（本提案 §2.5）；
- §17 修订记录：新增 2026-11-16 条目（ADR-023-A1）。

## 7. 测试与实现记录

- Core：`VisualizationSettings.VisualizerFrameRate` / `FrameRateLimit` / `ToFps()` / `Normalize` / `Clone`；
- App：`SpectrumView.FrameRateLimit` DP + `OnRendering` 节流 + `ResetFrameTiming()`；
  `MainViewModel.VisualizerFrameRateLimit` + `RefreshVisualizer` 通知；`MainWindow.xaml` 绑定；
  `SettingsWindow` 新增“帧率上限”行与读写；`SettingsWindow.ApplyVisualizer` 复用
  `Normalize → Populate → RefreshVisualizer → SaveSettings` 链路；
- 测试：`VisualizationSettingsTests` 新增默认值 / 未知枚举 / `ToFps` / `Clone` / JSON 往返 / 坏 JSON 修复断言；
- 验证：`scripts/test.ps1`（Debug 全量构建 + 584 项测试通过、0 失败）、
  `scripts/build.ps1 -Configuration Release`（成功，0 错误）；WPF 节流的手工验收步骤见 §3.3。
