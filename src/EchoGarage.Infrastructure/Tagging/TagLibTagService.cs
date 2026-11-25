using System.Globalization;
using System.Text;
using EchoGarage.Core.Abstractions;
using EchoGarage.Core.Models;
using EchoGarage.Core.Services;

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

        // Nero AAC 等旧式标签器把音轨号/碟片号写在 ---- 自由标签（com.apple.iTunes:track / disc）
        // 而不是标准 trkn / disk atom；foobar2000 认得这种写法，TagLib# 不认，这里做读取回退。
        // 只有标准 atom 缺省时才回退，避免有冲突值的老文件覆盖标准值。
        var trackNumber = (int)(tag?.Track ?? 0);
        var discNumber = (int)(tag?.Disc ?? 0);
        var consumedFreeformKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (file.GetTag(TagLib.TagTypes.Apple) is TagLib.Mpeg4.AppleTag appleTag)
        {
            if (trackNumber <= 0 &&
                TryReadLegacyNumber(appleTag, AppleLegacyTagNumber.TrackName, out var legacyTrack))
            {
                trackNumber = legacyTrack.Value;
                consumedFreeformKeys.Add(legacyTrack.Key);
            }

            if (discNumber <= 0 &&
                TryReadLegacyNumber(appleTag, AppleLegacyTagNumber.DiscName, out var legacyDisc))
            {
                discNumber = legacyDisc.Value;
                consumedFreeformKeys.Add(legacyDisc.Key);
            }
        }

        return new AudioTagData(
            title,
            artist,
            album,
            albumArtist,
            composer,
            genre,
            comment,
            tag?.Year is { } year && year != 0 ? (int)year : null,
            trackNumber,
            discNumber,
            properties is null ? 0 : (long)Math.Round(properties.Duration.TotalMilliseconds),
            properties?.AudioBitrate ?? 0,
            properties?.AudioSampleRate ?? 0,
            properties?.AudioChannels ?? 0,
            DetectCodec(file.MimeType, path),
            picture?.Data.Data,
            picture?.MimeType)
        {
            AdditionalTags = ReadAdditionalTags(file, consumedFreeformKeys),
            YearText = ReadYearText(file),
            BitsPerSample = properties is { BitsPerSample: > 0 } audioProperties
                ? audioProperties.BitsPerSample
                : null,
            Bpm = ReadBpm(file)
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

        // 只有用户真正修改了年份字段才写入，否则保留文件里原始的年份/日期文本
        // （如 m4a 的 ©day="2025.06.04"、ID3 的 TDRC="2023-05-12"），
        // 避免编辑标题等其它字段时把详细日期覆盖成纯年份。
        if (edit.YearChanged)
        {
            var yearNumber = ResolveYearNumber(edit);
            tag.Year = yearNumber;
            WriteRawYearText(file, path, ResolveYearText(edit, yearNumber));
        }

        // 旧式 Nero/Apple 自由标签里可能保存着音轨号/碟片号；这类文件继续写回同一处，
        // 不要顺带创建标准 trkn / disk atom，否则同一个值会同时以两种形式存在，
        // foobar2000 等工具会读到互相冲突的数字。
        var appleTag = file.GetTag(TagLib.TagTypes.Apple, false) as TagLib.Mpeg4.AppleTag;
        WriteNumberField(
            appleTag,
            tag.Track,
            Math.Max(0, edit.TrackNumber),
            AppleLegacyTagNumber.TrackName,
            value => tag.Track = value);
        WriteNumberField(
            appleTag,
            tag.Disc,
            Math.Max(0, edit.DiscNumber),
            AppleLegacyTagNumber.DiscName,
            value => tag.Disc = value);

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

        ApplyAdditionalTagChanges(file, edit.AdditionalTagChanges);

        // ADR-034：仅当本次编辑显式改动了 BPM 时才写回，避免编辑其它字段时丢失原有小数或触发无谓修改。
        if (edit.BpmChanged)
        {
            WriteBpm(file, path, edit.Bpm, edit.RoundId3BpmToInteger);
        }

        file.Save();
    }

    /// <summary>旧式 M4A 自由标签里读到的音轨号/碟片号（mean / name / 原文 / 数值）。</summary>
    private readonly record struct LegacyAppleNumber(string Mean, string Name, string RawText, int Value)
    {
        public string Key => AdditionalTagKey.AppleFreeform(Mean, Name);
    }

    /// <summary>
    /// 在 Apple 标签的自由标签（----）中查找 name 为 track / disc、mean 为 com.apple.iTunes
    /// 且能解析出正整数的条目。这正是 foobar2000 读取 Nero AAC 输出的方式；
    /// TagLib# 只认标准 trkn / disk atom，不会映射这些自由标签。
    /// </summary>
    private static bool TryReadLegacyNumber(
        TagLib.Mpeg4.AppleTag apple,
        string alias,
        out LegacyAppleNumber number)
    {
        number = default;
        foreach (var box in apple)
        {
            try
            {
                if (AtomName(box.BoxType) != "----")
                {
                    continue;
                }

                var mean = ChildText(box, "mean");
                var name = ChildText(box, "name");
                if (!string.Equals(mean, AppleLegacyTagNumber.ITunesMean, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(name, alias, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var text = box.Children.OfType<TagLib.Mpeg4.AppleDataBox>().FirstOrDefault()?.Text;
                if (!AppleLegacyTagNumber.TryParse(text, out var value))
                {
                    continue;
                }

                number = new LegacyAppleNumber(mean, name, text!.Trim(), value);
                return true;
            }
            catch
            {
                // 无法解析的自由标签盒直接跳过，继续找下一个。
            }
        }

        return false;
    }

    /// <summary>
    /// 写入音轨号/碟片号：文件只有旧式自由标签（没有标准 atom）时更新该自由标签并保留 "/总数"，
    /// 值未变化时完全不写；其它情况维持原来的标准标签写入路径。
    /// 这样编辑旧式文件时既能让 foobar2000 看到新值，又不会额外生成重复的标准 atom。
    /// </summary>
    private static void WriteNumberField(
        TagLib.Mpeg4.AppleTag? apple,
        uint standardValue,
        int desiredValue,
        string alias,
        Action<uint> writeStandard)
    {
        if (apple is not null &&
            standardValue == 0 &&
            TryReadLegacyNumber(apple, alias, out var legacy))
        {
            if (desiredValue == legacy.Value)
            {
                return;
            }

            apple.SetDashBox(legacy.Mean, legacy.Name, AppleLegacyTagNumber.Format(legacy.RawText, desiredValue));
            return;
        }

        writeStandard((uint)Math.Max(0, desiredValue));
    }

    /// <summary>
    /// 读取文件中的原始年份/日期文本（保留月份/日期等细节），供属性面板“年份”字段显示。
    /// 优先级与常见的年份写入位置一致：
    /// Apple ©day → ID3v2 TDRC（其次 TYER）→ Xiph DATE（其次 YEAR）→ ASF WM/Year。
    /// 都取不到时退回 <c>tag.Year</c> 的 4 位年份字符串。
    /// </summary>
    private string? ReadYearText(TagLib.File file)
    {
        try
        {
            if (file.GetTag(TagLib.TagTypes.Apple) is TagLib.Mpeg4.AppleTag apple)
            {
                var day = FirstNonEmpty(apple.GetText(
                    TagLib.ByteVector.FromString("©day", TagLib.StringType.Latin1)));
                if (day.Length > 0)
                {
                    return day;
                }
            }

            if (file.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3v2)
            {
                var recordingTime = FirstNonEmpty(id3v2.GetTextAsString(
                    TagLib.ByteVector.FromString("TDRC", TagLib.StringType.Latin1)));
                if (recordingTime.Length > 0)
                {
                    return recordingTime;
                }

                var year = FirstNonEmpty(id3v2.GetTextAsString(
                    TagLib.ByteVector.FromString("TYER", TagLib.StringType.Latin1)));
                if (year.Length > 0)
                {
                    return year;
                }
            }

            if (file.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph)
            {
                var date = FirstNonEmpty(xiph.GetField("DATE"));
                if (date.Length == 0)
                {
                    date = FirstNonEmpty(xiph.GetField("YEAR"));
                }

                if (date.Length > 0)
                {
                    return date;
                }
            }

            if (file.GetTag(TagLib.TagTypes.Asf) is TagLib.Asf.Tag asf)
            {
                var year = FirstNonEmpty(asf.GetDescriptorString("WM/Year"));
                if (year.Length > 0)
                {
                    return year;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"Reading raw year text failed: {ex.Message}");
        }

        return file.Tag?.Year is { } fallback && fallback > 0
            ? fallback.ToString(CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>
    /// 跨格式读取标准 BPM 标签并尽量保留小数（ADR-034 / KB-021 §6）：
    /// 直接从各容器原生帧读取文本解析 double（绕过 TagLib <c>uint</c> 截断），
    /// Apple M4A/MP4 的二进制整型 <c>tmpo</c> atom 则走 <c>AppleTag.BeatsPerMinute</c>。
    /// </summary>
    private double? ReadBpm(TagLib.File file)
    {
        try
        {
            if (file.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3v2)
            {
                var text = FirstNonEmpty(id3v2.GetTextAsString(
                    TagLib.ByteVector.FromString("TBPM", TagLib.StringType.Latin1)));
                if (BpmFormatter.TryParse(text, out var parsed))
                {
                    return parsed;
                }
            }

            if (file.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph)
            {
                var text = FirstNonEmpty(xiph.GetField("BPM"));
                if (text.Length == 0)
                {
                    text = FirstNonEmpty(xiph.GetField("TBPM"));
                }

                if (text.Length == 0)
                {
                    text = FirstNonEmpty(xiph.GetField("TEMPO"));
                }

                if (BpmFormatter.TryParse(text, out var parsed))
                {
                    return parsed;
                }
            }

            if (file.GetTag(TagLib.TagTypes.Apple) is TagLib.Mpeg4.AppleTag apple &&
                apple.BeatsPerMinute is > 0 and var appleBpm &&
                BpmFormatter.IsValid(appleBpm))
            {
                return appleBpm;
            }

            if (file.GetTag(TagLib.TagTypes.Asf) is TagLib.Asf.Tag asf)
            {
                var text = FirstNonEmpty(asf.GetDescriptorString("WM/BeatsPerMinute"));
                if (BpmFormatter.TryParse(text, out var parsed))
                {
                    return parsed;
                }

                if (asf.BeatsPerMinute is > 0 and var asfBpm && BpmFormatter.IsValid(asfBpm))
                {
                    return asfBpm;
                }
            }

            if (file.Tag?.BeatsPerMinute is > 0 and var genericBpm && BpmFormatter.IsValid(genericBpm))
            {
                return genericBpm;
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"Reading BPM tag failed: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// 按音频格式写回标准 BPM 标签：FLAC/Xiph 与 ID3v2 支持 1 位小数（整拍写整数），
    /// Apple M4A/MP4 的 <c>tmpo</c> atom 按规范以 16-bit 无符号整数写入；值为 null 或非法时清除标签。
    /// </summary>
    private static void WriteBpm(TagLib.File file, string path, double? bpm, bool roundId3ToInteger)
    {
        var hasValue = bpm is { } value && BpmFormatter.IsValid(value);
        var validValue = hasValue ? bpm!.Value : 0.0;
        var rawTag = file.GetTag(DefaultTagTypeFor(path), true);

        switch (rawTag)
        {
            case TagLib.Mpeg4.AppleTag apple:
                apple.BeatsPerMinute = hasValue ? BpmFormatter.ToIntegerTag(validValue) : 0u;
                break;

            case TagLib.Id3v2.Tag id3v2:
                var tbpm = TagLib.ByteVector.FromString("TBPM", TagLib.StringType.Latin1);
                if (hasValue)
                {
                    id3v2.SetTextFrame(tbpm, BpmFormatter.FormatTagText(validValue, roundId3ToInteger));
                }
                else
                {
                    id3v2.RemoveFrames(tbpm);
                }

                break;

            case TagLib.Ogg.XiphComment xiph:
                if (hasValue)
                {
                    xiph.SetField("BPM", [BpmFormatter.FormatTagText(validValue, roundToInteger: false)]);
                }
                else
                {
                    xiph.RemoveField("BPM");
                }

                break;

            case TagLib.Asf.Tag asf:
                if (hasValue)
                {
                    asf.SetDescriptorString(BpmFormatter.FormatTagText(validValue, roundToInteger: false), "WM/BeatsPerMinute");
                }
                else
                {
                    asf.RemoveDescriptors("WM/BeatsPerMinute");
                }

                break;

            default:
                if (file.Tag is not null)
                {
                    file.Tag.BeatsPerMinute = hasValue ? BpmFormatter.ToIntegerTag(validValue) : 0u;
                }

                break;
        }
    }

    /// <summary>
    /// 用户编辑年份时写入 <c>Tag.Year</c> 的数字值：优先用解析出的整数，
    /// 若用户输入的是 "2023-05-12" 这类详细日期（<see cref="TagEditModel.Year"/> 解析失败为 0），
    /// 取前 4 位数字，避免把 ID3v1 等只支持纯年份的标签清空。
    /// </summary>
    private static uint ResolveYearNumber(TagEditModel edit)
    {
        if (edit.Year > 0)
        {
            return (uint)edit.Year;
        }

        var text = edit.YearText?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length < 4)
        {
            return 0;
        }

        return uint.TryParse(text.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year) &&
               year > 0
            ? year
            : 0;
    }

    /// <summary>
    /// 决定写回文件原始字段（©day / TDRC / DATE / WM/Year）的年份文本。
    /// <see cref="TagEditModel.YearText"/> 非空时保留原文（如 "2023-05-12"）；
    /// 为空但仍有可解析年份时回退为 4 位年份，避免只设置 <see cref="TagEditModel.Year"/>
    /// 的调用方（如转换时复制标签）把年份整体清空；两者都为空才返回 null 表示清除。
    /// </summary>
    private static string? ResolveYearText(TagEditModel edit, uint yearNumber)
    {
        var text = edit.YearText?.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            return text;
        }

        return yearNumber > 0
            ? yearNumber.ToString(CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>
    /// 按文件扩展名的默认标签类型写回原始年份/日期文本（保留 "2023-05-12" 等细节）。
    /// 文本为空表示调用方确认年份被清空：删除对应的原始字段，而不是只写 4 位年份。
    /// 调用方应先用 <see cref="ResolveYearText"/> 做过回退，避免 Year 有值却清空年份字段。
    /// </summary>
    private static void WriteRawYearText(TagLib.File file, string path, string? yearText)
    {
        var text = yearText?.Trim();
        var hasText = !string.IsNullOrEmpty(text);
        var rawTag = file.GetTag(DefaultTagTypeFor(path), true);
        var dayAtom = TagLib.ByteVector.FromString("©day", TagLib.StringType.Latin1);
        var tdrcFrame = TagLib.ByteVector.FromString("TDRC", TagLib.StringType.Latin1);
        var tyerFrame = TagLib.ByteVector.FromString("TYER", TagLib.StringType.Latin1);

        switch (rawTag)
        {
            case TagLib.Mpeg4.AppleTag apple:
                if (hasText)
                {
                    apple.SetText(dayAtom, text!);
                }
                else
                {
                    apple.ClearData(dayAtom);
                }

                break;

            case TagLib.Id3v2.Tag id3v2:
                if (hasText)
                {
                    // TDRC 是 ID3v2.4 的录制日期；旧的 TYER（ID3v2.3）与新值冲突，删除。
                    id3v2.RemoveFrames(tyerFrame);
                    id3v2.SetTextFrame(tdrcFrame, text!);
                }
                else
                {
                    id3v2.RemoveFrames(tdrcFrame);
                    id3v2.RemoveFrames(tyerFrame);
                }

                break;

            case TagLib.Ogg.XiphComment xiph:
                if (hasText)
                {
                    xiph.RemoveField("YEAR");
                    xiph.SetField("DATE", text!);
                }
                else
                {
                    xiph.RemoveField("DATE");
                    xiph.RemoveField("YEAR");
                }

                break;

            case TagLib.Asf.Tag asf:
                if (hasText)
                {
                    asf.SetDescriptorString(text!, "WM/Year");
                }
                else
                {
                    asf.RemoveDescriptors("WM/Year");
                }

                break;
        }
    }

    private static void ApplyAdditionalTagChanges(TagLib.File file, IReadOnlyList<AudioTagChange> changes)
    {
        foreach (var change in changes)
        {
            var value = change.IsRemoval ? null : change.Value!.Trim();
            switch (change.Kind)
            {
                case AdditionalTagKinds.Id3Text when file.GetTag(TagLib.TagTypes.Id3v2, false) is TagLib.Id3v2.Tag id3:
                    ApplyId3Text(id3, change.Key, value);
                    break;

                case AdditionalTagKinds.Id3UserText when file.GetTag(TagLib.TagTypes.Id3v2, false) is TagLib.Id3v2.Tag id3:
                    ApplyId3UserText(id3, change.Key, value);
                    break;

                case AdditionalTagKinds.Xiph when file.GetTag(TagLib.TagTypes.Xiph, false) is TagLib.Ogg.XiphComment xiph:
                    var xiphField = CanonicalXiphField(change.Key);
                    RemoveXiphAliases(xiph, xiphField);
                    if (value is not null)
                    {
                        xiph.SetField(xiphField, [value]);
                    }

                    break;

                case AdditionalTagKinds.AppleAtom when file.GetTag(TagLib.TagTypes.Apple, false) is TagLib.Mpeg4.AppleTag apple:
                    var atom = TagLib.ByteVector.FromString(change.Key, TagLib.StringType.Latin1);
                    if (value is null)
                    {
                        apple.ClearData(atom);
                    }
                    else
                    {
                        apple.SetText(atom, [value]);
                    }

                    break;

                case AdditionalTagKinds.AppleFreeform when file.GetTag(TagLib.TagTypes.Apple, false) is TagLib.Mpeg4.AppleTag apple:
                    var (mean, name) = AdditionalTagKey.SplitAppleFreeform(change.Key);
                    apple.SetDashBox(mean, name, value ?? string.Empty);
                    break;

                case AdditionalTagKinds.Asf when file.GetTag(TagLib.TagTypes.Asf, false) is TagLib.Asf.Tag asf:
                    if (value is null)
                    {
                        asf.RemoveDescriptors(change.Key);
                    }
                    else
                    {
                        asf.SetDescriptorString(value, [change.Key]);
                    }

                    break;
            }
        }
    }

    private static void ApplyId3Text(TagLib.Id3v2.Tag tag, string frameId, string? value)
    {
        if (string.IsNullOrWhiteSpace(frameId))
        {
            return;
        }

        var ident = TagLib.ByteVector.FromString(frameId, TagLib.StringType.Latin1);
        var frame = TagLib.Id3v2.TextInformationFrame.Get(tag, ident, TagLib.StringType.UTF8, false);
        if (frame is null)
        {
            if (value is null)
            {
                return;
            }

            frame = TagLib.Id3v2.TextInformationFrame.Get(tag, ident, TagLib.StringType.UTF8, true)!;
        }

        if (value is null)
        {
            tag.RemoveFrame(frame);
        }
        else
        {
            frame.Text = [value];
        }
    }

    private static void ApplyId3UserText(TagLib.Id3v2.Tag tag, string description, string? value)
    {
        var frame = TagLib.Id3v2.UserTextInformationFrame.Get(tag, description, TagLib.StringType.UTF8, false);
        if (frame is null)
        {
            if (value is null)
            {
                return;
            }

            frame = TagLib.Id3v2.UserTextInformationFrame.Get(tag, description, TagLib.StringType.UTF8, true)!;
        }

        if (value is null)
        {
            tag.RemoveFrame(frame);
        }
        else
        {
            frame.Text = [value];
        }
    }

    private IReadOnlyList<AudioTagEntry> ReadAdditionalTags(
        TagLib.File file,
        IReadOnlyCollection<string> excludedFreeformKeys)
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
                CollectAppleBoxes(apple, entries, seen, excludedFreeformKeys);
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
                    var name = string.IsNullOrWhiteSpace(user.Description) ? "TXXX" : user.Description;
                    AddEntry(
                        entries,
                        seen,
                        name,
                        string.Join("; ", user.Text),
                        AdditionalTagKinds.Id3UserText,
                        user.Description ?? string.Empty);
                }
                else if (frame is TagLib.Id3v2.TextInformationFrame text)
                {
                    var frameId = frame.FrameId.ToString();
                    if (KnownId3FrameIds.Contains(frameId))
                    {
                        continue;
                    }

                    AddEntry(
                        entries,
                        seen,
                        Id3FrameLabels.GetValueOrDefault(frameId, frameId),
                        string.Join("; ", text.Text),
                        AdditionalTagKinds.Id3Text,
                        frameId);
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
        var fields = tag.ToList();
        foreach (var field in fields)
        {
            var canonical = CanonicalXiphField(field);
            if (!string.Equals(field, canonical, StringComparison.OrdinalIgnoreCase) &&
                fields.Any(other => string.Equals(other, canonical, StringComparison.OrdinalIgnoreCase)))
            {
                // TOTALTRACKS / TOTALDISCS 是 TagLib 的旧别名；同一语义只保留规范字段，避免重复行。
                continue;
            }

            if (KnownXiphFields.Contains(canonical))
            {
                continue;
            }

            var values = tag.GetField(field);
            AddEntry(
                entries,
                seen,
                XiphLabels.GetValueOrDefault(canonical.ToUpperInvariant(), canonical),
                string.Join("; ", values ?? []),
                AdditionalTagKinds.Xiph,
                canonical);
        }
    }

    private static string CanonicalXiphField(string field) =>
        field.ToUpperInvariant() switch
        {
            "TOTALTRACKS" => "TRACKTOTAL",
            "TOTALDISCS" => "DISCTOTAL",
            _ => field
        };

    private static void RemoveXiphAliases(TagLib.Ogg.XiphComment tag, string field)
    {
        tag.RemoveField(field);
        if (string.Equals(field, "TRACKTOTAL", StringComparison.OrdinalIgnoreCase))
        {
            tag.RemoveField("TOTALTRACKS");
        }
        else if (string.Equals(field, "DISCTOTAL", StringComparison.OrdinalIgnoreCase))
        {
            tag.RemoveField("TOTALDISCS");
        }
    }

    private static void CollectAppleBoxes(
        TagLib.Mpeg4.AppleTag tag,
        List<AudioTagEntry> entries,
        HashSet<string> seen,
        IReadOnlyCollection<string> excludedFreeformKeys)
    {
        foreach (var box in tag)
        {
            try
            {
                var atom = AtomName(box.BoxType);
                if (atom == "----")
                {
                    CollectAppleFreeform(box, entries, seen, excludedFreeformKeys);
                    continue;
                }

                if (KnownAppleAtoms.Contains(atom))
                {
                    continue;
                }

                var data = box.Children.OfType<TagLib.Mpeg4.AppleDataBox>().FirstOrDefault();
                if (data is null || string.IsNullOrWhiteSpace(data.Text))
                {
                    continue;
                }

                AddEntry(
                    entries,
                    seen,
                    AppleAtomLabels.GetValueOrDefault(atom, atom),
                    data.Text,
                    AdditionalTagKinds.AppleAtom,
                    atom);
            }
            catch
            {
                // Ignore malformed atoms.
            }
        }
    }

    private static void CollectAppleFreeform(
        TagLib.Mpeg4.Box box,
        List<AudioTagEntry> entries,
        HashSet<string> seen,
        IReadOnlyCollection<string> excludedFreeformKeys)
    {
        var mean = ChildText(box, "mean");
        var name = ChildText(box, "name");
        if (string.IsNullOrWhiteSpace(mean) && string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var data = box.Children.OfType<TagLib.Mpeg4.AppleDataBox>().FirstOrDefault();
        if (data is null || string.IsNullOrWhiteSpace(data.Text))
        {
            return;
        }

        var key = AdditionalTagKey.AppleFreeform(mean, name);
        if (excludedFreeformKeys.Contains(key))
        {
            // 该自由标签已被当作音轨号/碟片号读入，不再作为“其他标签”重复显示
            // （与 trkn / disk 标准 atom 不出现在其他标签列表里保持一致）。
            return;
        }

        AddEntry(
            entries,
            seen,
            string.IsNullOrWhiteSpace(name) ? mean : name,
            data.Text,
            AdditionalTagKinds.AppleFreeform,
            key);
    }

    private static string ChildText(TagLib.Mpeg4.Box box, string atom) =>
        box.Children
            .Where(child => AtomName(child.BoxType) == atom)
            .Select(child => child switch
            {
                TagLib.Mpeg4.AppleAdditionalInfoBox info => info.Text,
                TagLib.Mpeg4.AppleDataBox data => data.Text,
                _ => null
            })
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text))
            ?.Trim() ?? string.Empty;

    private static string AtomName(TagLib.ByteVector type) => Encoding.Latin1.GetString(type.Data);

    private static void CollectAsfDescriptors(TagLib.Asf.Tag tag, List<AudioTagEntry> entries, HashSet<string> seen)
    {
        foreach (var descriptor in tag.GetDescriptors())
        {
            if (KnownAsfNames.Contains(descriptor.Name))
            {
                continue;
            }

            AddEntry(
                entries,
                seen,
                descriptor.Name,
                descriptor.ToString(),
                AdditionalTagKinds.Asf,
                descriptor.Name);
        }
    }

    private static void AddEntry(
        List<AudioTagEntry> entries,
        HashSet<string> seen,
        string? name,
        string? value,
        string kind,
        string key)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        name = name.Trim();
        key = key.Trim();
        value = value.Trim();
        if (value.Length > 400)
        {
            value = string.Concat(value.AsSpan(0, 400), "…");
        }

        if (!seen.Add($"{kind}\u0000{key}"))
        {
            return;
        }

        entries.Add(new AudioTagEntry(name, value) { Kind = kind, Key = key });
    }

    private static readonly HashSet<string> KnownId3FrameIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "TIT2", "TPE1", "TPE2", "TALB", "TCOM", "TCON", "TYER", "TDRC", "TRCK", "TPOS", "COMM", "APIC", "USLT", "SYLT", "TBPM"
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
        "DATE", "YEAR", "TRACKNUMBER", "DISCNUMBER", "BPM", "METADATA_BLOCK_PICTURE", "COVERART", "COVERARTMIME"
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
        "©nam", "©ART", "©alb", "aART", "©wrt", "©gen", "gnre", "©cmt", "©day", "trkn", "disk", "covr", "tmpo"
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
        "WM/Genre", "WM/Year", "WM/TrackNumber", "WM/PartOfSet", "WM/Picture", "WM/BeatsPerMinute"
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
