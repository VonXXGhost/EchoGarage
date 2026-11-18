using EchoGarage.Core.Models;
using EchoGarage.Infrastructure.Configuration;
using EchoGarage.Infrastructure.Logging;
using EchoGarage.Infrastructure.Tagging;
using Xunit;

namespace EchoGarage.Tests;

/// <summary>
/// 旧式 M4A 标签器（Nero AAC 等）把音轨号/碟片号写成
/// <c>----:com.apple.iTunes:track</c> / <c>----:com.apple.iTunes:disc</c> 自由标签，
/// 而不是标准 trkn / disk atom。foobar2000 认这种写法，TagLib# 不认。
/// 这里覆盖：读取回退、标准 atom 优先、编辑后写回自由标签且不产生重复标准 atom。
/// </summary>
public sealed class AppleLegacyTagNumberTests
{
    [Theory]
    [InlineData("1/3", 1)]
    [InlineData(" 02 ", 2)]
    [InlineData("01/3", 1)]
    [InlineData("7", 7)]
    public void TryParse_ReadsLeadingNumber(string text, int expected)
    {
        Assert.True(AppleLegacyTagNumber.TryParse(text, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("A1")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("99999999999/3")]
    public void TryParse_RejectsInvalidText(string? text)
    {
        Assert.False(AppleLegacyTagNumber.TryParse(text, out _));
    }

    [Theory]
    [InlineData("1/3", 2, "2/3")]
    [InlineData("5", 2, "2")]
    [InlineData(null, 2, "2")]
    [InlineData("1/x", 2, "2")]
    [InlineData("1/", 2, "2")]
    [InlineData("/3", 2, "2/3")]
    public void Format_KeepsTotalWhenPresent(string? original, int value, string expected)
    {
        Assert.Equal(expected, AppleLegacyTagNumber.Format(original, value));
    }

    [Theory]
    [InlineData("1/3")]
    [InlineData("5")]
    [InlineData(null)]
    public void Format_ZeroMeansDelete(string? original)
    {
        Assert.Equal(string.Empty, AppleLegacyTagNumber.Format(original, 0));
    }

    [Fact]
    public void Read_LegacyFreeformNumbers_FillsTrackAndDiscAndHidesRows()
    {
        using var workspace = new TempWorkspace();
        var tags = CreateTagService(workspace);
        var target = CreateM4aSource(workspace);
        AddLegacyFreeformNumbers(target, track: "1/3", disc: "1/1");

        var read = tags.Read(target);

        Assert.Equal(1, read.TrackNumber);
        Assert.Equal(1, read.DiscNumber);
        // 已被当作音轨号/碟片号读取的自由标签不应再出现在“其他标签”里，
        // 与 trkn / disk 标准 atom 不出现在该列表的行为一致。
        Assert.DoesNotContain(
            read.AdditionalTags,
            entry => entry.Kind == AdditionalTagKinds.AppleFreeform &&
                     (string.Equals(entry.Name, AppleLegacyTagNumber.TrackName, StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(entry.Name, AppleLegacyTagNumber.DiscName, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Read_StandardAtomsTakePrecedenceOverLegacyFreeform()
    {
        using var workspace = new TempWorkspace();
        var tags = CreateTagService(workspace);
        var target = CreateM4aSource(workspace);
        tags.Write(target, new TagEditModel
        {
            Title = "Demo Track 01",
            TrackNumber = 7,
            DiscNumber = 2,
            YearChanged = false
        });
        // 有冲突值的老文件：标准 atom 已存在，旧式自由标签不应覆盖它。
        AddLegacyFreeformNumbers(target, track: "1/3", disc: "1/1");

        var read = tags.Read(target);

        Assert.Equal(7, read.TrackNumber);
        Assert.Equal(2, read.DiscNumber);
        // 标准值可用时自由标签只是普通“其他标签”，仍应可见，方便用户发现并清理。
        Assert.Contains(
            read.AdditionalTags,
            entry => entry.Kind == AdditionalTagKinds.AppleFreeform &&
                     string.Equals(entry.Name, AppleLegacyTagNumber.TrackName, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Write_UnchangedLegacyNumber_KeepsFreeformAndCreatesNoStandardAtom()
    {
        using var workspace = new TempWorkspace();
        var tags = CreateTagService(workspace);
        var target = CreateM4aSource(workspace);
        AddLegacyFreeformNumbers(target, track: "1/3", disc: "1/1");

        tags.Write(target, new TagEditModel
        {
            Title = "Demo Track 01",
            TrackNumber = 1,
            DiscNumber = 1,
            YearChanged = false
        });

        Assert.Equal("1/3", GetLegacyFreeform(target, AppleLegacyTagNumber.TrackName));
        Assert.Equal("1/1", GetLegacyFreeform(target, AppleLegacyTagNumber.DiscName));
        Assert.Equal(0u, GetStandardTrack(target));
        Assert.Equal(0u, GetStandardDisc(target));

        var read = tags.Read(target);
        Assert.Equal(1, read.TrackNumber);
        Assert.Equal(1, read.DiscNumber);
    }

    [Fact]
    public void Write_ChangedLegacyTrack_UpdatesFreeformPreservingTotal()
    {
        using var workspace = new TempWorkspace();
        var tags = CreateTagService(workspace);
        var target = CreateM4aSource(workspace);
        AddLegacyFreeformNumbers(target, track: "1/3", disc: "1/1");

        tags.Write(target, new TagEditModel
        {
            Title = "Demo Track 01",
            TrackNumber = 2,
            DiscNumber = 1,
            YearChanged = false
        });

        // 只改写了自由标签，且保留原来的“/总数”。
        Assert.Equal("2/3", GetLegacyFreeform(target, AppleLegacyTagNumber.TrackName));
        Assert.Equal("1/1", GetLegacyFreeform(target, AppleLegacyTagNumber.DiscName));
        Assert.Equal(0u, GetStandardTrack(target));
        Assert.Equal(0u, GetStandardDisc(target));

        var read = tags.Read(target);
        Assert.Equal(2, read.TrackNumber);
        Assert.Equal(1, read.DiscNumber);
    }

    [Fact]
    public void Write_DisabledLegacyNumber_RemovesFreeformBox()
    {
        using var workspace = new TempWorkspace();
        var tags = CreateTagService(workspace);
        var target = CreateM4aSource(workspace);
        AddLegacyFreeformNumbers(target, track: "1/3", disc: "1/1");

        tags.Write(target, new TagEditModel
        {
            Title = "Demo Track 01",
            TrackNumber = 0,
            DiscNumber = 0,
            YearChanged = false
        });

        Assert.True(string.IsNullOrEmpty(GetLegacyFreeform(target, AppleLegacyTagNumber.TrackName)));
        Assert.True(string.IsNullOrEmpty(GetLegacyFreeform(target, AppleLegacyTagNumber.DiscName)));
        Assert.Equal(0u, GetStandardTrack(target));
        Assert.Equal(0u, GetStandardDisc(target));

        var read = tags.Read(target);
        Assert.Equal(0, read.TrackNumber);
        Assert.Equal(0, read.DiscNumber);
    }

    private static TagLibTagService CreateTagService(TempWorkspace workspace)
    {
        var paths = new AppPaths(workspace.Combine("state"));
        paths.EnsureCreated();
        return new TagLibTagService(new FileLogger(paths));
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

    /// <summary>模拟 Nero AAC 输出：写入旧式 Alt-1 自由标签音轨号/碟片号。</summary>
    private static void AddLegacyFreeformNumbers(string path, string? track, string? disc)
    {
        using var file = TagLib.File.Create(path);
        var apple = Assert.IsType<TagLib.Mpeg4.AppleTag>(file.GetTag(TagLib.TagTypes.Apple, true));
        if (track is not null)
        {
            apple.SetDashBox(AppleLegacyTagNumber.ITunesMean, AppleLegacyTagNumber.TrackName, track);
        }

        if (disc is not null)
        {
            apple.SetDashBox(AppleLegacyTagNumber.ITunesMean, AppleLegacyTagNumber.DiscName, disc);
        }

        file.Save();
    }

    private static string GetLegacyFreeform(string path, string name)
    {
        using var file = TagLib.File.Create(path);
        var apple = Assert.IsType<TagLib.Mpeg4.AppleTag>(file.GetTag(TagLib.TagTypes.Apple));
        return apple.GetDashBox(AppleLegacyTagNumber.ITunesMean, name);
    }

    private static uint GetStandardTrack(string path)
    {
        using var file = TagLib.File.Create(path);
        var apple = Assert.IsType<TagLib.Mpeg4.AppleTag>(file.GetTag(TagLib.TagTypes.Apple));
        return apple.Track;
    }

    private static uint GetStandardDisc(string path)
    {
        using var file = TagLib.File.Create(path);
        var apple = Assert.IsType<TagLib.Mpeg4.AppleTag>(file.GetTag(TagLib.TagTypes.Apple));
        return apple.Disc;
    }
}
