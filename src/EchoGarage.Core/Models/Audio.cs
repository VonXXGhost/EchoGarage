namespace EchoGarage.Core.Models;

public enum OutputMode
{
    Shared,
    Exclusive
}

public enum AudioPlaybackState
{
    Stopped,
    Loading,
    Playing,
    Paused
}

public sealed record AudioDeviceInfo(string Id, string FriendlyName, bool IsDefault)
{
    public string DisplayName => FriendlyName;
}

/// <summary>
/// 当前真正提交给 WASAPI 的输出流格式（输出管线最后一级的采样率 / 位深 / 声道数）。
/// 独占模式取自协商成功的客户端格式（可能被设备替换为最接近的格式）；共享模式取自
/// 我们提交的流格式，此时 <see cref="DeviceMixFormatDescription"/> 记录 Windows 引擎
/// 最终混音用的设备格式。两者都可能与歌曲文件本身的格式不同（发生了重采样 / 声道转换）。
/// <see cref="ContainerBitsPerSample"/> 用于 24-in-32 这类「32-bit 容器装 24-bit 有效样本」
/// 的格式：<see cref="BitsPerSample"/> 是有效位深（24），它是容器位深（32）。
/// </summary>
public sealed record AudioOutputFormatInfo(
    string DeviceName,
    OutputMode Mode,
    int SampleRate,
    int Channels,
    int BitsPerSample,
    bool IsFloat,
    bool IsDeviceMixFormat,
    string? DeviceMixFormatDescription = null,
    int? ContainerBitsPerSample = null);

/// <summary>
/// 「音频信息」窗口里的一行明细（左标签 / 右值）。纯数据，不依赖 WPF，便于 Core 单测。
/// </summary>
public sealed record AudioPipelineDetail(string Label, string Value);

/// <summary>
/// 「音频信息」窗口里的一级管线段落；<see cref="Headline"/> 是该级最重要的一行结论，
/// <see cref="Details"/> 是补充明细。段落顺序即音频数据流的方向。
/// </summary>
public sealed record AudioPipelineStage(
    string Title,
    string Headline,
    IReadOnlyList<AudioPipelineDetail> Details);

/// <summary>
/// 播放器在输出链建立之后采集的端到端管线事实（不含歌曲文件本身与 UI 层信息）。
/// <see cref="Conversions"/> 描述从解码 PCM 到 WASAPI 客户端流之间发生的重采样 / 声道 / 位深转换。
/// </summary>
public sealed record AudioPipelineInfo(
    string DecoderName,
    string DecodedFormat,
    IReadOnlyList<AudioPipelineDetail> Conversions,
    int BufferLatencyMilliseconds,
    AudioOutputFormatInfo Output,
    string? EngineMixFormat,
    string? EndpointFormat,
    string? OutputFlags);

/// <summary>
/// 已整理好的音频管线说明：段落列表 + 一句话摘要（源格式 → 输出格式）。
/// <see cref="Hint"/> 是可选的弱化提示（例如独占模式下被忽略的 Windows 默认格式），
/// 没有提示时为 null；它与管线段落不同级，窗口以暗色小字显示。
/// </summary>
public sealed record AudioPipelineReport(
    IReadOnlyList<AudioPipelineStage> Stages,
    string Summary,
    string? Hint = null);

public sealed class AudioPlaybackStateChangedEventArgs : EventArgs
{
    public required AudioPlaybackState State { get; init; }
    public string? TrackPath { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// 两次输出设备快照之间的差异。用于决定是否需要通知 UI，以及状态栏该提示什么。
/// 设备 ID 按不区分大小写比较（WASAPI 端点 ID 的大小写不保证稳定）。
/// </summary>
public sealed record AudioDeviceChangeSet(
    IReadOnlyList<AudioDeviceInfo> Added,
    IReadOnlyList<AudioDeviceInfo> Removed,
    IReadOnlyList<AudioDeviceInfo> Renamed,
    AudioDeviceInfo? PreviousDefault,
    AudioDeviceInfo? CurrentDefault)
{
    public static AudioDeviceChangeSet Empty { get; } = new([], [], [], null, null);

    public bool DefaultChanged =>
        !string.Equals(PreviousDefault?.Id, CurrentDefault?.Id, StringComparison.OrdinalIgnoreCase);

    public bool HasChanges => Added.Count > 0 || Removed.Count > 0 || Renamed.Count > 0 || DefaultChanged;
}

/// <summary>输出设备列表变化通知（热插拔 / 默认设备切换 / 设备重命名）。</summary>
public sealed class AudioDevicesChangedEventArgs : EventArgs
{
    public required IReadOnlyList<AudioDeviceInfo> Devices { get; init; }
    public AudioDeviceChangeSet Changes { get; init; } = AudioDeviceChangeSet.Empty;

    /// <summary>监视器启动后的第一份快照：此时没有“变化”可言，UI 不必播报增减。</summary>
    public bool IsInitialSnapshot { get; init; }
}

/// <summary>
/// One additional tag field found in the file that is not part of the common
/// editable fields (e.g. BPM, ISRC, MusicBrainz ids, custom ID3 frames).
/// <see cref="Name"/> is the display label; <see cref="Kind"/> and <see cref="Key"/>
/// identify the underlying tag item for writing it back.
/// </summary>
public sealed record AudioTagEntry(string Name, string Value)
{
    public string Kind { get; init; } = string.Empty;

    public string Key { get; init; } = string.Empty;
}

/// <summary>额外标签的写入目标类型。</summary>
public static class AdditionalTagKinds
{
    /// <summary>ID3v2 标准文本帧（如 TIT1 / TPUB / TBPM）。</summary>
    public const string Id3Text = "id3-text";

    /// <summary>ID3v2 TXXX 自定义帧，Key 为 Description。</summary>
    public const string Id3UserText = "id3-user-text";

    /// <summary>Xiph (Vorbis) 注释字段，Key 为字段名。</summary>
    public const string Xiph = "xiph";

    /// <summary>Apple (M4A) 顶层 atom，Key 为 atom 名（Latin-1）。</summary>
    public const string AppleAtom = "apple-atom";

    /// <summary>Apple (M4A) 自由格式 atom（----），Key 由 mean + name 组成。</summary>
    public const string AppleFreeform = "apple-freeform";

    /// <summary>ASF (WMA) 描述符，Key 为描述符名称。</summary>
    public const string Asf = "asf";
}

/// <summary>额外标签的稳定 key 组合规则。</summary>
public static class AdditionalTagKey
{
    public const char AppleFreeformSeparator = '\u001f';

    public static string AppleFreeform(string mean, string name) =>
        $"{mean}{AppleFreeformSeparator}{name}";

    public static (string Mean, string Name) SplitAppleFreeform(string key)
    {
        var separator = key.IndexOf(AppleFreeformSeparator);
        return separator < 0
            ? (key, string.Empty)
            : (key[..separator], key[(separator + 1)..]);
    }
}

/// <summary>一次额外标签写入；<see cref="Value"/> 为空表示删除该项。</summary>
public sealed record AudioTagChange(string Kind, string Key, string? Value)
{
    public bool IsRemoval => string.IsNullOrWhiteSpace(Value);
}

public sealed record AudioTagData(
    string Title,
    string Artist,
    string Album,
    string AlbumArtist,
    string Composer,
    string Genre,
    string Comment,
    int? Year,
    int TrackNumber,
    int DiscNumber,
    long DurationMs,
    int Bitrate,
    int SampleRate,
    int Channels,
    string Codec,
    byte[]? CoverBytes,
    string? CoverMime)
{
    /// <summary>Other tags present in the file, shown in the tag editor.</summary>
    public IReadOnlyList<AudioTagEntry> AdditionalTags { get; init; } = [];

    /// <summary>
    /// 文件中的原始年份/日期文本（如 "2025.06.04"、"2023-05-12"）。
    /// 仅用于属性面板显示与写回文件；<see cref="Year"/> 仍是可用于排序/分组的前 4 位年份。
    /// 不写入数据库，数据库缓存只保存 <see cref="Year"/>。
    /// </summary>
    public string? YearText { get; init; }

    /// <summary>
    /// 文件本身声明的位深（TagLib 音频属性）。有损格式或容器未声明时为 null。
    /// 不写入数据库，只在「音频信息」这类需要展示原始格式的场景按需读取。
    /// </summary>
    public int? BitsPerSample { get; init; }

    /// <summary>
    /// 文件内嵌的标准 BPM 标签值（ID3v2 TBPM / Xiph BPM / Apple tmpo / ASF WM/BeatsPerMinute）。
    /// 不经 TagLib <c>uint</c> 截断以保留小数（M4A tmpo 按规范仍为整数）；缺失或非法时为 null（ADR-034）。
    /// </summary>
    public double? Bpm { get; init; }
}

public sealed class TagEditModel
{
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public string AlbumArtist { get; set; } = string.Empty;
    public string Composer { get; set; } = string.Empty;
    public string Genre { get; set; } = string.Empty;
    public string Comment { get; set; } = string.Empty;
    public int Year { get; set; }

    /// <summary>
    /// 年份字段的原始文本（可包含月/日，如 "2023-05-12"）；写回时保留详细日期。
    /// 仅当 <see cref="YearChanged"/> 为 true 时被 <c>TagLibTagService</c> 使用。
    /// </summary>
    public string? YearText { get; set; }

    /// <summary>
    /// 本次编辑是否包含年份字段。默认 true 以兼容直接构造模型并全量写入的旧调用方；
    /// 批量编辑（<c>TagBatchEditor.CreateEdit</c>）在用户未修改年份时置为 false，
    /// 避免写回时把文件中的详细日期覆盖成纯年份。
    /// </summary>
    public bool YearChanged { get; set; } = true;

    public int TrackNumber { get; set; }
    public int DiscNumber { get; set; }
    public byte[]? NewCoverBytes { get; set; }
    public string? NewCoverMime { get; set; }
    public bool RemoveCover { get; set; }

    /// <summary>额外标签改动；值为空表示删除。未列出的额外标签保持原值。</summary>
    public List<AudioTagChange> AdditionalTagChanges { get; } = [];

    /// <summary>
    /// 标准 BPM 标签的目标值（null 表示清除）。仅当 <see cref="BpmChanged"/> 为 true 时
    /// 才被 <c>TagLibTagService</c> 写入，避免编辑其它字段时覆盖原有 BPM 标签。
    /// </summary>
    public double? Bpm { get; set; }

    /// <summary>本次编辑是否修改了 BPM 字段（默认 false 以兼容既有调用方）。</summary>
    public bool BpmChanged { get; set; }

    /// <summary>写入 ID3v2 TBPM 时是否取整（默认 false 保留小数，兼容设置项）。</summary>
    public bool RoundId3BpmToInteger { get; set; }
}

public sealed class AudioFileDetails
{
    public required Track Track { get; init; }
    public byte[]? CoverBytes { get; init; }
    public string? CoverMime { get; init; }
}
