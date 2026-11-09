# ADR-002：分层架构与组合根

- 状态：待批准（Proposed）
- 日期：2026-11-09

## 背景

播放器会长期迭代（播放队列、DSP、更多标签字段、缓存策略等）。如果 UI 直接调用 NAudio / Sqlite / TagLib，
后续替换或测试会非常困难。

## 决策

代码分为三层，依赖只能向内：

```
EchoGarage.App (WPF, ViewModels, Views, Services)
        ↓
EchoGarage.Infrastructure (SQLite, TagLib, NAudio, FileSystemWatcher, Logging, Settings)
        ↓
EchoGarage.Core (Models, Abstractions, 纯业务规则)
```

- 所有跨层能力以 `Core/Abstractions` 接口定义（`ILibraryRepository`、`IPlaylistRepository`、`ITagService`、
  `ICoverCache`、`ILibraryScanner`、`IFileWatcherService`、`IAudioPlayer`、`ISettingsService`、`IAppLogger`）。
- `AppHost` 是唯一的组合根：手写装配、明确生命周期（Initialize / StartWarmup / Shutdown）。
- 不使用 DI 容器：依赖数量可控，手写装配能保证启动路径清晰、无反射开销。
- ViewModel 通过接口访问服务；对话框通过 `IDialogService` 抽象，便于测试时替换。

## 理由

- 编译期即可看出依赖方向；Core 项目不能引用 WPF/NAudio，避免业务逻辑被 UI 绑死。
- 手写组合根比容器更容易推理启动顺序与性能。
- 接口集中在一个文件（`Abstractions/Interfaces.cs`）便于快速浏览契约。

## 后果

- 新增能力需要同时改接口与实现，样板代码略多；这是刻意的取舍。
- 单元测试可以直接针对 Core 规则与 Infrastructure 实现（使用临时目录 / 临时数据库）。

## 备选方案

- **Microsoft.Extensions.DependencyInjection**：生态成熟，但启动期反射装配与“一个文件看清全部依赖”的目标冲突。
- **单一项目**：初期更快，但后续替换音频/标签/数据库时成本过高。
