# ADR-032：输出设备热插拔检测与“上次使用的设备”记忆

- 状态：已接受（Accepted，2026-11-22 owner 批准）
- 日期：2026-11-22

## 背景

当前输出设备列表只在启动和用户手动点“刷新”时枚举：

- 新增 / 启用设备（USB 声卡、蓝牙耳机、HDMI 显示器等）不会自动出现在下拉框里；
- 已移除 / 禁用的设备会留在列表里，选中后播放才报错；
- 播放中切换输出设备 / 共享 ↔ 独占模式只在下次 `Load` 时生效（ADR-009 §8），用户感知为“切了没反应”；
- `settings.json` 的 `outputDeviceId` 语义不完整：`ShutdownAsync` 会把当前下拉框里的设备写回设置
  （`MainViewModel.ShutdownAsync` 旧实现），一旦发生了“偏好设备不在 → 回退默认设备”，退出时就会把
  回退设备烧进设置，用户记住的设备永久丢失。

用户需求：新设备启用时自动识别（移除也要能识别）；记住上次使用的输出设备，设备不存在时回退到
系统默认输出设备。

## 决策

1. **新增 Core 抽象 `IAudioDeviceMonitor`**（`Core/Abstractions/Interfaces.cs`）：
   `event EventHandler<AudioDevicesChangedEventArgs>? DevicesChanged` + `Start()` + `Stop()` + `Dispose()`。
   事件参数携带最新设备快照、前后差异（新增 / 移除 / 重命名 / 默认设备变化）与“是否首份快照”标记。
2. **新增 `WasapiAudioDeviceMonitor`**（`Infrastructure/Audio/`）：
   - 专用 MTA 后台线程持有 `MMDeviceEnumerator`，注册 `IMMNotificationClient` 端点通知；
     通知客户端由字段强引用长期持有（NAudio 2.x 的 CCW 回收问题，见 NAudio issue #1021）；
   - 端点通知只唤醒线程，**去抖 250 ms** 后重新枚举 `Active` 的 Render 设备并与上一份快照比较，
     只有真正变化才触发事件（热插拔往往伴随一串事件）；
   - 每 10 s 兜底重扫，覆盖个别驱动不发端点通知 / 仅改属性不触发事件的情况；
   - 设备枚举复用 `WasapiOutputDeviceEnumerator`，与播放器的“默认设备”判定保持一致。
3. **记住设备 = 只存偏好，不存当前回退结果**：
   - `AppSettings.OutputDeviceId` 仍表示“用户上次显式选择的设备”，`null` = 跟随系统默认；
   - 只有用户在下拉框里**显式选择**设备时才写 `OutputDeviceId`；自动回退默认设备、
     `ShutdownAsync`、导入配置流程都不再改写它（导入时按导入值重新选择）；
   - 偏好设备缺失时，界面回退到系统默认设备并在 `OutputDescription` / 状态栏标注
     “上次使用的输出设备不可用，已回退到系统默认”（启动 / 刷新首次提示一次，避免重复打扰）；
     设备恢复后监视器事件会自动切回偏好设备。
4. **播放中切换立即生效**：`WasapiAudioPlayer.ConfigureOutput` 改为“配置 + 后台重建输出”：
   - 配置变化时在后台线程 `TeardownOutput` → 用新设备重建 → 从原位置恢复播放；
   - 暂停状态只释放输出、不立即重建（否则重建期间会出声且进度前移近一秒），
     恢复播放时再按新配置启动，进度停在暂停点；
   - 设备被拔出导致输出报错时，播放器记录“待恢复”；监视器事件触发重建后用新的默认设备继续播放；
   - 重建在后台串行执行，不阻塞 UI 线程（WASAPI 初始化 / 格式协商可能耗时数百毫秒）。
5. **纯逻辑与 WASAPI 解耦**：`OutputDeviceSelector`（偏好 → 系统默认 → 第一个）与
   `AudioDeviceChangeDetector`（快照 diff）放在 `Core/Services/`，用单元测试覆盖选择与差异规则。
6. **设置规范化**：`JsonSettingsService.Normalize` 把空白 `OutputDeviceId` 归一为 `null`（跟随默认），
   非法 `OutputMode` 回退 `Shared`；不新增设置字段，不提升 `SchemaVersion`。

## 理由

- `IMMNotificationClient` 是 Windows 官方热插拔通知通道，比轮询及时；但通知不可靠（驱动差异、
  蓝牙设备状态抖动），所以必须保留低频兜底轮询，二者结合才能满足“新增能识别、移除也能识别”。
- 去抖 + 快照 diff 避免一次插拔触发多次 UI 刷新，也避免没有实质变化时打断播放。
- 把“偏好设备”与“当前实际设备”分开，是实现“记住上次设备 + 缺失回退默认 + 恢复后自动切回”的前提；
  若继续在退出时写回当前设备，回退状态会覆盖用户偏好，需求无法满足。
- 后台重建而非下次播放生效：用户切换设备时通常正在播放，等待下次 Load 的体验不可接受；
  重建链路与 ADR-009 的跳转实现一致（停止 → 重建 → 定位恢复），风险可控。
- 纯逻辑放 Core 可用 xUnit 覆盖，符合仓库“业务规则必须有单元测试”的约定。

## 后果

- 修改 ADR-009 §8 的最后一句：输出设备 / 模式切换不再需要等到下次播放，播放中也会立即重建输出；
  重建瞬间可能有 < 1 s 的短暂中断（取决于设备初始化速度）。
- 新增一个后台线程与端点通知注册；应用退出时必须反注册并等待线程结束（`Shutdown` 里先于播放器
  `Dispose`）。启动关键路径不受影响（监视器在 `InitializeAsync` 里只启动线程，首次枚举在后台线程完成）。
- NAudio 的通知客户端必须长期强引用，后续重构不得把 `_notificationClient` 改成局部变量。
- 设备变化会在状态栏播报（新增 / 断开 / 默认切换 / 恢复偏好设备），属于用户可见行为变化。
- 无媒体库 / 数据库变更，不涉及 `PRAGMA user_version`。

## 备选方案

- **只用定时轮询**：实现简单，但最坏要等一个轮询周期（例如 3–10 s）才更新列表，插拔反馈慢，
  且播放中长时间对着已消失的设备。
- **只用 `IMMNotificationClient`**：部分驱动 / 虚拟声卡不发通知，或蓝牙设备在连接过程中状态抖动，
  会出现列表长期不更新；故保留 10 s 兜底。
- **重启应用才生效**：不满足“自动识别”的需求。
- **监听 WMI `Win32_SoundDevice`**：事件频率高、需要额外查询，且拿不到 WASAPI 端点 ID 与默认设备
  角色信息，映射到端点反而更复杂。

## 手动验证步骤（无真实插拔硬件时）

1. 启动发布版（`ECHOGARAGE_DATA_DIR` 指向独立目录），日志应出现
   `Audio device monitor started; initial output devices: ...`。
2. 在 Windows 设置里切换默认输出设备（或启用 / 禁用任意输出设备），日志应出现
   `Audio output devices changed: ...`，界面下拉框与状态栏随动；播放中切换时音频应从原位置继续。
3. 在 `settings.json` 写入一个不存在的 `outputDeviceId` 后启动：应回退到系统默认设备，界面显示
   回退提示，且退出后 `settings.json` 里的 `outputDeviceId` **仍保持原值**。
4. 恢复（重新启用）原偏好设备：应自动切回并播报“已恢复上次使用的输出设备”。
5. 运行 `dotnet build tests/EchoGarage.Tests` + `EchoGarage.Tests.exe`，`OutputDeviceSelectorTests`
   与 `AudioDeviceChangeDetectorTests` 覆盖选择 / 回退 / 增删 / 重命名 / 默认切换规则。
