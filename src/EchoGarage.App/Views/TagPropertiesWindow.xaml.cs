using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using EchoGarage.App.Services;
using EchoGarage.Core.Models;

namespace EchoGarage.App.Views;

public partial class TagPropertiesWindow : ThemedWindow
{
    private readonly Track _track;
    private readonly AudioTagData _current;
    private byte[]? _coverBytes;
    private string? _coverMime;
    private bool _removeCover;

    public TagPropertiesWindow(Track track, AudioTagData current)
    {
        _track = track;
        _current = current;
        InitializeComponent();

        FilePathText.Text = track.Path;
        FileInfoText.Text = $"{track.FormatDisplay} · {DisplayFormatter.SampleRate(track.SampleRate)}Hz · " +
                            $"{DisplayFormatter.Bitrate(track.Bitrate)}bps · {track.Channels} 声道 · " +
                            $"{DisplayFormatter.Duration(track.DurationMs)} · {DisplayFormatter.FileSize(track.FileSize)}";
        ModifiedText.Text = $"修改时间：{DisplayFormatter.Date(track.ModifiedAtUtc)}";

        TitleBox.Text = current.Title;
        ArtistBox.Text = current.Artist;
        AlbumBox.Text = current.Album;
        AlbumArtistBox.Text = current.AlbumArtist;
        ComposerBox.Text = current.Composer;
        GenreBox.Text = current.Genre;
        YearBox.Text = current.Year?.ToString() ?? string.Empty;
        TrackBox.Text = current.TrackNumber > 0 ? current.TrackNumber.ToString() : string.Empty;
        DiscBox.Text = current.DiscNumber > 0 ? current.DiscNumber.ToString() : string.Empty;
        CommentBox.Text = current.Comment;

        SetCover(current.CoverBytes);
    }

    public TagEditModel? Result { get; private set; }

    private void SetCover(byte[]? bytes)
    {
        _coverBytes = bytes;
        _removeCover = bytes is null && _coverBytes is null;
        if (bytes is null || bytes.Length == 0)
        {
            CoverImage.Source = null;
            CoverPlaceholder.Visibility = Visibility.Visible;
            RemoveCoverButton.IsEnabled = false;
            return;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 360;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            CoverImage.Source = image;
            CoverPlaceholder.Visibility = Visibility.Collapsed;
            RemoveCoverButton.IsEnabled = true;
        }
        catch
        {
            CoverImage.Source = null;
            CoverPlaceholder.Visibility = Visibility.Visible;
        }
    }

    private void OnPickCover(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择封面图片",
            Filter = "图片文件|*.jpg;*.jpeg;*.png;*.bmp;*.webp|所有文件|*.*"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var bytes = File.ReadAllBytes(dialog.FileName);
            _coverMime = System.IO.Path.GetExtension(dialog.FileName).ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".bmp" => "image/bmp",
                ".webp" => "image/webp",
                _ => "image/jpeg"
            };
            _removeCover = false;
            SetCover(bytes);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"读取图片失败：{ex.Message}", "EchoGarage", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnRemoveCover(object sender, RoutedEventArgs e)
    {
        _coverBytes = null;
        _coverMime = null;
        _removeCover = true;
        CoverImage.Source = null;
        CoverPlaceholder.Visibility = Visibility.Visible;
        RemoveCoverButton.IsEnabled = false;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Result = new TagEditModel
        {
            Title = TitleBox.Text.Trim(),
            Artist = ArtistBox.Text.Trim(),
            Album = AlbumBox.Text.Trim(),
            AlbumArtist = AlbumArtistBox.Text.Trim(),
            Composer = ComposerBox.Text.Trim(),
            Genre = GenreBox.Text.Trim(),
            Comment = CommentBox.Text.Trim(),
            Year = ParseInt(YearBox.Text),
            TrackNumber = ParseInt(TrackBox.Text),
            DiscNumber = ParseInt(DiscBox.Text),
            NewCoverBytes = _removeCover ? null : _coverBytes,
            NewCoverMime = _coverMime,
            RemoveCover = _removeCover
        };

        DialogResult = true;
    }

    private static int ParseInt(string text) => int.TryParse(text, out var value) && value > 0 ? value : 0;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
        }
    }
}
