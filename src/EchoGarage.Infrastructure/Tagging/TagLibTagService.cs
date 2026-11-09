using EchoGarage.Core.Abstractions;
using EchoGarage.Core.Models;

namespace EchoGarage.Infrastructure.Tagging;

public sealed class TagLibTagService : ITagService
{
    private readonly IAppLogger _logger;

    public TagLibTagService(IAppLogger logger)
    {
        _logger = logger;
    }

    public AudioTagData Read(string path, bool includeCover = true)
    {
        using var file = TagLib.File.Create(path, includeCover ? TagLib.ReadStyle.Average : TagLib.ReadStyle.PictureLazy);
        var tag = file.Tag;
        var properties = file.Properties;
        var picture = includeCover ? SelectCover(tag.Pictures) : null;

        var title = tag.Title ?? string.Empty;
        var artist = FirstNonEmpty(tag.JoinedPerformers, tag.FirstPerformer);
        var albumArtist = FirstNonEmpty(tag.JoinedAlbumArtists, tag.FirstAlbumArtist);
        var album = tag.Album ?? string.Empty;
        var genre = tag.JoinedGenres ?? string.Empty;
        var comment = tag.Comment ?? string.Empty;

        return new AudioTagData(
            title,
            artist,
            album,
            albumArtist,
            genre,
            comment,
            tag.Year == 0 ? null : (int)tag.Year,
            (int)tag.Track,
            (int)tag.Disc,
            (long)Math.Round(properties.Duration.TotalMilliseconds),
            properties.AudioBitrate,
            properties.AudioSampleRate,
            properties.AudioChannels,
            DetectCodec(file.MimeType, path),
            picture?.Data.Data,
            picture?.MimeType);
    }

    public void Write(string path, TagEditModel edit)
    {
        using var file = TagLib.File.Create(path, TagLib.ReadStyle.None);
        if (!file.Writeable)
        {
            throw new InvalidOperationException("该音频文件不支持写入标签。");
        }

        var tag = file.TagTypes == TagLib.TagTypes.None
            ? file.GetTag(DefaultTagTypeFor(path), true)
              ?? throw new NotSupportedException($"无法为该文件创建标签块：{System.IO.Path.GetExtension(path)}")
            : file.Tag;

        tag.Title = edit.Title;
        tag.Performers = Split(edit.Artist);
        tag.Album = edit.Album;
        tag.AlbumArtists = Split(edit.AlbumArtist);
        tag.Genres = Split(edit.Genre);
        tag.Comment = edit.Comment;
        tag.Year = (uint)Math.Max(0, edit.Year);
        tag.Track = (uint)Math.Max(0, edit.TrackNumber);
        tag.Disc = (uint)Math.Max(0, edit.DiscNumber);

        if (edit.RemoveCover)
        {
            tag.Pictures = [];
        }
        else if (edit.NewCoverBytes is { Length: > 0 })
        {
            tag.Pictures =
            [
                new TagLib.Picture(new TagLib.ByteVector(edit.NewCoverBytes))
                {
                    Type = TagLib.PictureType.FrontCover,
                    MimeType = edit.NewCoverMime ?? "image/jpeg",
                    Description = "Cover"
                }
            ];
        }

        file.Save();
    }

    private static TagLib.TagTypes DefaultTagTypeFor(string path) =>
        System.IO.Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".mp3" => TagLib.TagTypes.Id3v2,
            ".flac" => TagLib.TagTypes.Xiph,
            ".m4a" or ".mp4" or ".m4b" => TagLib.TagTypes.Apple,
            ".wav" => TagLib.TagTypes.Id3v2,
            ".aiff" or ".aif" => TagLib.TagTypes.Id3v2,
            ".wma" => TagLib.TagTypes.Asf,
            _ => throw new NotSupportedException($"暂不支持为该格式写入标签：{System.IO.Path.GetExtension(path)}")
        };

    private static TagLib.IPicture? SelectCover(TagLib.IPicture[]? pictures)
    {
        if (pictures is null || pictures.Length == 0)
        {
            return null;
        }

        return pictures.FirstOrDefault(p => p.Type == TagLib.PictureType.FrontCover) ?? pictures[0];
    }

    private static string[] Split(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? string.Empty;

    private static string DetectCodec(string? mimeType, string path)
    {
        var mime = mimeType?.ToLowerInvariant() ?? string.Empty;
        if (mime.Contains("mpeg") || mime.Contains("mp3"))
        {
            return "MP3";
        }

        if (mime.Contains("flac"))
        {
            return "FLAC";
        }

        if (mime.Contains("mp4") || mime.Contains("m4a") || mime.Contains("aac"))
        {
            return "AAC";
        }

        if (mime.Contains("wav") || mime.Contains("riff"))
        {
            return "WAV";
        }

        if (mime.Contains("aiff"))
        {
            return "AIFF";
        }

        if (mime.Contains("asf") || mime.Contains("wma"))
        {
            return "WMA";
        }

        var extension = System.IO.Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        return string.IsNullOrEmpty(extension) ? "AUDIO" : extension;
    }
}
