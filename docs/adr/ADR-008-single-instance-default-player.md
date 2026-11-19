# ADR-008：单实例与默认播放器行为

- 状态：已批准（Accepted，2026-11-09，owner）
- 日期：2026-11-09

## 背景

用户希望把 EchoGarage 注册为本地默认音乐播放器：从资源管理器一次打开一个或多个文件时，
自动用这些文件覆盖默认播放列表并播放，而不是每次启动一个新进程。

## 决策

1. **单实例**：命名 `Mutex`（`Local\EchoGarage.SingleInstance`）+ 命名管道。
   - 第二个进程若发现已有实例，把音频文件路径写入管道后退出。
   - 主实例在后台线程接收路径，`Dispatcher` 切回 UI 线程处理。
2. **外部打开语义**：`HandleExternalFiles(paths)`：
   - 过滤支持的扩展名；
   - 媒体库目录内的文件正常入库，目录外的文件只作为播放列表外部条目；
   - 清空默认播放列表 → 写入新条目 → 切到默认播放列表 → 从第一首开始播放。
3. **注册为默认播放器（可选手动操作）**：
   - 启动时**绝不自动注册**，也不修改任何文件的默认处理程序；只有用户在界面里明确点击并确认才写入。
   - 注册对话框列出支持的扩展名（mp3、flac、wav、m4a、aac、aiff、wma、mp4），用户勾选需要关联的格式；
     选择结果保存在配置中，下次打开对话框时恢复勾选。
   - 只写 `HKCU`，按勾选的扩展名分别写入：
     `Software\Classes\{ProgId}`（ProgId 描述、`DefaultIcon`、`shell\open\command`）、
     `Software\Classes\{扩展名}\OpenWithProgids`、
     `Software\Classes\Applications\EchoGarage.exe`（`FriendlyAppName` / `shell\open\command` / `SupportedTypes`）、
     `Software\{ProgId}\Capabilities`（`ApplicationName` / `ApplicationDescription` / `FileAssociations`）
     与 `Software\RegisteredApplications`。
   - 重复注册时以本次勾选为准：取消勾选的格式会移除其 `OpenWithProgids` 与 `Capabilities` 条目。
   - **不覆盖**扩展名当前的默认处理程序（`UserChoice` 由系统保护，直接写默认值无效，也会被视为劫持）；
     只提供“打开方式”候选，注册后提示用户到 `ms-settings:defaultapps` 确认。
   - 提供“取消注册”，清理上述 ProgId、Applications、`OpenWithProgids`、Capabilities 与
     `RegisteredApplications` 值。
4. 单实例失败（互斥体 / 管道不可用）时**优先保证能启动**，不阻塞用户使用。

## 理由

- 覆盖默认播放列表符合“双击文件即播放”的直觉，也不会无限堆积临时列表。
- 只写 HKCU，无需管理员权限，卸载时容易清理。
- Windows 不允许静默篡改默认应用，明确提示用户是唯一合规做法。
- 注册可选、按格式勾选，避免软件“抢”用户不想关联的格式；也满足 owner 对可选项的要求。

## 后果

- 同一个文件被再次打开会重置默认播放列表（用户在弹出的第二个窗口里继续追加需要手动操作）。
- 管道不可用的环境（某些沙箱）会退化成“启动新实例”，不影响正常桌面使用。

## 备选方案

- 每次新开窗口：任务栏混乱，播放状态分散。
- 用 `WM_COPYDATA`：跨进程窗口查找不如命名管道直观，且受 UIPI 限制。
