# ADR-034：「正在播放」页 BPM 测量、ONNX/DirectML 推理、缓存持久化与 Tap 修正

- 状态：Accepted（2026-11-25 owner 批准；2026-11-26 并入变速曲目分段 BPM 识别与可视化增补并实施落地）
- 日期：2026-11-25（2026-11-26 增补分段 BPM 与 schema v6）
- 关联：
  - ADR-001（平台与技术栈——2026-11-25 增补按需懒加载的 ONNX Runtime DirectML 依赖）
  - ADR-002（分层架构与组合根）
  - ADR-003（文件即真相——2026-11-25 增补 §2 未落盘手动修正 BPM 的软件侧状态说明）
  - ADR-006（配置与媒体库分离、便携目录布局）
  - ADR-015（波形分析与后台调度范式）
  - ADR-019（标签编辑页与额外标签）
  - ADR-020（播放中文件共享句柄与临时副本原子写标签）
  - ADR-024（五套主题与主题令牌）
  - ADR-028（音频解码与分析器架构）
  - ADR-031（「正在播放」页布局与标签卡片）
  - KB-021（BPM 调研、`Beat This!` ONNX/DirectML 细节、Tap 回归算法与 TagLib# 规范）

---

## 0. 决策摘要

1. **算法与推理引擎**：采用 **Beat This!（ISMIR 2024，MIT 许可）+ `Microsoft.ML.OnnxRuntime.DirectML`（MIT 许可）**，支持 Windows DirectX 12 全厂商 **GPU 加速（NVIDIA / AMD / Intel）** 并自动回退至 **CPU**；架构通过 `Core/Abstractions/IBpmAnalyzer` 解耦。
2. **模型策略（内置 `small0` + 可选 `final0` 指引）**：
   - 随包**内置 `beat_this_small0.onnx`（~10.5 MB，MIT）**，开箱即用，兼顾 GPU 极速与 CPU 回退体验；
   - 在设置页 BPM 选项中提供高精度主模型 **`beat_this_final0.onnx`（~80 MB，MIT）** 的**下载与安装指引**、一键「打开模型目录」与「重新检测」按钮，用户按指引放置模型文件后即可在下拉框切至 `final0`。
3. **非 BPM 期间零性能影响**：启动期 **0 加载**、**0 VRAM/RAM 占用**；仅在首次点击「BPM 测量」时后台懒加载 `InferenceSession`；采用 **30 秒固定静态分块（`[1, 1500, 128]`）** 避免 DirectML 动态形状重编译；推理完成后 **60 秒空闲自动卸载（`Dispose()`）** 释放全部显存与原生内存（亦支持在设置中切为“测量后立即释放”）。
4. **持久化缓存存储**：在 SQLite `library.db` 中新增独立表 **`track_bpm_cache`**（schema v5 建表，v6 增补分段明细列 `SegmentsJson` / `DetectedSegmentCount`），按规范化路径持久化自动测量结果、2–5 段变速明细与未写文件的手动修正；「清空缓存」只清除自动测量与分段列，**永久保护用户手动修正**；以时长变化而非单纯 `Size + Mtime` 判失效，避免改封面/改标题误清 BPM。
5. **数值精度与分段识别**：内部以 `double` 存储，界面默认展示 **1 位小数**（如 `128.0` / `127.5`）；写入 FLAC/ID3 文件标签保留小数（整数去 `.0` 写 `"128"`，小数写 `"128.5"`），写入 M4A（`tmpo` 协议硬限制整数）时自动取整并在界面提示。对变速曲目复用同一次 Beat This! 50 fps 拍点输出执行纯 C# 分段常数动态规划（`TempoSegmentAnalyzer`）：`2–5` 段以**进度条比例可视化 + 明细列表**同时展示（支持点击跳转试听与采用）；`> 5` 段时给出变速过多提示并隐藏分段明细。
6. **UI 与手动修正**：「正在播放」页标题栏右侧新增手动按钮 `♩ BPM 测量`（与 `✎ 标签属性` 并列）；标签卡片第 3 行拆出 `BPM` 槽位（零垂直高度增量）；点击弹出内联修正浮层，支持 **手动输入 + 一键 `÷2` / `×2` + Tap 轻点测速（OLS 最小二乘回归）+ 可选勾选「同时写入音频文件标签」**（默认不勾选，走 ADR-020 播放中无中断原子替换）。
7. **修复现有标签缺口**：补齐 `TagLibTagService` 跨格式 BPM 读写，修复 M4A `tmpo` 二进制 atom 当前读不出来与不能用 `SetText` 写的问题，并与「标签属性」窗口联动。

---

## 1. 背景与现状盘点

| 现有模块 | 现状事实（源码核实） | 本方案衔接点 |
| --- | --- | --- |
| `MainWindow.xaml`（`NowPlayingPageRoot`） | 标题栏右侧仅 `✎ 标签属性`（L1192）；`NowPlayingTagCardHost`（L1200）第 3 行为 `编码器 (ColumnSpan=2)` + `备注 (Col 2)`；窗口高 < 520 DIP 隐藏标签卡片 | 标题栏并列放 `♩ BPM 测量`；标签卡片第 3 行拆为 `编码器` \| `BPM` \| `备注`，**零额外垂直高度** |
| `SharedAudioFileReader`（ADR-020） | `FileShare.ReadWrite \| FileShare.Delete` 共享打开 WAV/AIFF/MP3/M4A/FLAC/WMA 输出 float32 | BPM 解码复用同款共享句柄，播放中测量与写标签均不产生文件锁冲突 |
| `SqliteDatabase.cs` | 当前 `PRAGMA user_version = 4`；`SqliteLibraryRepository.UpdateTrackPathAsync` 负责文件移动后的路径事务更新 | 升至 **schema v5** 新增 `track_bpm_cache`；文件移动/删除在同事务中同步维护缓存 |
| `TagLibTagService.cs` | 已有 ADR-020 原子写替换；但 `CollectAppleBoxes` L756 用 `data.Text` 读不出二进制 `tmpo`，`Known*` 未排除 BPM | 升级跨格式 BPM 读写（`AudioTagData.Bpm` + `TagEditModel.Bpm`），修复 `tmpo` 并消除重复展示 |
| `Directory.Packages.props` | 当前 5 个运行时包（CommunityToolkit.Mvvm / Dapper / Microsoft.Data.Sqlite / NAudio / TagLibSharp） | 新增 1 个 MIT 运行时包 `Microsoft.ML.OnnxRuntime.DirectML` + 内置 `tools\models\beat_this_small0.onnx`（MIT） |

---

## 2. 依赖引入与模型分发设计

### 2.1 新增依赖清单

| 依赖 / 资产 | 许可证 | 发布包体积增量 | 启动时间影响 | 说明 |
| --- | --- | --- | --- | --- |
| `Microsoft.ML.OnnxRuntime.DirectML`（NuGet，仅 `win-x64`） | **MIT** | ~22–32 MB（`onnxruntime.dll` + `DirectML.dll` + 托管包装） | **0 ms**（仅首次点击「BPM 测量」时加载） | Windows 原生 DX12 全厂商 GPU 加速（NVIDIA / AMD / Intel）+ 自动 CPU 回退，无需安装 CUDA |
| `tools\models\beat_this_small0.onnx`（随包内置） | **MIT** | ~10.5 MB | **0 ms**（按需懒加载） | 紧凑高泛化模型；GPU ~0.2–0.5 s，无独显 CPU 回退 ~1.5–3.5 s |
| `beat_this_final0.onnx`（用户可选自行安装） | **MIT** | 随包 **0 MB**（用户按指引下载放置，约 ~80 MB） | **0 ms** | 论文主模型极致精度；GPU ~0.5–1.2 s，纯 CPU 回退 ~6–14 s |

### 2.2 模型发现路径与 `final0` 下载安装指引

- **模型存放与发现优先级（`BpmModelLocator`）**：
  - 内置模型 `small0`：`<exe目录>\tools\models\beat_this_small0.onnx`（随 `scripts/publish.ps1` 一并发布，附 `LICENSE-beat-this.txt` 与 `LICENSE-onnxruntime.txt`）；
  - 可选模型 `final0`：优先查找便携数据目录 **`<EchoGarageData>\data\Models\beat_this_final0.onnx`**（受 `ECHOGARAGE_DATA_DIR` 重定向；**覆盖更新程序目录时不会丢失用户已安装的 `final0`**），其次兼容回退查找 `<exe目录>\tools\models\beat_this_final0.onnx`；
  - 检测校验：校验文件存在、大小合理（> 1 MB）且可读；真正切换或测量时若 ONNX 加载失败，写 `[WARN]` 日志并提示用户检查模型文件完整性，同时允许一键回退至内置 `small0`。
- **设置页「`final0` 下载与安装指引」交互（类似现有「编码器 → Nero AAC」配置风格）**：
  - 在设置页 BPM 区块提供「分析模型」下拉框：
    - `Beat This! Small（内置，推荐）`
    - `Beat This! Final（高精度，已安装）` / `Beat This! Final（高精度，未安装）`（未安装时选择它会提示仍回退使用内置 `small0`，并在下方展开安装指引）
  - 状态行实时显示两个模型的就绪状态与所在路径；
  - 附带简洁的「安装高精度模型（final0）指引」卡片/折叠说明：
    1. 说明 `final0`（约 80 MB，MIT 许可）在无鼓原声、复杂爵士、古典与变速曲目上精度更高（需 GPU 或更长 CPU 耗时）；
    2. 提供官方来源说明与可点击复制/在浏览器打开的下载/导出指引链接（`CPJKU/beat_this` 官方仓库及对应的 ONNX 获取/转换说明，不自动联网静默下载）；
    3. 明确写出目标文件名 **`beat_this_final0.onnx`** 与放置目录（默认 `<EchoGarageData>\data\Models\`）；
    4. 提供 **`打开模型目录`**（自动创建并打开 `data\Models\`）与 **`重新检测`** 两个按钮，放入文件点击检测后即刻就绪可用。

---

## 3. 与已生效 ADR 的衔接与修订

1. **ADR-001（平台与技术栈）**：已同步增补第 8 条，登记 `Microsoft.ML.OnnxRuntime.DirectML`（MIT）与内置 `beat_this_small0.onnx`（MIT）作为按需懒加载的离线分析依赖，明确其不介入启动路径与音频播放链路。
2. **ADR-003（文件即真相）**：已同步增补 §2 修订说明：
   - `track_bpm_cache.MeasuredBpm` 属于可重算的派生分析缓存（同 ADR-015 波形缓存），可在「清空缓存」中单独清理；
   - `track_bpm_cache.ManualBpm`（用户手动修正且未写入文件级 Tag）登记为软件侧用户状态（与 `PlayCount`、`LastPlayedAtUtc`、`Rating` 同级），随 `library.db` 持久保存且不随「清空缓存」删除；
   - 一旦用户勾选「同时写入音频文件标签」，写回文件标准标签并清空 `ManualBpm`，回归「文件即真相」。

---

## 4. 详细架构与模块设计

严格遵循 `App → Infrastructure → Core` 单向依赖（ADR-002）；Core 不引用 NAudio / ONNX Runtime / TagLib / SQLite / WPF：

```text
src/EchoGarage.Core/
  Models/
    Bpm.cs                       BpmSource、BpmCandidate、BpmAnalysisResult、BpmAnalysisProgress、
                                 BpmCacheEntry、BpmDisplayState、BpmModelKind、BpmSettings
    Audio.cs                     AudioTagData + double? Bpm；TagEditModel + double? Bpm / bool BpmChanged
    AppSettings.cs               + BpmSettings（PreferredModel=Small0、WriteToFileByDefault=false、
                                   DecimalPlaces=1、UseGpuAcceleration=true、IdleUnloadSeconds=60、
                                   RoundId3BpmToInteger=false）
  Abstractions/
    IBpmAnalyzer.cs              IBpmAnalyzer（AlgorithmId、Version、AnalyzeAsync、UnloadRuntime）
    IBpmCache.cs                 IBpmCache（TryGetAsync、SaveMeasuredAsync、SaveManualAsync、
                                 ClearManualAsync、MovePathAsync、DeletePathsAsync、ClearMeasuredAsync、GetStatsAsync）
    IBpmService.cs               IBpmService（GetDisplayStateAsync、MeasureAsync、SaveManualAsync、
                                 WriteToFileAsync、ClearMeasuredCacheAsync、GetModelStatus）
  Services/
    MelSpectrogramMath.cs        纯 C# 128-bin Slaney Log-Mel 预处理（精确对齐 Beat This! 官方参数，可单测）
    BeatThisPostProcessor.cs     纯 C# 无 DBN 局部峰值拾取 + 抛物线亚帧精修 + 核密度/中位数 BPM 聚合 + 置信度/变速判断
    TapTempoCalculator.cs        纯 C# Tap 轻点测速（OLS 最小二乘回归、180 ms 防抖、2.5 s 超时重置、16 点滑窗）
    BpmResolver.cs               纯 C# 文件 Tag / 自动测量 / 手动修正合并优先级 + 智能失效判定
    BpmFormatter.cs              数值解析、范围校验（20.0–300.0）、默认 1 位小数展示、÷2/×2、各容器写回格式化

src/EchoGarage.Infrastructure/
  Configuration/
    AppPaths.cs                  + ModelsDirectory = <DataDirectory>\Models（用户可选模型目录，升级不丢）
  Audio/
    BpmModelLocator.cs           定位内置 small0 与用户可选 final0（data\Models 优先，tools\models 回退）
    OnnxBeatThisSessionHost.cs   懒加载 + DirectML GPU 优先 / CPU 回退 + 60 s 空闲自动 Dispose + 线程安全单飞
    OnnxBeatThisBpmAnalyzer.cs   SharedAudioFileReader 共享解码 → 22050 Hz 单声道重采样 → MelSpectrogramMath
                                 → 固定 1500 帧（30 s）重叠分块推理 → BeatThisPostProcessor
    BpmService.cs                缓存优先 + 单飞取消 + 协同 TagService 读写（含 ADR-020 原子替换）+ 日志
  Data/
    SqliteDatabase.cs            PRAGMA user_version = 5（CREATE TABLE track_bpm_cache）
    SqliteBpmCache.cs            Dapper 读写 track_bpm_cache、按路径迁移与清理
    SqliteLibraryRepository.cs   UpdateTrackPathAsync 事务同步更新 track_bpm_cache.Path
  Tagging/
    TagLibTagService.cs          跨格式 BPM 读写（直读 TBPM / Xiph BPM 保留小数；M4A 走 apple.BeatsPerMinute 整型；
                                 ASF WM/BeatsPerMinute；加入 Known* 去重）

src/EchoGarage.App/
  AppHost.cs                     唯一装配点：公开 IBpmService、IBpmCache；Shutdown 时释放 ONNX 会话
  ViewModels/
    MainViewModel.cs             NowPlayingBpm* 绑定、MeasureCurrentTrackBpmCommand、切歌取消与刷新、
                                 手动修正 / Tap / 写文件协调
  Views/
    MainWindow.xaml(.cs)         「正在播放」页「♩ BPM 测量」按钮、标签卡片 BPM 槽位、内联编辑/Tap 浮层 Popup
    MainWindow.NowPlaying.cs     BPM 按钮、Popup 与 Tap 敲击交互处理
    TagPropertiesWindow.xaml(.cs) 联动标准 BPM 编辑（修复 M4A tmpo 读写）并同步刷新正在播放页
    SettingsWindow.xaml(.cs)     「清空缓存 ▾」新增清理 BPM 自动测量缓存；BPM 设置块（含 final0 下载安装指引）
```

---

## 5. 数据库 Schema（v5）与缓存智能失效策略

### 5.1 SQLite Schema v5 迁移（`SqliteDatabase.cs`）

```sql
CREATE TABLE IF NOT EXISTS track_bpm_cache (
    Path              TEXT NOT NULL COLLATE NOCASE PRIMARY KEY,
    FileSize          INTEGER NOT NULL,
    ModifiedAtUtc     TEXT NOT NULL,
    DurationMs        INTEGER NOT NULL,        -- 区分“仅改其它标签”与“音频内容被替换”
    MeasuredBpm       REAL NULL,               -- 算法测量值（高精度 double）
    Confidence        REAL NULL,               -- 综合置信度 0.0..1.0
    IsVariableTempo   INTEGER NOT NULL DEFAULT 0,
    CandidatesJson    TEXT NULL,               -- 候选列表 JSON（含倍频参考）
    AnalyzerId        TEXT NULL,               -- 如 "beat-this-small0-onnx" / "beat-this-final0-onnx"
    AnalyzerVersion   INTEGER NOT NULL DEFAULT 0,
    ManualBpm         REAL NULL,               -- 手动修正且未写文件的值（受保护，不被自动清空）
    UpdatedAtUtc      TEXT NOT NULL
);
PRAGMA user_version = 5;
```

- 升级路径：仅追加新表，**不修改 `tracks` 表结构、不触发全库重扫**，旧库升级耗时 < 5 ms。
- 外部播放列表曲目同样以规范化路径 `Path` 存入本表，能力一致。
- 文件操作联动（ADR-021）：
  - 移动/重命名 → `UpdateTrackPathAsync` 同事务同步更新 `track_bpm_cache.Path`（先删目标旧行防冲突）；
  - 删除到回收站 → 同步 `DELETE FROM track_bpm_cache WHERE Path IN @deleted`。

### 5.2 来源合并优先级与智能失效规则（`BpmResolver`）

切歌或刷新「正在播放」页时，后台异步读取该文件标准 BPM Tag（`includeCover: false`，毫秒级）并查询 `track_bpm_cache`：

| 场景 | 展示值与来源标记 | 说明 |
| --- | --- | --- |
| 存在未落盘 `ManualBpm`，且外部未新写入不同的文件 Tag | 展示 `ManualBpm`（标记 `已修正`） | 保护用户手动修正；勾选写文件后清空 `ManualBpm` 转正为「文件标签」 |
| 文件带有标准 BPM Tag（且无较新的 `ManualBpm` 覆盖） | 展示 `FileTagBpm`（标记「文件标签」） | 若外部工具新写入了与旧 `ManualBpm` 不同的文件 Tag，以文件为先并在 Tooltip 提示 |
| 无文件 Tag、无手动修正，命中 `MeasuredBpm` | 展示 `MeasuredBpm`（标记 `缓存`） | 直接展示，零重算 |
| 以上皆无 | 展示 `—` | 等待点击「BPM 测量」或点击手动/Tap 输入 |

**防误失效策略（改封面/改标题绝不丢 BPM）**：

- `FileSize` 与 `ModifiedAtUtc` 相同 → 直接命中；
- `FileSize` / `ModifiedAtUtc` 变化，但 `|NewDurationMs - CachedDurationMs| <= 1000 ms` 且文件未新增不同的文件级 BPM Tag → 视作仅元信息变动，**保留原有 `MeasuredBpm` 与 `ManualBpm`** 并刷新戳记；
- `|NewDurationMs - CachedDurationMs| > 1000 ms` → 判定音频内容已替换：自动失效 `MeasuredBpm`；若存在 `ManualBpm` 则保留并在 Tooltip 标示「音频时长已变化，建议重测」。

---

## 6. ONNX Runtime DirectML 生命周期与零开销保障

```text
[未初始化 (0 MB RAM / 0 MB VRAM)]
        │  用户首次点击「♩ BPM 测量」
        ▼
[后台初始化 Session] ── DirectML GPU (device 0, static time=1500)
        │                  └─ 失败/无 DX12 → 自动回退 CPU EP + [WARN] 日志
        ▼
[推理执行中 (BelowNormal 线程, 30s 固定块, 单飞, 可取消)]
        │  每块汇报进度 + Task.Yield() 礼让播放与 UI 渲染
        ▼
[热保留等待期 (默认 60 秒)] ── 期间再次测歌零冷启动、零 Shader 重编译
        │  60 秒无新测量 / 用户设为 0 秒(立即释放) / 切换模型 / 程序退出
        ▼
[InferenceSession.Dispose() → 回到未初始化，释放全部显存与原生内存]
```

- 诊断日志清晰记录实际加载模型与执行后端（例如 `[INFO] BPM session initialized: model=beat_this_small0.onnx, ep=DirectML(device=0), loadMs=312` 或 `ep=CPU`）与单曲耗时/实时倍速。
- 若运行中 DirectML 偶发驱动异常（如 TDR / 设备重置），单次推理捕获后自动销毁旧会话并以 CPU EP 重试一次，保证测量不中断。

---

## 7. 「正在播放」页、Tap 测速与设置页交互设计

所有颜色均绑定现有五套主题令牌（ADR-024），不引入硬编码颜色。

### 7.1 「正在播放」页布局

```text
┌─────────────────────────────────────────────────────────────────────────┐
│ Demo Track 01                                [♩ BPM 测量] [✎ 标签属性]  │
│ Demo Artist · Demo Album A                                              │
│ [Electronic]  [2025]  [FLAC · 950 kbps / 44.1 kHz]  [输出 共享 · 48k ⓘ] │
├─────────────────────────────────────────────────────────────────────────┤
│ 专辑艺术家           作曲家                流派                           │
│ Demo Artist          Demo Composer         Electronic                   │
│                                                                         │
│ 年份                 音轨号                碟片号                         │
│ 2025                 01                    1                            │
│                                                                         │
│ 编码器               BPM                   备注                           │
│ FLAC                 128.0  缓存           Demo Note                    │
├─────────────────────────────────────────────────────────────────────────┤
│ D:\Music\Demo Album A\Demo Track 01.flac                                │
└─────────────────────────────────────────────────────────────────────────┘
```

- **「♩ BPM 测量」按钮**：
  - 空闲：`♩ BPM 测量`（无播放曲目时禁用）；
  - 测量中：`取消 (xx%)`（实时进度，再次点击取消；切歌自动取消旧任务，结果绝不串台）；
  - 完成：立即刷新 `BPM` 栏并状态栏提示（如 `BPM 测量完成：128.0（GPU · 0.6 s，已缓存）`）；若文件原本已有不同的文件级 BPM Tag，对比提示「文件原值 vs 实测值」，由用户决定是否覆盖。

### 7.2 Tag 栏 BPM 单元格与内联修正 / Tap 浮层（`Popup`）

点击标签卡片中的 `BPM` 数值区域（即使当前为 `—` 也可点），在下方弹出主题化 `Popup`：

```text
┌─────────────────────────────────────────────────────┐
│ BPM 修正（默认 1 位小数）                             │
│ [ 128.0          ]   [ ÷2 ]   [ ×2 ]                │
│                                                     │
│ [ ♩ 轻点测速 (Tap / 空格) ]  [重置]   已敲击 6 次 · 稳定│
│                                                     │
│ 候选参考：128.0  ·  64.0  ·  256.0                   │ ← 算法测量后显示，点选即填入
│                                                     │
│ [ ] 同时写入音频文件标签                              │ ← 默认不勾选（设置页可改默认）
│     提示：M4A (tmpo) 仅支持整数，将取整为 129 写入    │ ← 仅对 M4A/MP4 动态显示
│                                                     │
│ [恢复文件值 / 清除]                  [取消]  [保存]  │
└─────────────────────────────────────────────────────┘
```

- **Tap 轻点测速交互**：
  - 支持鼠标反复点击 `♩ 轻点测速` 按钮，也支持聚焦该按钮后按 **空格键 / `T` 键** 跟拍敲击；
  - Popup 处于 Tap 态时在 `PreviewKeyDown` 中消费 `Key.Space`，**阻止冒泡到主窗口快捷键（防止把歌暂停）**；
  - 从第 2 次敲击起通过 `TapTempoCalculator`（OLS 最小二乘回归）实时回填 BPM 输入框（默认 1 位小数）；停顿 > 2.5 秒自动开启新一轮，亦可手动点「重置」；
  - 敲完可继续微调或点 `÷2` / `×2`，确认后点「保存」。
- **写入歌曲文件级 Tag 闭环**：
  - 未勾选：仅写 `track_bpm_cache.ManualBpm`，原文件零修改；
  - 勾选：后台调用 `TagLibTagService.Write`（走 **ADR-020 共享句柄 + 临时副本 `File.Replace` 原子替换，播放完全不中断**）→ 重新索引 `tracks` → 清空 `ManualBpm` 并更新缓存戳记 → 刷新「正在播放」页；若格式不支持写标签（如原始 `.aac`）或文件只读，明确提示错误并保留本地缓存值。

### 7.3 设置页与「标签属性」窗口联动

- **设置页 → 「媒体库与缓存 → 清空缓存 ▾」**：新增 `清空 BPM 测量缓存（N 条，保留手动修正）`；`清空全部缓存` 确认提示中说明不删已写入文件的标签与未写文件的手动修正。
- **设置页 → BPM 测量设置块**：
  - `分析模型`：`Beat This! Small（内置，推荐）` / `Beat This! Final（高精度，需安装）`；
  - `高精度模型（final0）安装指引`：显示就绪状态、官方来源与获取指引说明、目标文件名（`beat_this_final0.onnx`）、目标路径（`<EchoGarageData>\data\Models\`），以及 **`打开模型目录`** 与 **`重新检测`** 按钮；
  - `启用 GPU 加速（DirectML，不可用时自动回退 CPU）`（默认开启）；
  - `推理会话空闲释放`（立即释放 / 60 秒[默认] / 300 秒）；
  - `修正 BPM 时默认同时写入音频文件标签`（默认关闭）；
  - `显示精度`（1 位小数[默认] / 整数）。
- **「标签属性」窗口（`TagPropertiesWindow`）**：
  - 纳入标准 BPM 读写（修复 M4A `tmpo` 读取与写入，排除 `AdditionalTags` 重复项），保存后自动同步刷新「正在播放」页 BPM 展示。

---

## 8. 测试与验收计划（实施阶段执行）

### 8.1 Core 单元测试

1. **`MelSpectrogramMathTests`**：与官方 Python `torchaudio` 在合成测试音频上的 Log-Mel 矩阵比对，断言最大绝对误差 < 1e-3。
2. **`BeatThisPostProcessorTests`**：喂入合成 50 fps 拍点 logits（60 / 92.5 / 120 / 128.0 / 140 / 174 BPM、含休止符、含变速段、全零静音），断言误差 < ±0.2 BPM 且正确识别变速与不可测边界。
3. **`TapTempoCalculatorTests`**：精确节拍（120.0 / 128.5 BPM）、带 ±15 ms 随机手部抖动（验证 OLS 精度优于首尾差）、<180 ms 防抖过滤、>2.5 s 自动超时重置、超过 16 次滚动窗口。
4. **`BpmResolverTests` & `BpmFormatterTests`**：覆盖 4 种来源优先级、改封面/标题（时长不变）保留缓存、音频替换（时长大变）失效自动测量、范围校验（20–300）、`÷2`/`×2`、1 位小数展示与 M4A 取整格式化。

### 8.2 Infrastructure 集成测试

1. **`SqliteBpmCacheTests`**：全新库建表 + 从 schema v4 旧库迁移至 v5；自动测量/手动修正读写；「清空自动测量缓存」保留 `ManualBpm`；`UpdateTrackPathAsync` 迁移路径与删除清理。
2. **`BpmModelLocatorTests`**：验证内置 `small0` 发现、`final0` 优先匹配 `data\Models\` 并回退 `tools\models\`、缺失 `final0` 时安全回退 `small0`。
3. **`TagLibBpmRoundTripTests`**：MP3（`TBPM` 小数与整数）、FLAC（`BPM` 小数与整数）、WAV（ID3v2 `TBPM`）、M4A（`tmpo` 二进制整数往返）；以及在 `SharedAudioFileReader` 打开播放期间同步执行 BPM 测量 + ADR-020 原子写 BPM 标签，验证播放不中断、位置连续、标签落盘。
4. **`OnnxBeatThisBpmAnalyzerTests`**：
   - 验证 DirectML GPU 与 CPU EP 对同一合成节拍 WAV 输出一致（误差 < ±0.3 BPM）；
   - 验证中途取消（`CancellationToken`）及时中止且无异常泄漏；
   - 验证未调用测量前不加载模型、空闲超时 `Dispose()` 后再次测量可正常重建。

### 8.3 真机性能与 UI 验收（遵循 ADR-012）

- 使用独立 `ECHOGARAGE_DATA_DIR` 副本 + 静音输出：
  - 启动耗时对比：确认冷启动 ~0.9 s **零退化**，未点测量时进程不加载 `onnxruntime.dll` / `DirectML.dll`、VRAM 增量 **0 MB**；
  - 播放中点击「BPM 测量」：验证音频零爆音/零卡顿、频谱可视化帧率稳定、GPU 耗时记录在日志；
  - 测量结束等待空闲卸载：通过任务管理器 / 性能计数器确认 VRAM 与原生工作集回落；
  - 五套主题 + 980×600 窗口检查按钮、标签卡片 BPM 槽位、修正/Tap Popup（含空格敲击不误暂停播放）、设置页 `final0` 指引卡片，以及 520 DIP 矮窗口隐藏回归检查；
  - 多流派真实曲目精度回归记录，所有文档记录脱敏为 `Demo Track 01..N`。

---

## 9. 风险与缓解

| 风险 | 影响 | 缓解措施 |
| --- | --- | --- |
| **特定显卡驱动与 DirectML 兼容异常** | 初始化失败、TDR 或算子报错 | 静态固定块长（1500 帧）避开动态形状坑；捕获初始化与推理异常自动无缝回退 CPU EP，并可在设置页手动关闭 GPU 加速 |
| **DX12 驱动页缓存未即时归还 OS** | `Session.Dispose()` 后部分驱动仍缓存少量显存页 | 实测监控工作集/专用显存；若某硬件下显存残留显著，借助 `IBpmAnalyzer` 解耦边界，二期可无缝切换为按需启停的子进程 runner，进程退出由 OS 100% 回收 |
| **Tap 敲空格与主窗口播放/暂停快捷键冲突** | 轻点测速时把正在播放的歌曲暂停 | Popup 处于 Tap 模式时显式在 `PreviewKeyDown` 中消费 `Key.Space`，且阻止路由冒泡到主窗口快捷键处理器 |
| **M4A `tmpo` 仅支持整数 vs 默认 1 位小数** | `128.5` 写入 M4A 变成 `129` | 浮层内对 M4A 动态显示提示；写回成功时按文件实际写入值刷新，避免缓存与文件展示不一致 |
| **用户自行放置的 `final0` 文件损坏或不匹配** | 加载模型失败 | 加载异常时写 `[WARN]`、明确提示文件无效，并自动回退使用内置 `small0`，不影响正常测量 |
| **首次点击测量存在冷启动（Shader 编译 ~0.3–1 s）** | 第一次点击略慢于后续测量 | 按钮显示「正在初始化引擎…」阶段提示；60 秒热保留保证连续测多首歌瞬间响应 |

---

## 10. 修订记录

### 2026-11-26 增补：变速曲目分段 BPM 识别、SQLite Schema v6 与进度条比例可视化（owner 批准）

1. **纯 C# 分段速度识别（`TempoSegmentAnalyzer`）**：
   - 不更换 ONNX 模型、不增加推理开销，直接复用 `BeatThisPostProcessor` 输出的 50 fps 亚帧拍点时间戳；
   - 按 `> 2.0 s` 无鼓空洞切分连续拍流、丢弃 `< 4` 间隔孤立杂拍、在 $\ln(\text{BPM})$ 空间做 5 拍滑动中值平滑；
   - 通过前缀和自适应惩罚精确动态规划（超长曲目自动微装箱至 ≤ 600 箱，耗时 `< 1 ms`）检出速度跃变点，对每段原始拍间隔复用 KDE + 10% 截尾均值精算精确 BPM；
   - 并入 `< 6.0 s` 短过渡段、迭代合并相邻差值 `< 3.0 BPM` 或 `< 3%` 的段落（消除静音间奏误报变速），过滤段内一致率 `< 40%` 的不稳定过渡段；
   - **段落数 `> 5` 保护（`BpmFormatter.MaxDisplaySegments = 5`）**：仅 1 段视为恒速；`2–5` 段保留各段起止时间、BPM、置信度并标记主段落；`> 5` 段或极端连续自由拍时置 `TooManyTempoChanges = true` 并清空明细，避免界面杂乱。
2. **SQLite Schema 迁移（v5 → v6）**：
   - `track_bpm_cache` 新增 `SegmentsJson TEXT NULL` 与 `DetectedSegmentCount INTEGER NOT NULL DEFAULT 0`（`0`=旧缓存未计算、`1`=恒速、`2–5`=有效分段、`> 5`=变速过多），`PRAGMA user_version = 6`；
   - 「清空 BPM 测量缓存」同步清空 `SegmentsJson` 与 `DetectedSegmentCount`，继续完整保留 `ManualBpm`；文件级标准标签仍仅写入单一主值。
3. **可视化与交互**：
   - 标签卡片在 BPM 旁展示轻量变速角标（`N段变速` / `变速过多`），悬停提示同步列出段落摘要；
   - BPM 内联浮层在 `2–5` 段时同时展示**进度条比例可视化**（按真实时长等比切分，全部使用现有五套主题令牌 `WaveformHighBrush` / `WaveformMidBrush` / `WaveformLowBrush` / `AccentPressedBrush` / `TextSecondaryBrush`，相近速度段复用同色；宽段内嵌自适应高对比度 BPM 文本；点击任意条块直接跳转该段试听）与**明细列表**（色点、时间范围、`BPM`、`主` / `倍速` / `半速` 标注、时长占比、当前播放段落高亮、`▶` 跳转试听与 `采用` 填入）；`> 5` 段时隐藏进度条与明细列表并展示提示文案。
