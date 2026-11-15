# ADR-001：平台与技术栈

- 状态：已批准（Accepted，2026-11-09，owner）
- 日期：2026-11-09
- 决策者：owner
- 记录者：agent

## 背景

需要一个只面向 Windows 的本地音乐播放器，要求启动快、能读写真实音频标签、支持共享与 WASAPI 独占输出，
并且方便长期迭代。候选方案包括 Electron/Tauri + Web Audio、C# + WPF、C# + WinUI 3。

## 决策

1. 运行时：.NET 10，目标 `net10.0-windows`，仅 x64。
2. UI：WPF（不用第三方 UI 框架，自绘主题）。
3. **主题**：支持多套 UI 风格，主题以独立 `ResourceDictionary` 提供，控件用 `DynamicResource`
   引用颜色令牌，可在运行时切换并持久化到配置。
   - v0.1 提供五套（ADR-024）：纯净极简白（Pure Studio，默认浅色）、温润纸感白、极简冰青白、
     午夜黑胶、深海蓝灰；后两套为深色主题。
   - 后续新增主题只需增加一个主题字典并在设置/视图菜单中暴露，不改控件代码。
4. 音频：NAudio 2.2（`WasapiOut` + `AudioFileReader` + `WdlResamplingSampleProvider`）。
5. 标签：TagLib#（NuGet 包 `TagLibSharp` 2.3）。
6. 数据：SQLite（`Microsoft.Data.Sqlite`）+ Dapper，WAL 模式。
7. 测试：xUnit v3（进程内 runner）。

## 理由

- WPF 在 Windows 上成熟、启动开销可控、DataGrid 虚拟化足够支撑数万行列表；相比 WinUI 3 不需要额外打包/部署复杂度。
- WPF 的 `ResourceDictionary` + `DynamicResource` 天然适合“多主题、热切换”，不需要引入第三方主题库。
- NAudio 直接封装 WASAPI，支持共享与独占两种 `AudioClientShareMode`；`AudioFileReader` 借助 Media Foundation 覆盖 FLAC/AAC/M4A，配合 WDL 重采样可统一到设备格式。
- TagLib# 是目前 .NET 生态里对各格式标签支持最完整的库（ID3v2、Xiph、Apple、ASF、WAV/RIFF 内嵌 ID3）。
- SQLite 单文件、零部署，Dapper 让查询保持可控（智能播放列表需要动态 SQL）。

## 后果

- 只能在 Windows 运行；跨平台需要重写 UI 与音频层。
- Media Foundation 依赖系统编解码器：FLAC/AAC/M4A 在 Windows 10/11 上可用，但极端精简系统可能缺失。
- NAudio 2 的 MP3 解码走 ACM/DMO，首次使用有约 1 秒初始化；启动后用后台预热缓解（见 ADR-009）。
- 多主题意味着每套主题都要维护完整的颜色令牌；新增主题时必须同时检查浅色 / 深色下的文本对比度、边框与图标可见性。

## 备选方案

- **Tauri/Electron**：跨平台与 UI 灵活，但 WASAPI 独占、标签读写需要额外原生插件，启动与内存开销更大。
- **LibVLC / BASS**：解码能力更强，但体积、许可证与“只保留必要功能”的目标不符。

