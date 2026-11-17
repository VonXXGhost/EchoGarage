using EchoGarage.Core.Models;
using EchoGarage.Infrastructure.Configuration;
using EchoGarage.Infrastructure.Conversion;
using EchoGarage.Infrastructure.Logging;
using EchoGarage.Infrastructure.Tagging;
using Xunit;

namespace EchoGarage.Tests;

/// <summary>
/// 回归测试：转换输出复制标签时必须保留源文件的年份/日期（ADR-026 的 YearText）。
/// 旧实现只传 Year 且不传 YearText（YearChanged 默认 true），TagLibTagService 会按
/// “清空年份”处理，把 ©day / TDRC / DATE 全部删除；真实用例是 FLAC 源
/// （Year=2026、YearText="2026"）经 AAC 预设转成 m4a 后年份完全丢失。
/// </summary>
public sealed class ConversionTagCopyTests
{
    private const string DetailedYearText = "2024-05-12";

    private static readonly TagLib.ByteVector AppleDayAtom =
        TagLib.ByteVector.FromString("©day", TagLib.StringType.Latin1);

    [Fact]
    public void CreateTagCopy_CopiesYearTextCoverAndOtherFields()
    {
        var cover = TestAudio.CoverBytes();
        var tags = CreateTags(cover);

        var edit = AudioConverter.CreateTagCopy(tags, copyCover: true);

        Assert.Equal("Demo Track 01", edit.Title);
        Assert.Equal("Demo Artist", edit.Artist);
        Assert.Equal("Demo Album A", edit.Album);
        Assert.Equal("Demo Artist", edit.AlbumArtist);
        Assert.Equal("Demo Composer", edit.Composer);
        Assert.Equal("Pop", edit.Genre);
        Assert.Equal("Demo Comment", edit.Comment);
        Assert.Equal(2024, edit.Year);
        Assert.Equal(DetailedYearText, edit.YearText);
        Assert.True(edit.YearChanged, "复制标签必须请求写入年份，否则输出文件不会有年份。");
        Assert.Equal(3, edit.TrackNumber);
        Assert.Equal(1, edit.DiscNumber);
        Assert.Same(cover, edit.NewCoverBytes);
        Assert.Equal("image/png", edit.NewCoverMime);
        Assert.False(edit.RemoveCover);
    }

    [Fact]
    public void CreateTagCopy_WithoutCover_DoesNotRequestCoverWrite()
    {
        var edit = AudioConverter.CreateTagCopy(CreateTags(cover: null), copyCover: false);

        Assert.Null(edit.NewCoverBytes);
        Assert.Null(edit.NewCoverMime);
        Assert.False(edit.RemoveCover);
    }

    [Fact]
    public void CreateTagCopy_WithoutYearText_StillRequestsYearWrite()
    {
        var tags = CreateTags(cover: null) with { YearText = null };

        var edit = AudioConverter.CreateTagCopy(tags, copyCover: true);

        Assert.Null(edit.YearText);
        Assert.Equal(2024, edit.Year);
        Assert.True(edit.YearChanged, "源文件没有年份原文时也要写 4 位年份，不能清空。");
    }

    [Fact]
    public void CreateTagCopy_RejectsNullTags()
    {
        Assert.Throws<ArgumentNullException>(() => AudioConverter.CreateTagCopy(null!, copyCover: true));
    }

    /// <summary>Xiph（.flac）路径：只传 Year、YearText 为空时回退写 4 位年份。</summary>
    [Fact]
    public void Write_PlainYearWithNullYearText_KeepsYearInFlac()
    {
        var flacPath = TestTools.ResolveTool("flac.exe");
        if (flacPath is null)
        {
            Assert.Skip("tools\\flac.exe is not available.");
        }

        using var workspace = new TempWorkspace();
        var tags = CreateTagService(workspace);
        var target = CreateFlacSource(flacPath, workspace);

        tags.Write(target, new TagEditModel { Title = "Demo Track 01", Year = 2025, YearChanged = true });

        var read = tags.Read(target);
        Assert.Equal(2025, read.Year);
        Assert.Equal("2025", read.YearText);

        using var file = TagLib.File.Create(target);
        var xiph = Assert.IsType<TagLib.Ogg.XiphComment>(file.GetTag(TagLib.TagTypes.Xiph));
        Assert.Contains("2025", xiph.GetField("DATE"));
    }

    /// <summary>Apple（.m4a）路径：只传 Year、YearText 为空时回退写 4 位年份；两者都空才清除。</summary>
    [Fact]
    public void Write_PlainYearWithNullYearText_KeepsYearInM4a()
    {
        using var workspace = new TempWorkspace();
        var tags = CreateTagService(workspace);
        var target = CreateM4aSource(workspace);

        tags.Write(target, new TagEditModel { Title = "Demo Track 01", Year = 2025, YearChanged = true });

        var read = tags.Read(target);
        Assert.Equal(2025, read.Year);
        Assert.Equal("2025", read.YearText);
        using (var file = TagLib.File.Create(target))
        {
            var apple = Assert.IsType<TagLib.Mpeg4.AppleTag>(file.GetTag(TagLib.TagTypes.Apple));
            Assert.Contains("2025", apple.GetText(AppleDayAtom));
        }

        // 只有 YearText 与 Year 都为空时才清除年份。
        tags.Write(target, new TagEditModel { Title = "Demo Track 01", Year = 0, YearChanged = true });

        var cleared = tags.Read(target);
        Assert.Null(cleared.Year);
        Assert.Null(cleared.YearText);
        using (var file = TagLib.File.Create(target))
        {
            var apple = Assert.IsType<TagLib.Mpeg4.AppleTag>(file.GetTag(TagLib.TagTypes.Apple));
            Assert.True(string.IsNullOrEmpty(apple.GetText(AppleDayAtom).FirstOrDefault()));
        }
    }

    /// <summary>
    /// 模拟转换输出（真实 AAC-in-M4A 容器）：把源标签用 CreateTagCopy 写进 Apple 标签，
    /// 断言详细日期文本按 ©day 原样写入，读回年份/日期不丢。这正是 CopyTags 的实际动作。
    /// </summary>
    [Fact]
    public void CopyTagsModel_ToRealM4a_WritesAppleDayAtom()
    {
        using var workspace = new TempWorkspace();
        var tags = CreateTagService(workspace);
        var target = CreateM4aSource(workspace);
        var cover = TestAudio.CoverBytes();

        tags.Write(target, AudioConverter.CreateTagCopy(CreateTags(cover), copyCover: true));

        var read = tags.Read(target);
        Assert.Equal(2024, read.Year);
        Assert.Equal(DetailedYearText, read.YearText);
        Assert.Equal(cover, read.CoverBytes);
        using var file = TagLib.File.Create(target);
        var apple = Assert.IsType<TagLib.Mpeg4.AppleTag>(file.GetTag(TagLib.TagTypes.Apple));
        Assert.Contains(DetailedYearText, apple.GetText(AppleDayAtom));
    }

    /// <summary>
    /// 真实转换器集成测试（FLAC→FLAC）：CopyTags 必须把源文件的详细年份文本带到输出文件，
    /// 旧实现会让输出年份变成 null。
    /// </summary>
    [Fact]
    public async Task Convert_FlacToFlac_PreservesDetailedYearText()
    {
        var flacPath = TestTools.ResolveTool("flac.exe");
        if (flacPath is null)
        {
            Assert.Skip("tools\\flac.exe is not available.");
        }

        using var workspace = new TempWorkspace();
        var paths = new AppPaths(workspace.Combine("state"));
        paths.EnsureCreated();
        var logger = new FileLogger(paths);
        var tags = new TagLibTagService(logger);
        var source = CreateFlacSource(flacPath, workspace);
        var cover = TestAudio.CoverBytes();
        tags.Write(source, new TagEditModel
        {
            Title = "Demo Track 01",
            Artist = "Demo Artist",
            Album = "Demo Album A",
            Year = 2024,
            YearText = DetailedYearText,
            YearChanged = true,
            NewCoverBytes = cover,
            NewCoverMime = "image/png"
        });

        var converter = new AudioConverter(tags, logger, paths);
        var preset = new ConversionPreset
        {
            Name = "flac",
            Format = ConversionFormat.Flac,
            Quality = 5,
            BitDepth = ConversionBitDepth.Source,
            FileNameTemplate = "%title%",
            CopyTags = true,
            CopyCover = true
        };
        var outputPath = Path.Combine(workspace.CreateFolder("out"), "converted.flac");

        var result = await converter.ConvertAsync(
            new ConversionRequest(source, outputPath, preset, new ConversionEncoderPaths { FlacPath = flacPath }),
            progress: null);

        Assert.True(result.Success, result.Error);
        var read = tags.Read(outputPath);
        Assert.Equal("Demo Track 01", read.Title);
        Assert.Equal(2024, read.Year);
        Assert.Equal(DetailedYearText, read.YearText);
        Assert.Equal(cover, read.CoverBytes);
    }

    private static AudioTagData CreateTags(byte[]? cover)
    {
        return new AudioTagData(
            Title: "Demo Track 01",
            Artist: "Demo Artist",
            Album: "Demo Album A",
            AlbumArtist: "Demo Artist",
            Composer: "Demo Composer",
            Genre: "Pop",
            Comment: "Demo Comment",
            Year: 2024,
            TrackNumber: 3,
            DiscNumber: 1,
            DurationMs: 2000,
            Bitrate: 128,
            SampleRate: 44100,
            Channels: 2,
            Codec: "FLAC",
            CoverBytes: cover,
            CoverMime: cover is null ? null : "image/png")
        {
            YearText = DetailedYearText
        };
    }

    private static TagLibTagService CreateTagService(TempWorkspace workspace)
    {
        var paths = new AppPaths(workspace.Combine("state"));
        paths.EnsureCreated();
        return new TagLibTagService(new FileLogger(paths));
    }

    private static string CreateFlacSource(string flacTool, TempWorkspace workspace)
    {
        var wavPath = TestAudio.CreateWav(workspace.Combine("source.wav"), seconds: 1);
        var flacPath = workspace.Combine("source.flac");
        if (!TestTools.RunAsync(flacTool, ["-5", "-o", flacPath, wavPath]).GetAwaiter().GetResult())
        {
            Assert.Skip("tools\\flac.exe failed to encode the test source.");
        }

        return flacPath;
    }

    private static string CreateM4aSource(TempWorkspace workspace)
    {
        var wavPath = TestAudio.CreateWav(workspace.Combine("source.wav"), seconds: 1);
        var m4aPath = workspace.Combine("source.m4a");
        if (!TestTools.TryCreateM4a(wavPath, m4aPath))
        {
            Assert.Skip("当前环境没有可用的 Media Foundation AAC 编码器。");
        }

        return m4aPath;
    }
}
