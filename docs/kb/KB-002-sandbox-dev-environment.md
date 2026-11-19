# KB-002：受限开发环境（DSH 沙箱）下的构建与测试

- 状态：Accepted（2026-11-09 批准）
- 日期：2026-11-09（2026-11-19 校对过示例路径与测试计数）
- 环境：DSH（DeepSeek Harness）Windows 主机，workspace-write 文件策略

## 现象与解决

### 1. `dotnet build` 显示“0 错误”但返回失败

原因：MSBuild 编译器服务器 / 节点复用依赖命名管道，沙箱禁止；服务器连接失败后某些目标静默失败。

解决：

```powershell
dotnet build EchoGarage.sln -nodeReuse:false -p:UseSharedCompilation=false
# 或设置环境变量
$env:DOTNET_CLI_USE_MSBUILD_SERVER='0'; $env:MSBUILDDISABLENODEREUSE='1'
```

### 2. `dotnet test` 的 TestHost 启动失败：`OpenProcess ... 拒绝访问`

VSTest 宿主会尝试监视父进程，被沙箱拒绝；即使提升权限也可能失败（父进程属于不同的作业/完整性上下文）。

解决：测试项目改为 **xUnit v3 进程内 runner**（`xunit.v3` + `OutputType=Exe`），直接执行测试可执行文件：

```powershell
dotnet build tests/EchoGarage.Tests
.\tests\EchoGarage.Tests\bin\Debug\net10.0-windows\EchoGarage.Tests.exe
```

### 3. NuGet 还原报“无法获取锁定文件访问权限”

原因：`%TEMP%\NuGetScratch\lock\...` 被之前的沙箱进程占用 / 权限异常。

解决：设置 `TEMP` / `TMP` 到工作区内目录后还原，或清理 `%TEMP%\NuGetScratch\lock` 并结束残留 `dotnet` 进程。

```powershell
$env:TEMP='<repo>\.tmp'; $env:TMP=$env:TEMP
dotnet restore EchoGarage.sln
```

### 4. `%APPDATA%` / `%USERPROFILE%\.dotnet` 不可写

原因：DSH 只允许写工作区。

解决：

```powershell
$env:DOTNET_CLI_HOME='<repo>\.dotnet'
$env:NUGET_PACKAGES='<repo>\.nuget'
```

### 5. 工作区目录 ACL 损坏（`SetNamedSecurityInfoW failed: 5`）

DSH 的 `diagnose-windows-sandbox-acl` 技能命令可诊断并修复：给当前用户补完全控制 ACE、清理外来应用包条目，
并生成可撤销的恢复脚本。

### 6. 从工具进程启动的应用无法写 `%APPDATA%` / `%LOCALAPPDATA%`

现象：`Start-Process` 启动编译/发布后的 `EchoGarage.exe` 时，启动即报
`Access to the path 'C:\Users\<user>\AppData\Roaming\EchoGarage' is denied.`；
同一会话里的 PowerShell / cmd 却能正常创建目录。

判断：这是 DSH 沙箱对该可执行文件的限制，而非程序权限问题（应用用同样的 API 写入工作区路径完全正常）。

解决：给应用设置 `ECHOGARAGE_DATA_DIR` 指向工作区内目录再启动，例如：

```powershell
$env:ECHOGARAGE_DATA_DIR = '<repo>\artifacts\.testdata'
.\artifacts\publish\win-x64\EchoGarage.exe
```

终端用户在自己的桌面会话中运行不受此限制。启动失败时应用也会提示这一环境变量。

### 7. 大量 `dotnet` 子进程堆积导致构建中断

现象：构建一会儿就“失败但 0 错误”，反复出现大量 `dotnet.exe`（每个约 35 MB）。

原因：SDK 的 workload 解析器在受限环境下反复启动子进程并失败。

解决：结束残留的 `dotnet` 进程后在**沙箱外**执行一次构建（本仓库开发时使用 DSH 的 danger-full-access），
或使用 `-p:UseSharedCompilation=false -nodeReuse:false` 并避免并发构建。

### 8. HKCU 注册表写入被限制

现象：`Registry.CurrentUser.CreateSubKey(@"Software\Classes\...", writable: true)` 在从工具启动的
应用 / 测试进程里抛 `UnauthorizedAccessException`，而同一会话的 PowerShell 可以写。

影响：文件关联的“真实注册表”集成测试会自动跳过（`Assert.Skip`）；文件关联的键路径逻辑用内存
`FakeRegistryStore` 做了完整覆盖，仍可发现路径 / 值写错的问题（“62 个测试中的 8 个”为 2026-11-09
的项目快照，测试总数此后已增长）。

建议：发布后由 owner 在普通桌面会话点一次“注册为默认播放器”，确认资源管理器中出现 EchoGarage。

## 影响评估

- 以上都是**开发环境**问题，不影响终端用户运行与发布产物。
- 但会导致“看似编译成功其实失败”“测试跑不起来”，所以必须写进 KB，避免下次重复排查。
