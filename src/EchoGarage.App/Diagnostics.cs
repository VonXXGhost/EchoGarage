using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EchoGarage.App.Services;
using EchoGarage.App.ViewModels;
using EchoGarage.App.Views;
using EchoGarage.Core.Models;
using EchoGarage.Core.Services;

namespace EchoGarage.App;

/// <summary>
/// Developer-only visual diagnostics used by the hidden command line switches
/// (--screenshot / --screenshot-all). Keeps UI verification possible on high-DPI
/// machines where external screen capture is unreliable.
/// </summary>
internal static class Diagnostics
{
    public static async Task CaptureAllAsync(AppHost host, MainWindow main, string directory)
    {
        Directory.CreateDirectory(directory);
        var vm = main.ViewModel;

        await CaptureWindowAsync(new TextInputWindow("新建播放列表", "播放列表名称", "新播放列表"), directory, "dialog-input.png", main);

        var firstTrack = vm.Tracks.FirstOrDefault()?.Track;
        if (firstTrack is not null)
        {
            AudioTagData tags;
            try
            {
                tags = host.TagService.Read(firstTrack.Path);
            }
            catch
            {
                tags = new AudioTagData("样例", "艺术家", "专辑", "艺术家", "作曲家", "流派", string.Empty, 2024, 1, 1,
                    180000, 320, 44100, 2, "FLAC", null, null);
            }

            await CaptureWindowAsync(new TagPropertiesWindow([firstTrack], [tags]), directory, "dialog-tags.png", main);

            var multiTracks = vm.Tracks.Take(3).Select(r => r.Track).ToList();
            if (multiTracks.Count > 1)
            {
                var multiTags = new List<AudioTagData>();
                foreach (var track in multiTracks)
                {
                    try
                    {
                        multiTags.Add(host.TagService.Read(track.Path));
                    }
                    catch
                    {
                        multiTags.Add(tags);
                    }
                }

                await CaptureWindowAsync(
                    new TagPropertiesWindow(multiTracks, multiTags),
                    directory,
                    "dialog-tags-multi.png",
                    main);
            }
        }

        await CaptureWindowAsync(new SmartPlaylistWindow("智能播放列表", new SmartPlaylistDefinition
        {
            MatchMode = SmartMatchMode.Any,
            Rules =
            [
                new SmartRule { Field = "artist", Operator = "contains", Value = "测试" },
                new SmartRule { Field = "durationSeconds", Operator = "greaterThan", Value = "10" }
            ]
        }), directory, "dialog-smart.png", main);

        await CaptureWindowAsync(new ColumnsWindow(vm.CurrentLayout, vm.Presets.ToList()), directory, "dialog-columns.png", main);
        await CaptureWindowAsync(
            new SearchWindow(new SearchQuery("kizuna", [SearchField.Composer, SearchField.Artist])),
            directory,
            "dialog-search.png",
            main);
        await CaptureWindowAsync(new FieldGuideWindow(), directory, "dialog-field-guide.png", main);
        await CaptureWindowAsync(new SettingsWindow(host, vm), directory, "dialog-settings.png", main);
        var dialogs = new DialogService(main);
        await CaptureWindowAsync(
            new ConversionSettingsWindow(host.Settings.Current.ConversionPresets, dialogs),
            directory,
            "dialog-conversion-settings.png",
            main);
        await CaptureWindowAsync(new ConversionConflictWindow(3), directory, "dialog-conversion-conflict.png", main);
        await CaptureWindowAsync(new FileAssociationWindow(host), directory, "dialog-associations.png", main);
        await CaptureWindowAsync(new PlaylistPickerWindow(
            [new Playlist { Id = 2, Name = "测试播放列表" }, new Playlist { Id = 3, Name = "另一个列表" }],
            "添加到播放列表"), directory, "dialog-picker.png", main);

        host.Logger.Info($"Diagnostic screenshots written to {directory}.");
    }

    private static async Task CaptureWindowAsync(Window window, string directory, string fileName, Window owner)
    {
        try
        {
            window.Owner = owner;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            window.ShowInTaskbar = false;
            window.Show();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(450);
            SaveVisual(window, Path.Combine(directory, fileName), (int)window.ActualWidth, (int)window.ActualHeight);
        }
        catch (Exception ex)
        {
            owner.Dispatcher.Invoke(() => MessageBox.Show(ex.ToString(), "Diagnostics"));
        }
        finally
        {
            window.Close();
        }
    }

    private static void SaveVisual(Visual visual, string path, int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
