# KB-004：性能基线与优化记录

- 状态：待批准（Proposed）
- 日期：2026-11-09
- 环境：Windows 11（26200）、.NET 10、4 首测试曲目；Release/Debug 均为 Debug 构建测得的保守值

## 基线（Debug 构建，带日志）

| 指标 | 数值 | 说明 |
| --- | --- | --- |
| 进程启动 → 主窗口显示 | **~0.9 s** | `Startup complete ... (window shown at 949 ms)` |
| Host 初始化（设置 + SQLite 建表） | ~170–200 ms | 数据库已存在时 |
| 来源列表加载（文件夹/播放列表/计数） | ~55 ms | 4 首库 + 3 播放列表 |
| 初始播放列表加载 | ~20 ms | 4 行 |
| 后台预热（MF + WASAPI + SQLite） | ~1.0 s | 与 UI 并行，不阻塞 |
| 首次扫描 4 个文件 | 0.2 s | 含标签读取与封面键计算 |
| 重复扫描（无变化） | ~0.0 s | 只比对 stat |
| 外部打开单文件（索引 + 替换默认列表 + 切源 + 起播） | ~1.1–2.4 s | 取决于是否首次初始化音频；第二次约 1.1 s |

补充实测：

- 跳转（`Seeked to 00:06.000`）后会重建输出链路，约 0.1 s 内恢复播放；跳转前后位置显示正确。
- 单实例转发：第二个进程启动后立即退出，主实例约 1.1 s 内完成“索引 → 覆盖默认列表 → 播放”。

## 关键优化与依据

1. **不在启动路径枚举音频设备**：设备枚举 + `MixFormat` 第一次约 1.5 s（COM/MF 初始化）。
   改为窗口显示后 `DispatcherPriority.Background` 执行；启动时间从 2.1 s 降到 0.9 s。
2. **后台预热**：窗口显示后 `Task.Run` 调用 `MediaFoundationApi.Startup()`、默认设备 `MixFormat`、打开一次 DB 连接。
   代价约 1 s 后台 CPU，换来首次播放/首次切设备的低延迟。
3. **扫描增量**：以 `FileSize + LastWriteTimeUtc` 判断，未变化不读标签；批量 `UpsertTracks` 使用单事务 + 200 条分批。
4. **封面按专辑缓存**：同专辑只解析一次；行封面按需（`LoadingRow`）加载，宽度 56px；专辑面板 420px；
   内存 LRU 上限 300 条，虚拟化回收时把行的 `CoverImage` 置空。
5. **数据库**：WAL + `synchronous=NORMAL`；常用查询有 `(IsInLibrary, IsMissing)`、`FolderId`、
   `(PlaylistId, Position)` 索引。

## 已知的慢点

- **首次 MP3 播放约 1 s**：NAudio 的 `Mp3FileReader` 走 ACM/DMO 初始化；预热无法覆盖（没有可解码的 MP3 样本）。
  后续可考虑：统一改用 MediaFoundationReader 解 MP3（已预热），或在安装目录放一个极小的静音 MP3 预热。
- **大量文件（数万）首次导入**：受磁盘随机读与标签解析限制；扫描并发度限制在 `min(CPU, 8)`，避免打满磁盘。
- **DataGrid 分组**：分组本身对 1 万行有可见开销；关闭“按专辑分组”可换取更快的滚动。

## 测量方法

日志中检索：

```
Host initialized in ... ms
Sources loaded in ... ms
Initial playlist loaded in ... ms total
Startup complete in ... ms (window shown at ... ms)
Warmup finished in ... ms
External open phases: index=..., playlist=..., sources=..., select=..., play=... ms
```
