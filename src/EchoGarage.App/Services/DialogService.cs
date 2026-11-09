using System.Windows;
using EchoGarage.App.Views;
using EchoGarage.Core.Models;

namespace EchoGarage.App.Services;

public sealed class DialogService : IDialogService
{
    private readonly Window _owner;

    public DialogService(Window owner)
    {
        _owner = owner;
    }

    public ColumnsEditResult? EditColumns(PlaylistViewLayout layout, IReadOnlyList<ColumnPreset> presets)
    {
        var window = new ColumnsWindow(layout, presets) { Owner = _owner };
        return window.ShowDialog() == true ? window.Result : null;
    }

    public SmartPlaylistDefinition? EditSmartPlaylist(string playlistName, SmartPlaylistDefinition definition)
    {
        var window = new SmartPlaylistWindow(playlistName, definition) { Owner = _owner };
        return window.ShowDialog() == true ? window.Result : null;
    }

    public TagEditModel? EditTags(Track track, AudioTagData currentTags)
    {
        var window = new TagPropertiesWindow(track, currentTags) { Owner = _owner };
        return window.ShowDialog() == true ? window.Result : null;
    }

    public string? AskText(string title, string prompt, string initialValue)
    {
        var window = new TextInputWindow(title, prompt, initialValue) { Owner = _owner };
        return window.ShowDialog() == true ? window.Value : null;
    }

    public long? PickPlaylist(IReadOnlyList<Playlist> candidates, string title)
    {
        var window = new PlaylistPickerWindow(candidates, title) { Owner = _owner };
        return window.ShowDialog() == true ? window.SelectedPlaylistId : null;
    }

    public string? PickFolder(string title, string? initialDirectory = null)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title };
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog(_owner) == true ? dialog.FolderName : null;
    }

    public IReadOnlyList<string> PickAudioFiles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "添加音频文件",
            Multiselect = true,
            Filter = "音频文件|*.mp3;*.flac;*.wav;*.m4a;*.aac;*.aiff;*.aif;*.wma;*.mp4|所有文件|*.*"
        };
        return dialog.ShowDialog(_owner) == true ? dialog.FileNames : [];
    }

    public string? PickImageFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择图片",
            Filter = "图片文件|*.jpg;*.jpeg;*.png;*.bmp;*.webp|所有文件|*.*"
        };
        return dialog.ShowDialog(_owner) == true ? dialog.FileName : null;
    }

    public string? PickSaveFile(string filter, string defaultFileName)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存",
            Filter = filter,
            FileName = defaultFileName,
            AddExtension = true
        };
        return dialog.ShowDialog(_owner) == true ? dialog.FileName : null;
    }

    public string? PickOpenFile(string filter)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "打开", Filter = filter };
        return dialog.ShowDialog(_owner) == true ? dialog.FileName : null;
    }

    public void ShowFileAssociations(AppHost host)
    {
        var window = new FileAssociationWindow(host) { Owner = _owner };
        window.ShowDialog();
    }

    public bool Confirm(string message, string title = "确认") =>
        MessageBox.Show(_owner, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

    public void Info(string message, string title = "提示") =>
        MessageBox.Show(_owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void Error(string message, string title = "错误") =>
        MessageBox.Show(_owner, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
}
