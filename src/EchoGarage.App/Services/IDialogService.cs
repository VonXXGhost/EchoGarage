using EchoGarage.Core.Models;

namespace EchoGarage.App.Services;

public sealed record ColumnsEditResult(PlaylistViewLayout Layout, IReadOnlyList<ColumnPreset> Presets);

public sealed record ConversionSettingsResult(IReadOnlyList<ConversionPreset> Presets);

/// <summary>Batch-level answer for "target file already exists" conflicts.</summary>
public enum ConversionConflictChoice
{
    OverwriteAll,
    SkipConflicts,
    RenameAll,
    Cancel
}

public interface IDialogService
{
    ColumnsEditResult? EditColumns(PlaylistViewLayout layout, IReadOnlyList<ColumnPreset> presets);
    ConversionSettingsResult? EditConversionSettings(IReadOnlyList<ConversionPreset> presets);
    SmartPlaylistDefinition? EditSmartPlaylist(string playlistName, SmartPlaylistDefinition definition);
    TagBatchEdit? EditTags(IReadOnlyList<Track> tracks, IReadOnlyList<AudioTagData> currentTags);
    WebTagApplyPlan? SearchWebTags(IReadOnlyList<Track> tracks);
    SearchQuery? ShowAdvancedSearch(SearchQuery? current);
    string? AskText(string title, string prompt, string initialValue);
    long? PickPlaylist(IReadOnlyList<Playlist> candidates, string title);
    string? PickFolder(string title, string? initialDirectory = null);
    IReadOnlyList<string> PickAudioFiles();
    string? PickImageFile();
    string? PickSaveFile(string filter, string defaultFileName);
    string? PickOpenFile(string filter);
    void ShowFileAssociations(AppHost host);
    bool Confirm(string message, string title = "确认");
    void Info(string message, string title = "提示");
    void Error(string message, string title = "错误");
    ConversionConflictChoice ShowConversionConflict(int conflictCount);
}
