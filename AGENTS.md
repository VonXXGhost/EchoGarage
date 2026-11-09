# AGENTS.md — EchoGarage 协作规范

本文件面向在此仓库工作的自动化 agent（以及未来的贡献者）。目标是：改动可控、决策可追溯、问题可定位。

## 1. 权威文档与决策流程

- `docs/adr/`：架构决策记录（ADR），**只有 owner 可以批准写入**。
  Agent 不得直接新增 / 修改 / 删除 ADR 文件，除非 owner 在对话中明确同意。
  提议流程：把提案写到 `docs/proposals/ADR-xxx-*.md` → 向 owner 说明变更点与理由 → owner 同意后再移动到 `docs/adr/` 并更新 `docs/adr/ADR-index.md`。
- `docs/kb/`：长期知识库（KB），同样**先提案、经 owner 同意后写入**。提案放 `docs/proposals/KB-xxx-*.md`。
- 当 owner 的最新指令与已生效 ADR 冲突时：先指出冲突条目，询问 owner 如何裁决（改代码 / 改 ADR），不要默默违背。
- 已生效的 ADR 默认必须遵循。

## 2. 项目地图

```
src/EchoGarage.Core/
  Models/          领域模型：Track、Playlist、AppSettings、Audio 相关 DTO
  Services/        ColumnCatalog、ViewLayoutFactory、SmartQueryBuilder、PathUtil、CoverKeyFactory
  Abstractions/    所有跨层接口（仓储、扫描、播放、日志、设置、封面缓存…）

src/EchoGarage.Infrastructure/
  Data/            SqliteDatabase（建表/迁移）、Library/Playlist 仓储（Dapper）
  Library/         LibraryScanner（增量扫描）、LibraryFileWatcher（FileSystemWatcher）
  Tagging/         TagLibTagService（读写音频文件标签）
  Caching/         FileCoverCache（封面文件缓存 + 负缓存标记）
  Audio/           WasapiAudioPlayer（共享 / 独占、重采样、跳转）
  Configuration/   AppPaths（目录与环境变量）、JsonSettingsService
  Logging/         FileLogger

src/EchoGarage.App/
  App.xaml(.cs)    组合根入口、单实例、全局异常、启动计时
  AppHost.cs       手写依赖装配；生命周期与预热
  Services/        对话框服务、封面图片解码缓存、文件关联、单实例、格式化、转换器
  ViewModels/      MainViewModel / TrackRowViewModel / SourceNodeViewModel
  Views/           MainWindow 与各对话框（列布局、智能播放列表、标签属性、设置…）
  Resources/       主题与控件样式（Theme.xaml / Controls.xaml）

tests/EchoGarage.Tests/   xUnit v3，覆盖布局归一化、智能查询、路径、仓储、扫描、标签、缓存、设置
```

## 3. 构建 / 测试 / 运行

```powershell
# 构建
dotnet build EchoGarage.sln

# 测试（xUnit v3 进程内运行器；不要用 dotnet test 依赖 VSTest 宿主）
dotnet build tests/EchoGarage.Tests
.\tests\EchoGarage.Tests\bin\Debug\net10.0-windows\EchoGarage.Tests.exe

# 运行
dotnet run --project src/EchoGarage.App

# 发布（框架依赖）
.\scripts\publish.ps1
```

在受限 / 沙箱环境（例如 DSH 的 workspace-write）中，如果出现 `dotnet build` 无错误但失败、或编译器服务器连接失败：

```powershell
dotnet build EchoGarage.sln -nodeReuse:false -p:UseSharedCompilation=false
```

## 4. 编码约定

- 目标框架：`net10.0`（Core）/ `net10.0-windows`（Infrastructure、App、Tests）。启用 nullable 与隐式 using。
- 分层依赖只能向内：`App → Infrastructure → Core`。Core 不引用 WPF / NAudio / TagLib / SQLite。
- 所有跨层能力通过 `Core/Abstractions` 的接口暴露；AppHost 是唯一的装配点。
- UI 绑定使用 CommunityToolkit.Mvvm 的 `[ObservableProperty]` / `[RelayCommand]`；不用第三方 MVVM 框架。
- 异步代码：IO / 解码 / 扫描不得阻塞 UI 线程；后台线程回调 UI 必须经 `Dispatcher`。
- 不吞异常：用户可见错误要 `SetStatus(...)` 并 `Logger.Error(...)`；后台可预期失败至少 `Warn`/`Debug`。
- 中文 UI 文案统一放 XAML / 视图模型里，代码注释用中文或英文均可，但同一文件内保持一致。

## 5. 数据与缓存规则

- **文件是唯一真相**：标签、时长、封面都来自文件本身；数据库只是缓存。任何写入数据库的字段都必须能从文件重新推导（播放计数 / 最近播放 / 评分除外，它们是软件状态）。
- 媒体库扫描的增量判断依据：`FileSize` + `ModifiedAtUtc`。修改这两个字段的处理逻辑时必须同时更新扫描器与文件监听。
- 封面缓存键：有专辑时按“专辑艺术家 + 专辑”，否则按文件路径；未找到封面写 `.none` 标记，标记内容包含源文件的修改戳。
- 修改文件标签后必须：重新索引该文件 → 失效旧 / 新封面的内存缓存 → 刷新当前列表与正在播放面板。
- 配置（`settings.json`）与媒体库（`library.db`）必须保持分离；导出配置不得包含媒体库内容。

## 6. 性能红线

- 启动：主窗口应在 ~1 秒内出现（当前约 0.9 s）。不要在启动路径上做全库扫描、设备枚举、封面解码；这些放到窗口显示后或后台预热。
- 播放列表加载：切换播放列表应在几十毫秒内完成；不要把封面解码放在列表加载里，按需加载可见行。
- 扫描：未变化的文件不能重复读标签；数据库写入要批量事务。
- 任何新增的“每次启动都执行”的同步 IO 都需要在 PR / 提交说明中解释。

## 7. 测试要求

- 业务规则（智能查询、布局归一化、路径判断、扫描增量、标签读写、设置归一化）必须有单元测试。
- 修改 SQLite schema 时必须：新增 `PRAGMA user_version` 迁移分支 + 测试覆盖新旧两种库。
- 音频设备 / 独占模式 / 全局快捷键等依赖真实环境的行为，至少保留日志与手动验证步骤。

## 8. 诊断与日志

- 日志目录：`%LOCALAPPDATA%\EchoGarage\Logs\echogarage-YYYYMMDD.log`（可用 `ECHOGARAGE_DATA_DIR` 重定向）。
- 日志级别：`[INFO]` 生命周期与关键操作、`[WARN]` 可恢复问题、`[ERROR]` 需要用户/开发介入、`[DEBUG]` 高频细节。
- 内置诊断开关：`--screenshot <png>`、`--screenshot-all <dir>`（用于高 DPI 下检查 UI）。
- 用户反馈问题时，优先索取：日志文件、`settings.json`、问题歌曲的格式与时长。

## 9. 提交与文档

- 提交信息用中文或英文皆可，需说明“改了什么 / 为什么”。
- 引入新依赖前：确认是否真的需要、许可证、是否影响启动时间与安装体积，并记录到 ADR 提案。
- 改动架构、数据模型、缓存策略、默认播放器注册方式时，必须同步准备 ADR 提案，等待 owner 批准。
