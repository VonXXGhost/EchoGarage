# ADR-017：音频转换（编码转换）

- 状态：已批准（Accepted，2026-11-14，owner）；已实现（2026-11-14）；2026-11-19 由 ADR-028 修订（见下）
- 日期：2026-11-14
- 相关：ADR-013（列表行次要信息）、ADR-014（schema v3 缓存修复，已批准）、ADR-016（搜索 / 快捷键 / 播放模式）、
  ADR-028（响度分析与调整，修订本 ADR 的转换请求模型）

> **2026-11-19 修订（ADR-028，owner 已批准）**：本 ADR 的 `ConversionRequest` 已扩展两个可选参数
> `GainDb`（默认 0；线性增益 dB，仅非 0 时经 `GainSampleProvider` 包装，保证旧调用逐位不变）与
> `EnableDither`（默认 false；16-bit 输出启用 TPDF dither），用于 ADR-028 的响度调整输出。
> 另新增 SSRC 重采样前导延迟补偿：`SsrcLatencyProbe` 运行时探测延迟、`PcmWavWriter.TrimToFrames(path,
> maxFrames, skipFrames)` 裁剪，WDL 回退路径不裁剪（见 ADR-028 §3.6.2）。
> 本文 §2 的请求模型与 §8 的转换链路按 2026-11-14 文本保留，未逐一回填上述字段；字段与语义以 ADR-028 为准。
> 格式 / 预设 / 外部编码器等决策不变。

## 背景

owner 提出新的功能迭代：

> 参考 foobar2000 的功能，新增歌曲文件编码转换功能。需要存在一个转换器设置页面，记录常用的转换逻辑，
> 转换逻辑包括音频格式、输出位置和文件名模板、处理器（现阶段只需要高质量的重采样）、tag 复制等可设置项目。
> 只需要 AAC、FLAC、MP3、WAV 四种转换，AAC 使用 Nero AAC 编码。在播放列表中选中一个或多个音频文件后，
> 可以使用右键或快捷键（可配置）调用特定的转换器规则进行转换。

参考界面（owner 提供的 foobar2000 截图）：

1. 右键“转换”子菜单：`DSD 转换` / `快捷转换` / `[最后使用的操作]` / 按格式列出的预设（如
   `AAC(VBR Q1.00) 到同目录`、`MP3 (320) 到…`、`WAV 单文件+CUE到…`）。
2. “转换器设置”窗口：左侧“已保存的预设”+ 载入/保存，右侧“当前设置”分
   **输出格式 / 目标（输出位置、文件名模板）/ 处理（重采样）/ 其他（拷贝标签和内嵌图像）**。
3. “命令行编码器配置 - 编辑预设”窗口：编码器（AAC (Nero)）、模式（目标质量 VBR）、
   质量滑杆（最小文件 ↔ 最佳品质，显示约 400 kbps / Q1.00）。

目标：在现有架构内实现一个**预设驱动**的转换器，覆盖 AAC / FLAC / MP3 / WAV 四种输出，
右键菜单或可配置快捷键触发，带进度与取消，并尽量接近 foobar2000 的操作习惯。

## 调研

### foobar2000 Converter（成熟方案）

- Converter 是 foobar2000 安装包内置组件；源格式为播放器可解码的任意格式。
- 输出格式：WAV 原生；FLAC 调用 `flac.exe`、MP3 调用 `LAME.exe`、AAC/MP4 调用 Nero AAC 编码器
  （均为**外部命令行编码器**，程序不内置这些编码器）。
- 转换器设置分页：输出格式、目标（输出文件夹 + 文件名模板，支持 `%tag%` 与 `/` 子目录，
  命令区分“转换到…”与“转换到同目录”）、处理（ReplayGain、DSP、重采样）、其他（预览、转换后动作）、标签。
- 文件名模板使用 Title Formatting 语法；转换前可预览；预设可保存/载入。
- 右键“转换”子菜单直接列出已保存预设，并保留“最后使用的操作”“快捷转换”等入口。

结论：外部命令行编码器 + 预设 + 模板 + 可选处理/标签，是成熟且与本项目许可策略兼容的方案。

### 外部编码器

| 格式 | 工具 | 许可 / 分发 | 常用参数 |
| --- | --- | --- | --- |
| AAC (MP4) | `neroAacEnc.exe`（Nero AAC Codec） | **Nero EULA：仅个人非商业/评估使用，明确禁止再分发** | `-q 0.0–1.0`（VBR 质量）、`-br <bps>`（ABR）、`-cbr <bps>`（CBR）、`-if in.wav`、`-of out.m4a` |
| FLAC | `flac.exe` | libFLAC 为 BSD 类；命令行工具 GPL/LGPL（分发需附许可与源码获取方式） | `-0..-8` 压缩级别、`-o out.flac`、`--tag`、`--picture`；输入 WAV |
| MP3 | `lame.exe` | LGPL | `-V 0..9`（VBR 质量，V0 最好）、`-b <kbps>`（CBR）、`--silent`；输入 WAV |
| WAV | 无（NAudio 直接写 PCM） | — | 16 / 24-bit PCM |

- Nero AAC 输出为 MP4 容器（建议扩展名 `.m4a`），编码器要求输入 PCM WAV；质量 Q1.00 约 400 kbps、
  Q0.50 约 175 kbps（44.1 kHz 立体声，官方说明仅对 CD 有效，界面按“约”展示）。
- 分发策略（owner 已确认）：**FLAC / LAME 随发布包附带**（放在程序目录 `tools\`，附官方许可文件与源码获取说明），
  **Nero AAC 由用户自备**（Nero EULA 明确禁止再分发），设置中可指定路径。
- 这与 foobar2000 一致：foobar 同样要求用户自行下载 Nero AAC 编码器（其安装包只提供下载引导）。

### 解码与重采样

- Windows 10+ 的 Media Foundation 可解码：WAV、MP3、AAC/M4A、FLAC、WMA；不支持 ALAC、APE、WavPack 等
  （遇到时给出明确错误，而不是静默失败）。
- 仓库已依赖 NAudio 2.2.1（`src/EchoGarage.Infrastructure`），现有播放器 `WasapiAudioPlayer` 已使用
  `AudioFileReader` 解码，说明解码链路可用。
- 重采样（owner 指定参考 foobar2000 的 dBpoweramp/SSRC）：foobar2000 内置的 “Resampler (dBpoweramp/SSRC)”
  是作者基于 dBpoweramp/SSRC 代码实现的闭源组件，不能直接复用；但其开源前身 **SSRC（Shibatch Sample Rate Converter）**
  仍活跃维护（GitHub `shibatch/SSRC`，v2.4.0，**Boost Software License 1.0**，提供 Windows 预编译二进制），
  采用 FFT + 长 FIR、支持 `--profile` 质量档位，正是同源的高质量方案。本项目把它作为外部重采样工具随包附带。
- 解码仍用 32-bit float 内部管线（`MediaFoundationReader` / `WaveFileReader`）；需要重采样时先写临时 PCM WAV
  （16/24-bit），调用 `ssrc.exe --rate <target> --bits <16|24> --profile <standard|high|insane>` 得到目标采样率 WAV，
  再进入编码器；`ssrc.exe` 缺失或失败时回落到 NAudio `WdlResamplingSampleProvider` 并记录 WARN。
- 不新增 NuGet 依赖；不引入 ffmpeg 等大型外部依赖。

### 仓库现状（可复用点）

- 分层清晰：Core（纯逻辑、可单测）→ Infrastructure（NAudio/TagLib/进程）→ App（WPF）。
- `ITagService.Read/Write` 已支持 mp3/flac/m4a/wav/aiff/wma 的标签与内嵌封面读写，可直接用于转换后复制标签。
- `AppPaths` 提供便携数据目录（`EchoGarageData`，可被 `ECHOGARAGE_DATA_DIR` 重定向），可用于临时文件目录。
- `HotkeyService` 已实现局部/全局快捷键、`RegisterHotKey`、`WM_HOTKEY`、冲突时 WARN 跳过，可扩展“运行转换预设”。
- `DialogService` / `Diagnostics.CaptureAllAsync` / 设置窗口的即时保存模式都有现成先例。
- 两个列表（`TrackGrid` 与 `GroupedList`）已支持多选并同步 `MainViewModel.SelectedRows`；
  右键菜单 `TrackMenu` 为两者共用，动态子菜单已有先例（`OnPresetButtonClick`）。
- **现存缺陷**：右键点击未选中的行不会改变选择（WPF 默认行为），转换菜单会作用在旧的选中项上；
  本功能一并修复（右键先把鼠标所在行设为唯一选中）。

## 决策

### 1. 总体结构与数据流

```
MainWindow（右键“转换”子菜单 / 预设快捷键）
  └─ MainViewModel.RunConversionPreset(presetId) / ConvertSelectedCommand
       └─ ConversionCoordinator（App，批次调度、进度、取消）
            ├─ ConversionPlanner（Core，纯函数：输出路径、冲突、跳过）
            ├─ IAudioConverter（Core 抽象；Infrastructure 实现）
            │    ├─ MediaDecoder（NAudio / Media Foundation，float32）
            │    ├─ WdlResamplingSampleProvider（可选高质量重采样）
            │    ├─ PcmWavWriter（16 / 24-bit 临时 WAV；WAV 输出直接写最终文件）
            │    ├─ ExternalEncoderRunner（neroAacEnc / flac / lame 子进程）
            │    └─ ITagService（复制标签与内嵌封面）
            └─ ConversionWindow（进度、取消、结果、打开输出目录）
```

### 2. 数据模型（Core）

新增 `src/EchoGarage.Core/Models/Conversion.cs`：

```csharp
public enum ConversionFormat { Aac, Flac, Mp3, Wav }

public enum ConversionRateMode { Vbr, Abr, Cbr }   // AAC 三种；MP3 只用 Vbr/Cbr

public enum ConversionOutputMode { SourceFolder, CustomFolder }

public enum ConversionConflictPolicy { Ask, Skip, Overwrite, Rename }   // 默认 Ask：冲突时询问用户

public enum ConversionBitDepth { Source, Pcm16, Pcm24 }

/// <summary>重采样质量档位，映射到 SSRC 的 --profile；High 为默认（170 dB / double）。</summary>
public enum ConversionResampleProfile { Standard, High, Insane }

public sealed class ConversionPreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public ConversionFormat Format { get; set; }
    public ConversionRateMode RateMode { get; set; } = ConversionRateMode.Vbr;

    /// <summary>AAC：0–100（映射到 Nero -q 0.00–1.00）；MP3 VBR：0–9（V0 最好）；FLAC：压缩级别 0–8。</summary>
    public int Quality { get; set; }

    /// <summary>ABR / CBR 比特率（kbps），VBR 模式忽略。</summary>
    public int BitrateKbps { get; set; } = 192;

    public ConversionBitDepth BitDepth { get; set; } = ConversionBitDepth.Source;
    public ConversionOutputMode OutputMode { get; set; } = ConversionOutputMode.SourceFolder;
    public string OutputFolder { get; set; } = string.Empty;
    public string FileNameTemplate { get; set; } = "%artist% - %title%";
    public ConversionConflictPolicy ConflictPolicy { get; set; } = ConversionConflictPolicy.Ask;

    public bool Resample { get; set; }
    /// <summary>0 表示与源相同；否则 44100 / 48000 / 88200 / 96000 / 176400 / 192000。</summary>
    public int TargetSampleRate { get; set; }

    /// <summary>重采样质量档位（SSRC --profile），默认 High。</summary>
    public ConversionResampleProfile ResampleProfile { get; set; } = ConversionResampleProfile.High;

    public bool CopyTags { get; set; } = true;
    public bool CopyCover { get; set; } = true;

    /// <summary>预设快捷键（可为空），格式同 HotkeyGesture，例如 Ctrl+Alt+1。</summary>
    public string Hotkey { get; set; } = string.Empty;
    public bool HotkeyGlobal { get; set; }

    public ConversionPreset Clone();
}

public sealed class ConversionEncoderPaths
{
    public string FlacPath { get; set; } = string.Empty;
    public string LamePath { get; set; } = string.Empty;
    public string SsrcPath { get; set; } = string.Empty;
    public string NeroAacPath { get; set; } = string.Empty;
}
```

`AppSettings` 增加两个字段（旧 settings 缺失时自动得到内置默认值，不需要 schema 迁移）：

```csharp
public List<ConversionPreset> ConversionPresets { get; set; } = ConversionPresetFactory.CreateBuiltInPresets();
public ConversionEncoderPaths Encoders { get; set; } = new();
```

`JsonSettingsService.Normalize` 增加预设与编码器路径的归一化（详见第 3 节）。

### 3. 预设归一化与内置预设

新增 `ConversionPresetFactory`（Core）：

- `CreateBuiltInPresets()` 提供 4 个默认预设：
  - `MP3 · 320 kbps`（Cbr, BitrateKbps=320, `%artist% - %title%`，输出到源文件夹，冲突询问，复制标签+封面）
  - `FLAC · 无损（级别 5）`（Flac, Quality=5, BitDepth=Source）
  - `AAC · Nero Q1.00`（Aac, Vbr, Quality=100）
  - `WAV · 16-bit`（Wav, BitDepth=Pcm16）
  （owner 要求 MP3 与 AAC 默认最高音质：MP3 320 kbps CBR、AAC Q1.00；FLAC/WAV 保持无损 / 16-bit 起点。）
- `Normalize(preset)` 规则：
  - `Name` 去空白；空则“预设 N”；重名追加 ` (2)`；`Id` 为空/重复则重新生成。
  - 按格式夹取参数：AAC Quality 0–100、MP3 0–9、FLAC 0–8；BitrateKbps 32–512；
    RateMode 对格式不适用时回落（MP3 只允许 Vbr/Cbr；FLAC/WAV 忽略）；
    BitDepth 对 WAV 只允许 Pcm16/Pcm24（Source→Pcm16）；TargetSampleRate 不在允许列表则 0。
  - `FileNameTemplate` 为空回落 `%artist% - %title%`；长度上限 200。
  - 快捷键用 `HotkeyGesture.TryParse` 规范化；无效则清空；与其它预设或全局播放快捷键重复时清空并记录 WARN。
  - `OutputFolder` 去空白（`CustomFolder` 且为空由设置界面校验，转换时再报错）。
- `JsonSettingsService.Normalize`：`settings.ConversionPresets = ConversionPresetFactory.NormalizeAll(settings.ConversionPresets)`；
  `Encoders` 三个路径去空白、展开环境变量；不做文件存在性校验（路径可能暂时不可用）。

### 4. 输出位置与文件名模板

新增 `ConversionNaming`（Core，纯函数、可单测）：

- 占位符：`%title%` `%artist%` `%album%` `%albumartist%` `%composer%` `%genre%` `%year%`
  `%track%`（两位补零）`%disc%` `%filename%`（源文件名去扩展名）`%codec%`（源格式）。
  未知 `%xxx%` 保留为字面文本（便于用户在预览中发现拼写错误）。
- 空值清理：某占位符为空时，删除其相邻的分隔符（空格、`-`、`_`、`.`），折叠连续空格，
  去掉首尾分隔符与空括号；最终为空时回落到 `%filename%`，再为空则用源文件名。
- 子目录：模板中的 `/` 或 `\` 视为子目录分隔符；每个路径段单独清洗。
- 清洗：替换 `<>:"/\|?*` 与控制字符为 `_`；去掉段尾的 `.` 与空格；Windows 保留设备名（CON、PRN、NUL、COM1…）
  前加 `_`；单段最长 120 字符，完整路径控制在 240 字符内。
- 扩展名由格式决定：`.m4a` / `.flac` / `.mp3` / `.wav`。
- 冲突策略：
  - `Ask`（默认）：批次开始前先规划全部输出路径，收集已存在的目标；若存在冲突，弹出“文件已存在”对话框，
    列出冲突数量与示例，提供 **覆盖 / 跳过 / 自动重命名 / 取消** 四个选择，一次性应用到本批次所有冲突项。
  - `Skip`：已存在则整项跳过，结果标记“已跳过（文件已存在）”。
  - `Overwrite`：覆盖已存在文件；**若解析出的输出路径与源文件相同，强制改用 Rename**（防止把源文件覆盖掉）。
  - `Rename`：追加 ` (2)`、` (3)` … 直到不冲突。
  - `Ask` 对话框中选择“覆盖”时同样遵循“输出==源则强制 Rename”的安全规则。
- 预览：`ConversionNaming.Preview(preset, track)` 返回完整输出路径，设置窗口实时显示
  （优先用当前选中的第一首，否则用示例曲目）。

### 5. 外部编码器依赖与查找

- 分发策略（owner 已确认）：
  - **FLAC / LAME / SSRC 随发布包附带**：仓库 `tools\` 下放置 `flac.exe`、`lame.exe`、`ssrc.exe` 及许可文件
    （`flac_license.txt`、`lame_license.txt`、`ssrc\LICENSE.txt`）；发布脚本把 `tools\` 复制到输出目录。
    README 与 `tools/README.txt` 记录官方来源、版本与源码获取方式（flac 命令行工具 GPL、LAME LGPL、
    SSRC Boost 1.0）。SSRC 使用 v2.4.0 Windows 包（约 2.1 MB），解压后取 `ssrc.exe`；
    若运行依赖 `libomp.dll` 等运行库则一并附带。
  - **Nero AAC 由用户自备**（EULA 禁止再分发）：在设置中指定 `neroAacEnc.exe` 路径；
    owner 本机验证路径为 `D:\Program\foobar2000\encoders\neroAacEnc.exe`。
- 查找顺序（`EncoderLocator`，Infrastructure）：
  1. 设置中显式指定的路径；
  2. 程序目录下 `tools\flac.exe` / `tools\lame.exe` / `tools\ssrc.exe` / `tools\neroAacEnc.exe`
     （发布包自带 FLAC / LAME / SSRC）；
  3. 程序目录下的同名 exe；
  4. 系统 `PATH`。
- 设置窗口提供“编码器与工具”段：FLAC / LAME / SSRC / Nero AAC 四项路径
  （FLAC/LAME/SSRC 可留空表示自动使用 `tools\` 中的版本）+ 浏览… + 状态（已找到 / 未找到）+ “重新检测”。
- 运行时报错示例：“未找到 `flac.exe`。请把 flac.exe 放到程序目录的 tools 文件夹，或在“转换器设置 → 编码器”中指定路径。”
- README 增加“编码器准备”一节：FLAC/LAME 已随发布包提供（含许可说明）；Nero AAC 说明（个人非商业、用户自备）。

### 6. 单文件转换流水线（Infrastructure）

新增 `AudioConverter : IAudioConverter`：

1. 校验：源文件存在且可解码；编码器存在（AAC/FLAC/MP3）；输出目录可创建；输出路径合法。
2. 解码：按扩展名选择 `WaveFileReader`（wav）或 `MediaFoundationReader`（其余，`RequestFloatOutput=true`），
   得到 `ISampleProvider`（float32）；失败时返回“当前系统无法解码该格式（缺少解码器）”。
3. 重采样：`Resample && TargetSampleRate > 0 && != 源采样率` 时，把解码后的 float 写成临时 PCM WAV（源采样率），
   调用 `ssrc.exe --rate <target> --bits <16|24> --profile <standard|high|insane> in.wav out.wav`；
   SSRC 缺失或失败时回落到 `WdlResamplingSampleProvider`（记录 WARN）。
4. 中间 WAV（16 / 24-bit，按预设）：
   - MP3 / AAC 固定 16-bit；
   - FLAC / WAV 用预设位深；`Source` 时取源位深（≤16→16，>16→24）。
   - 启用重采样时，先按源采样率写临时 WAV，再由 SSRC 输出目标采样率 WAV（位深同上），编码器直接使用该文件；
     未启用重采样时由 `PcmWavWriter` 写。
   - WAV 输出直接写到最终文件（不再启动编码器），其余写临时目录。
5. 编码：`ExternalEncoderRunner` 启动子进程（参数见第 7 节），异步读取 stdout/stderr，取消时 Kill 整个进程树；
   退出码非 0 或输出文件不存在/长度为 0 视为失败。
6. 标签复制：`ITagService.Read(source, includeCover: preset.CopyCover)` → `TagEditModel` → `Write(output)`。
   标签写入失败不改变“转换成功”结论，结果附加警告“已转换，但标签写入失败：…”。
7. 清理临时目录；失败时删除未完成的输出文件。
8. 返回 `ConversionResult { Success, Skipped, OutputPath, Error, Warning }`，进度通过
   `IProgress<ConversionProgress>`（阶段：Preparing/Decoding/Resampling/Encoding/WritingTags/Done + 0–1 比例）。

临时目录：`<EchoGarageData>/data/Conversion/<guid>/`；程序启动时清理该目录下超过 1 天的残留（安全兜底）。

### 7. 各格式命令行

| 格式 | 命令 |
| --- | --- |
| AAC (Nero) | `neroAacEnc.exe -q 1.00 -if "in.wav" -of "out.m4a"`（VBR）；ABR：`-br 192000`；CBR：`-cbr 192000` |
| FLAC | `flac.exe -5 -o "out.flac" "in.wav"`（覆盖时加 `-f`） |
| MP3 | `lame.exe -b 320 --silent "in.wav" "out.mp3"`（VBR：`-V 0`） |
| WAV | 不调用编码器，直接写 PCM |
| 重采样 | `ssrc.exe --rate 48000 --bits 24 --profile high "in.wav" "out.wav"`（仅启用重采样时） |

`EncoderCommandLine`（Core，纯函数）负责构造参数数组（含带空格路径的引号处理），并有单测覆盖。
编码器输出只用于错误诊断；成功与否以退出码 + 输出文件为准。

### 8. 批次、进度窗口与取消

- `ConversionCoordinator`（App）：
  - 同一时间只允许一个批次；重复触发时把已打开的进度窗口带到前台。
  - 过滤缺失/不可播放的曲目（标记跳过）；顺序执行（v1 不做并行，避免磁盘/CPU 争用）。
  - 每个文件更新状态与总体进度（已完成数/总数），完成后 `_vm.SetStatus("转换完成：成功 N，跳过 K，失败 M")`。
  - 取消：`CancellationTokenSource`，取消后终止当前编码进程、删除未完成输出、剩余项标记“已取消”。
- `ConversionWindow`（ThemedWindow，约 760×560，modeless，Owner=主窗口）：
  - 顶部：预设名 + 总体进度条 + “正在转换第 i / N 首”；
  - 列表：文件名、目标格式、状态（等待 / 解码 / 重采样 / 编码 / 写入标签 / 完成 / 跳过 / 失败 / 已取消）、输出路径；
  - 底部：取消（运行中）、打开输出文件夹、关闭；
  - 运行中关闭窗口：确认“转换正在进行，关闭将取消剩余任务”，确认后取消并关闭；
  - 失败项显示原因（Tooltip 完整错误）。
- 转换结束后不自动加入播放列表；若输出在媒体库目录内，文件监听会照常索引新文件。

### 9. 右键菜单与快捷键

- `MainWindow.xaml` 的 `TrackMenu` 增加：
  - `转换` 子菜单（运行时根据 `Settings.ConversionPresets` 动态生成）：
    每个预设一项，`Header = 预设名`，有快捷键则显示 `InputGestureText`，点击 → `_vm.RunConversionPreset(preset.Id)`；
    无预设时显示禁用的“（尚未创建预设）”；末尾分隔线 + “转换器设置…”。
  - 右键选中修复：`TrackGrid` / `GroupedList` 增加 `PreviewMouseRightButtonDown`，
    找到鼠标所在行；若该行未被选中则将其设为唯一选中项（随后菜单作用对象符合直觉）。
- 文件菜单增加“转换器设置…”（“设置…”之后），打开 `ConversionSettingsWindow`。
- 快捷键：
  - 每个预设自带 `Hotkey` + `HotkeyGlobal`（在转换器设置里编辑）。
  - `HotkeyService.Reload()` 在播放快捷键之后加载预设快捷键：
    - 与播放快捷键或其它预设重复时跳过并 WARN（设置窗口同时给出内联错误）；
    - 全局注册 id 使用新段 `0xEA00 + index`（播放控制仍用 `0xE900` 段）；
    - `TryHandleLocal` / `WM_HOTKEY` 命中后调用 `_vm.RunConversionPreset(presetId)`。
  - 预设增删改 / 快捷键修改后调用 `_vm.NotifyHotkeysChanged()` 重新注册。
  - 无选中项时触发：状态栏提示“请先在列表中选择要转换的歌曲。”，不弹错误框。
- 若某个全局快捷键被其它程序占用：沿用现有行为，记录 WARN 并跳过，不影响程序运行；
  设置窗口的快捷键状态行显示实际注册数量。

### 10. 转换器设置窗口

新增 `ConversionSettingsWindow`（ThemedWindow，约 900×680，Min 780×560，即时保存，底部“关闭”）：

- 左侧：预设列表（名称 + 格式标签）、按钮 新建 / 复制 / 删除 / 上移 / 下移。
- 右侧（ScrollViewer）：
  1. **预设名称**：TextBox。
  2. **输出格式**：ComboBox（AAC (Nero) / FLAC / MP3 / WAV）+ 随格式变化的参数：
     - AAC：模式（VBR 质量 / ABR 比特率 / CBR 比特率）；质量滑杆 0.00–1.00（显示 `Q0.50 · 约 175 kbps`）或比特率下拉；
     - MP3：模式（VBR 质量 / CBR 比特率）；质量滑杆 V0–V9（显示约 kbps）或比特率下拉（128/160/192/256/320）；
     - FLAC：压缩级别 0–8（默认 5）；
     - WAV：位深 16 / 24。
  3. **目标**：输出到源文件文件夹 / 指定文件夹（TextBox + 浏览…）；文件名模板 TextBox
     + 占位符说明（`%title%`、`%artist%` …，支持 `/` 子目录）+ 实时“示例：…”；
     冲突处理（询问（默认）/ 跳过 / 覆盖 / 自动重命名）。
  4. **处理**：重采样 CheckBox + 目标采样率 ComboBox（与源相同 / 44100 / 48000 / 88200 / 96000 / 176400 / 192000）
     + 质量 ComboBox（标准 / 高（默认）/ 最高（最慢），映射 SSRC `--profile`）
     + 说明“SSRC 高质量重采样（与 foobar2000 同源方案）；不改变声道数”。
  5. **标签**：复制标签（默认开）、复制内嵌封面（默认开，未复制标签时禁用）。
  6. **快捷键**：TextBox（如 `Ctrl+Alt+1`）+ “全局”CheckBox；实时校验格式与冲突，错误显示在字段下方。
  7. **编码器与工具**：FLAC / LAME / SSRC / Nero AAC 四行路径（TextBox + 浏览… + 状态）；FLAC/LAME/SSRC
     留空时自动使用发布包 `tools\` 中的版本；Nero AAC 需用户指定路径；“重新检测”按钮。
- 任何修改即时写回 `Settings.Current.ConversionPresets` / `Encoders` 并 `QueueSettingsSave()`；
  预设列表顺序即右键菜单顺序。
- 顶部/底部不设“确定/取消”，与现有设置窗口一致；关闭即完成。

### 11. 错误处理、日志与安全

- 每个文件独立 try/catch：失败只影响该项，批次继续；`Logger.Error` 记录源文件、预设、命令与 stderr。
- 日志关键点：`Conversion started: preset=..., format=..., count=...`、
  `Conversion item failed: source=..., error=...`、`Conversion finished: ok=..., skipped=..., failed=...`。
- 取消/失败时删除未完成输出与临时文件；`Overwrite` 策略下先写临时文件再原子替换，避免中途失败损坏原文件
  （WAV 直写最终文件的情况除外：失败时删除新文件）。
- 禁止输出路径等于源路径（自动 Rename），避免误覆盖。
- 不修改源文件、不删除源文件（v1 明确不做“转换后删除源文件”）。

### 12. 测试与验证计划

**Core 单元测试（新增）**

- `ConversionNamingTests`：占位符替换、空值分隔符清理、非法字符/保留名/子目录、冲突 Skip/Overwrite/Rename、
  输出==源的安全回退、模板为空回落、预览。
- `ConversionPresetTests`：参数夹取、名称/Id 去重、内置预设有效、快捷键规范化与重复清理。
- `EncoderCommandLineTests`：AAC VBR/ABR/CBR、MP3 VBR/CBR、FLAC 级别、SSRC 重采样参数（rate/bits/profile）、
  覆盖标志、带空格路径。
- `ConversionPlannerTests`：输出路径计算、跳过判定、批次顺序（临时目录内构造文件）。

**Infrastructure 集成测试**

- WAV→WAV 全链路（解码、写 PCM、复制标签与封面，无需外部编码器）。
- 若 `tools` / `.scratch/tools` 存在 `flac.exe`、`lame.exe`、`ssrc.exe`，追加 FLAC/MP3 全链路测试与
  SSRC 重采样测试（校验输出采样率/时长/声道）；不存在时动态跳过（xUnit v3 `Assert.Skip`）。
  Nero 只做命令行构造测试（许可原因不下载分发）。

**手动验证（AGENTS 要求保留日志与步骤）**

1. 准备测试曲目（mp3 / flac / m4a / wav 各一）；发布包已带 `tools\flac.exe` 与 `tools\lame.exe`，
   Nero 路径指向 owner 自备的 `D:\Program\foobar2000\encoders\neroAacEnc.exe`。
2. 四种预设各转换一次，检查输出可播放、时长正确、标签与封面复制成功（应用内播放 + 标签属性窗口）。
3. 多选 3 首 → 右键“转换” → 选预设；检查进度、取消、失败提示；再验证预设快捷键（局部 + 全局）。
4. 模板/目标/冲突策略各改一次并验证；重点验证默认“询问”在批次冲突时只弹一次、并正确应用到全部冲突项，
   以及“输出到源文件夹”同名冲突的安全行为。
5. `--screenshot-all` 增加 `dialog-conversion-settings.png`；进度窗口手动截图（不提交仓库，放 `artifacts/verify/`）。
6. 全部单测通过、构建 0 警告 0 错误。

**实现结果（2026-11-14）**

- 已实现：Core（`Conversion` 模型、`ConversionNaming`、`ConversionPresetFactory`、`EncoderCommandLine`、
  `ConversionPlanner`、`IAudioConverter`）；Infrastructure（`MediaDecoder`、`PcmWavWriter`、`EncoderLocator`、
  `ExternalEncoderRunner`、`AudioConverter`）；App（`ConversionSettingsWindow`、`ConversionConflictWindow`、
  `ConversionProgressWindow`、`ConversionCoordinator`、右键“转换”子菜单、每预设快捷键、文件菜单入口）。
- 发布包 `tools\` 放入 flac 1.4.3、LAME 3.100.1、SSRC 2.4.0 与许可文件，`scripts/publish.ps1` 自动复制。
- 构建 0 警告 0 错误；测试 178 项全部通过（含 WAV→FLAC / WAV→MP3、SSRC 重采样、FLAC→WAV 的真实工具集成测试；
  在受限环境下测试会把 `tools` 复制到临时目录再执行，避免沙箱限制）。
- SSRC 输出带固定滤波器拖尾，已按输入帧数换算目标帧数后裁剪，保证输出时长与源一致。
- `--screenshot-all` 输出 `dialog-conversion-settings.png`、`dialog-conversion-conflict.png` 供界面检查。
- 待 owner 验收：真实 AAC 转换（使用 owner 本机 Nero 路径）、右键菜单 / 快捷键 / 进度窗口的手动操作手感。

### 13. 影响与兼容性

- 启动路径不新增同步 IO；编码器只在打开设置窗口或开始转换时检测。
- 无数据库 schema 变更；`settings.json` 增加两个字段（旧配置自动补默认值），导出/导入配置自动包含。
- 发布包：随包附带 FLAC / LAME / SSRC（约 +4.3 MB），发布脚本把仓库 `tools\` 复制到输出目录 `tools\`。
- 线程：转换全部在后台线程，UI 通过 `IProgress` 更新；不阻塞播放与扫描。
- 许可：FLAC/LAME 随包分发，附官方许可文件与源码获取说明（flac 工具 GPL、LAME LGPL）；
  SSRC 为 Boost 1.0（宽松许可，附 LICENSE.txt）；Nero AAC 由用户自备（EULA 禁止再分发）。
- 新增依赖：无 NuGet 新依赖（NAudio、TagLibSharp 已在 Infrastructure）；SSRC 作为外部工具随包附带，
  NAudio `WdlResamplingSampleProvider` 仅作缺失时的回退。

## 已确认的选择（owner，2026-11-14）

| # | 问题 | 决定 |
| --- | --- | --- |
| 1 | 编码器来源 | **FLAC / LAME / SSRC 随发布包附带**（`tools\` + 许可文件）；Nero AAC 由用户自备。 |
| 2 | Nero AAC 路径 | owner 本机已有 `D:\Program\foobar2000\encoders\neroAacEnc.exe`，实现后用它做 AAC 验证。 |
| 3 | 默认冲突策略 | **询问用户是否覆盖**：批次开始时若目标已存在，弹一次对话框（覆盖 / 跳过 / 自动重命名 / 取消），应用到全部冲突项；预设可改为固定策略。 |
| 4 | 默认文件名模板 | `%artist% - %title%`。 |
| 5 | 预设快捷键 | 每个预设自带一个本地/全局快捷键。 |
| 6 | MP3 / AAC 默认音质 | **MP3 320 kbps CBR、AAC Nero Q1.00**（默认预设取最高音质）。 |
| 7 | 重采样方案 | 使用开源 **SSRC（Shibatch）v2.4.0**，随包附带 `ssrc.exe`，默认 `high` 档（170 dB / double）；foobar2000 内置的 “dBpoweramp/SSRC” 为闭源实现，无法直接复用。 |

## 风险与待确认问题

| # | 问题 | 建议 |
| --- | --- | --- |
| 1 | 转换后行为 | v1 不自动加入播放列表、不自动删除源文件、不替换列表条目；输出在媒体库目录时由文件监听自动索引。 |
| 2 | 设置窗口形态 | 单窗口“左预设列表 + 右即时编辑”（即时保存、无确定/取消），比 foobar 的“列表 + 独立编辑弹窗”更简单。 |
| 3 | AAC 容器与模式 | 输出 `.m4a`；模式 VBR 质量 + ABR/CBR 比特率（覆盖 Nero 常用能力）。 |

## 实施步骤（owner 确认后）

1. Core：`Conversion.cs`、`ConversionNaming`、`ConversionPresetFactory`、`EncoderCommandLine`、
   `ConversionPlanner`（含冲突收集）+ 单元测试。
2. 设置持久化：`AppSettings` 字段、`JsonSettingsService.Normalize` + 测试。
3. 工具与发布：把 `flac.exe` / `lame.exe` / `ssrc.exe` 及许可文件放入仓库 `tools\`；
   `scripts/publish.ps1` 复制 `tools\`。
4. Infrastructure：`MediaDecoder`、`PcmWavWriter`、`ExternalEncoderRunner`、`EncoderLocator`、`AudioConverter`
   + WAV 全链路测试（外部编码器可用时追加）。
5. App：`ConversionCoordinator`（含冲突询问）、`ConversionWindow`、`ConversionSettingsWindow`、
   `MainViewModel` 命令与事件、右键菜单与右键选中修复、`HotkeyService` 扩展。
6. 诊断与文档：`--screenshot-all` 增加设置窗口截图、README、`tools/README.txt`。
7. 构建/测试/截图验证/发布；ADR-017 经 owner 批准后移入 `docs/adr/` 并更新索引。

## 备选方案与取舍

- **Media Foundation 内置 AAC/MP3 编码器**：Windows 自带，可免外部工具；但 AAC 码率/质量控制有限，
  且 owner 明确要求 Nero AAC，故不采用。
- **foobar2000 内置的 “Resampler (dBpoweramp/SSRC)”**：质量口碑很好，但它是 foobar 作者基于 dBpoweramp/SSRC
  代码实现的闭源内置组件，无法作为库或命令行工具复用；因此改用其开源同源方案 SSRC（Shibatch，Boost 许可）。
- **libsoxr / SoX Resampler**：质量与 SSRC 同级、速度更快（LGPL），也是可行方案；v1 选择 SSRC 以贴近
  owner 指定的 foobar 方案，后续可加“重采样器”选项切换。
- **ffmpeg 单文件**：可覆盖全部格式与重采样，但体积大（数十 MB）、许可与分发复杂，且无法使用 Nero AAC；不采用。
- **内置 FLAC 编码库**：需引入新依赖与许可审查，收益有限；改用官方 `flac.exe`。
- **转换后自动删除源文件 / 替换播放列表**：风险高，v1 不做。
- **并行转换**：v1 顺序执行，稳定优先；后续可加并发数设置。
