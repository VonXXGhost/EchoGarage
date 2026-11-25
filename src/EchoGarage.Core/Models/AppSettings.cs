using EchoGarage.Core.Services;

namespace EchoGarage.Core.Models;

/// <summary>界面主题。顺序与 <see cref="ThemeCatalog"/> 保持一致，索引即 UI 中的主题序号。</summary>
public enum ThemeKind
{
    /// <summary>Pure Studio：默认浅色主题。</summary>
    PureStudio = 0,
    /// <summary>Warm Paper：暖白纸张浅色主题。</summary>
    WarmPaper = 1,
    /// <summary>Nordic Ice：冷调冰蓝浅色主题。</summary>
    NordicIce = 2,
    /// <summary>Midnight Vinyl：暖色深色主题。</summary>
    MidnightVinyl = 3,
    /// <summary>Slate Studio：冷灰深色主题。</summary>
    SlateStudio = 4
}

/// <summary>How the transport picks the next track.</summary>
public enum PlaybackMode
{
    Sequential,
    Shuffle,

    /// <summary>单曲循环：每次自然播放结束后重播当前曲目。</summary>
    RepeatOne
}

/// <summary>进度条形态：滑块（默认，兼容旧配置）或波形（ADR-015）。</summary>
public enum SeekBarMode
{
    Slider,
    Waveform
}

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public ThemeKind Theme { get; set; } = ThemeKind.PureStudio;
    public string? OutputDeviceId { get; set; }
    public OutputMode OutputMode { get; set; } = OutputMode.Shared;

    /// <summary>
    /// 全局音量（0–1），也是“该设备/模式还没有记忆”时的回落音量。ADR-033。
    /// 实际输出增益 = 显示音量 × 对应模式的上限（见 <see cref="VolumeCeilings"/>）。
    /// </summary>
    public double Volume { get; set; } = 0.75;

    /// <summary>按输出设备（实际端点 ID）+ 模式记住的音量；只保存用户显式调过的值。ADR-033。</summary>
    public Dictionary<string, OutputDeviceVolume> DeviceVolumes { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>按模式配置的音量上限（0–1），默认 100%。ADR-033。</summary>
    public OutputVolumeCeilings VolumeCeilings { get; set; } = new();

    public PlaybackMode PlaybackMode { get; set; } = PlaybackMode.Sequential;
    public SeekBarMode SeekBarMode { get; set; } = SeekBarMode.Slider;

    /// <summary>波形缓存上限（MB）；0 表示不限。ADR-015 §7.5。</summary>
    public int WaveformCacheLimitMb { get; set; } = 256;

    /// <summary>封面缓存目录上限（MB）；0 表示不限。超出后按最后写入时间删除最旧的封面。</summary>
    public int CoverCacheLimitMb { get; set; } = 2048;
    public bool ShowAlbumPanel { get; set; } = true;
    public bool RestoreLastPlaylist { get; set; } = true;
    public long? LastPlaylistId { get; set; }

    /// <summary>
    /// 上一次真正开始播放的曲目路径。启动恢复播放列表时只用来选中并滚动到该行，
    /// 不会自动播放；通过文件关联 / 命令行指定文件启动时不使用。
    /// </summary>
    public string? LastPlayedTrackPath { get; set; }
    public string CacheDirectory { get; set; } = string.Empty;
    public PlaylistViewLayout LibraryLayout { get; set; } = ViewLayoutFactory.CreateDefault();
    public List<ColumnPreset> ColumnPresets { get; set; } = ViewLayoutFactory.CreateBuiltInPresets();
    public bool AutoScanOnStartup { get; set; } = true;

    /// <summary>User-configured playback hotkeys (local and optional system-wide).</summary>
    public List<HotkeyBinding> Hotkeys { get; set; } = [];

    /// <summary>
    /// 用户配置的标签“快捷规范化”正则规则（提案 ADR-025），顺序即叠加应用顺序。
    /// 默认两条内置规则；用户删空后保存为空列表，不会再次复活默认规则（null 才补默认）。
    /// </summary>
    public List<TagNormalizationRule> TagNormalizationRules { get; set; } = TagNormalizationDefaults.Create();

    /// <summary>Saved conversion rules (shown in the track context menu and converter settings).</summary>
    public List<ConversionPreset> ConversionPresets { get; set; } = ConversionPresetFactory.CreateBuiltInPresets();

    /// <summary>Explicit encoder/tool paths; empty means auto-detect from tools\ or PATH.</summary>
    public ConversionEncoderPaths Encoders { get; set; } = new();

    /// <summary>响度调整窗口打开时填入的默认目标积分响度（LUFS），默认 -10.0，范围 -60.0…0.0。</summary>
    public double LoudnessDefaultTargetLufs { get; set; } = -10.0;

    /// <summary>Extensions the user last chose in the file-association dialog (never auto-registered).</summary>
    public List<string> AssociationExtensions { get; set; } = [];

    /// <summary>「复制到 / 移动到」记住的目标文件夹（绝对路径，顺序即菜单顺序；ADR-021）。</summary>
    public List<string> FileDestinations { get; set; } = [];

    /// <summary>网络代理设置（ADR-022）。</summary>
    public NetworkSettings Network { get; set; } = new();

    /// <summary>Web 标签源与字段勾选记忆（ADR-022）。</summary>
    public WebTagSettings WebTags { get; set; } = new();

    /// <summary>右栏「正在播放」下方的播放可视化设置（ADR-023）。</summary>
    public VisualizationSettings Visualization { get; set; } = new();

    /// <summary>「正在播放」页 BPM 测量、模型与展示设置（ADR-034）。</summary>
    public BpmSettings Bpm { get; set; } = new();

    public double WindowWidth { get; set; } = 1440;
    public double WindowHeight { get; set; } = 880;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public bool WindowMaximized { get; set; }
    public double SidebarWidth { get; set; } = 236;
    public double AlbumPanelWidth { get; set; } = 320;

    public AppSettings Clone()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this, JsonDefaults.Options);
        return System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json, JsonDefaults.Options) ?? new AppSettings();
    }
}
