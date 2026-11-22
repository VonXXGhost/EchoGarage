using System.Globalization;
using EchoGarage.Core.Models;

namespace EchoGarage.Core.Services;

/// <summary>
/// 把播放器采集的端到端音频管线信息与歌曲元数据整理成「音频信息」窗口的展示段落。
/// 纯函数，不依赖 WPF / NAudio，便于单元测试（共享与独占的段落差异、转换明细、缺失信息回退）。
/// </summary>
public static class AudioPipelineFormatter
{
    /// <summary>
    /// 构建管线说明。<paramref name="track"/> 为 null 时省略歌曲段落；
    /// <paramref name="fileBitsPerSample"/> 为文件声明的位深（可能未知）；
    /// <paramref name="info"/> 为 null 表示当前没有已建立的输出流。
    /// </summary>
    public static AudioPipelineReport Build(
        Track? track,
        int? fileBitsPerSample,
        double volume,
        AudioPipelineInfo? info)
    {
        var stages = new List<AudioPipelineStage>();
        var sourceSummary = "未知音轨";

        if (track is not null)
        {
            sourceSummary = FormatSource(track, fileBitsPerSample);
            stages.Add(BuildTrackStage(track, fileBitsPerSample));
        }

        if (info is null)
        {
            stages.Add(new AudioPipelineStage(
                "输出",
                "未建立输出",
                [new AudioPipelineDetail("说明", "当前没有正在播放的输出流（已停止或正在加载）")]));
            return new AudioPipelineReport(stages, $"{sourceSummary} → 未建立输出");
        }

        stages.Add(new AudioPipelineStage(
            "解码器",
            info.DecoderName,
            [
                new AudioPipelineDetail("解码 PCM", info.DecodedFormat),
                new AudioPipelineDetail("说明", "解码输出统一进入 32-bit float 采样链")
            ]));

        stages.Add(new AudioPipelineStage(
            "转换",
            info.Conversions.Count == 0 ? "无需转换" : "采样链转换",
            info.Conversions.Count == 0
                ? [new AudioPipelineDetail("说明", "文件格式与输出流格式一致")]
                : info.Conversions));

        stages.Add(new AudioPipelineStage(
            "信号处理",
            $"音量 {Math.Round(volume * 100, MidpointRounding.AwayFromZero):0}%",
            [
                new AudioPipelineDetail("增益", volume <= 0 ? "-∞ dB" : $"{20 * Math.Log10(volume):0.0} dB"),
                new AudioPipelineDetail("DSP", "音量（VolumeSampleProvider）；未启用均衡器 / 音效")
            ]));

        stages.Add(BuildBufferStage(info));

        var modeText = info.Output.Mode == OutputMode.Exclusive ? "独占" : "共享";
        stages.Add(new AudioPipelineStage(
            "输出",
            $"{modeText} · {FormatOutput(info.Output)}",
            [
                new AudioPipelineDetail("设备", info.Output.DeviceName),
                new AudioPipelineDetail("WASAPI 标志", info.OutputFlags ?? "—")
            ]));

        if (!string.IsNullOrEmpty(info.EngineMixFormat))
        {
            stages.Add(new AudioPipelineStage(
                "引擎混音",
                info.EngineMixFormat!,
                [new AudioPipelineDetail("说明", "应用流注入 Windows 音频引擎的混音格式")]));
        }

        if (!string.IsNullOrEmpty(info.EndpointFormat) && info.Output.Mode == OutputMode.Shared)
        {
            // 独占模式下 Windows 默认端点格式不参与音频管线，不显示该段（只在窗口底部给一句暗色提示）。
            stages.Add(new AudioPipelineStage(
                "设备端点（最终输出）",
                info.EndpointFormat!,
                [new AudioPipelineDetail("说明", "Windows 声音设置中的默认格式；共享模式最终转换到该格式后送硬件")]));
        }

        var hint = info.Output.Mode == OutputMode.Exclusive
            ? "Windows 设备默认格式不参与独占模式的音频管线（该设置仅由共享模式的音频引擎使用），已省略。"
            : null;

        return new AudioPipelineReport(
            stages,
            $"{sourceSummary} → {FormatOutput(info.Output)}",
            hint);
    }

    private static AudioPipelineStage BuildTrackStage(Track track, int? fileBitsPerSample)
    {
        var codec = track.FormatDisplay;
        var headline = track.Bitrate > 0 ? $"{codec} · {track.Bitrate} kbps" : codec;
        return new AudioPipelineStage(
            "音轨",
            headline,
            [
                new AudioPipelineDetail("采样率", FormatRate(track.SampleRate)),
                new AudioPipelineDetail(
                    "位深",
                    fileBitsPerSample is > 0 ? $"{fileBitsPerSample}-bit" : "未知（文件未声明）"),
                new AudioPipelineDetail("声道", FormatChannels(track.Channels))
            ]);
    }

    private static AudioPipelineStage BuildBufferStage(AudioPipelineInfo info)
    {
        var latency = info.BufferLatencyMilliseconds;
        if (info.Output.Mode == OutputMode.Exclusive)
        {
            return new AudioPipelineStage(
                "缓冲区",
                $"{latency} ms",
                [
                    new AudioPipelineDetail("模式", "独占：事件驱动，回调周期 ≈ 缓冲区时长"),
                    new AudioPipelineDetail("说明", "小缓冲区失败时自动回退到 20 / 50 / 100 ms")
                ]);
        }

        return new AudioPipelineStage(
            "缓冲区",
            $"{latency} ms",
            [
                new AudioPipelineDetail("模式", "共享：由 Windows 音频引擎调度（约 10 ms 周期）"),
                new AudioPipelineDetail("说明", "请求值 100 ms；实际回调节奏由 Windows 引擎决定")
            ]);
    }

    private static string FormatSource(Track track, int? fileBitsPerSample)
    {
        var bits = fileBitsPerSample is > 0 ? $" · {fileBitsPerSample}-bit" : string.Empty;
        return $"{track.FormatDisplay} · {FormatRate(track.SampleRate)}{bits}";
    }

    private static string FormatRate(int sampleRate) =>
        sampleRate > 0
            ? (sampleRate / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + " kHz"
            : "未知采样率";

    private static string FormatChannels(int channels) => channels switch
    {
        <= 0 => "未知声道",
        1 => "单声道",
        2 => "立体声",
        _ => $"{channels} 声道"
    };

    private static string FormatOutput(AudioOutputFormatInfo output)
    {
        var bits = output.IsFloat
            ? "32-bit float"
            : output.ContainerBitsPerSample is int container && container > output.BitsPerSample
                ? $"{output.BitsPerSample}-bit（{container}-bit 容器）"
                : $"{output.BitsPerSample}-bit";
        return $"{FormatRate(output.SampleRate)} · {bits} · {FormatChannels(output.Channels)}";
    }
}
