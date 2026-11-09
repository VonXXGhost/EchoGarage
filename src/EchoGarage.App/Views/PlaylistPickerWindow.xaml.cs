using System.Windows;
using System.Windows.Input;
using EchoGarage.Core.Models;

namespace EchoGarage.App.Views;

public partial class PlaylistPickerWindow : ThemedWindow
{
    public PlaylistPickerWindow(IReadOnlyList<Playlist> playlists, string title)
    {
        InitializeComponent();
        Title = title;
        PlaylistList.ItemsSource = playlists;
        PlaylistList.SelectedIndex = playlists.Count > 0 ? 0 : -1;
    }

    public long? SelectedPlaylistId { get; private set; }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (PlaylistList.SelectedItem is Playlist playlist)
        {
            SelectedPlaylistId = playlist.Id;
            DialogResult = true;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
        }
        else if (e.Key == Key.Enter)
        {
            OnOk(sender, e);
        }
    }
}
