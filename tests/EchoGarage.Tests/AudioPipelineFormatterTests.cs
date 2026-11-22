using EchoGarage.Core.Models;
using EchoGarage.Core.Services;

namespace EchoGarage.Tests;

/// <summary>
/// 「音频信息」窗口的管线文本：共享 / 独占的段落差异、转换明细、缺失信息回退。
/// 纯 Core 逻辑，不依赖 WPF / NAudio。
/// </summary>
public sealed class AudioPipelineFormatterTests
{
    [Fact]
    public void SharedMode_ShowsEngineMixAndEndpointAndExplainsFloatVs16Bit()
    {
        var info = CreateInfo(
            OutputMode.Shared,
            sampleRate: 48_000,
            channels: 2,
            bits: 32,
            isFloat: true,
            flags: "AUTO_CONVERT_PCM · SRC_DEFAULT_QUALITY",
            engineMix: "48 kHz · 32-bit float · 立体声",
            endpoint: "48 kHz · 16-bit · 立体声",
            latencyMilliseconds: 100);

        var report = AudioPipelineFormatter.Build(CreateTrack(), 16, 0.7, info);

        var titles = report.Stages.Select(stage => stage.Title).ToList();
        Assert.Contains("引擎混音", titles);
        Assert.Contains("设备端点（最终输出）", titles);
        Assert.Contains("32-bit float", report.Summary);
        Assert.Contains("32-bit float", titles.Count > 0 ? report.Stages.First(s => s.Title == "输出").Headline : string.Empty);
        Assert.Null(report.Hint);
    }

    [Fact]
    public void ExclusiveMode_OmitsEngineMixStageAndUsesNegotiatedClientFormat()
    {
        var info = CreateInfo(
            OutputMode.Exclusive,
            sampleRate: 48_000,
            channels: 2,
            bits: 16,
            isFloat: false,
            flags: null,
            engineMix: null,
            endpoint: "48 kHz · 24-bit · 立体声",
            latencyMilliseconds: 10);

        var report = AudioPipelineFormatter.Build(CreateTrack(), 16, 1.0, info);

        Assert.DoesNotContain(report.Stages, stage => stage.Title == "引擎混音");
        var output = report.Stages.First(stage => stage.Title == "输出");
        Assert.Contains("独占", output.Headline);
        Assert.Contains("16-bit", output.Headline);
        var buffer = report.Stages.First(stage => stage.Title == "缓冲区");
        Assert.Contains("10 ms", buffer.Headline);
        // 独占模式下 Windows 默认端点格式不参与管线：不出段落，只留一句弱化提示。
        Assert.DoesNotContain(report.Stages, stage => stage.Title.Contains("设备默认格式"));
        Assert.DoesNotContain(report.Stages, stage => stage.Title == "设备端点（最终输出）");
        Assert.Contains("不参与独占模式的音频管线", report.Hint);
    }

    [Fact]
    public void Conversions_AreRenderedAsDetailRows()
    {
        var info = CreateInfo(
            OutputMode.Shared,
            sampleRate: 48_000,
            channels: 2,
            bits: 32,
            isFloat: true,
            flags: null,
            engineMix: "48 kHz · 32-bit float · 立体声",
            endpoint: null,
            latencyMilliseconds: 100,
            conversions:
            [
                new AudioPipelineDetail("重采样", "44.1 kHz → 48 kHz（WDL）"),
                new AudioPipelineDetail("位深转换", "16-bit → 32-bit float")
            ]);

        var report = AudioPipelineFormatter.Build(CreateTrack(), 16, 0.8, info);

        var conversion = report.Stages.First(stage => stage.Title == "转换");
        Assert.Equal("采样链转换", conversion.Headline);
        Assert.Contains(conversion.Details, detail => detail.Label == "重采样");
        Assert.Contains(conversion.Details, detail => detail.Value.Contains("44.1 kHz → 48 kHz"));
        Assert.Contains(conversion.Details, detail => detail.Label == "位深转换");
    }

    [Fact]
    public void NullInfo_FallsBackToNotEstablishedStage()
    {
        var report = AudioPipelineFormatter.Build(CreateTrack(), 16, 1.0, null);

        var output = report.Stages.Last();
        Assert.Equal("输出", output.Title);
        Assert.Equal("未建立输出", output.Headline);
        Assert.EndsWith("未建立输出", report.Summary);
    }

    [Fact]
    public void TrackStage_ShowsUnknownBitDepthWhenFileDoesNotDeclareIt()
    {
        var report = AudioPipelineFormatter.Build(CreateTrack(), null, 1.0, null);

        var track = report.Stages.First(stage => stage.Title == "音轨");
        var bitDepth = track.Details.First(detail => detail.Label == "位深");
        Assert.Equal("未知（文件未声明）", bitDepth.Value);
    }

    [Fact]
    public void TrackStage_ShowsFileBitDepthAndFormat()
    {
        var report = AudioPipelineFormatter.Build(CreateTrack(), 24, 1.0, null);

        var track = report.Stages.First(stage => stage.Title == "音轨");
        Assert.Contains("FLAC", track.Headline);
        Assert.Equal("24-bit", track.Details.First(detail => detail.Label == "位深").Value);
        Assert.Equal("44.1 kHz", track.Details.First(detail => detail.Label == "采样率").Value);
        Assert.Equal("立体声", track.Details.First(detail => detail.Label == "声道").Value);
    }

    private static Track CreateTrack() => new()
    {
        Path = "C:\\Music\\Demo Track.flac",
        Title = "Demo Track",
        Artist = "Demo Artist",
        Album = "Demo Album",
        SampleRate = 44_100,
        Channels = 2,
        Bitrate = 859,
        Codec = "FLAC"
    };

    private static AudioPipelineInfo CreateInfo(
        OutputMode mode,
        int sampleRate,
        int channels,
        int bits,
        bool isFloat,
        string? flags,
        string? engineMix,
        string? endpoint,
        int latencyMilliseconds,
        IReadOnlyList<AudioPipelineDetail>? conversions = null)
    {
        var output = new AudioOutputFormatInfo(
            "Demo Device",
            mode,
            sampleRate,
            channels,
            bits,
            isFloat,
            mode == OutputMode.Shared,
            engineMix);

        return new AudioPipelineInfo(
            "Media Foundation 解码器",
            "44.1 kHz · 16-bit · 立体声",
            conversions ?? [],
            latencyMilliseconds,
            output,
            engineMix,
            endpoint,
            flags);
    }
}
