# ADR-019：标签编辑页重设计、额外标签展示、封面 JPG 转换与多文件批量编辑

- 状态：已批准（Accepted，2026-11-14，owner；2026-11-15 修订：其他标签改为可编辑，owner 批准）
- 日期：2026-11-14
- 修订：2026-11-15
- 相关：ADR-003（文件是唯一真相）、ADR-013（作曲家标签 / 列表次要信息）、ADR-018（标签缓存刷新）

## 背景

用户反馈三处体验问题：

1. 列表 `#` 列只显示音轨号（两位数字），文件带碟片号时无法区分不同碟；没有音轨号的文件会回退显示行号，看起来像“错误的音轨号”。
2. 标签编辑页是单列 10 行表单，“年份 / 音轨”挤在同一行；大量文件自带的额外标签（BPM、ISRC、发行方、编码器、MusicBrainz、ReplayGain 等）
   无法查看；底部还占用了“文件信息”区域（路径、编码、修改时间），信息密度低。
3. 封面区只显示预览与“建议使用正方形的 JPG/PNG 图片”提示，看不到封面格式 / 分辨率 / 体积；也无法在保存标签时把封面转成 JPG 或限制尺寸，
   而内嵌封面往往是文件体积的大头。
4. 封面转换默认关闭；选择图片总是从上次目录开始，在歌曲目录里找封面需要手动翻找；多选文件后无法一次批量编辑标签。

## 决策

### 1. `#` 列显示“碟片号.音轨号”

- 新增 `TrackIndexFormatter.Format(discNumber, trackNumber)`（Core 纯函数）：
  - `trackNumber <= 0` → 空字符串（不再回退行号）；
  - `discNumber > 0` → `"{disc}.{track:00}"`（如 `1.01`、`2.03`）；
  - 否则 `trackNumber.ToString("00")`。
- `TrackRowViewModel.IndexDisplay` 改用该函数；行号变化不再影响 `#` 列。
- `ColumnCatalog` 的 `index` 列默认宽度 40 → 56、最小宽度 30 → 50，避免 `1.01` 被裁切（旧布局加载时按最小宽度归一化）。

### 2. 标签编辑页改为 2 列 × N 行表格 + 其他标签（可编辑）

- 左侧改为两列表格（标签列 Auto / 输入列 `*`）：标题 / 艺术家、专辑 / 专辑艺术家、作曲家 / 流派、年份 / 音轨号、碟片号 / 备注；
  年份、音轨号、碟片号各占一行。
- 删除“文件信息”区域（路径、编码 / 码率 / 声道、修改时间），窗口从 760×560 调整为 940×640。
- 新增“其他标签”编辑区（2026-11-15 修订，owner 批准）：`AudioTagData` 增加
  `IReadOnlyList<AudioTagEntry> AdditionalTags`（非数据库字段，仅读取时填充）。`AudioTagEntry` 除 `Name` / `Value`
  外增加 `Kind` / `Key` 作为写回标识，`Kind` 取值见 `AdditionalTagKinds`。
  `TagLibTagService.Read` 按具体标签类型读取并过滤已在上方标准字段中展示的项：
  - ID3v2：`GetFrames()`（TextInformationFrame / UserTextInformationFrame），跳过 TIT2/TPE1/TPE2/TALB/TCOM/TCON/TYER/TDRC/TRCK/TPOS/COMM/APIC 等标准帧；
    标准文本帧以帧 ID 为 Key，TXXX 以 Description 为 Key；
  - Xiph（FLAC/OGG）：枚举字段名 + `GetField`，跳过 TITLE/ARTIST/ALBUM 等标准字段，以字段名为 Key；
  - Apple（M4A）：只枚举顶层 atom，以 atom 名（Latin-1）为 Key，跳过 ©nam/©ART/©alb/aART/©gen/©day/trkn/disk/covr 等；
    `----` 自由格式读取 `mean` / `name` / `data`，Key 为 `mean\u001fname`。不再递归子 `data` atom——旧实现把子 atom 的
    `data` 当作标签名，导致“年份 2022”被显示成一条名为 `data` 的额外标签；
  - ASF（WMA）：`GetDescriptors`，跳过标准字段，以描述符名称为 Key。
  - 统一按 `Kind + Key` 去重、trim、超过 400 字符截断；读取失败只记 WARN，不影响标签编辑。
- 写回：`TagEditModel.AdditionalTagChanges` 携带 `AudioTagChange(Kind, Key, Value)`，`Value` 为空表示删除。
  `TagBatchEdit.AdditionalTagChanges` 由 UI 只收集被改动的项，`TagBatchEditor.CreateEdit` 原样带入每个文件的写回模型。
  `TagLibTagService` 仅在文件已有对应标签块时应用改动（混合格式批量编辑不会给不支持的类型凭空创建标签块）：
  - ID3v2 文本帧用 `TextInformationFrame.Get(...)`，TXXX 用 `UserTextInformationFrame.Get(...)`，删除用 `RemoveFrame`；
  - Xiph 用 `SetField` / `RemoveField`；
  - Apple atom 用 `SetText` / `ClearData`，自由格式用 `SetDashBox(mean, name, value)`（空值删除；不使用空数组的 `SetDashBoxes`）。
- 编辑范围（owner 批准）：只修改已有项的值或删除；不支持新增自定义 key、重命名 key，也不把额外标签写入数据库。
- 注意：`TagLib.File.Tag` 在 MP3 上可能是 `NonContainer.Tag`（ID3v2 + ID3v1 的包装），因此必须用 `file.GetTag(TagTypes.Id3v2)`
  等具体类型取标签，不能直接对 `file.Tag` 做类型分支。

### 3. 封面信息与 JPG / 尺寸转换

- 右侧封面区显示 `格式 · 宽×高 · 体积`（如 `JPEG · 1200×900 · 27.5 KB`），移除“建议使用正方形的 JPG/PNG 图片”提示。
- 新增“封面转换”选项（默认开启）：勾选后保存标签时把当前封面转成 JPG，可设置 JPG 质量（1–100，默认 90）与最大边长（默认 1000，0 = 不限制）。
- 纯逻辑放 Core（可单测）：
  - `CoverImageSizing.ScaleDown(width, height, maxEdge)`：不放大、保持宽高比、按整数像素尽量逼近目标边长（`Math.Round(..., AwayFromZero)`）；
  - `CoverConversionPolicy.ShouldConvert(sourceIsJpeg, sourceWidth, sourceHeight, targetMaxEdge, originalSize, convertedSize)`：
    - 编码失败（convertedSize <= 0）→ 不采用；
    - 需要缩放（原图最长边 > 目标边长）→ 采用转换结果；
    - 不需要缩放时：已是 JPG 且原图小于目标边长 → 跳过；否则仅当转换后体积更小才采用。
- App 层 `CoverImageProcessor` 负责解码（WPF `BitmapDecoder`）、缩放（`TransformedBitmap` + 高质量插值）与 JPG 编码（`JpegBitmapEncoder`）。
- 转换只在用户保存标签时执行，不修改原文件封面缓存；保存后仍走现有的重新索引 / 封面缓存失效 / 列表刷新流程。

### 4. 多文件批量编辑

- 标签编辑页 ctor 改为接收 `IReadOnlyList<Track>` 与 `IReadOnlyList<AudioTagData>`；多选时标题为“标签属性 - N 个文件”，
  保存按钮为“保存到 N 个文件”，顶部提示“只保存你修改过的字段，未修改的字段保持每个文件自己的原值”。
- 每个字段比较所有选中文件的当前值：一致时显示该值；不一致时显示 `<多值>`，右侧出现下拉按钮（▾）列出所有不同值，
  选择后填入输入框，另有“保持不变（多值）”项可还原。语义参考 Mp3tag 的 `<keep>`：不一致字段默认保留各文件原值，
  只有用户显式输入或选择的值才写入。
- 保存结果改为 `TagBatchEdit`：`Fields` 只包含被修改字段，`CoverChanged` / `NewCoverBytes` / `NewCoverMime` / `RemoveCover`
  描述封面改动，`AdditionalTagChanges` 只包含被修改或删除的其他标签；Core 纯函数
  `TagBatchEditor.CreateEdit(AudioTagData source, TagBatchEdit edit)` 以每个文件自己的当前标签为底稿，
  只覆盖被修改字段（未修改字段不会因批量编辑被清空）。
- `MainViewModel.EditTagsAsync` 改为取选中行集合：逐文件读取标签 → 只打开一次对话框 → 逐文件写回、重新索引、
  失效并重载封面、刷新列表；单个文件失败不影响其它文件，结束时汇总成功 / 失败数量。
- 封面与其他标签：多选时预览与信息取第一个文件；封面改动（选择 / 快速匹配 / 移除 / 转换）与其他标签改动统一应用到所有选中文件；
  未改动时保留每个文件自己的封面 / 其他标签。

### 5. 封面默认转换与快速匹配

- “保存时转换为 JPG”默认勾选（质量 90、最大边长 1000），仍可取消。
- “选择图片…”默认打开歌曲所在目录。
- 新增“快速匹配专辑封面”按钮：Core `AlbumCoverMatcher.FindImages` / `MatchCover`（可单测）：
  - 过滤常见图片扩展名（jpg/jpeg/png/bmp/webp/gif/tif/tiff）并按文件名排序；
  - 只有一个图片时直接使用；
  - 多个时优先使用文件名（去扩展名、忽略大小写）为 `cover` 的图片，其次 `folder`；
  - 多个图片但没有 cover / folder 时提示手动选择；目录中没有图片时提示。

## 备选方案与取舍

- `#` 列回退行号：保留现状最简单，但用户明确要求“没有音轨号时留空”，且碟片号信息有实际意义。
- 用 TagLib 基类属性（BPM/Conductor/MusicBrainz…）展示额外标签：只能覆盖常见字段，无法显示 TXXX / 自定义 Xiph / Apple 自由字段；
  选择直接枚举原始标签项，覆盖面更广，代价是需要按格式分别处理。
- 在标签编辑页直接调整封面并即时预览转换结果：交互更直观，但会增加解码与预览复杂度；当前选择“保存时转换”，
  保持编辑页轻量，规则也能被单测覆盖。
- 用 `System.Drawing` 处理封面：Windows-only 且引入 GDI+ 依赖；WPF 自带解码/编码器已足够。

## 影响

- 列表 `#` 列语义变化：没有音轨号的文件显示为空；`index` 列默认 / 最小宽度加宽（旧布局会自动归一化到最小宽度）。
- `AudioTagData` 新增只读属性，不影响 SQLite schema 与扫描缓存；`AudioTagEntry` 新增 `Kind` / `Key`，
  `TagEditModel` 新增 `AdditionalTagChanges`。
- 标签编辑页信息密度提高，删除文件信息区（路径仍可在资源管理器中查看，修改时间不是编辑所需信息）。
- 封面转换可显著减小内嵌封面体积；规则只缩小不放大，避免画质/体积反向变差。
- 多选批量编辑只写入被修改字段，未修改字段保持每个文件原值；封面与其他标签改动统一应用，避免批量保存误伤未改字段或封面。
- 新增 `TrackIndexFormatter`、`CoverImageSizing`、`CoverConversionPolicy`、`CoverImageProcessor`、`AlbumCoverMatcher`、
  `TagBatchEditor` / `TagBatchEdit` / `AudioTagChange` 及对应单测；`IDialogService.EditTags` 签名改为批量版本。

## 验证

- 单元测试：`TrackIndexFormatterTests`（4）、`CoverImageTests`（尺寸 / 比例 / 无效输入 / 无限制 / 7 个 Policy 用例）、
  `AlbumCoverMatcherTests`（6）、`TagBatchEditTests`（8，含额外标签改动传递与 Apple 自由标签键往返），
  `TagAndCoverTests.BatchEdit_WritesOnlyModifiedFieldsAndKeepsPerFileValues`（两个文件批量改专辑 / 备注，标题与艺术家各自保留）
  以及 `TagAndCoverTests.AdditionalTags_RoundTripEditAndRemove`（WAV / ID3v2 额外标签改值、删除、重新读取）；
  构建 0 警告 0 错误，全部 326 项测试通过。
- 真实文件验证：M4A（Apple atom）不再出现 `data` 行，只显示顶层 atom 与 `----` 自由标签；改值 `©pub`、改 / 删
  `----` 自由标签后重新读取正确。FLAC（Xiph）的 `TOTALTRACKS` / `TOTALDISCS` 归一化为 `TRACKTOTAL` / `DISCTOTAL`，
  改值 / 删除后不产生重复行。
- 真实文件截图：带封面的 MP3（1200×900 JPEG、ID3v2.3，含 BPM/ISRC/发行方/编码器标签）打开标签编辑页，
  确认 2 列表格、额外标签区、封面信息与转换选项；多选 3 个文件时 `dialog-tags-multi.png` 显示 `<多值>` 与下拉按钮、
  顶部批量编辑提示、“保存到 3 个文件”；`#` 列显示 `1.01`–`1.05`。
- 临时 WPF harness 验证：多选只提交被修改字段（`MULTI_FIELDS=album,title`）、未改字段保留、PNG 2000×1800 → JPEG 1000×900；
  单文件无改动时字段集合为空、默认转换生效；快速匹配选中目录内唯一 `cover.jpg` 且小尺寸 JPG 按策略保持原字节。
- 已知限制：其他标签只展示第一个选中文件的项，且只支持改值 / 删除，不支持新增或重命名；跨格式批量编辑时，
  对不存在对应标签块的文件跳过该项；封面转换只在保存标签时执行；`#` 列在超宽碟片 / 音轨号（如 `10.100`）下仍可能被裁切，需要用户手动加宽列。
