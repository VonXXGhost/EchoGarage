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

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public ThemeKind Theme { get; set; } = ThemeKind.Light;
    public string? OutputDeviceId { get; set; }
    public OutputMode OutputMode { get; set; } = OutputMode.Shared;
    public double Volume { get; set; } = 0.75;
    public PlaybackMode PlaybackMode { get; set; } = PlaybackMode.Sequential;
    public bool ShowAlbumPanel { get; set; } = true;
    public bool RestoreLastPlaylist { get; set; } = true;
    public long? LastPlaylistId { get; set; }
    public string CacheDirectory { get; set; } = string.Empty;
    public PlaylistViewLayout LibraryLayout { get; set; } = ViewLayoutFactory.CreateDefault();
    public List<ColumnPreset> ColumnPresets { get; set; } = ViewLayoutFactory.CreateBuiltInPresets();
    public bool AutoScanOnStartup { get; set; } = true;

    /// <summary>User-configured playback hotkeys (local and optional system-wide).</summary>
    public List<HotkeyBinding> Hotkeys { get; set; } = [];

    /// <summary>Extensions the user last chose in the file-association dialog (never auto-registered).</summary>
    public List<string> AssociationExtensions { get; set; } = [];
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
