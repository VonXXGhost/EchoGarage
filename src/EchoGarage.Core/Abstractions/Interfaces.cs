using EchoGarage.Core.Models;
using EchoGarage.Core.Services;

namespace EchoGarage.Core.Abstractions;

public interface ILibraryRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LibraryFolder>> GetFoldersAsync(CancellationToken cancellationToken = default);
    Task<LibraryFolder> AddFolderAsync(string path, CancellationToken cancellationToken = default);
    Task RemoveFolderAsync(long folderId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Track>> GetLibraryTracksAsync(long? folderId = null, CancellationToken cancellationToken = default);
    Task<int> GetLibraryTrackCountAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Track>> GetTracksInFolderAsync(string folderPath, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Track>> GetTracksInDirectoryAsync(string directoryPath, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TrackState>> GetTrackStatesAsync(long? folderId = null, CancellationToken cancellationToken = default);
    Task<Track?> GetTrackByPathAsync(string path, CancellationToken cancellationToken = default);
    Task UpsertTrackAsync(Track track, CancellationToken cancellationToken = default);
    Task UpsertTracksAsync(IReadOnlyList<Track> tracks, CancellationToken cancellationToken = default);
    Task MarkTrackMissingAsync(string path, CancellationToken cancellationToken = default);
    Task MarkTracksMissingAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default);
    Task MarkTracksMissingUnderFolderAsync(string folderPath, CancellationToken cancellationToken = default);
    Task UpdateFolderScanTimeAsync(long folderId, DateTime scannedAtUtc, CancellationToken cancellationToken = default);
    Task RecordPlaybackAsync(string path, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<long, int>> GetFolderTrackCountsAsync(CancellationToken cancellationToken = default);
}

public interface IPlaylistRepository
{
    Task EnsureDefaultsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Playlist>> GetPlaylistsAsync(CancellationToken cancellationToken = default);
    Task<Playlist?> GetDefaultPlaylistAsync(CancellationToken cancellationToken = default);
    Task<Playlist> CreatePlaylistAsync(string name, PlaylistKind kind, SmartPlaylistDefinition? definition,
        CancellationToken cancellationToken = default);
    Task RenamePlaylistAsync(long playlistId, string name, CancellationToken cancellationToken = default);
    Task DeletePlaylistAsync(long playlistId, CancellationToken cancellationToken = default);
    Task UpdatePlaylistLayoutAsync(long playlistId, string layoutJson, CancellationToken cancellationToken = default);
    Task UpdatePlaylistRulesAsync(long playlistId, SmartPlaylistDefinition definition,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Track>> GetPlaylistTracksAsync(Playlist playlist, CancellationToken cancellationToken = default);
    Task AddPlaylistItemsAsync(long playlistId, IReadOnlyList<Track> tracks,
        CancellationToken cancellationToken = default);
    Task RemovePlaylistItemsAsync(IReadOnlyList<long> playlistItemIds, CancellationToken cancellationToken = default);
    Task ClearPlaylistAsync(long playlistId, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<long, int>> GetPlaylistCountsAsync(IReadOnlyList<Playlist> playlists,
        CancellationToken cancellationToken = default);
}

public interface ITagService
{
    AudioTagData Read(string path);
    void Write(string path, TagEditModel edit);
}

public interface ICoverCache
{
    string CoversDirectory { get; }
    string? Resolve(string? coverKey);
    Task<string?> EnsureAsync(CoverRequest request, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
    Task RelocateAsync(string newRoot, CancellationToken cancellationToken = default);
}

public interface ILibraryScanner
{
    Task<ScanResult> ScanFolderAsync(LibraryFolder folder, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task<Track?> IndexFileAsync(string path, bool inLibrary, long? folderId = null,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Track>> IndexFilesAsync(IEnumerable<string> paths, bool inLibrary,
        IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default);
}

public interface IFileWatcherService : IDisposable
{
    event EventHandler<LibraryTrackChangedEventArgs>? Changed;
    bool IsRunning { get; }
    void Start(IReadOnlyList<LibraryFolder> folders);
    void Stop();
}

public interface ISettingsService
{
    AppSettings Current { get; }
    event EventHandler<AppSettings>? Changed;
    Task LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(CancellationToken cancellationToken = default);
    Task ExportAsync(string path, CancellationToken cancellationToken = default);
    Task ImportAsync(string path, CancellationToken cancellationToken = default);
}

public interface IAudioPlayer : IDisposable
{
    event EventHandler<AudioPlaybackStateChangedEventArgs>? StateChanged;
    event EventHandler? PlaybackEnded;
    AudioPlaybackState State { get; }
    TimeSpan Position { get; }
    TimeSpan Duration { get; }
    string? CurrentTrackPath { get; }
    double Volume { get; set; }
    string OutputDescription { get; }
    IReadOnlyList<AudioDeviceInfo> GetOutputDevices();
    void Warmup();
    Task LoadAsync(string path, bool playWhenReady, CancellationToken cancellationToken = default);
    void Play();
    void Pause();
    void Stop();
    void Seek(TimeSpan position);
    void ConfigureOutput(string? deviceId, OutputMode mode);
}

public interface IAppLogger
{
    string LogDirectory { get; }
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? exception = null);
    void Debug(string message);
}
