# KB-020：“正在播放”封面交叉淡入过渡

- 状态：Accepted（2026-11-20 获 owner 批准，由 `docs/proposals/KB-020-now-playing-cover-crossfade.md`
  迁入；同日按本文第 7 节替换 `docs/kb/KB-008-grouped-cover-and-layout-persistence.md` §5）
- 日期：2026-11-20
- 关联：KB-008 §5（“正在播放”封面加载态，其中原行为描述已被本文取代）、
  KB-017（封面大图浏览窗口，不受影响）、ADR-024（主题令牌）
- 适用范围：`src/EchoGarage.App/Views/MainWindow.xaml`（右栏封面区域）、
  `src/EchoGarage.App/ViewModels/MainViewModel.cs`（`BeginNowPlayingCoverLoad` /
  `LoadNowPlayingCoverAsync` / `Stop`）、新增
  `src/EchoGarage.App/Views/Controls/CrossFadeImage.cs`

## 1. 背景

- owner 反馈：切歌时右栏「正在播放」封面切换很生硬。
- 改动前行为（见 KB-008 §5）：切歌一开始立即 `CurrentCover = null`（旧封面瞬间消失）→ 空白 /
  ♪ 占位符 → 异步解码 420px 封面 → 淡入新封面；加载态旋转弧线延迟 120ms 淡入。
  该行为由 commit `8ba2eba` 引入，当时理由是：旧封面一直显示到新封面解码完成，容易让人误以为
  “封面没换”。
- 但现在解码很快（命中文件 / 内存缓存时约 20–30ms，未命中通常 < 100ms），并且切歌时标题、
  艺术家与专辑信息立即更新；真正生硬的来源是“旧图瞬间消失 + 空白间隙 + 新图重新出现”的跳变，
  而“旧图多停留一小段时间”本身并不构成误导。因此旧理由不再成立。

## 2. 决策

1. **保留旧封面直到新封面解码完成**：`BeginNowPlayingCoverLoad` 不再清空 `CurrentCover`；
   `LoadNowPlayingCoverAsync` 在版本令牌（`_nowPlayingCoverVersion`）与 `CurrentRow.Path` 校验
   通过后才替换封面，过期结果继续丢弃。
2. **新增交叉淡入控件** `CrossFadeImage`（`sealed Grid`，内部两层 `Image`）：
   - `Source` 变化：旧图压到下层，新图在上层 0→1 淡入，默认 200ms、`QuadraticEase(EaseOut)`；
     完成后释放下层旧图。
   - `Source` 变为 `null`：当前图 1→0 淡出 200ms，结束后才清空，露出父容器中的 ♪ 占位符。
   - 动画被打断（快速连续切歌）：先快进到上一次过渡的终点再开始新过渡，避免半透明叠影；
     version 守卫保证只有最新一次过渡能修改层状态。
   - 依赖属性：`Source`、`TransitionDuration`（默认 200ms；`<= 0` 视为关闭动画，`> 2s` 截断）。
   - 两层 `Image` 均 `Stretch=Uniform` + `BitmapScalingMode=HighQuality`，与原单张封面一致。
3. **占位符与加载弧线**：♪ 占位符移到封面下层（Grid 中靠前，被过渡中的图自然覆盖/露出）；
   旋转加载弧线只在新增属性 `ShowCoverLoadingSpinner`（= `IsNowPlayingCoverLoading && CurrentCover is null`）
   为 true 时显示——即有旧图可看时不再叠一个转圈。
4. **其余生命周期不变**：`Stop()` 仍立即让封面进入 200ms 淡出并复位加载态；
   `CoverViewerWindow` 的加载/无图提示逻辑不改。

## 3. 理由

- 交叉淡入消除硬切；200ms 短促，满足 owner“平滑但不拖沓”的要求；EaseOut 让主体变化集中在开头，
  观感更利落。
- “保留旧图 + 新图淡入”使过渡全程都有画面，不经过背景色或占位符，不会闪烁。
- 转圈只在没有旧图可显示（首图加载）时出现，避免它在旧封面上叠加造成第二次跳变。
- 命中缓存时旧图只多停留约一帧到几十毫秒，配合标题区同步更新，不会被误认为没切歌。

## 4. 后果与影响

- 变更文件：
  - `src/EchoGarage.App/ViewModels/MainViewModel.cs`：删除 `_currentCoverPath` 字段；
    `BeginNowPlayingCoverLoad(Track track)` 去掉 `keepExistingCover` 参数且不再清空封面；
    `LoadNowPlayingCoverAsync` 不再记录路径；新增 `ShowCoverLoadingSpinner` 计算属性。
  - `src/EchoGarage.App/Views/MainWindow.xaml`：封面 Border 内改为
    “♪ 占位符 → `CrossFadeImage` → spinner” 的层次；spinner 的 `DataTrigger` 改绑
    `ShowCoverLoadingSpinner`。
  - 新增 `src/EchoGarage.App/Views/Controls/CrossFadeImage.cs`。
- 无新依赖，无设置 / 数据库 / schema 变化；控件构造函数只创建两个 `Image`，不影响启动路径。
- 无封面曲目：旧图 200ms 淡出到 ♪ 占位符；首次播放且无封面时仍由 spinner（120ms 延迟）兜底。
- 内存：过渡期间短暂同时持有两张 420px 已 freeze 位图，结束后释放下层引用；与普通切歌持平。
- KB-008 §5 已按本文第 7 节替换（2026-11-20 获 owner 批准后写入，提案区不保留副本）。

## 5. 备选方案（均未采纳）

- A. 只让新图淡入、旧图立即移除：仍有“旧图瞬间消失”的硬切，未解决生硬问题。
- B. 先清空旧图再交叉淡入：空白间隙更明显。
- C. 更强的位移 / 模糊 / 缩放动效：与当前简洁风格不符，且更容易显得拖沓。

## 6. 验证（应用内临时诊断，未提交）

- 临时给 `MainWindow` 增加“把 `NowPlayingCoverBorder` 渲染成 PNG”的方法，并在 `App` 中加一个
  环境变量驱动的诊断入口：先播放 `Demo Track A1`，2.5s 后切到 `Demo Track B1`，每约 25ms 截取
  封面区域一帧并逐帧分析。
- 结果：
  - 每帧与 “A、B 两图按 alpha 线性混合” 的拟合残差 RMS ≈ 0.4/255（PNG 舍入级），证明是真正的
    交叉淡入而非硬切；
  - alpha 序列约 0.20 / 0.48 / 0.70 / 0.83 / 0.91 / 1.00（对应切换后约 40/75/110/155/185/215ms），
    与 200ms + EaseOut 一致；
  - 过渡期间无空白 / 占位符帧，也无 spinner 帧。
- 临时诊断代码已移除；`git status` 仅剩 3 个正式改动（2 改 1 新增）。
- `dotnet build EchoGarage.sln`：0 警告 0 错误；单元测试 795 项全部通过；
  `scripts/publish.ps1` 发布 Release 版，启动截图正常，发布目录无 `EchoGarageData`。
- 说明：验证使用了 `Demo Album A/B`、`Demo Track A1/B1` 等 dummy 演示数据，未使用真实歌曲信息。

## 7. KB-008 §5 替换文本（2026-11-20 已应用）

> ## 5. “正在播放”封面过渡态
>
> - 旧行为（2026-11-19 之前，已由本次改动取代）：切歌后立即清空旧封面，延迟 120ms 显示旋转加载
>   弧线，新封面解码完成后淡入。理由是旧图残留会让人误以为封面没换，但代价是旧图瞬间消失，
>   出现空白间隙与跳变。
> - 现行为：
>   - `BeginNowPlayingCoverLoad` 不再清空 `CurrentCover`：旧封面保留到新封面解码完成，
>     `LoadNowPlayingCoverAsync` 在版本令牌与 `CurrentRow.Path` 校验通过后才替换，过期结果丢弃。
>   - 右栏封面使用 `CrossFadeImage`（`Views/Controls/CrossFadeImage.cs`）：两层 `Image`，
>     旧图在下、新图 0→1 淡入 200ms（`QuadraticEase`/`EaseOut`）；新曲无封面时旧图 200ms 淡出
>     露出 ♪ 占位符；快速连切时先快进上一次过渡再开始新过渡，避免叠影。
>   - 旋转加载弧线只在 `ShowCoverLoadingSpinner`（= `IsNowPlayingCoverLoading && CurrentCover is null`）
>     为 true 时显示，即只有“没有旧图可显示”的首图加载才转圈；120ms 淡入延迟保留，缓存命中时
>     基本不闪。
>   - ♪ 占位符位于封面下层，不遮挡过渡中的图；`Stop()` 立即清空版本并让当前图 200ms 淡出。
>   - 标签编辑刷新当前曲目也走同一路径；若封面未变化、加载到位图引用相同，视图不会触发过渡。
> - 应用内验证（临时诊断，未提交）：截图逐帧拟合 alpha 为线性混合（RMS ≈ 0.4/255），
>   alpha 在约 200ms 内从 0 到 1，无空白帧与转圈帧。
