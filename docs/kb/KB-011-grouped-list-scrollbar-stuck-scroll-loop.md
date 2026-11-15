# KB-011：分组列表拖动滚动条后“卡住抽搐”的排查与防御性修复（提案）

- 状态：待 owner 批准（Proposed，2026-11-12）
- 日期：2026-11-12

## 现象

owner 反馈：在按专辑分组（foobar2000 风格分组列表）的媒体库中随便拖动一下滚动条后，列表会卡住并上下小幅抽搐，之后拖动滚动条滑块或滚动鼠标滚轮都无法改变列表显示位置。发生在 0.1.0 的新构建（播放信息状态栏 / 播放行自动居中 / 封面加载生命周期那一版）。运行日志里没有任何 `[ERROR]` / `[WARN]`，应用仍可正常关闭。

## 复现尝试（均未复现）

- 使用 owner 的真实媒体库（2825 首，`F:\Temp\POP`）与独立数据目录副本；
- UIAutomation 定位列表 + 真实鼠标事件（`SetCursorPos` + `mouse_event`）操作滚动条：拖动滑块、拖动中上下抖动、慢速 1~2px 微移、大幅跳跃后以微小移动收尾、点击滑轨翻页、滚轮连续滚动、拖到最顶端/最底端等；
- 以“播放《Demo Track 01》并自动居中”为初始状态（与 owner 场景一致），150% DPI、相同窗口尺寸（1677×930 DIP）；
- 程序化把列表从顶部到底部逐段扫描（每 300 DIP 一步，共 660+ 步），并用 watchdog 检测 UI 线程是否卡死；Debug 与 Release 构建均未出现卡住或滚动循环。

结论：无法在本机稳定复现，触发条件很可能与具体鼠标手势/拖动时序有关，属于时序敏感问题。

## 机理分析（基于 dotnet/wpf 源码）

1. `ScrollViewer` 的命令队列（`MakeVisible` / `SetVerticalOffset` …）每次 `LayoutUpdated` 只执行一条命令；`VirtualizingStackPanel.MakeVisible()` 在偏移改变后会再次调用 `ScrollOwner.MakeVisible(...)`（源码注释：“When layout gets updated it may happen that visual is obscured by a ScrollBar…”）。如果目标矩形始终无法满足，就会每帧重试——表现为列表小幅抖动、用户输入被下一次重试覆盖。
2. 像素滚动 + 分组虚拟化 + 容器回收（Recycling）时，组尺寸依赖估算；滚动过程中 `ExtentHeight` 会变化（本机实测拖动一次可变化数千到上万 DIP），`VirtualizingStackPanel.OnAnchorOperation()` 在“期望视口间距”与“实际间距”不一致时会调整偏移并重新测量、再次排队重试，直到收敛。若锚定容器在期间被回收、或估算一直变化，就可能不收敛。
3. 本应用正好命中高风险组合：分组虚拟化 + `ScrollUnit=Pixel`（`Controls.xaml` 中 DataGrid 样式设置的像素滚动，分组列表同样以像素为单位）+ 150% DPI（大量小数 DIP 偏移）。WPF 源码在 `SetVerticalOffsetImpl()` 中专门注释过：“像布局取整开启时一样，把偏移取整到像素边界以避免收敛到小数偏移的死循环”，说明小数偏移的锚定收敛是 WPF 已知的脆弱点。

## 采取的防御性修改

1. **自动居中不再使用合成的 `BringIntoView(Rect)`**（用一个超大负 top 矩形“骗”居中的写法），改为标准 `ScrollIntoView` + 直接 `ScrollToVerticalOffset` 的像素修正（最多 5 次）。这样去掉了应用里唯一的非常规 BringIntoView/MakeVisible 请求源。
2. **自动居中在用户操作滚动条期间推迟**：当左键按住且鼠标捕获位于 `ScrollBar` 内（拖拽 Thumb 或翻页按钮）时不抢滚动偏移，每 200ms 重试，最多 30 次；松开后立即居中。
3. **分组封面释放延迟到 Background**：`IsVisibleChanged` 里的 `ReleaseGroupCover` 由同步改为 `Dispatcher.BeginInvoke(Background)` 并二次校验可见性/数据上下文，避免在布局或容器回收过程中同步修改绑定、反复触发 measure。
4. **分组列表增加 `VirtualizingPanel.CacheLength=2`**：减少快速拖动时的容器回收/重新生成，降低“锚定容器被回收”的概率。
5. **分组列表启用 `UseLayoutRounding=True`**：对应上面 WPF 源码中的小数偏移规避路径；150% DPI 下尤其相关。
6. **滚动异常检测与自愈**：1 秒内超过 200 次 `ScrollChanged` 视为可疑“滚动风暴”，写 `[WARN] Suspicious scroll burst: ...`（包含 offset / extent / viewport / 来源列表与累计位移），并尝试把当前偏移吸附到整数 DIP 一次（正常拖动/滚轮远低于该阈值，不影响正常使用）。

## 局限

- 未能复现原始卡住现象，因此以上修改是“消除高风险路径 + 自愈/取证”的防御性修复，不保证根因一定在其中。
- 若再次出现，日志中应出现 `[WARN] Suspicious scroll burst`，可直接看到哪个列表、偏移/范围/视口以及累计位移，用于判断是否为自循环。
- 后续若仍复现，候选方向：分组列表改用 `ScrollUnit=Item`（需重新评估分组虚拟化性能与体验）、改为 Standard 虚拟化模式、或移除自定义 `MinThumbTrack`（最小滑块）后再对比。

## 参考

- [ScrollViewer.cs（dotnet/wpf）](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Controls/ScrollViewer.cs)
- [VirtualizingStackPanel.cs（dotnet/wpf）](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Controls/VirtualizingStackPanel.cs)
