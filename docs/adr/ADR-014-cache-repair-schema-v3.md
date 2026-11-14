# ADR-014 缓存修复迁移：schema v3 回填作曲家

> 状态：已批准（Accepted，2026-11-14，owner）
> 日期：2026-11-14
> 相关：ADR-003（文件是唯一真相）、ADR-010（缓存行失效）、ADR-013（作曲家标签与次要信息）

## 背景

ADR-013 把“作曲家”（Composer）加入 SQLite 缓存，schema 升到 v2 时只执行了
`ALTER TABLE tracks ADD COLUMN Composer TEXT NOT NULL DEFAULT ''`，没有让已有缓存行失效。

结果是：升级前已入库的曲目，`Composer` 一直是空串；而扫描器的增量判断
（`DurationMs > 0 && FileSize 相同 && ModifiedAtUtc 相同` 时跳过）认为文件没变，不会重读标签，
所以这些曲目的作曲家永远不会回填。界面表现：标签属性对话框（实时读文件）能看到作曲家，
但列表“艺术家 → 次行 `{composer}`”显示为空。

owner 在 2026-11-14 反馈了该问题（“为艺术家配置了作曲的次要显示，但是没显示出来”）。

## 决策

新增 schema v3 迁移，只做数据修复，不改表结构：

```sql
UPDATE tracks SET DurationMs = 0 WHERE FolderId IS NOT NULL;
PRAGMA user_version = 3;
```

- 复用 ADR-010 的既有约定：`DurationMs <= 0` 表示缓存行不可用。
  `MainViewModel.InitializeAsync` 会统计 `GetIncompleteTrackCountAsync()`（条件为
  `FolderId IS NOT NULL AND IsMissing = 0 AND DurationMs <= 0`），数量大于 0 时在窗口显示后
  启动一次静默扫描（`silent: true, repair: true`），扫描器对时长缺失的行强制重读标签，
  从而回填 `Composer` 以及其它标签字段。
- 限定 `FolderId IS NOT NULL`：只失效媒体库内的缓存行；播放列表中的外部文件没有可重扫的
  文件夹，其快照保持原样（外部文件的属性仍直接读文件）。
- `IsMissing` 行也会被重置时长；文件重新出现时同样会触发重读，避免遗漏。
- 迁移是幂等的（`PRAGMA user_version` 保证只执行一次）。

## 备选与取舍

- **每次启动都扫描整库**：不可接受，违反性能红线（AGENTS §6）。
- **v3 只把 Composer 置为特殊标记值**：需要额外解释标记含义、并改扫描器判断，比复用
  “时长为 0 = 缓存失效”复杂。
- **回填时直接读所有文件**：迁移里做 IO 会拖慢启动，且绕过扫描器的线程/进度/日志体系。
- **改扫描器：Composer 为空就重读**：合法无作曲家的文件会被永久反复读取，不可接受。
- **撤回 v2 迁移、重建库**：会丢失播放列表与布局数据，不可接受。

## 影响

- 已升级到 v2 的用户在下次启动时会看到一次后台重扫（只读标签，按文件大小/时间缓存仍然生效
  的是修复之后的行）；扫描期间界面可正常使用。
- 新建库仍从 v1 → v2 → v3 顺序迁移；`tests/EchoGarage.Tests/RepositoryTests.cs` 覆盖
  v1→v3 与 v2→v3 两条路径（迁移后 `user_version = 3`、旧行 `DurationMs = 0`、`Composer` 保留）。
- 数据模型未变，`Track`/仓储 SQL/布局序列化无需改动；ADR-013 中的 v2 描述成为历史记录，
  以本提案的 v3 为准（待批准后补充到 ADR 索引）。

## 验证

- `dotnet build EchoGarage.sln -nodeReuse:false -p:UseSharedCompilation=false` 0 警告 0 错误。
- 全部单元测试通过（含新增迁移测试）。
- 用 v2 库启动应用，日志出现 v3 迁移与 “Metadata repair required for N tracks”，
  扫描完成后列表“艺术家 → `{composer}`”显示正常。
