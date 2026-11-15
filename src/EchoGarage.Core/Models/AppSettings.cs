using EchoGarage.Core.Services;

namespace EchoGarage.Core.Models;

public enum ThemeKind
{
    Dark,
    Light
}

/// <summary>How the transport picks the next track.</summary>
public enum PlaybackMode
{
    Sequential,
    Shuffle
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
    public ThemeKind Theme { get; set; } = ThemeKind.Light;
    public string? OutputDeviceId { get; set; }
    public OutputMode OutputMode { get; set; } = OutputMode.Shared;
    public double Volume { get; set; } = 0.75;
    public PlaybackMode PlaybackMode { get; set; } = PlaybackMode.Sequential;
    public SeekBarMode SeekBarMode { get; set; } = SeekBarMode.Slider;

    /// <summary>波形缓存上限（MB）；0 表示不限。ADR-015 §7.5。</summary>
    public int WaveformCacheLimitMb { get; set; } = 256;
    public bool ShowAlbumPanel { get; set; } = true;
    public bool RestoreLastPlaylist { get; set; } = true;
    public long? LastPlaylistId { get; set; }
    public string CacheDirectory { get; set; } = string.Empty;
    public PlaylistViewLayout LibraryLayout { get; set; } = ViewLayoutFactory.CreateDefault();
    public List<ColumnPreset> ColumnPresets { get; set; } = ViewLayoutFactory.CreateBuiltInPresets();
    public bool AutoScanOnStartup { get; set; } = true;

    /// <summary>User-configured playback hotkeys (local and optional system-wide).</summary>
    public List<HotkeyBinding> Hotkeys { get; set; } = [];

    /// <summary>Saved conversion rules (shown in the track context menu and converter settings).</summary>
    public List<ConversionPreset> ConversionPresets { get; set; } = ConversionPresetFactory.CreateBuiltInPresets();

    /// <summary>Explicit encoder/tool paths; empty means auto-detect from tools\ or PATH.</summary>
    public ConversionEncoderPaths Encoders { get; set; } = new();

    /// <summary>Extensions the user last chose in the file-association dialog (never auto-registered).</summary>
    public List<string> AssociationExtensions { get; set; } = [];

    /// <summary>「复制到 / 移动到」记住的目标文件夹（绝对路径，顺序即菜单顺序；ADR-021）。</summary>
    public List<string> FileDestinations { get; set; } = [];

    /// <summary>网络代理设置（ADR-022）。</summary>
    public NetworkSettings Network { get; set; } = new();

    /// <summary>Web 标签源与字段勾选记忆（ADR-022）。</summary>
    public WebTagSettings WebTags { get; set; } = new();

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
