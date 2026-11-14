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
        // TagLibSharp treats PictureLazy as a flag that is combined with a quality level.
        // Passing PictureLazy alone also skips the audio properties (file.Properties stays
        // null), which used to throw a NullReferenceException here and made every scanned
        // track fall back to its file name. Average|PictureLazy keeps duration/bitrate while
        // leaving embedded cover bytes on disk.
        var readStyle = includeCover
            ? TagLib.ReadStyle.Average
            : TagLib.ReadStyle.Average | TagLib.ReadStyle.PictureLazy;

        using var file = TagLib.File.Create(path, readStyle);
        var tag = file.Tag;
        var properties = file.Properties;
        var picture = includeCover && tag is not null ? SelectCover(tag.Pictures) : null;

        var title = tag?.Title ?? string.Empty;
        var artist = FirstNonEmpty(tag?.JoinedPerformers, tag?.FirstPerformer);
        var albumArtist = FirstNonEmpty(tag?.JoinedAlbumArtists, tag?.FirstAlbumArtist);
        var composer = FirstNonEmpty(tag?.JoinedComposers, tag?.FirstComposer);
        var album = tag?.Album ?? string.Empty;
        var genre = tag?.JoinedGenres ?? string.Empty;
        var comment = tag?.Comment ?? string.Empty;

        return new AudioTagData(
            title,
            artist,
            album,
            albumArtist,
            composer,
            genre,
            comment,
            tag?.Year is { } year && year != 0 ? (int)year : null,
            (int)(tag?.Track ?? 0),
            (int)(tag?.Disc ?? 0),
            properties is null ? 0 : (long)Math.Round(properties.Duration.TotalMilliseconds),
            properties?.AudioBitrate ?? 0,
            properties?.AudioSampleRate ?? 0,
            properties?.AudioChannels ?? 0,
            DetectCodec(file.MimeType, path),
            picture?.Data.Data,
            picture?.MimeType)
        {
            AdditionalTags = ReadAdditionalTags(file)
        };
    }

    public void Write(string path, TagEditModel edit)
    {
        // When the file is held open (e.g. it is the track currently being played), writing in
        // place would corrupt the decoder's view of the file. Write a full copy next to the
        // original and atomically replace it instead; the existing reader keeps its old handle
        // and finishes playing from the old file object without interruption.
        if (IsExclusivelyLocked(path))
        {
            WriteViaReplacement(path, edit);
            return;
        }

        WriteCore(path, edit);
    }

    private static bool IsExclusivelyLocked(string path)
    {
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private void WriteViaReplacement(string path, TagEditModel edit)
    {
        var directory = System.IO.Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"无法确定文件所在目录：{path}");
        var extension = System.IO.Path.GetExtension(path);
        var tempPath = System.IO.Path.Combine(
            directory,
            $"{System.IO.Path.GetFileNameWithoutExtension(path)}.echogarage-{Guid.NewGuid():N}{extension}");

        try
        {
            File.Copy(path, tempPath, overwrite: true);
            WriteCore(tempPath, edit);
            try
            {
                File.Replace(tempPath, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                _logger.Warn($"Atomic tag replace failed for {path}; falling back to move: {ex.Message}");
                File.Move(tempPath, path, overwrite: true);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // Ignore cleanup errors.
            }
        }
    }

    private void WriteCore(string path, TagEditModel edit)
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
        tag.Composers = Split(edit.Composer);
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

    private IReadOnlyList<AudioTagEntry> ReadAdditionalTags(TagLib.File file)
    {
        var entries = new List<AudioTagEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // TagLib 的 file.Tag 可能是 NonContainer.Tag（ID3v2 + ID3v1 的包装），
            // 因此按具体标签类型分别取出，避免 switch 匹配不到而丢失额外标签。
            if (file.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3)
            {
                CollectId3Frames(id3, entries, seen);
            }

            if (file.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph)
            {
                CollectXiphFields(xiph, entries, seen);
            }

            if (file.GetTag(TagLib.TagTypes.Apple) is TagLib.Mpeg4.AppleTag apple)
            {
                CollectAppleBoxes(apple, entries, seen);
            }

            if (file.GetTag(TagLib.TagTypes.Asf) is TagLib.Asf.Tag asf)
            {
                CollectAsfDescriptors(asf, entries, seen);
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"Reading additional tags failed: {ex.Message}");
        }

        return entries;
    }

    private static void CollectId3Frames(TagLib.Id3v2.Tag tag, List<AudioTagEntry> entries, HashSet<string> seen)
    {
        foreach (var frame in tag.GetFrames())
        {
            try
            {
                if (frame is TagLib.Id3v2.UserTextInformationFrame user)
                {
                    var name = string.IsNullOrWhiteSpace(user.Description)
                        ? "TXXX"
                        : $"TXXX ({user.Description})";
                    AddEntry(entries, seen, name, string.Join("; ", user.Text));
                }
                else if (frame is TagLib.Id3v2.TextInformationFrame text)
                {
                    var frameId = frame.FrameId.ToString();
                    if (KnownId3FrameIds.Contains(frameId))
                    {
                        continue;
                    }

                    AddEntry(entries, seen, Id3FrameLabels.GetValueOrDefault(frameId, frameId), string.Join("; ", text.Text));
                }
            }
            catch
            {
                // Ignore frames whose text cannot be read (binary/unsupported frames).
            }
        }
    }

    private static void CollectXiphFields(TagLib.Ogg.XiphComment tag, List<AudioTagEntry> entries, HashSet<string> seen)
    {
        foreach (var field in tag)
        {
            if (KnownXiphFields.Contains(field))
            {
                continue;
            }

            var values = tag.GetField(field);
            AddEntry(entries, seen, XiphLabels.GetValueOrDefault(field.ToUpperInvariant(), field), string.Join("; ", values ?? []));
        }
    }

    private static void CollectAppleBoxes(TagLib.Mpeg4.AppleTag tag, List<AudioTagEntry> entries, HashSet<string> seen)
    {
        foreach (var box in tag)
        {
            CollectAppleBox(box, entries, seen);
        }
    }

    private static void CollectAppleBox(TagLib.Mpeg4.Box box, List<AudioTagEntry> entries, HashSet<string> seen)
    {
        try
        {
            if (box is TagLib.Mpeg4.AppleDataBox data && !string.IsNullOrWhiteSpace(data.Text))
            {
                var atom = data.BoxType.ToString();
                if (!KnownAppleAtoms.Contains(atom))
                {
                    AddEntry(entries, seen, AppleAtomLabels.GetValueOrDefault(atom, atom), data.Text);
                }
            }

            if (box.HasChildren)
            {
                foreach (var child in box.Children)
                {
                    CollectAppleBox(child, entries, seen);
                }
            }
        }
        catch
        {
            // Ignore malformed atoms.
        }
    }

    private static void CollectAsfDescriptors(TagLib.Asf.Tag tag, List<AudioTagEntry> entries, HashSet<string> seen)
    {
        foreach (var descriptor in tag.GetDescriptors())
        {
            if (KnownAsfNames.Contains(descriptor.Name))
            {
                continue;
            }

            AddEntry(entries, seen, descriptor.Name, descriptor.ToString());
        }
    }

    private static void AddEntry(List<AudioTagEntry> entries, HashSet<string> seen, string? name, string? value)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        name = name.Trim();
        value = value.Trim();
        if (value.Length > 400)
        {
            value = string.Concat(value.AsSpan(0, 400), "…");
        }

        if (!seen.Add(name))
        {
            return;
        }

        entries.Add(new AudioTagEntry(name, value));
    }

    private static readonly HashSet<string> KnownId3FrameIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "TIT2", "TPE1", "TPE2", "TALB", "TCOM", "TCON", "TYER", "TDRC", "TRCK", "TPOS", "COMM", "APIC", "USLT", "SYLT"
    };

    private static readonly Dictionary<string, string> Id3FrameLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TBPM"] = "BPM",
        ["TPE3"] = "指挥",
        ["TIT1"] = "分组",
        ["TCOP"] = "版权",
        ["TKEY"] = "初始调",
        ["TSRC"] = "ISRC",
        ["TPUB"] = "发行方",
        ["TSSE"] = "编码器",
        ["TENC"] = "编码者",
        ["TLEN"] = "时长（毫秒）",
        ["TSOT"] = "标题排序",
        ["TSOA"] = "专辑排序",
        ["TSOP"] = "艺术家排序",
        ["TSO2"] = "专辑艺术家排序",
        ["TSOC"] = "作曲家排序",
        ["TMOO"] = "情绪",
        ["TOPE"] = "原艺术家",
        ["TOAL"] = "原专辑",
        ["TORY"] = "原年份",
        ["TDOR"] = "原发行日期",
        ["TDRL"] = "发行日期",
        ["TDEN"] = "编码日期",
        ["TDTG"] = "标记日期",
        ["TIPL"] = "参与人员",
        ["TMCL"] = "乐器",
        ["TCMP"] = "合辑",
        ["MVNM"] = "乐章",
        ["MVIN"] = "乐章编号",
        ["GRP1"] = "分组",
        ["POPM"] = "评分",
        ["PCNT"] = "播放次数",
        ["MCDI"] = "CD 标识",
        ["WXXX"] = "网址",
        ["WOAR"] = "艺术家网址",
        ["PRIV"] = "私有数据"
    };

    private static readonly HashSet<string> KnownXiphFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "TITLE", "ARTIST", "ALBUM", "ALBUMARTIST", "ALBUM ARTIST", "COMPOSER", "GENRE", "COMMENT",
        "DATE", "YEAR", "TRACKNUMBER", "DISCNUMBER", "METADATA_BLOCK_PICTURE", "COVERART", "COVERARTMIME"
    };

    private static readonly Dictionary<string, string> XiphLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BPM"] = "BPM",
        ["CONDUCTOR"] = "指挥",
        ["GROUPING"] = "分组",
        ["COPYRIGHT"] = "版权",
        ["INITIALKEY"] = "初始调",
        ["ISRC"] = "ISRC",
        ["PUBLISHER"] = "发行方",
        ["REMIXEDBY"] = "混音",
        ["SUBTITLE"] = "副标题",
        ["DESCRIPTION"] = "描述",
        ["TRACKTOTAL"] = "音轨总数",
        ["DISCTOTAL"] = "碟片总数",
        ["MUSICBRAINZ_TRACKID"] = "MusicBrainz 音轨",
        ["MUSICBRAINZ_ALBUMID"] = "MusicBrainz 专辑",
        ["MUSICBRAINZ_ARTISTID"] = "MusicBrainz 艺术家",
        ["MUSICBRAINZ_ALBUMARTISTID"] = "MusicBrainz 专辑艺术家",
        ["MUSICBRAINZ_RELEASEGROUPID"] = "MusicBrainz 专辑组",
        ["MUSICBRAINZ_RELEASETRACKID"] = "MusicBrainz 发行音轨",
        ["MUSICBRAINZ_DISCID"] = "MusicBrainz 碟片",
        ["REPLAYGAIN_TRACK_GAIN"] = "ReplayGain 音轨增益",
        ["REPLAYGAIN_TRACK_PEAK"] = "ReplayGain 音轨峰值",
        ["REPLAYGAIN_ALBUM_GAIN"] = "ReplayGain 专辑增益",
        ["REPLAYGAIN_ALBUM_PEAK"] = "ReplayGain 专辑峰值",
        ["ENCODER"] = "编码器",
        ["ENCODEDBY"] = "编码者",
        ["ENCODING"] = "编码设置",
        ["ARTISTSORT"] = "艺术家排序",
        ["ALBUMSORT"] = "专辑排序",
        ["TITLESORT"] = "标题排序",
        ["ALBUMARTISTSORT"] = "专辑艺术家排序",
        ["COMPOSERSORT"] = "作曲家排序",
        ["LYRICS"] = "歌词",
        ["PERFORMERROLE"] = "演出者角色",
        ["DATETAGGED"] = "标记日期",
        ["AMAZONID"] = "Amazon ID",
        ["MUSICIPID"] = "MusicIP ID",
        ["RELEASESTATUS"] = "发行状态",
        ["RELEASETYPE"] = "发行类型",
        ["RELEASECOUNTRY"] = "发行国家",
        ["RELEASEDATE"] = "发行日期",
        ["ORIGINALDATE"] = "原发行日期"
    };

    private static readonly HashSet<string> KnownAppleAtoms = new(StringComparer.OrdinalIgnoreCase)
    {
        "©nam", "©ART", "©alb", "aART", "©wrt", "©gen", "gnre", "©cmt", "©day", "trkn", "disk", "covr"
    };

    private static readonly Dictionary<string, string> AppleAtomLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["©too"] = "编码器",
        ["©lyr"] = "歌词",
        ["tmpo"] = "BPM",
        ["cpil"] = "合辑",
        ["©grp"] = "分组",
        ["cprt"] = "版权",
        ["pgap"] = "无缝专辑",
        ["©wrk"] = "作品",
        ["©mvn"] = "乐章",
        ["©mvi"] = "乐章编号",
        ["©mvc"] = "乐章总数",
        ["soal"] = "专辑排序",
        ["soar"] = "艺术家排序",
        ["soaa"] = "专辑艺术家排序",
        ["soco"] = "作曲家排序",
        ["sonm"] = "标题排序",
        ["©pub"] = "发行方",
        ["©enc"] = "编码者"
    };

    private static readonly HashSet<string> KnownAsfNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Title", "Author", "Description", "WM/AlbumTitle", "WM/AlbumArtist", "WM/Composer",
        "WM/Genre", "WM/Year", "WM/TrackNumber", "WM/PartOfSet", "WM/Picture"
    };

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
