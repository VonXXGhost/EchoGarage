using EchoGarage.Core.Models;

namespace EchoGarage.App.Services;

public sealed record ColumnsEditResult(PlaylistViewLayout Layout, IReadOnlyList<ColumnPreset> Presets);

public interface IDialogService
{
    ColumnsEditResult? EditColumns(PlaylistViewLayout layout, IReadOnlyList<ColumnPreset> presets);
    SmartPlaylistDefinition? EditSmartPlaylist(string playlistName, SmartPlaylistDefinition definition);
    TagEditModel? EditTags(Track track, AudioTagData currentTags);
    string? AskText(string title, string prompt, string initialValue);
    long? PickPlaylist(IReadOnlyList<Playlist> candidates, string title);
    string? PickFolder(string title, string? initialDirectory = null);
    IReadOnlyList<string> PickAudioFiles();
    string? PickImageFile();
    string? PickSaveFile(string filter, string defaultFileName);
    string? PickOpenFile(string filter);
    bool Confirm(string message, string title = "确认");
    void Info(string message, string title = "提示");
    void Error(string message, string title = "错误");
}
