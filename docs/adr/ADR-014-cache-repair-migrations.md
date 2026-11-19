# ADR-014：缓存修复迁移：schema v3 回填作曲家 / schema v4 Apple 音轨号自愈

> 状态：已批准（Accepted，2026-11-14；2026-11-18 并入 v4 迁移，原编号 ADR-029）
> 日期：2026-11-14（v3）/ 2026-11-18（v4）
> 相关：ADR-003（文件是唯一真相）、ADR-010（缓存行失效）、ADR-013（作曲家标签与次要信息）、
> ADR-018（播放列表外部文件刷新）、ADR-019（`#` 列显示碟片号.音轨号）

## 背景 / 问题

两类问题共享同一根因：schema 升级新增或修正了缓存字段，但没有让已有缓存行失效；而扫描器的
增量判断（`DurationMs > 0 && FileSize 相同 && ModifiedAtUtc 相同` 时跳过）认为文件没变、不会重读标签，
于是旧行永远不会自愈。

### 1）schema v2 缺 Composer 回填（2026-11-14）

ADR-013 把“作曲家”（Composer）加入 SQLite 缓存，schema 升到 v2 时只执行了
`ALTER TABLE tracks ADD COLUMN Composer TEXT NOT NULL DEFAULT ''`，没有让已有缓存行失效。

结果是：升级前已入库的曲目，`Composer` 一直是空串；扫描器认为文件没变，不会重读标签，
所以这些曲目的作曲家永远不会回填。界面表现：标签属性对话框（实时读文件）能看到作曲家，
但列表“艺术家 → 次行 `{composer}`”显示为空。owner 在 2026-11-14 反馈了该问题
（“为艺术家配置了作曲的次要显示，但是没显示出来”）。

### 2）schema v4 缺 Apple 旧式音轨号回填（2026-11-18）

部分历史 M4A/MP4 文件由 Nero AAC 等旧式 Apple 工具写入标签，音轨号与碟片号不是标准
`trkn` / `disk` atom，而是自由标签 `----:com.apple.iTunes:track` / `disc`（文本形如 `1/3`、`1/1`）。
TagLib 的 `AppleTag.Track` / `Disc` 只读标准 atom，因此旧版本把这些文件读成
`TrackNumber = 0`、`DiscNumber = 0` 并写入 `library.db`。

服务层已修复读取逻辑（`9812462`：读取时回退到自由标签，写入时保持旧式标签形态），
但界面仍不显示：主列表 `#` 列绑定缓存行的 `TrackNumber` / `DiscNumber`，而旧的缓存行仍是 0；
扫描器因文件未变化而跳过，缓存里的 0 不会自愈。标签属性对话框直接读文件，所以能看到音轨号，
列表却看不到（owner 于 2026-11-18 反馈：“tag详情页是有了，但列表页还是没显示”）。

UAT 库副本实测（9563 个 M4A 行）：`TrackNumber = 0` 的行 367 条；修复前主列表 `#` 列为空，
同一行缓存 `DurationMs = 225941`、`FileSize` / `ModifiedAtUtc` 与文件一致，因此扫描判定“未变化”。
（367 行为全库 SQL 命中行数，与实际重读文件数的口径差异见“影响与边界”。）

## 决策

统一复用 ADR-010 的既有约定：**`DurationMs <= 0` 表示缓存行不可用**，扫描器对时长缺失的
缓存行强制重读标签。两个版本都只做数据修复，不改表结构。

### v3（2026-11-14，回填 Composer）

```sql
UPDATE tracks SET DurationMs = 0 WHERE FolderId IS NOT NULL;
PRAGMA user_version = 3;
```

- `MainViewModel.InitializeAsync` 会统计 `GetIncompleteTrackCountAsync()`（条件为
  `FolderId IS NOT NULL AND IsMissing = 0 AND DurationMs <= 0`），数量大于 0 时在窗口显示后
  启动一次静默扫描（`silent: true, repair: true`），扫描器对时长缺失的行强制重读标签，
  从而回填 `Composer` 以及其它标签字段。
- 限定 `FolderId IS NOT NULL`：只失效媒体库内的缓存行；播放列表中的外部文件没有可重扫的
  文件夹，其快照保持原样（外部文件的属性仍直接读文件）。
- `IsMissing` 行也会被重置时长；文件重新出现时同样会触发重读，避免遗漏。
- 迁移是幂等的（`PRAGMA user_version` 保证只执行一次）。

### v4（2026-11-18，Apple 旧式音轨号自愈）

```sql
UPDATE tracks SET DurationMs = 0
WHERE TrackNumber = 0
  AND (Path LIKE '%.m4a' OR Path LIKE '%.mp4' OR Path LIKE '%.m4b');
PRAGMA user_version = 4;
```

- 复用同一套修复机制：`MainViewModel.InitializeAsync()` 统计 `GetIncompleteTrackCountAsync()`
  （`FolderId IS NOT NULL AND IsMissing = 0 AND DurationMs <= 0`），数量大于 0 时在窗口显示后
  启动一次静默修复扫描（`silent: true, repair: true`），扫描器对失效行强制重读标签，
  回填 `TrackNumber` / `DiscNumber` 及其它字段，完成后刷新列表。
- 只失效 Apple 容器（`.m4a` / `.mp4` / `.m4b`）且 `TrackNumber = 0` 的行：其它格式的读取逻辑
  没有变化，已有音轨号的行也不需要重读，避免无谓的 IO。`Path LIKE` 对 ASCII 扩展名大小写不敏感
  （`.M4A` 同样命中）。
- 不限定 `FolderId IS NOT NULL`：播放列表中的外部文件同样可能来自旧式标签工具。这些行在下次
  打开对应列表时由 ADR-018 的 `RefreshStaleTracksAsync` 处理——`TrackRefresh.NeedsRefresh`
  在 `DurationMs <= 0` 时返回 true，会重新索引并刷新列表。
- 迁移幂等（`PRAGMA user_version` 保证只执行一次），不删除、不新增缓存行，
  播放列表引用、播放计数、添加时间等软件状态全部保留。

## 备选与取舍

- **每次启动都扫描整库 / 按扩展名重扫 `TrackNumber = 0` 的行**：真正缺失字段的文件会被永久
  反复读取，且没有“已修复”标记，违反 ADR-010“修复一次后不再触发”的约定；不可接受。
- **v3 只把 Composer 置为特殊标记值 / 在设置里记“标签读取版本号”**：需要额外解释标记含义或做
  配置迁移，且配置与媒体库分离时可能不同步；ADR-010 备选方案中已否决同类做法。
- **回填时直接读所有文件 / 在迁移里做 IO**：会拖慢启动，且绕过扫描器的线程/进度/日志体系。
- **清空 `tracks` 让用户重新导入、或撤回前序迁移重建库**：丢失播放列表条目、播放计数与添加时间，
  不可接受。
- **给 `tracks` 增加“元数据版本”列**：需要真实结构变更与长期维护；本次问题可用既有的
  “时长缺失 = 缓存失效”语义表达，不值得新增列。
- **改扫描器：Composer 为空就重读**：合法无作曲家的文件会被永久反复读取，不可接受。

## 影响与边界

- 升级后首次启动会多一次后台静默扫描：v3 修复 Composer；v4 在 UAT 库副本实测标记失效后
  实际重读 211 个 M4A 文件，耗时约 0.5 s（7,984 个文件命中增量跳过），期间界面可正常使用，
  状态栏显示“正在修复媒体库标签…”，完成后显示“媒体库标签已修复”并刷新列表。
- 367 行是 v4 迁移 SQL 在全库 `tracks` 中的命中行数；211 行是启动修复扫描实际重读的文件数。
  两者统计口径不同（修复扫描只处理媒体库内、`IsMissing = 0` 且时长待修复的缓存行），
  具体差异记录见提交 `71c5f2b`。
- 修复后列表 `#` 列显示碟片号.音轨号（如 `1.01`），碟片号列与排序也恢复；
  标签属性对话框、foobar2000 等外部工具仍显示“音轨号/总数”形态（写入策略见 `9812462`）。
- 播放列表中的外部旧式文件在打开列表时自动刷新，行为与 ADR-018 一致。
- 新建库仍从 v1 → v2 → v3 → v4 顺序迁移；`tests/EchoGarage.Tests/RepositoryTests.cs` 覆盖
  v1→latest、v2→latest 与 v3→v4 三条路径（`Database_MigratesVersion1ToLatest_*`、
  `Database_MigratesVersion2ToLatest_*`、`Database_MigratesVersion3ToVersion4_*`），
  迁移后 `user_version = 4`、旧行 `DurationMs = 0`、`Composer` 保留。
- 数据模型（`Track` / 仓储 SQL / 布局序列化）未变；ADR-013 中的 v2 描述成为历史记录，
  以本文的 v3 / v4 迁移为准。
- 极端情况下（文件损坏、读不出时长）每次扫描会重试该文件；可接受，必要时后续加“损坏”标记。

## 验证

- `dotnet build EchoGarage.sln -nodeReuse:false -p:UseSharedCompilation=false` 0 警告 0 错误。
- 单元测试（`tests/EchoGarage.Tests/RepositoryTests.cs`）：
  - `Database_MigratesVersion1ToLatest_*`、`Database_MigratesVersion2ToLatest_*`：迁移链后
    `user_version = 4`，既有 Composer / DurationMs 行为不变。
  - `Database_MigratesVersion3ToVersion4_InvalidatesAppleRowsWithoutTrackNumber`：
    `.m4a` / `.M4A` / `.mp4` 且 `TrackNumber = 0` 的行（含 `FolderId = NULL`）时长被清 0；
    已有音轨号的 `.m4a` 与非 Apple 格式不受影响。
- 全量测试：733 项通过、0 失败（2026-11-18 时点）。
- 用 v2 库启动应用，日志出现 v3 迁移与 “Metadata repair required for N tracks”，
  扫描完成后列表“艺术家 → `{composer}`”显示正常。
- UAT 库副本实跑（`ECHOGARAGE_DATA_DIR` 指向数据库副本，未修改验收目录）：
  日志 `SQLite schema migrated to version 4 …` → `Metadata repair required for 211 tracks` →
  `D:\Music: +211/-0 … 0.5s` → `Metadata repair scan completed`；迁移后 `Incomplete = 0`，
  样例歌曲缓存行 `TrackNumber = 1`、`DiscNumber = 1`；列表截图 `#` 列显示 `1.01`。

## 修订记录

- 2026-11-14：v3 迁移（Composer 回填）批准并归档，编号 ADR-014。
- 2026-11-18：v4 迁移（Apple 旧式音轨号自愈）批准并归档，原编号 ADR-029；2026-11-19 按同一
  “DurationMs <= 0 = 缓存失效”机制并入本文，ADR-029 编号不再单独维护。
- 2026-11-19：修正测试名与版本断言（现为 v1→latest / v2→latest / v3→v4，最新
  `user_version = 4`），删除已过期的索引补充说明，补充 367 行命中与
  211 行实际重读的口径说明。
