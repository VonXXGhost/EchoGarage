# ADR-005：每个播放列表独立的显示布局与预设

- 状态：待批准（Proposed）
- 日期：2026-11-09

## 背景

foobar2000 的列表列可以自由配置；用户要求“不同播放列表可以有不同的显示格式记忆，也可以快速选择已有的格式”。

## 决策

1. 布局模型 `PlaylistViewLayout`：
   - `Columns`：按显示顺序排列的全部列（`Key`、`Width`、`Visible`），隐藏列也保留在模型中；
   - `SortColumn` / `SortDescending`：排序列（保存的是 ViewModel 属性名，如 `Title`、`DurationSort`）；
   - `PresetName`：来源预设名（可空）。
2. 存储位置：
   - 播放列表：`playlists.ViewLayoutJson`（每个播放列表独立）；
   - 媒体库视图：`settings.json` 的 `LibraryLayout`；
   - 预设：`settings.json` 的 `ColumnPresets`（全局，内置“标准/简洁/文件管理”三种）。
3. 列定义集中在 `ColumnCatalog`（Key、表头、默认宽度、最小宽度、是否可排序），UI 只负责把 Key 映射到单元格模板。
4. 归一化规则（`ViewLayoutFactory.Normalize`）：
   - 丢弃未知列、去重、补齐缺失列（隐藏）；
   - 宽度夹在 `[MinWidth, 1200]`；
   - 至少保留 title/cover/path 之一可见；
   - 排序列必须可见，否则清空排序。
5. 切换播放列表前先写回当前列宽与顺序（因为用户可能刚拖动列宽），再加载目标布局。

## 理由

- 显式保存“全部列 + 可见性”而不是只保存可见列，用户重新勾选时宽度不会丢。
- 预设存在全局配置而不是每个列表里，符合“快速选择已有格式”的用法。
- 保存 ViewModel 属性名让排序在 DataGrid 的 `SortMemberPath` 与 `SortDescriptions` 间直接复用。

## 后果

- 列 Key 是对外契约：改名会丢失用户已保存的布局（需要迁移或别名）。
- 媒体库与播放列表的布局分开存储，用户需要理解“媒体库也有自己的列设置”。

## 备选方案

- 全局唯一布局：不满足需求。
- 保存 DataGrid 的内置列对象：无法序列化，且与 UI 框架强绑定。
