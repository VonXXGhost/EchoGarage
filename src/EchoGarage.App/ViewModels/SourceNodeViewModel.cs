using CommunityToolkit.Mvvm.ComponentModel;
using EchoGarage.Core.Models;

namespace EchoGarage.App.ViewModels;

public enum SourceKind
{
    Library,
    Folder,
    Playlist
}

public sealed partial class SourceNodeViewModel : ObservableObject
{
    [ObservableProperty]
    private int _count;

    public SourceNodeViewModel(SourceKind kind, string name)
    {
        Kind = kind;
        Name = name;
        Section = kind == SourceKind.Playlist ? "播放列表" : "媒体库";
    }

    public SourceKind Kind { get; }
    public string Name { get; }
    public string Section { get; }
    public LibraryFolder? Folder { get; init; }
    public Playlist? Playlist { get; init; }

    public bool IsSmart => Playlist?.IsSmart == true;
    public bool IsDefault => Playlist?.IsDefault == true;
    public bool IsRemovablePlaylist => Kind == SourceKind.Playlist && Playlist is { IsDefault: false };
    public bool IsFolderNode => Kind == SourceKind.Folder;

    public string CountText => Count.ToString("N0");

    public string Glyph => Kind switch
    {
        SourceKind.Library => "▤",
        SourceKind.Folder => "◫",
        _ => IsSmart ? "✦" : IsDefault ? "◉" : "≡"
    };

    public string Tooltip => Kind switch
    {
        SourceKind.Library => "媒体库中的全部歌曲",
        SourceKind.Folder => Folder?.Path ?? string.Empty,
        _ => IsSmart ? "智能播放列表（按条件自动生成）" : Playlist?.Name ?? string.Empty
    };

    public bool Matches(SourceNodeViewModel other)
    {
        if (Kind != other.Kind)
        {
            return false;
        }

        return Kind switch
        {
            SourceKind.Library => true,
            SourceKind.Folder => Folder?.Id == other.Folder?.Id,
            _ => Playlist?.Id == other.Playlist?.Id
        };
    }

    partial void OnCountChanged(int value) => OnPropertyChanged(nameof(CountText));
}
