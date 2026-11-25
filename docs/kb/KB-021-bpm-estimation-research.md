# KB-021：歌曲 BPM 精确测量——业界调研、Beat This! ONNX/DirectML 管线、Tap 测速与标签规范

- 状态：Accepted（2026-11-25，owner 批准）
- 日期：2026-11-25
- 关联：ADR-034（BPM 测量与缓存）、ADR-001（平台与技术栈）、ADR-003（文件即真相）、ADR-015（波形分析与缓存）、ADR-020（播放中文件原子写标签）、ADR-028（响度分析）、KB-001（解码与 TagLib# 笔记）
- 调研来源：Rekordbox / Traktor / Serato / djay Pro / Mixxx / MusicBee / foobar2000（`foo_bpm`、`foo_cnn_bpm`）/ beaTunes / Mixed In Key / Ableton Live 公开资料；MIR 文献（Davies & Plumbley 2007、Böck & Widmer 2013、Percival & Tzanetakis 2014、Zapata et al. 2014、Böck et al. 2015/2020、Schreiber & Müller ISMIR 2018 / TISMIR 2020、Foscarin et al. ISMIR 2024）；Microsoft ONNX Runtime DirectML 文档；TagLibSharp 2.3.0 源码与本仓库 `TagLibTagService` 现状核查。

> 本文是调研记录与可直接照抄实现的算法参考笔记；所有示例曲目统一使用 `Demo Track 01`、`Demo Album A`、`Demo Artist` 等 dummy 值。

---

## 1. 什么是「精确 BPM」：核心难点与评估指标

### 1.1 BPM 测量的四大天然难点

与响度（ITU-R BS.1770）或波形 RMS 不同，BPM（Beats Per Minute）是**音乐感知量（Perceived Tempo / Tactus）**，不存在单一的物理瞬时公式：

1. **倍频 / 半频歧义（Octave / Metrical-Level Error）**：同一段律动在不同听感下常被感知为 70 BPM 或 140 BPM（1/2× / 2×），三拍子/切分下还可能出现 2/3× / 3/2× 分歧。这是所有自动算法最主要的误判来源。
2. **无打击乐与弱瞬态曲目**：电子、流行、摇滚多有清晰底鼓与军鼓；但弦乐、钢琴独奏、原声民谣、环境音与古典乐缺乏尖锐瞬态，传统低频峰值或简单谱通量极易失效。
3. **非恒定速度（Rubato / 真人演奏 / 结构性变速）**：现场鼓手与古典演奏存在自然速度漂移，部分曲目主副歌间还会主动变速，需要稳健提取主速度并识别变速特征。
4. **前奏/尾奏铺底干扰**：大量曲目开头是无节奏人声或长铺底，仅采样前 30 秒极易误判，全曲分析或完整主体分块聚合显著更稳健。

### 1.2 通用评估指标（MIREX）

| 指标 | 定义 | 工程含义 |
| --- | --- | --- |
| **Acc1（Accuracy 1）** | 估计 BPM 落在人工真值 **±4%** 容差内的比例 | **严格准确率**：用户直接拿到就能用、无需手动 `×2`/`÷2` 的可用率 |
| **Acc2（Accuracy 2）** | 落在真值或其 **1/3×、1/2×、2×、3×** 的 ±4% 容差内的比例 | **节拍族准确率**：反映是否抓住了基本周期 |
| **Beat F1 / CMLt / AMLt** | 逐拍点时间戳（±70 ms 容差）的 F1 与连续对齐率 | 拍点级精度；由高精度拍点间隔（IOI）稳健聚合可直接导出高质量全局 BPM |

---

## 2. 市面主流软件的 BPM 实现调研

### 2.1 专业 DJ 软件

| 软件 | 核心算法路线 | 存储与文件级 Tag 策略 | 人工修正与交互 |
| --- | --- | --- | --- |
| **Pioneer DJ Rekordbox** | 闭源引擎；分 Normal（固定，保留 2 位小数）与 Dynamic（可变网格） | 存内部库与 `ANLZ` 文件，**默认不回写物理音频文件 Tag** | 手改、`÷2` / `×2`、Tap Tempo、网格微调 |
| **Native Instruments Traktor Pro** | 闭源瞬态 + 固定 Beatgrid 引擎，支持 BPM 区间预设 | 存内部集合库，**支持可选写回文件 Tag** | 手改、`÷2` / `×2`、Tap、锁定防覆盖 |
| **Serato DJ Pro** | 闭源高速瞬态引擎，偏重固定网格 | 存内部标签/库，支持写入文件 Tag | 手改、`÷2` / `×2`、Tap |
| **Algoriddim djay Pro 5** | **端侧 AI 神经网络（Fluid Beatgrid™）**，可跟踪人手演奏与变速曲目 | 存本地分析库 | 自动/固定切换、倍频修正、手调锚点 |
| **Mixxx（开源，GPL）** | 插件化 `AnalyzerPlugin`；默认 **Queen Mary DSP（`qm-dsp TempoTrackV2`）**，备选 SoundTouch | 默认存内置 **SQLite 库**，可选同步写回文件 Tag | 手改、`÷2` / `×2` / `×2/3` / `×3/2`、Tap Tempo、锁定防重测 |

### 2.2 本地音乐播放器与曲库管理工具

| 软件 | 核心算法路线 | 存储与文件级 Tag 策略 | 人工修正与交互 |
| --- | --- | --- | --- |
| **MusicBee** | 调用 **Un4seen BASS (`bass_fx.dll` `BASS_FX_BPM_DecodeGet`)**，以 **<250 Hz 低频能量周期**为主 | 存自身库并按配置写入文件 Tag | 手改；原声/弱底鼓曲目误差较大 |
| **foobar2000 + `foo_bpm`** | 经典 DSP：下混降采样 → STFT（KISS FFT）→ 谱通量 → 中值峰拾取 → 自相关/梳状滤波 | 无内置缓存；弹出预览表核对后写入文件 `%bpm%` | 强依赖 Min/Max BPM 范围设置；预览窗支持 `÷2` / `×2` |
| **foobar2000 + `foo_cnn_bpm`** | 接入 Hendrik Schreiber 的 **TempoCNN**（通过 TensorFlow C API） | 写入文件级 BPM Tag | 验证了播放器端 CNN 对倍频错误的压制效果，但 TF 运行时偏重 |
| **beaTunes** | 作者即 TempoCNN 提出者；由早期 DSP 演进为 **单步频谱 CNN（TempoCNN）** | 存本地数据库，独立开关控制是否回写音频文件 Tag | 手改、倍频修正、Tap Tempo |
| **Mixed In Key** | 闭源商用引擎（调性 + BPM + 能量） | 本地库 + 可选回写文件 Tag（整数/小数可选） | 批量高精度打标 |

### 2.3 调研结论汇总

1. **算法已全面迈向端侧深度学习**：从低频峰值（SoundTouch / BASS_FX）→ 频谱通量+梳状滤波（`foo_bpm` / Aubio）→ 多特征/动态规划（`qm-dsp` / Essentia / Percival 2014）→ **端侧神经网络（TempoCNN / madmom / Beat This! / djay Pro 5）**，Acc1 出现跨代提升。
2. **许可证筛查决定最终选型**：

   | 开源项目 / 库 | 许可证 | 能否直接用于 EchoGarage |
   | --- | --- | --- |
   | `Essentia` / `TempoCNN` 代码 | **AGPL-3.0** | 不可直接嵌入（强传染性） |
   | `qm-dsp` / `MiniBPM` / `Aubio` | **GPL-2.0+ / GPL-3.0** | 不可直接嵌入（强传染性） |
   | `madmom` 预训练权重 | **含非商用条款** | 不可直接分发（且 DBN 沉重） |
   | `SoundTouch` | LGPL-2.1+ | 仅动态链接合规，但 BPM 精度垫底，不推荐 |
   | **`Beat This!`（ISMIR 2024）代码 + 权重** | **MIT** | **完全合规，SOTA 精度，无 DBN 后处理** |
   | **`Microsoft.ML.OnnxRuntime.DirectML`** | **MIT** | **完全合规，原生支持 Windows DX12 全厂商 GPU（N/A/I）+ CPU 回退** |

3. **公开学术基准参考**：
   - Schreiber & Müller (ISMIR 2018, Table 1) 显示：轻量神经网络（TempoCNN）在跨流派严格准确率 **Acc1** 上显著领先传统方法（Ballroom **92.0%**、GTZAN **91.0%**、GiantSteps **88.7%**、ACM Mirum **79.5%**；综合 Acc2 达 **92.1%–93.6%**）。
   - **Beat This!（Foscarin, Schlüter & Widmer, ISMIR 2024）** 在完全移除 DBN 后处理的前提下刷新多数据集拍点 F1 SOTA（Ballroom 达 **~0.975**），对无鼓、原声、古典与变速曲目表现尤为突出。
4. **产品体验铁律**：本地缓存与文件级 Tag 回写解耦；提供 **手动改值 + `÷2`/`×2` + Tap 轻点测速**，保护用户手动修正不被覆盖。

---

## 3. 选定算法详细规格：`Beat This!` ONNX 端到端管线

本项目采用 **Beat This!（ISMIR 2024，`CPJKU/beat_this`，MIT 许可）** 经 ONNX 导出后在 .NET 中以 **`Microsoft.ML.OnnxRuntime.DirectML`** 推理，并由拍点序列导出高精度全局 BPM（默认展示 1 位小数）。

### 3.1 模型变体与分发策略（内置 `small0` + 可选安装 `final0`）

两者共享**完全相同的输入/输出张量与预处理/后处理代码**：

| 模型变体 | 分发方式 | FP32 ONNX 体积 | 精度定位 | DirectML GPU 耗时（4 min） | 纯 CPU 回退耗时（4 min） | 峰值内存 / 显存（单 30 s 块） |
| --- | --- | --- | --- | --- | --- | --- |
| **`beat_this_small0.onnx`** | **随包内置**（`tools\models\`） | **~10.5 MB** | 紧凑高泛化，日常曲库极稳 | **~0.2–0.5 秒** | **~1.5–3.5 秒** | 约 150–250 MB |
| **`beat_this_final0.onnx`** | **用户按设置页指引可选安装**（优先 `<EchoGarageData>\data\Models\`，兼容 `tools\models\`） | **~80 MB** | 论文主模型 SOTA 极致精度 | **~0.5–1.2 秒** | ~6–14 秒 | 约 350–600 MB |

> 说明：FP32 格式使同一 `.onnx` 文件既能在 DirectML GPU 上高速运行，也能在无 DX12 显卡的机器上无缝回退 CPU EP（避免老 CPU 上 FP16 算子退化慢的问题）。将用户自行安装的 `final0` 推荐放在 `<EchoGarageData>\data\Models\` 可确保后续覆盖更新程序目录时不丢失。

### 3.2 音频解码与 128-bin Log-Mel 预处理（精确对齐官方训练口径）

`Beat This!` 对前端频谱图参数敏感，**必须严格按官方训练规格复现**（可在 `EchoGarage.Core` 中用纯 C# 实现，与 `torchaudio.transforms.MelSpectrogram` 数值对齐，误差 < 1e-4）：

| 参数 | 官方精确值 | 实现要点 |
| --- | --- | --- |
| 采样率 `sample_rate` | **22,050 Hz** | 经 `SharedAudioFileReader` 解码 float32 → 下混单声道 `(L+R)/2` → 重采样至 22,050 Hz（使用 NAudio `WdlResamplingSampleProvider`） |
| FFT 窗长 `n_fft` | **1024** | 周期性 Hann 窗（与 `torch.hann_window(1024, periodic=True)` 一致），中心填充 `center=True`（首尾各 reflect pad 512 样本） |
| 帧移 `hop_length` | **441** | 严格对应 **50 fps**（20 ms / 帧，$22050 / 441 = 50$） |
| 幅度谱 `power` | **1** | 取**幅度** $\sqrt{\text{re}^2 + \text{im}^2}$（**不是**功率谱平方！） |
| 归一化 `normalized` | **`frame_length`** | STFT 幅度谱除以 **`sqrt(n_fft) = 32`**（与 `torchaudio` `normalized='frame_length'` 实测对齐，经 `logmel.onnx` 黄金基准验证平均误差 `~1e-6`） |
| Mel 频带数 `n_mels` | **128** | 频率范围 **`f_min = 30 Hz`** 至 **`f_max = 11,000 Hz`** |
| Mel 标度与面积归一 | **`mel_scale = 'slaney'`** | Slaney 分段线性+对数公式（1000 Hz 以下线性，以上对数）；三角滤波器**不做面积归一化（`norm=None`）**（经 `logmel.onnx` 实测核验） |
| 对数压缩 | **`ln(1 + 1000 · S)`** | 自然对数 `MathF.Log(1f + 1000f * mel)`，即 `.mul(1000).log1p()` |
| 输入张量形状 | **`[1, frames, 128]`**（float32） | 轴 1 为动态时间维 `frames`（50 fps，推理时锁定为 `1500`），轴 2 为 128 个 Mel 频带；输出 `beat_logits` / `downbeat_logits` |

> **交叉验证建议**：实现阶段应以 Python `torchaudio` 在合成测试音频上生成的参考 Log-Mel 矩阵和拍点输出作为黄金基准，确保纯 C# 预处理与官方前端数值一致（类似 `KB-019 §7` 对齐 ffmpeg 的做法）。

### 3.3 固定静态分块策略（兼顾长曲目与 DirectML 性能）

DirectML 面对动态形状会在每次尺寸变化时重新编译 DX12 Compute Shader 并引发显存碎片（见 §4.1），因此推理阶段**必须采用固定静态块长**：

- **固定块长**：`ChunkFrames = 1500`（恰为 **30.0 秒** @ 50 fps，与官方训练/推理窗一致）。
- **重叠与步进**：`BorderFrames = 150`（每侧 3.0 秒边界余量），`StepFrames = ChunkFrames - 2 * BorderFrames = 1200`（24.0 秒有效步进）。
- **拼接规则**：
  - 首块保留 `[0, 1350)`；中间块保留 `[150, 1350)`；末块不足 1500 帧时在尾部以 `0f` 填充至精确 `1500` 帧送入推理，再按真实帧数截取有效区间。
  - 每处理完 1 个 30 秒块汇报一次进度并检查 `CancellationToken`。
- **导出要求**：导出 `.onnx` 时使用 `opset_version >= 14`（社区 `nilsbc/beat-this-onnx` 使用标准导出），输入输出名为 `mel` / `beat_logits` / `downbeat_logits`；在加载 Session 时通过 `AddFreeDimensionOverrideByName("frames", 1500)` 锁定静态维度。

### 3.4 无 DBN 拍点拾取与全局 BPM 稳健聚合（Core 纯逻辑）

模型输出逐帧 logits（`beat[t]`、`downbeat[t]`，50 fps = 20 ms 步长），后处理完全在 `EchoGarage.Core` 用纯 C# 完成：

1. **局部极大值峰值拾取（对齐官方 `MinimalPostprocessor`）**：
   - 取有效帧 logits，寻找满足 `beat[t] > 0`（等价 `sigmoid(beat[t]) > 0.5`）且在局部窗口 `[t - 3, t + 3]`（7 帧 = 140 ms）内为最大值的帧索引；
   - 对相邻极大值（间距 ≤ 3 帧或平台）合并为均值位置，并用二次抛物线插值（或 3 点加权质心）把拍点时间戳精修到亚帧级（< 5 ms），得拍点序列 $t_1, t_2, \dots, t_M$（秒）。
2. **拍间隔（IOI）过滤与瞬时 BPM 计算**：
   - 计算相邻拍间隔 $\Delta t_i = t_{i+1} - t_i$；
   - 过滤明显非正常节拍间隔（$\Delta t_i < 0.20\text{ s}$ 即 >300 BPM，或 $\Delta t_i > 2.0\text{ s}$ 即 <30 BPM / 段落休止符）；
   - 有效间隔数 < 4 时判定无明显节拍（返回不可测提示）。
   - 换算瞬时速度 $b_i = 60.0 / \Delta t_i$。
3. **核密度众数 + 10% 截尾均值（消除 20 ms 量化阶梯并抗离群值）**：
   - 在 $[30, 300]$ BPM 区间以 0.1 BPM 网格构造高斯核密度估计（带宽 $\sigma = 0.8$ BPM），定位全局主峰 $b_{\text{mode}}$；
   - 选取落在主峰邻域 $|b_i - b_{\text{mode}}| \le \max(2.5, 0.03 \cdot b_{\text{mode}})$ 内的全部亚帧拍间隔，取其 **10% 截尾均值 $\overline{\Delta t}_{\text{trim}}$** 换算主 BPM：
     $$\text{BPM} = \frac{60.0}{\overline{\Delta t}_{\text{trim}}}$$
     （实测单纯取中位数会受 20 ms 帧量化阶梯影响产生 ~2–5 BPM 偏移，采用亚帧抛物线插值 + 10% 截尾均值后误差降至 `0.01–0.16 BPM`）；默认展示四舍五入至 **1 位小数**（如 `128.0`、`127.5`）。
4. **置信度、变速检测与候选输出**：
   - 综合主峰内间隔占比（`InlierRatio`）与拍点平均强度给出置信度 `[0, 1]`；
   - 有效 $b_i$ 的四分位距 `IQR > 3.0 BPM` 或 `InlierRatio < 0.65` 时标记 `IsVariableTempoSuspected = true`；
   - 自动附带合法范围内的 `BPM / 2`、`BPM * 2`（以及显著次峰）作为候选参考。

---

## 4. Windows GPU 加速（DirectML）与「非 BPM 零性能影响」工程守则

### 4.1 为什么选 DirectML 而不是 CUDA

| 方案 | 支持显卡 | 外部环境要求 | 体积与部署 |
| --- | --- | --- | --- |
| CUDA EP | 仅 NVIDIA | 需用户安装匹配版本 CUDA / cuDNN（数 GB） | 不适合便携桌面播放器 |
| **DirectML EP (`Microsoft.ML.OnnxRuntime.DirectML`)** | **NVIDIA / AMD / Intel（核显与独显）全覆盖** | **零额外安装**（Windows 10/11 原生 DirectX 12） | 单一 MIT NuGet，直接随程序便携部署 |

### 4.2 DirectML 必备 `SessionOptions` 与安全回退

```csharp
var options = new SessionOptions
{
    ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,       // DirectML 强制要求顺序执行
    EnableMemoryPattern = false,                        // DirectML 不支持 MemoryPattern 优化
    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
    LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING
};
// 锁定静态时间维，避免 DirectML 在每块不同长度时重编译 DX12 Compute Shader
options.AddFreeDimensionOverrideByName("time", 1500);

// 优先挂 DirectML (GPU 0)，若初始化失败自动降级纯 CPU EP
try
{
    options.AppendExecutionProvider_DML(deviceId: 0);
}
catch (Exception ex)
{
    logger.Warn($"DirectML GPU 不可用，已回退至 CPU 推理：{ex.Message}");
}
options.AppendExecutionProvider_CPU();
```

### 4.3 守住「非 BPM 期间零性能影响」的四道防线

1. **启动期零接触**：`AppHost.InitializeAsync` 与 `StartWarmup()` **完全不加载** `onnxruntime.dll`、`DirectML.dll` 或 `.onnx` 模型文件；主窗口启动耗时增量严格为 **0 ms**。
2. **首次按需懒加载**：只有用户真正点击「BPM 测量」时，才在后台线程首次初始化 `InferenceSession`；从未点过该按钮时内存与显存占用增量严格为 **0 MB**。
3. **推理中礼让播放与 UI 渲染**：解码与预处理在 `BelowNormal` 后台线程执行，每块结束 `Task.Yield()`，绝不干扰 WASAPI 播放与 60–180 FPS 频谱可视化（ADR-023）。
4. **空闲自动卸载释放显存与内存（Idle Unload）**：推理完成后启动 **60 秒空闲回收计时器**（设置可选立即释放），超时自动 `session.Dispose()` 释放 DirectML / DX12 显存堆与原生权重；`AppHost.Shutdown()` 同步释放。若特定显卡驱动仍保留显著页缓存，可借助 `IBpmAnalyzer` 接口无缝切至短命子进程 runner 彻底回收。

---

## 5. Tap 轻点测速算法（Core 纯逻辑 `TapTempoCalculator`）

### 5.1 为什么不能用「相邻间隔算术平均」

相邻间隔算术平均 $\frac{1}{n-1}\sum_{k=1}^{n-1}(t_k - t_{k-1}) = \frac{t_{n-1}-t_0}{n-1}$，中间 $n-2$ 次敲击完全抵消，仅首尾两次手部抖动决定结果。

### 5.2 推荐算法：普通最小二乘线性回归（OLS）+ 滚动窗口

将敲击序列建模为 $t_k = \alpha + k \cdot T + \varepsilon_k$，用 OLS 求解拍周期 $T$：

$$\bar{k} = \frac{n-1}{2}, \qquad \bar{t} = \frac{1}{n}\sum_{k=0}^{n-1} t_k, \qquad T = \frac{\sum_{k=0}^{n-1}(k - \bar{k})(t_k - \bar{t})}{\sum_{k=0}^{n-1}(k - \bar{k})^2}, \qquad \text{BPM} = \frac{60.0}{T}$$

- **关键边界参数**：
  1. 使用高精度单调时钟 `Stopwatch.GetTimestamp()`；
  2. 相邻间隔 > **2.5 秒**（< 24 BPM）自动超时重置，开启新一轮；
  3. 相邻间隔 < **180 ms**（> 333 BPM）静默防抖忽略；
  4. 滚动保留最近 **16 次**敲击；
  5. 若单次间隔相对中位周期偏离 > **±35%**（如漏拍），自动重置或标示不稳定；
  6. 第 2 次敲击起实时回填 **1 位小数** BPM，并显示「已敲击 N 次」。

---

## 6. 跨格式文件级 BPM Tag 规范与 TagLib# 2.3.0 实测踩坑点

### 6.1 各音频格式标准 BPM 标签与 1 位小数写入口径

| 音频格式 | 标签标准 | 标准字段 / 帧 / Atom | 协议数据类型 | 1 位小数写入口径（`0.#` 规则） |
| --- | --- | --- | --- | --- |
| **FLAC / OGG / Opus** | Vorbis Comment (Xiph) | **`BPM`** | UTF-8 自由文本 | 保留 1 位小数（如 `128.5` 写 `"128.5"`；整拍 `128.0` 写 `"128"`） |
| **MP3 / WAV / AIFF** | ID3v2.3 / 2.4 | **`TBPM`**（文本帧） | 文本字符串 | 写 `0.#`（如 `"128.5"` / `"128"`）；兼容主流现代播放器（设置提供可选“ID3 兼容取整”） |
| **M4A / MP4 / M4B** | Apple / iTunes Atom | **`tmpo`** | **16-bit 无符号大端整数**（`FlagType.ForTempo = 21`） | **协议硬限制不支持小数！** 写入时四舍五入取整，界面动态提示 |
| **WMA** | ASF | **`WM/BeatsPerMinute`** | 字符串 / DWORD 描述符 | 写 `0.#`（或整数） |
| **原始 ADTS `.aac`** | 无标准标签块 | 不支持写入 | — | `file.Writeable == false`，明确提示仅保存至本地缓存（KB-001 §4） |

### 6.2 本仓库 `TagLibTagService.cs` 现状的 3 个关键坑点（实查源码）

1. **`file.Tag.BeatsPerMinute` 为 `uint` 会截断小数**：读取 MP3（`TBPM`）与 FLAC（`BPM`）须直接按具体容器帧读原始文本并 `double.TryParse`（M4A 的 `tmpo` 走 `apple.BeatsPerMinute` 读整数）。
2. **M4A `tmpo` 在当前 `CollectAppleBoxes` 中读不出来且不能用 `SetText` 写**：`TagLibTagService.cs` L756–760 用 `data.Text` 过滤 Apple atom，但二进制 `tmpo` 的 `data.Text` 为空；写入也必须通过 `apple.BeatsPerMinute = (uint)rounded` 写入正确的 `ForTempo` 二进制数据盒。
3. **「标准字段」与「其他标签」去重**：将 `double? Bpm` 纳入 `AudioTagData` 后，须同步把 `TBPM`、`BPM`、`tmpo`、`WM/BeatsPerMinute` 加入 `Known*` 排除集合，避免重复展示与写回冲突。

---

## 7. 参考资料

- Francesco Foscarin, Jan Schlüter & Gerhard Widmer, *"Beat This! Accurate Beat Tracking Without DBN Postprocessing"*, ISMIR 2024（`CPJKU/beat_this`，MIT License）：<https://github.com/CPJKU/beat_this>
- `Beat This!` 官方预处理规格与预训练权重发布（Zenodo / HuggingFace）：`torchaudio.transforms.MelSpectrogram(22050, n_fft=1024, hop_length=441, f_min=30, f_max=11000, n_mels=128, mel_scale="slaney", normalized="frame_length", power=1).mul(1000).log1p()`
- Microsoft ONNX Runtime DirectML Execution Provider 文档：<https://onnxruntime.ai/docs/execution-providers/DirectML-ExecutionProvider.html>
- Hendrik Schreiber & Meinard Müller, *"A Single-Step Approach to Musical Tempo Estimation Using a Convolutional Neural Network"* (TempoCNN), ISMIR 2018；*"Music Tempo Estimation: Are We Done Yet?"*, TISMIR 2020.
- Graham Percival & George Tzanetakis, *"Streamlined Tempo Estimation Based on Autocorrelation and Cross-correlation With Pulses"*, IEEE/ACM TASLP, 2014.
- Mixxx BPM & Beatgrid：<https://github.com/mixxxdj/mixxx>；foobar2000 `foo_bpm`：<https://github.com/stengerh/foo_bpm>；TagLibSharp：<https://github.com/mono/taglib-sharp>
