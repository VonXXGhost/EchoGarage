# KB-019：响度测量算法（LUFS / LRA / True Peak）与实现笔记

- 状态：Accepted（owner 批准：2026-11-19）
- 日期：2026-11-18
- 关联：ADR-028（响度计算与响度调整）、ADR-017（音频转换流水线）
- 调研来源：ITU-R BS.1770-5（2023-11）、EBU R 128 / Tech 3341 / Tech 3342（2023-11）、libebur128 / ffmpeg / Audacity / foobar2000 / Audition / REAPER 公开资料；本机 ffmpeg（N-110436-geaf15b5882，2023-05-01）与 Python/numpy/scipy 原型实测。

> 本文是可直接照抄实现的算法笔记。示例歌名统一使用 `Demo Track 01` 等 dummy 值。

## 1. 规范与单位

- **LUFS ≡ LKFS**；1 LU = 1 dB；显示保留 1 位小数（EBU Tech 3341 §2.8）。
- 参考：BS.1770-5 Annex 1（响度）、Annex 2（真峰值）、Annex 3（高级声道）；EBU R 128（-23 LUFS、生产真峰上限 -1 dBTP）；Tech 3341（M/S/I/LRA/TP 计量与容差）；Tech 3342（LRA 定义与 MATLAB 参考实现）；Tech 3343（生产实践）。
- 播放器/本地库常自定义目标（-14/-16/-18 LUFS）；标准 target 不是强制项。

## 2. K 加权

### 2.1 48 kHz 官方系数（BS.1770-5 Table 1/2）

`H(z) = (b0 + b1·z⁻¹ + b2·z⁻²) / (1 + a1·z⁻¹ + a2·z⁻²)`

第一级 pre-filter（模拟头部声学效应）：

```
b0 =  1.53512485958697
b1 = -2.69169618940638
b2 =  1.19839281085285
a1 = -1.69065929318241
a2 =  0.73248077421585
```

第二级 RLB 高通：

```
b0 =  1.0
b1 = -2.0
b2 =  1.0
a1 = -1.99004745483398
a2 =  0.99007225036621
```

### 2.2 任意采样率设计公式（libebur128 / ffmpeg 使用）

第一级：

```
f0 = 1681.974450955533
G  = 3.999843853973347      // dB 高架增益
Q  = 0.7071752369554196
K  = tan(pi * f0 / fs)
Vh = 10^(G/20)
Vb = Vh^0.4996667741545416
a0 = 1 + K/Q + K*K
b0 = (Vh + Vb*K/Q + K*K) / a0
b1 = 2*(K*K - Vh) / a0
b2 = (Vh - Vb*K/Q + K*K) / a0
a1 = 2*(K*K - 1) / a0
a2 = (1 - K/Q + K*K) / a0
```

第二级 RLB：

```
f0 = 38.13547087602444
Q  = 0.5003270373238773
K  = tan(pi * f0 / fs)
d  = 1 + K/Q + K*K
b0 = 1.0; b1 = -2.0; b2 = 1.0
a1 = 2*(K*K - 1) / d
a2 = (1 - K/Q + K*K) / d
```

验证：`fs=48000` 代入与官方表 15 位小数一致；44.1/48/88.2/96/192 kHz 通用。997 Hz 正弦 K 加权增益 +0.6910 dB，正好被 `-0.691` 常数抵消（BS.1770-5 NOTE 1）。实现时 double、每声道独立状态、换文件必须 reset；连续静音时把过小状态清零以避免 denormal。

## 3. 积分响度（Integrated, I）

### 3.1 公式

```
z_i(T) = (1/T)·∫_T y_i(t)² dt                    // 每声道均方，y = K 加权信号
L_K = -0.691 + 10·log10( Σ_i G_i · z_i )  [LUFS]
```

### 3.2 分块

- 块长 400 ms（样本数取整），步进 100 ms（75% 重叠）；末尾不完整块丢弃；不补静音。
- 与 libebur128 对齐：`hop = (fs + 5) / 10`，`block = 4 * hop`（44.1k → 4410/17640；48k → 4800/19200；96k → 9600/38400）。
- 块数 `n = (N - block)/hop + 1`；`N < block` 时不可测。

### 3.3 两遍门限（严格顺序）

```
Γa = -70 LUFS
Jg1 = { j : l_j > Γa }                       // 严格 >
若 Jg1 为空 → 返回「不可测」
Γr = -0.691 + 10·log10( Σ_i G_i · mean_{j∈Jg1} z_ij ) - 10
Jg  = { j : l_j > Γr 且 l_j > Γa }
I   = -0.691 + 10·log10( Σ_i G_i · mean_{j∈Jg} z_ij )
```

要点：先在**能量域**对通过绝对门限的块每声道求平均 z，再算相对门限；相对门限是「绝对门限后响度 -10 LU」；门限比较用含 -0.691 的响度值；libebur128/ffmpeg 在相对门限聚合时用 `>=`，与严格 `>` 差异 <0.01 LU。

### 3.4 声道权重（BS.1770-5 Annex 1 Table 3）

- L / R / C：**1.0**
- Ls / Rs / Back / Side / Surround：**1.41**（乘在**能量**上，≈+1.5 dB）
- LFE：**0（不计入）**
- 高级声道（Annex 3）：|仰角|<30° 时 |方位| 60°–120° → 1.41，其余 1.0；其它仰角 → 1.0。
- Dual mono：ITU 未定义；libebur128 的 DUAL_MONO 模式把该声道权重 ×2.0（+3.01 LU）；**播放器默认不启用**，按文件真实声道数测。
- 常见 PCM 回退（无 ChannelMask 时）：4ch=L,R,Ls,Rs；5ch=L,R,C,Ls,Rs；6ch=L,R,C,LFE,Ls,Rs；7.1 的后/侧环绕全 1.41；LFE 恒 0。

## 4. Momentary / Short-term（M / S，v1 可选）

| 量 | 窗口 | 门限 | 更新率 |
| --- | --- | --- | --- |
| M | 0.4 s 滑动矩形窗 | 不门限 | 100 ms |
| S | 3 s 滑动矩形窗 | 不门限 | 100 ms |

- M 值就是 400 ms 块的响度 `l_j`，可直接复用积分响度分块结果。
- EBU Mode 的 M 是 0.4 s 矩形窗，**不是** BS.1771 的一阶 IIR M（两者可差 2 LU）。
- 文件 <400 ms 无 I/M；<3 s 无 S，但 I 仍可测（至少 1 块）。

## 5. Loudness Range（LRA，EBU Tech 3342）

1. 取 S 序列：3 s 矩形窗、K 加权、通道加权、不门限；按 100 ms 步进取一个 S 值（首个在 t=3 s）。EBU Tech 3342（2023）要求相邻窗重叠 ≥2.9 s，即步进 ≤100 ms；本实现取 100 ms（与 3342 附录 MATLAB 参考一致）。注意 libebur128 与 ffmpeg 的 `ebur128.c` 历史实现实际是**每 1 s 取一个短时值**（首个 3 s，之后每秒；相邻窗只重叠 2 s，不满足 2023 版最低要求），对本机样本 LRA 差异实测 <0.2 LU（100 ms 步进 10.12、1 s 步进 10.10），不影响 3341 容差；本项目按 3342 原文与流式友好度取 100 ms，并在测试中允许 ±0.5 LU 对比容差。
2. 绝对门限：保留 `S_j >= -70 LUFS`。
3. 相对门限：`L_abs = 10·log10( mean(10^(S_j/10)) )`（S 已含偏移，不再减 -0.691），`Γr = L_abs - 20`，保留 `S_j >= Γr`。
4. 百分位（最近秩、无插值）：
   `low = sorted[floor((n-1)·0.10 + 0.5)]`，`high = sorted[floor((n-1)·0.95 + 0.5)]`，`LRA = high - low`。
   （3342 MATLAB 用 1 基 `round((n-1)·P/100 + 1)`，等价。）
5. 边界：n=0 → 「不可测」；<60 s 的节目 LRA 不稳定，UI 可提示；文件型测量若与 EBU 官方工具对齐，注意 3342 建议尾部补 ≥1.5 s 静音（ffmpeg/libebur128 不补；本项目按 ffmpeg 口径不补）。

## 6. True Peak（dBTP，BS.1770-5 Annex 2）

- 定义：连续时间波形最大幅度，可能高于采样峰值；`dBTP = 20·log10(max|y|)`，0 dBTP = 满刻度。
- ITU 流程：衰减 12.04 dB（整型 2 bit；**浮点实现可省**）→ 4× 过采样 → 低通插值 → 取绝对值最大 → +12.04 dB 补偿。
- 过采样后采样率应 ≥192 kHz；48 kHz 输入必须 4×；96 kHz 可 2×；≥192 kHz 可直接 sample peak。**本项目统一 4×**，代码单分支，精度更好。
- 多声道每声道独立取最大；**LFE 参与 TP**（与响度不同）。

### 6.1 ITU 4× 48-tap 系数（BS.1770-5 Annex 2 Table 6，逐字核对）

每相 12 个，共 4 相；`y_p[n] = Σ_{t=0..11} h_p[t]·x[n-t]`，四相输出都算，取 `max(|y|)`。

```
Phase 0:
 0.0017089843750,  0.0109863281250, -0.0196533203125,  0.0332031250000,
-0.0594482421875,  0.1373291015625,  0.9721679687500, -0.1022949218750,
 0.0476074218750, -0.0266113281250,  0.0148925781250, -0.0083007812500

Phase 1:
-0.0291748046875,  0.0292968750000, -0.0517578125000,  0.0891113281250,
-0.1665039062500,  0.4650878906250,  0.7797851562500, -0.2003173828125,
 0.1015625000000, -0.0582275390625,  0.0330810546875, -0.0189208984375

Phase 2:
-0.0189208984375,  0.0330810546875, -0.0582275390625,  0.1015625000000,
-0.2003173828125,  0.7797851562500,  0.4650878906250, -0.1665039062500,
 0.0891113281250, -0.0517578125000,  0.0292968750000, -0.0291748046875

Phase 3:
-0.0083007812500,  0.0148925781250, -0.0266113281250,  0.0476074218750,
-0.1022949218750,  0.9721679687500,  0.1373291015625, -0.0594482421875,
 0.0332031250000, -0.0196533203125,  0.0109863281250,  0.0017089843750
```

- 对称关系：Phase3 = reverse(Phase0)，Phase2 = reverse(Phase1)；各相 DC 和 = 1.001587 / 0.973022 / 0.973022 / 1.001587；48 系数总和 = 1.0。
- 因果实现延迟 11 个输入样本；只测最大值时延迟无影响。
- **4× 理论最大欠读** 0.688 dB（fnom=0.5）；16× 为 0.042 dB。实测 ITU 表在 12 kHz 扫相位偏差 -0.197 ~ +0.204 dB，满足 EBU 3341 TP 容差（+0.2/-0.4 dB）。
- libebur128 用 49-tap Hann 窗 sinc 多相（<96k 4×；96k–192k 2×；≥192k 不过采样），与 ITU 表接近但不同。**本项目直接用 ITU 官方表**，可审计。
- ffmpeg 的 TP = swresample 升到 192 kHz 后取峰值，是近似，可做 sanity check，不应当作黄金参考（突变信号振铃会抬高读数）。

## 7. 本机验证结果（原型）

Python 原型（numpy/scipy，`.scratch/` 内，不提交）与 ffmpeg ebur128 对照：

| 项目 | 原型/自研 | ffmpeg | 差值 |
| --- | --- | --- | --- |
| 44.1 kHz 立体声样本积分响度 I | -6.421 LUFS | -6.4 LUFS | 0.02 LU |
| 同文件 LRA | 10.12 LU（100 ms 步进） | 10.2 LU | 0.08 LU |
| 同文件 True Peak | 0.59 dBTP（ITU 表） | 0.6 dBTP | 0.01 dB |
| 44.1 kHz 立体声 1 kHz -23 dBFS 20 s | — | -23.0 LUFS | 标准值一致 |
| 5.1 权重测试（L/R -28、C -24、Ls/Rs -30、LFE 静音） | — | -23.0 LUFS | 标准值一致 |
| 20s@-20 + 20s@-30 | — | 10.0 LU | 与 3342 参考一致 |
| 0.5 幅度 fs/4 相位 45° 正弦 | -5.98 dBTP | -6.0 dBTP | 0.02 dB |

- 与 ffmpeg 对比容差：I ±0.1 LU；LRA ±0.1~0.5 LU（步进/百分位差异）；TP +0.2/-0.4 dB（EBU 3341）。
- 注意：TP 必须对**原始 PCM** 计算，不能对 K 加权后的信号算（原型曾犯此错，读数虚高约 7 dB）。

## 8. 性能与流式实现

- 复杂度：K 加权 2 biquad ≈ 10 MAC/样本/声道；每 100 ms 只需把 hop 内平方和滚成 400 ms/3 s 和；TP 约 48 MAC/样本/声道。
- 内存：I 需每块每声道能量（5 声道 × 10 块/s ≈ 400 doubles/s，1 小时 ~11.5 MB；10 分钟 5 声道 ~2 MB）；LRA 只存 S 序列（10 doubles/s）。
- 速度估算：C# 标量 double，仅 I >200× 实时；加 TP 约 60~180× 实时；实际瓶颈通常是解码。**不做结果缓存时，扫描时长 ≈ 解码时间 + TP 时间**。
- 流式设计：解码 float32 → 每帧 K 滤波/平方累加 + 原始样本进 TP 12-tap 历史；hop 满后更新滚动块与 S；文件结束做两遍门限/百分位。不需要整曲 PCM。
- 解码 float32 后不要先量化回 16-bit 再测；K 滤波与累加用 double。

## 9. 业界工具对照与增益口径

| 工具 | 关键行为 |
| --- | --- |
| foobar2000 True Peak Scanner（foo_truepeak） | 多选扫描表（Track/Album Peak、Gain、LRA、可选 LUFS-I/M/S、DR、clip 数、峰值时间戳）；可中止/暂停且保留已完成结果；失败行内标注；可写 ReplayGain tag；底部显示耗时/实时倍速 |
| Audacity Loudness Normalization | 默认 -23 LUFS；只乘常数增益，**无最大增益/峰值上限/削波检查**；算法为自研 EBUR128 类（400ms/100ms、-70/-10 门限）；仅降位深时 dither |
| Adobe Audition Match Loudness | Match To 预设 + Maximum True Peak Level，用 **true-peak limiting（有失真）** 达标 |
| REAPER | Render 归一化（Peak/RMS/LUFS-I/True Peak）+ 可选 Brickwall limit；**有损输出按编码后信号重算统计** |
| ffmpeg loudnorm | linear 模式 = 线性缩放，增益被 TP 截断、不限幅；dynamic 模式经 true_peak_limiter（有失真）；内部升采样 192 kHz |
| ReplayGain 2.0（loudgain） | 参考 -18 LUFS；`LUFS_TO_RG(L) = -18 - L`；peak 用 true peak；写 LRA/RANGE；播放器防削波 `min(tag_gain+preamp, 20log10(ceiling/peak))` |

**无失真增益口径**（各工具普遍使用的公式）：

```
max_gain_dB  = ceiling_dBTP - measured_TP_dBTP
target_gain  = target_LUFS - measured_LUFS
applied_gain = (target_gain > 0) ? min(target_gain, max_gain_dB) : target_gain
// 提升受峰值天花板限制；降低直接取目标增益（≤0 dB），不做额外衰减
```

- 天花板：EBU/Spotify/Apple 用 **-1.0 dBTP**；有损输出（MP3/AAC）建议 **-2.0 dBTP**（编码过冲 0.5~2 dB）；纯 PCM 交付有人用 -0.3 dBTP；**不要用 0 dBFS 采样峰**当安全上限。
- 线性增益下 `TP_new = TP_old + gain`（严格成立）；重采样会改变 ±0.1~0.3 dB；**有损编码后必须对实际输出复测或额外留 1 dB**。
- 目标不可达时：不启用限幅，把增益截到 max_gain 并明确提示「目标 X LUFS 不可达，已按 -1 dBTP 上限给到 +Y dB」。

**为什么天花板不是 0 dBFS，max_gain 又来自哪里：**

- `0 dBTP` 是数字满刻度（Full Scale）；dBTP 是相对满刻度的 dB 值，负值表示低于满刻度。`TP = -1.5 dBTP` 等价于峰值幅度 `10^(-1.5/20) ≈ 0.841` 倍满刻度；`TP = -1.0 dBTP` 是 `10^(-1.0/20) ≈ 0.891` 倍满刻度。
- 数字增益是线性乘法：`g` dB 增益 = 幅度 ×`10^(g/20)`；峰值同样被乘，所以在 dB 域里严格成立 `TP_new = TP + g`（例如 -1.5 + 0.5 = -1.0）。因此「最多能加多少 dB」= 天花板 − 当前峰值 = `-1.0 − (-1.5) = +0.5 dB`。
- 天花板取 -1.0 dBTP 不是物理极限（物理极限是 0 dBTP），而是工程安全余量：DAC 重建的模拟波形可能超过采样峰值（intersample peak，采样点都在 0 dBFS 以下时重建峰仍可能超过 0 dBTP）；有损编码器解码后会过冲 0.5~2 dB；真峰值计量本身还有约 0.2~0.7 dB 的估算误差。EBU R128 / Spotify / Apple 因此把交付上限定为 -1 dBTP，MP3/AAC 输出建议 -2 dBTP。
- 若没有天花板限制：把峰值加过 0 dBFS 后，整数输出必须先削波（clipping）才能存下，产生硬削波失真。因此「无失真提升」在数学上就是「把峰值提升到天花板为止」，与目标响度无关；目标响度是否可达，取决于需要的增益是否 ≤ 这个峰值余量。

平台目标参考：Spotify -14 LUFS（母带 TP < -1 dBTP，较响母带建议 -2 dBTP）；Apple Podcasts -16 LKFS ±1、TP ≤ -1 dBFS；EBU R128 -23 LUFS ±0.5、TP ≤ -1 dBTP；ReplayGain 2.0 -18 LUFS。**本项目默认目标为 -10 LUFS，且默认值可在设置页配置（默认 -10.0；不提供预设，用户在调整窗口直接输入），16-bit 输出启用 TPDF dither**。

「无失真可达到」的条件是 `TP − I ≤ ceiling − target`（即峰值-响度比 PLR 不超过「天花板与目标之差」）。取 ceiling=-1、target=-10 时要求 **PLR ≤ 9 dB**：流行/电子等响母带（PLR 常 6–10 dB）多数可达到，动态较大的素材（PLR 12–18 dB）会在 `max_gain` 处被截断——因此 -10 目标对多数动态素材表现为「降低」，提升场景需要接受「达不到目标、只到最大安全值」的提示。

## 10. 输出位深 / dither / 有损输出

- 内部：解码到 float32 → 线性增益 → 目标位深。FLAC 优先保持源位深（16→16，24→24）；MP3/AAC 内部 16-bit；WAV 源位深 >16 用 24-bit。
- 「无损」在本功能中指：FLAC/WAV 不重采样、不引入有损编码、不削波；增益处理本身会改变样本值，且整数输出会重新量化，因此**不是 bit-perfect**（与源文件逐位相同）。
- dither：降到 16-bit 时建议 TPDF（噪声约 -93 dBFS）；24-bit 可省略；32-bit float/int 不需要；MP3/AAC **不要** dither（编码器自己量化），但要为编码过冲留 1.5–2 dB 峰值余量。
- 本项目：FLAC/WAV 输出保持源采样率与位深（不重采样、不有损编码）；**响度调整**的 16-bit 输出启用 TPDF dither（`PcmWavWriter` 可选开关，由 `ConversionRequest.EnableDither` 控制，仅响度调整路径传 true；普通转换菜单不加），24-bit/32f 与有损输出不加。

## 11. 实现易错点清单

1. 滤波器状态：每声道独立；跨 block 连续；换文件 reset；seek 后 reset 并重新计时。
2. `-0.691` 只出现一次；门限比较用含偏移的响度；能量域聚合时不加偏移。
3. 相对门限：先算绝对门限后**平均能量**，再 -10 LU；不是对 dB 求平均。
4. 75% 重叠：步进 100 ms；末尾不完整块丢弃；不以补静音造完整块。
5. 声道权重：1.41 乘能量；LFE 从响度排除（但参与 TP）；dual-mono 默认关。
6. LRA：相对门限 -20 LU；最近秩、不插值；3 s 窗/100 ms 步进；n 太小返回不可测。
7. True Peak：对原始 PCM 算、四相全算、取绝对值、多声道取最大、LFE 参与、浮点不做 ±12.04 dB；输入 ≥96 kHz 可 2×/sample peak，但项目统一 4×。
8. 不要用 BS.1771 的 IIR M 冒充 EBU M。
9. 采样数取整用 `hop=(fs+5)/10`、`block=4*hop`。
10. 静音会触发 biquad denormal，状态过小时清零。
11. 短文件：<400 ms 无 I；<3 s 无 S/LRA；空文件/损坏文件给明确错误。
12. 增益归一化：只做线性增益 + 峰值上限；目标不可达提示，不做限幅。

## 12. 参考实现与文档

- ITU-R BS.1770-5（2023-11）：<https://www.itu.int/dms_pubrec/itu-r/rec/bs/R-REC-BS.1770-5-202311-I!!PDF-E.pdf>
- EBU R 128：<https://tech.ebu.ch/docs/r/r128.pdf>；Tech 3341：<https://tech.ebu.ch/docs/tech/tech3341.pdf>；Tech 3342：<https://tech.ebu.ch/docs/tech/tech3342.pdf>；Tech 3343：<https://tech.ebu.ch/docs/tech/tech3343.pdf>
- libebur128：<https://github.com/jiixyj/libebur128>；ffmpeg `f_ebur128.c`：<https://raw.githubusercontent.com/FFmpeg/FFmpeg/master/libavfilter/f_ebur128.c>；`af_loudnorm.c`：<https://raw.githubusercontent.com/FFmpeg/FFmpeg/master/libavfilter/af_loudnorm.c>
- foobar2000 True Peak Scanner：<https://www.foobar2000.org/components/view/foo_truepeak>
- Audacity 手册 Loudness Normalization：<https://manual.audacityteam.org/man/loudness_normalization.html>；Perceived Loudness 说明：<https://manual.audacityteam.org/man/perceived_loudness_ebu_r_128.html>
- Adobe Audition Match Loudness：<https://helpx.adobe.com/audition/desktop/editing-audio-files/match-loudness.html>
- loudgain（ReplayGain 2.0 实现）：<https://github.com/ghedo/loudgain>
- Spotify Loudness Normalization：<https://support.spotify.com/us/artists/article/loudness-normalization/>
