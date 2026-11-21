# ADR-030：文件关联注册的容错与 Windows 11 候选来源（修订 ADR-008 §3）

- 状态：已批准（Accepted，2026-11-21，owner）
- 日期：2026-11-20
- 相关：[ADR-008](ADR-008-single-instance-default-player.md)（单实例与默认播放器行为，本提案修订其第 3 条「注册为默认播放器」的实现细节与结果语义）、KB-002（受限开发环境下的构建与测试）
- 涉及代码：`src/EchoGarage.Infrastructure/Windows/FileAssociationService.cs`、`FileAssociationResult.cs`、`IRegistryStore.cs`、`src/EchoGarage.App/Views/FileAssociationWindow.xaml(.cs)`、`src/EchoGarage.App/Views/SettingsWindow.xaml.cs`、`src/EchoGarage.App/AppHost.cs`、`tests/EchoGarage.Tests/FileAssociationTests.cs`

## 1. 背景与问题

owner 反馈（2026-11-20）：「注册格式功能无法使用」。在「文件关联」对话框勾选 FLAC / WAV / M4A / AAC 后点「注册选中格式」，界面显示「注册失败：无法写入注册表，或程序路径不可用。」，状态行仍停留在「当前状态：尚未注册」，看起来功能完全不可用。

实际现象不是「完全写不进去」，而是：

1. 注册在**第一个选中格式的第一条失败**处就中止（旧实现 `foreach (… ) if (!SetValue(…)) return false;`），于是 ProgId / Applications / RegisteredApplications 已写入，而所有格式的 `OpenWithProgids` 与 `Capabilities\FileAssociations` 均未写；
2. 界面只在成功分支刷新状态，失败分支保持旧文案，用户无法知道到底哪一条写失败、哪些格式其实已部分可用；
3. `IRegistryStore` 把注册表异常全部吞掉只返回 `false`，日志里没有任何线索（UAT 日志中确实找不到任何关联注册记录）。

## 2. 调查结论（本机实测）

### 2.1 根因：部分扩展名键被 Windows / 安全软件设为只读

owner 机器（Windows 11 25H2）的 `HKCU\Software\Classes` 下，**只有 `.flac` / `.mkv` / `.mp3` / `.mp4` 四个扩展名键是受保护、只读的**，其余 527 个扩展名键（含 `.wav` / `.m4a` / `.aac` / `.aiff` / `.wma`）正常可写。只读键的 SDDL 形如：

```
O:<用户 SID> G:... D:PAI(A;CI;KR;;;WD)(A;CI;KR;;;SY)(A;CI;KR;;;BA)
```

即只给 Everyone / SYSTEM / Administrators 容器继承的 `KEY_READ`，没有本用户的写 ACE。实测：

- `Set-ItemProperty HKCU:\Software\Classes\.flac\OpenWithProgids` → `System.Security.SecurityException: Requested registry access is not allowed`
- `Registry.CurrentUser.CreateSubKey(@"Software\Classes\.flac\OpenWithProgids", true)` → `Access to the registry key 'HKEY_CURRENT_USER\Software\Classes\.flac\OpenWithProgids' is denied.`

旧实现按 `.flac`（勾选顺序第一个）→ `SupportedTypes` 成功 → `OpenWithProgids` 失败 → 中止。这与注册表中残留的半成功状态完全吻合（已有 ProgId 打开命令、Applications 条目、RegisteredApplications，`SupportedTypes` 仅 `.flac`，`Capabilities\FileAssociations` 为空，`IsRegistered()` 因 ProgId 命令存在而返回 true）。

### 2.2 只写 Capabilities + SupportedTypes 已足够被 Windows 认可

用 `SHAssocEnumHandlers(ext, ASSOC_FILTER_RECOMMENDED)`（Windows 11「默认应用 / 打开方式」推荐列表的真实来源）做实验：

- 只补写 `HKCU\Software\{ProgId}\Capabilities\FileAssociations` 与 `HKCU\Software\Classes\Applications\<exe>\SupportedTypes`（两者都可写），调用 `SHChangeNotify(SHCNE_ASSOCCHANGED)` 后，`.flac`（只读键）与 `.wav` 的推荐列表里都出现了 EchoGarage；
- 对照：未写这两个位置时 `.flac` 推荐列表为「媒体播放器 / 旧版 WMP / mpv / foobar2000 / Spek」，没有 EchoGarage；
- 对照：从未注册过的扩展名（`.egtest`）推荐列表为空，证明该 API 有鉴别力。

结论：**不需要修改只读键的 ACL，也不需要写 `HKCU\Software\Classes\{ext}\OpenWithProgids`，EchoGarage 就能成为这些格式在系统「默认应用」中的候选程序**。`OpenWithProgids` 只影响旧式「打开方式」菜单，应降级为 best-effort。

## 3. 决策

保持 ADR-008 的约束不变：**启动时绝不自动注册；注册是对话框里的逐格式手动操作；只写 HKCU；不覆盖用户当前的 UserChoice / 默认处理程序；提供取消注册清理。** 在此基础上修订实现细节：

1. **候选来源调整（Capabilities 为主）**
   - 每个选中格式的主注册路径改为：`HKCU\Software\{ProgId}\Capabilities\FileAssociations\{ext}` = ProgId + `HKCU\Software\Classes\Applications\<exe>\SupportedTypes\{ext}` = 空。两者成功即视为该格式「已注册为候选打开方式」（Windows 11 推荐列表可见）。
   - `HKCU\Software\Classes\{ext}\OpenWithProgids\{ProgId}` 继续写，但仅作为旧式「打开方式」补强（best-effort）；写入被拒不影响注册成功，只把该格式标记为「部分完成」。

2. **逐条目容错，不再首条失败即中止**
   - 核心条目（ProgId 描述 / DefaultIcon / 打开命令 / Applications FriendlyAppName 与打开命令 / RegisteredApplications / Capabilities ApplicationName 与 ApplicationDescription）逐条写，失败的记录原因并继续；
   - 每个格式的 SupportedTypes、Capabilities、OpenWithProgids 独立写入，任一失败不阻断其余格式；
   - 返回结构化结果 `FileAssociationResult`：核心是否成功（`CoreRegistered`）、每个格式的 `Registered / Partial / Failed` 状态、已注册 / 部分完成 / 失败格式列表；取消注册返回 `FileAssociationCleanupResult`（核心是否清理、因只读无法删除的残留扩展名）。

3. **不可写条目不视为整体失败**
   - 只要 `CoreRegistered` 为 true 且至少一种格式的 Capabilities + SupportedTypes 写入成功，注册就按「成功（可能部分格式缺少 OpenWithProgids）」处理，并把受保护格式明确告知用户（「已改用“默认应用”入口注册」）。

4. **UI 与状态**
   - 「文件关联」对话框：无论成功失败都重新读取状态；成功文案区分「全部写入」「部分格式缺少旧式条目」「部分格式失败」「核心条目失败」四种情形；勾选选择只在核心成功且无失败格式时保存（避免把没真正注册的格式记成偏好）；
   - 设置页状态行按 `GetRegistrationState()` 的候选数量显示；若 `IsRegistered()` 为 true 但没有任何格式登记（旧版失败留下的残留），提示用户重新注册。

5. **日志与错误原因**
   - `IRegistryStore` 增加 `LastError`（可空的失败原因），`WindowsRegistryStore` 区分「只读 / 被安全策略拒绝 / IO 失败 / 其它异常」，成功时清空；
   - `FileAssociationService` 注入 `IAppLogger`（AppHost 装配），每条失败写入 `[WARN]` 含 `HKCU\...` 路径与具体原因，成功时 `[INFO]` 汇总。

6. **不自动修 ACL、不提权、不写 HKLM**
   - 只读扩展名键保持原样：不尝试取得所有权、不弹 UAC、不把用户设置改到 `HKLM`；用户仍可在 Windows「默认应用」中把 EchoGarage 设为默认（与 ADR-008 「不覆盖 UserChoice」一致）。

7. **取消注册容错**
   - 只在值确实存在时才删除（只读键上"本来就没有我们的值"不应报失败）；核心树删除失败上报 `CoreCleared=false`；`OpenWithProgids` 因只读删不掉时列入 `LockedExtensions` 并提示"不影响使用"。

## 4. 备选方案与取舍

| 方案 | 不采用的原因 |
| --- | --- |
| 修改 `.flac` / `.mp3` 等只读键的 ACL 取得写权限 | 侵入用户注册表安全配置、通常需要管理员/取得所有权、可能被安全软件还原，且换一台机器状态又不同；违背最小惊讶原则 |
| 提示用户用管理员权限重新运行后再注册 | ADR-008 明确注册无需管理员（只写 HKCU）；只为了旧式 open-with 条目而提权不值得 |
| 写 `HKLM\Software\Classes` | 需要管理员；ADR-008 规定只写 HKCU；且会变成全机器生效，与「可选、手动」冲突 |
| 只写 `{ext}\OpenWithProgids`（现状） | 在只读键上必然失败，正是本问题的直接原因 |
| 写 `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{ext}\OpenWithList` | 属于 Explorer 未文档化遗留数据，不是 Windows 11 推荐列表的权威来源；且同样可能被保护 |
| 只引导用户去「默认应用」页，不做任何写入 | 用户在默认应用中根本无法看到 EchoGarage（除非已注册 capabilities），等于功能不可用 |

## 5. 影响与后果

- 在存在只读扩展名键的机器上，EchoGarage 现在能正常出现在这些格式的「默认应用 / 打开方式推荐」列表中，并可通过系统页面设为默认；
- 旧式「打开方式」右键菜单对受保护格式可能少一条 EchoGarage 候选——这是系统注册表权限所致，对话框会明确说明，不是功能失败；
- 注册表写入仍是 HKCU、只增不覆盖，用户已有默认程序不受影响；
- 失败诊断能力显著改善：日志能定位到具体 `HKCU\...` 路径与拒绝原因。

## 6. 测试与验证

- 单元测试（`tests/EchoGarage.Tests/FileAssociationTests.cs`，`FakeRegistryStore` 可注入逐 key 写/删拒绝）覆盖：只读 `OpenWithProgids` 时仍成功且该格式为 Partial；单个格式 Capabilities 被拒时其它格式继续并整体仍成功；核心条目被拒时 `CoreRegistered=false` 但格式条目仍写入；取消注册只读残留上报但核心清理成功；`GetRegistrationState()` 正确反映 Capabilities / OpenWithProgids；以及原有全部 key 路径断言。
- 真实注册表端到端验证（本机，随机 ProgId、exe 指向 UAT 发布版）：
  - `Register(['.flac', '.wav'])` → `CoreRegistered=True`、`.flac` = Partial（本机只读键）、`.wav` = Registered、无 Failed；
  - `SHAssocEnumHandlers(…, RECOMMENDED)`：`.flac` 与 `.wav` 列表均出现 `D:\Program\EchoGarage\EchoGarage.exe | EchoGarage`；
  - `Unregister()` → 核心清理成功、无残留；随机 ProgId 的键树全部消失。
- 说明：本会话的受限环境里子 .NET 进程无法写 HKCU（详见 KB-002），`FileAssociationRegistryTests` 会按既有约定 Skip；真实注册表验证通过父进程反射调用完成，用户正常启动的 UAT 版本不受此限制。

## 7. 需要 owner 确认

1. 同意修订 ADR-008 第 3 条：以 `Capabilities\FileAssociations` + `Applications\<exe>\SupportedTypes` 为主注册路径，`OpenWithProgids` 降级为 best-effort；
2. 同意「不修改只读注册表键的 ACL、不提权、不写 HKLM」；
3. 同意新的结果语义：Capabilities + SupportedTypes 写入成功即算注册成功，缺少 OpenWithProgids 只报告「部分完成」，不再整体报失败；
4. 同意新增 ADR-030（并在批准后把 ADR-008 第 3 条标注「由 ADR-030 修订」）。
