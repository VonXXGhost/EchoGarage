using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using EchoGarage.Core.Abstractions;
using EchoGarage.Core.Models;
using EchoGarage.Core.Services;
using EchoGarage.Infrastructure.Audio;

namespace EchoGarage.Infrastructure.Caching;

/// <summary>
/// ADR-015 §7 的波形缓存：每首曲目一个 <c>wave_&lt;sha1(路径)&gt;.wfm</c>，EGWV v1 二进制格式，
/// 载入时用源文件的 size + mtime 校验新鲜度，写入后按容量上限做 LRU 清理。
/// 与 <see cref="FileCoverCache"/> 保持同样的目录 / 原子写 / Relocate 语义。
/// </summary>
public sealed class FileWaveformCache : IWaveformCache
{
    /// <summary>EGWV 格式版本（ADR-015 §7.2 偏移 4）。</summary>
    public const int FormatVersion = 1;

    /// <summary>固定头长度（ADR-015 §7.2）。</summary>
    public const int HeaderSize = 72;

    /// <summary>每列字节数：height / low / mid / high（ADR-015 §7.2）。</summary>
    public const int BytesPerColumn = 4;

    private static readonly byte[] MagicBytes = "EGWV"u8.ToArray();
    private const string FilePrefix = "wave_";
    private const string FileExtension = ".wfm";
    private const double TrimTargetRatio = 0.9;

    private readonly IAppLogger _logger;
    private readonly int _analyzerVersion;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private string _directory;

    /// <param name="directory">Waveforms 目录（本类会把它当作最终目录，不再拼子目录）。</param>
    /// <param name="logger">日志。</param>
    /// <param name="analyzerVersion">分析器版本；写入头部，版本不同视为失效。</param>
    public FileWaveformCache(
        string directory,
        IAppLogger logger,
        int analyzerVersion = WaveformAnalyzer.AnalyzerVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _analyzerVersion = analyzerVersion;
        _directory = directory;
        System.IO.Directory.CreateDirectory(_directory);
    }

    /// <summary>缓存目录（Waveforms）。</summary>
    public string Directory => _directory;

    /// <summary>缓存容量上限（MB），0 表示不限。默认 256 MB（ADR-015 §7.5）。</summary>
    public int LimitMb { get; set; } = 256;

    public async Task<WaveformPeaks?> TryLoadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        var cachePath = GetCachePath(filePath);
        if (!File.Exists(cachePath))
        {
            return null;
        }

        byte[] data;
        try
        {
            data = await File.ReadAllBytesAsync(cachePath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.Warn($"读取波形缓存失败 path={cachePath} error={exception.Message}");
            return null;
        }

        var source = new FileInfo(filePath);
        if (!source.Exists)
        {
            // 源文件不在了，缓存没有继续存在的意义。
            TryDelete(cachePath, "源文件不存在");
            return null;
        }

        var peaks = Parse(data, source);
        if (peaks is null)
        {
            // 损坏 / 版本不符 / 时间戳过期：删掉重算（ADR-015 §7.2）。
            TryDelete(cachePath, "缓存校验失败");
            return null;
        }

        // 命中即刷新 LRU 时间戳（ADR-015 §7.5）。
        try
        {
            File.SetLastWriteTimeUtc(cachePath, DateTime.UtcNow);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.Debug($"刷新波形缓存时间戳失败 path={cachePath} error={exception.Message}");
        }

        return peaks;
    }

    public async Task SaveAsync(string filePath, WaveformPeaks peaks, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("音频路径不能为空。", nameof(filePath));
        }

        ArgumentNullException.ThrowIfNull(peaks);

        if (!peaks.IsComplete)
        {
            // 服务层只应提交完成快照；这里再兜一层，避免把半成品写进缓存。
            _logger.Debug($"跳过未完成的波形快照 path={filePath} valid={peaks.ValidCount} total={peaks.ColumnCount}");
            return;
        }

        var columnCount = peaks.ColumnCount;
        if (peaks.Height.Length < columnCount || peaks.Low.Length < columnCount ||
            peaks.Mid.Length < columnCount || peaks.High.Length < columnCount)
        {
            throw new ArgumentException("WaveformPeaks 的列数组长度小于 ColumnCount。", nameof(peaks));
        }

        var source = new FileInfo(filePath);
        if (!source.Exists)
        {
            _logger.Warn($"源文件不存在，跳过写入波形缓存 path={filePath}");
            return;
        }

        var cachePath = GetCachePath(filePath);
        var data = BuildFile(peaks, source);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            System.IO.Directory.CreateDirectory(_directory);

            var tempPath = cachePath + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(tempPath, data, cancellationToken).ConfigureAwait(false);
                File.Move(tempPath, cachePath, overwrite: true);
            }
            finally
            {
                TryDelete(tempPath, "写入临时文件清理");
            }

            TrimToLimit(cachePath);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public void Invalidate(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        TryDelete(GetCachePath(filePath), "主动失效");
    }

    public Task ClearAsync(CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                try
                {
                    if (!System.IO.Directory.Exists(_directory))
                    {
                        return;
                    }

                    var removed = 0;
                    foreach (var file in EnumerateCacheFiles())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            file.Delete();
                            removed++;
                        }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                        {
                            _logger.Warn($"删除波形缓存失败 path={file.FullName} error={exception.Message}");
                        }
                    }

                    _logger.Info($"已清空波形缓存 directory={_directory} files={removed}");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _logger.Warn($"清空波形缓存失败 directory={_directory} error={exception.Message}");
                }
            },
            cancellationToken);

    public Task RelocateAsync(string newRoot, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(newRoot) ||
            string.Equals(Path.GetFullPath(newRoot), Path.GetFullPath(_directory), StringComparison.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }

        var oldDirectory = _directory;
        _directory = newRoot;

        return Task.Run(
            () =>
            {
                try
                {
                    System.IO.Directory.CreateDirectory(_directory);
                    if (!System.IO.Directory.Exists(oldDirectory))
                    {
                        return;
                    }

                    foreach (var source in EnumerateCacheFiles(oldDirectory))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var target = Path.Combine(_directory, source.Name);
                        try
                        {
                            File.Move(source.FullName, target, overwrite: true);
                        }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                        {
                            _logger.Warn($"迁移波形缓存失败 from={source.FullName} to={target} error={exception.Message}");
                        }
                    }

                    _logger.Info($"波形缓存目录已迁移 from={oldDirectory} to={_directory}");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _logger.Warn($"迁移波形缓存目录失败 from={oldDirectory} to={_directory} error={exception.Message}");
                }
            },
            cancellationToken);
    }

    /// <summary>按当前上限立即裁剪（不跳过任何文件），返回删除数量。</summary>
    public async Task<int> TrimAsync(CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var removed = await Task.Run(() => TrimToLimit(null), cancellationToken).ConfigureAwait(false);
            if (removed > 0)
            {
                _logger.Info($"波形缓存已按上限裁剪 files={removed} limitMb={LimitMb}");
            }

            return removed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.Warn($"波形缓存裁剪失败 directory={_directory} error={exception.Message}");
            return 0;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>源文件路径 → 缓存文件路径（SHA-1 前 20 位十六进制，ADR-015 §7.1）。</summary>
    private string GetCachePath(string filePath)
    {
        var normalized = filePath.Trim().ToUpperInvariant();
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(normalized)))[..20].ToLowerInvariant();
        return Path.Combine(_directory, FilePrefix + hash + FileExtension);
    }

    private IEnumerable<FileInfo> EnumerateCacheFiles() => EnumerateCacheFiles(_directory);

    private IEnumerable<FileInfo> EnumerateCacheFiles(string directory)
    {
        var info = new DirectoryInfo(directory);
        return info.Exists
            ? info.GetFiles("*" + FileExtension, SearchOption.TopDirectoryOnly)
            : [];
    }

    /// <summary>把快照编码成 EGWV v1 字节（72 字节头 + 每列 4 字节，ADR-015 §7.2）。</summary>
    private byte[] BuildFile(WaveformPeaks peaks, FileInfo source)
    {
        var columnCount = peaks.ColumnCount;
        var data = new byte[HeaderSize + columnCount * BytesPerColumn];
        var span = data.AsSpan();

        MagicBytes.CopyTo(span);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], (ushort)FormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..], HeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], _analyzerVersion);
        BinaryPrimitives.WriteInt32LittleEndian(span[12..], peaks.SampleRate);
        BinaryPrimitives.WriteUInt16LittleEndian(span[16..], (ushort)Math.Clamp(peaks.ColumnsPerSecond, 0, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16LittleEndian(span[18..], (ushort)Math.Clamp(WaveformMath.LowCrossoverHz, 0, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16LittleEndian(span[20..], (ushort)Math.Clamp(WaveformMath.HighCrossoverHz, 0, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16LittleEndian(span[22..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], columnCount);
        BinaryPrimitives.WriteInt64LittleEndian(span[28..], peaks.Duration.Ticks);
        BinaryPrimitives.WriteInt64LittleEndian(span[36..], source.Length);
        BinaryPrimitives.WriteInt64LittleEndian(span[44..], source.LastWriteTimeUtc.Ticks);
        BinaryPrimitives.WriteSingleLittleEndian(span[52..], (float)peaks.FloorDb);
        BinaryPrimitives.WriteSingleLittleEndian(span[56..], (float)peaks.CeilingDb);
        span.Slice(60, 12).Clear();

        for (var i = 0; i < columnCount; i++)
        {
            var offset = HeaderSize + i * BytesPerColumn;
            data[offset] = peaks.Height[i];
            data[offset + 1] = peaks.Low[i];
            data[offset + 2] = peaks.Mid[i];
            data[offset + 3] = peaks.High[i];
        }

        return data;
    }

    /// <summary>解析并校验缓存字节；任何一项不符都返回 null（调用方负责删除）。</summary>
    private WaveformPeaks? Parse(byte[] data, FileInfo source)
    {
        if (data.Length < HeaderSize)
        {
            return null;
        }

        var span = data.AsSpan();
        if (!span[..4].SequenceEqual(MagicBytes) ||
            BinaryPrimitives.ReadUInt16LittleEndian(span[4..]) != FormatVersion ||
            BinaryPrimitives.ReadUInt16LittleEndian(span[6..]) != HeaderSize ||
            BinaryPrimitives.ReadInt32LittleEndian(span[8..]) != _analyzerVersion)
        {
            return null;
        }

        var sampleRate = BinaryPrimitives.ReadInt32LittleEndian(span[12..]);
        var columnsPerSecond = BinaryPrimitives.ReadUInt16LittleEndian(span[16..]);
        var columnCount = BinaryPrimitives.ReadInt32LittleEndian(span[24..]);
        if (sampleRate <= 0 || columnsPerSecond == 0 || columnCount < 0 ||
            data.Length != HeaderSize + (long)columnCount * BytesPerColumn)
        {
            return null;
        }

        var durationTicks = BinaryPrimitives.ReadInt64LittleEndian(span[28..]);
        var sourceLength = BinaryPrimitives.ReadInt64LittleEndian(span[36..]);
        var sourceModifiedTicks = BinaryPrimitives.ReadInt64LittleEndian(span[44..]);
        if (durationTicks < 0 || sourceLength != source.Length ||
            sourceModifiedTicks != source.LastWriteTimeUtc.Ticks)
        {
            return null;
        }

        var height = new byte[columnCount];
        var low = new byte[columnCount];
        var mid = new byte[columnCount];
        var high = new byte[columnCount];
        for (var i = 0; i < columnCount; i++)
        {
            var offset = HeaderSize + i * BytesPerColumn;
            var lowValue = data[offset + 1];
            var midValue = data[offset + 2];
            var highValue = data[offset + 3];
            if (lowValue + midValue + highValue != 255)
            {
                return null;
            }

            height[i] = data[offset];
            low[i] = lowValue;
            mid[i] = midValue;
            high[i] = highValue;
        }

        return new WaveformPeaks
        {
            ColumnsPerSecond = columnsPerSecond,
            ColumnCount = columnCount,
            ValidCount = columnCount,
            Duration = TimeSpan.FromTicks(durationTicks),
            SampleRate = sampleRate,
            FloorDb = BinaryPrimitives.ReadSingleLittleEndian(span[52..]),
            CeilingDb = BinaryPrimitives.ReadSingleLittleEndian(span[56..]),
            Height = height,
            Low = low,
            Mid = mid,
            High = high,
        };
    }

    /// <summary>
    /// 超过上限时按 LastWriteTimeUtc 从旧到新删到 90%。<paramref name="keepPath"/> 非空时跳过该文件
    /// （刚写入的那首不能被自己挤掉）；返回实际删除数量。
    /// </summary>
    private int TrimToLimit(string? keepPath)
    {
        if (LimitMb <= 0)
        {
            return 0;
        }

        var limitBytes = (long)LimitMb * 1024 * 1024;
        var fullKeepPath = string.IsNullOrWhiteSpace(keepPath) ? null : Path.GetFullPath(keepPath);
        try
        {
            var files = EnumerateCacheFiles().ToArray();
            var total = files.Sum(file => file.Length);
            if (total <= limitBytes)
            {
                return 0;
            }

            var target = (long)(limitBytes * TrimTargetRatio);
            var removed = 0;
            foreach (var file in files.OrderBy(file => file.LastWriteTimeUtc))
            {
                if (total <= target)
                {
                    break;
                }

                if (fullKeepPath is not null &&
                    string.Equals(file.FullName, fullKeepPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    var length = file.Length;
                    file.Delete();
                    total -= length;
                    removed++;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    _logger.Warn($"波形缓存 LRU 删除失败 path={file.FullName} error={exception.Message}");
                }
            }

            if (removed > 0)
            {
                _logger.Debug($"波形缓存 LRU 清理 files={removed} remainingBytes={total} limitBytes={limitBytes}");
            }

            return removed;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.Warn($"波形缓存 LRU 清理失败 directory={_directory} error={exception.Message}");
            return 0;
        }
    }

    private void TryDelete(string path, string reason)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.Debug($"删除波形缓存失败 path={path} reason={reason} error={exception.Message}");
        }
    }
}
