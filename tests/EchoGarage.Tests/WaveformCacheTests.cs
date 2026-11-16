using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using EchoGarage.Core.Models;
using EchoGarage.Core.Services;
using EchoGarage.Infrastructure.Audio;
using EchoGarage.Infrastructure.Caching;

namespace EchoGarage.Tests;

/// <summary>
/// ADR-015 §7 波形缓存：EGWV v1 读写、校验、失效、原子写、LRU 裁剪与目录迁移。
/// </summary>
public sealed class WaveformCacheTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();
    private readonly RecordingLogger _logger = new();

    public void Dispose() => _workspace.Dispose();

    // ---- 路径与格式（§7.1 / §7.2）----

    [Fact]
    public async Task CachePath_MatchesAdrConvention()
    {
        var wav = WriteTestWav("song.wav");
        var directory = NewCacheDirectory();
        var cache = CreateCache(directory);

        await cache.SaveAsync(wav, CreatePeaks());

        var upper = wav.Trim().ToUpperInvariant();
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(upper)))[..20].ToLowerInvariant();
        var expected = Path.Combine(directory, "wave_" + hash + ".wfm");
        Assert.True(File.Exists(expected), $"缓存文件名应符合 ADR-015 §7.1：{expected}");
    }

    [Fact]
    public async Task SavedFile_MatchesEgwvV1Layout()
    {
        var wav = WriteTestWav("layout.wav");
        var directory = NewCacheDirectory();
        var cache = CreateCache(directory);
        var peaks = CreatePeaks(columnCount: 37, duration: TimeSpan.FromMilliseconds(740));
        var source = new FileInfo(wav);

        await cache.SaveAsync(wav, peaks);

        var cachePath = FindCacheFile(directory);
        var data = File.ReadAllBytes(cachePath);
        var span = data.AsSpan();
        Assert.Equal(72, FileWaveformCache.HeaderSize);
        Assert.Equal(4, FileWaveformCache.BytesPerColumn);
        Assert.Equal(72 + 37 * 4, data.Length);
        Assert.Equal("EGWV", Encoding.ASCII.GetString(span[..4]));
        Assert.Equal(FileWaveformCache.FormatVersion, BinaryPrimitives.ReadUInt16LittleEndian(span[4..]));
        Assert.Equal(72, BinaryPrimitives.ReadUInt16LittleEndian(span[6..]));
        Assert.Equal(WaveformAnalyzer.AnalyzerVersion, BinaryPrimitives.ReadInt32LittleEndian(span[8..]));
        Assert.Equal(peaks.SampleRate, BinaryPrimitives.ReadInt32LittleEndian(span[12..]));
        Assert.Equal(50, BinaryPrimitives.ReadUInt16LittleEndian(span[16..]));
        Assert.Equal(200, BinaryPrimitives.ReadUInt16LittleEndian(span[18..]));
        Assert.Equal(4000, BinaryPrimitives.ReadUInt16LittleEndian(span[20..]));
        Assert.Equal(37, BinaryPrimitives.ReadInt32LittleEndian(span[24..]));
        Assert.Equal(peaks.Duration.Ticks, BinaryPrimitives.ReadInt64LittleEndian(span[28..]));
        Assert.Equal(source.Length, BinaryPrimitives.ReadInt64LittleEndian(span[36..]));
        Assert.Equal(source.LastWriteTimeUtc.Ticks, BinaryPrimitives.ReadInt64LittleEndian(span[44..]));
        Assert.Equal((float)peaks.FloorDb, BinaryPrimitives.ReadSingleLittleEndian(span[52..]));
        Assert.Equal((float)peaks.CeilingDb, BinaryPrimitives.ReadSingleLittleEndian(span[56..]));
        Assert.True(span.Slice(60, 12).SequenceEqual(new byte[12]), "保留字段必须为零");
    }

    // ---- 基本读写 ----

    [Fact]
    public async Task SaveAndLoad_RoundTripsAllFields()
    {
        var wav = WriteTestWav("roundtrip.wav");
        var cache = CreateCache(NewCacheDirectory());
        var peaks = CreatePeaks(columnCount: 120);

        await cache.SaveAsync(wav, peaks);
        var loaded = await cache.TryLoadAsync(wav);

        Assert.NotNull(loaded);
        Assert.True(loaded.IsComplete);
        Assert.Equal(50, loaded.ColumnsPerSecond);
        Assert.Equal(120, loaded.ColumnCount);
        Assert.Equal(120, loaded.ValidCount);
        Assert.Equal(peaks.SampleRate, loaded.SampleRate);
        Assert.Equal(peaks.Duration, loaded.Duration);
        Assert.Equal((float)peaks.FloorDb, (float)loaded.FloorDb);
        Assert.Equal((float)peaks.CeilingDb, (float)loaded.CeilingDb);
        Assert.Equal(peaks.Height, loaded.Height);
        Assert.Equal(peaks.Low, loaded.Low);
        Assert.Equal(peaks.Mid, loaded.Mid);
        Assert.Equal(peaks.High, loaded.High);
    }

    [Fact]
    public async Task TryLoad_WithoutCacheFile_ReturnsNull()
    {
        var wav = WriteTestWav("miss.wav");
        var cache = CreateCache(NewCacheDirectory());

        Assert.Null(await cache.TryLoadAsync(wav));
    }

    [Fact]
    public async Task Invalidate_DeletesCacheFile()
    {
        var wav = WriteTestWav("invalidate.wav");
        var directory = NewCacheDirectory();
        var cache = CreateCache(directory);
        await cache.SaveAsync(wav, CreatePeaks());

        cache.Invalidate(wav);

        Assert.Empty(Directory.GetFiles(directory, "*.wfm"));
        Assert.Null(await cache.TryLoadAsync(wav));
    }

    [Fact]
    public async Task SaveIncompleteSnapshot_DoesNotWriteFile()
    {
        var wav = WriteTestWav("incomplete.wav");
        var directory = NewCacheDirectory();
        var cache = CreateCache(directory);
        var peaks = CreatePeaks(columnCount: 10).WithValidCount(4);

        await cache.SaveAsync(wav, peaks);

        Assert.Empty(Directory.GetFiles(directory, "*.wfm"));
    }

    [Fact]
    public async Task Save_MissingSourceFile_WarnsAndDoesNotWrite()
    {
        var directory = NewCacheDirectory();
        var cache = CreateCache(directory);

        await cache.SaveAsync(_workspace.Combine("missing.wav"), CreatePeaks());

        Assert.Empty(Directory.GetFiles(directory, "*.wfm"));
        Assert.Contains(_logger.Warnings, message => message.Contains("源文件不存在"));
    }

    [Fact]
    public async Task Load_SourceFileMissing_DeletesCacheAndReturnsNull()
    {
        var wav = WriteTestWav("deleted-source.wav");
        var directory = NewCacheDirectory();
        var cache = CreateCache(directory);
        await cache.SaveAsync(wav, CreatePeaks());
        File.Delete(wav);

        Assert.Null(await cache.TryLoadAsync(wav));
        Assert.Empty(Directory.GetFiles(directory, "*.wfm"));
    }

    [Fact]
    public async Task Save_LeavesNoTempFiles()
    {
        var wav = WriteTestWav("atomic.wav");
        var directory = NewCacheDirectory();
        var cache = CreateCache(directory);

        await cache.SaveAsync(wav, CreatePeaks());

        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    // ---- 校验 / 失效（§7.2）----

    [Fact]
    public async Task TruncatedFile_ReturnsNullAndDeletes()
    {
        var wav = WriteTestWav("truncated.wav");
        var directory = NewCacheDirectory();
        var cache = CreateCache(directory);
        await cache.SaveAsync(wav, CreatePeaks());
        var cachePath = FindCacheFile(directory);
        File.WriteAllBytes(cachePath, File.ReadAllBytes(cachePath)[..10]);

        Assert.Null(await cache.TryLoadAsync(wav));
        Assert.Empty(Directory.GetFiles(directory, "*.wfm"));
    }

    [Fact]
    public async Task BadMagic_ReturnsNullAndDeletes()
    {
        var wav = WriteTestWav("magic.wav");
        var directory = NewCacheDirectory();
        var cache = CreateCache(directory);
        await cache.SaveAsync(wav, CreatePeaks());
        var cachePath = FindCacheFile(directory);
        var data = File.ReadAllBytes(cachePath);
        data[0] = (byte)'X';
        File.WriteAllBytes(cachePath, data);

        Assert.Null(await cache.TryLoadAsync(wav));
        Assert.Empty(Directory.GetFiles(directory, "*.wfm"));
    }

    [Fact]
    public async Task FormatVersionMismatch_ReturnsNullAndDeletes()
    {
        var wav = WriteTestWav("format-version.wav");
        var directory = NewCacheDirectory();
        var cache = CreateCache(directory);
        await cache.SaveAsync(wav, CreatePeaks());
        var cachePath = FindCacheFile(directory);
        PatchUInt16(cachePath, 4, 2);

        Assert.Null(await cache.TryLoadAsync(wav));
        Assert.Empty(Directory.GetFiles(directory, "*.wfm"));
    }

    [Fact]
    public async Task AnalyzerVersionMismatch_ReturnsNullAndDeletes()
    {
        var wav = WriteTestWav("analyzer-version.wav");
        var directory = NewCacheDirectory();
        await CreateCache(directory, analyzerVersion: 1).SaveAsync(wav, CreatePeaks());

        var newer = CreateCache(directory, analyzerVersion: 7);
        Assert.Null(await newer.TryLoadAsync(wav));
        Assert.Empty(Directory.GetFiles(directory, "*.wfm"));
    }

    [Fact]
    public async Task CacheFromPreviousAnalyzerVersion_IsRejectedAndDeleted()
    {
        // ADR-015-A2 把 AnalyzerVersion 从 1 升到 2：旧 .wfm 必须被视为失效并删除，下次请求重新分析。
        var wav = WriteTestWav("legacy-analyzer.wav");
        var directory = NewCacheDirectory();
        var previousVersion = WaveformAnalyzer.AnalyzerVersion - 1;
        Assert.True(previousVersion >= 1, "测试前提：当前分析器版本至少为 2");
        await CreateCache(directory, analyzerVersion: previousVersion).SaveAsync(wav, CreatePeaks());

        Assert.Null(await CreateCache(directory).TryLoadAsync(wav));
        Assert.Empty(Directory.GetFiles(directory, "*.wfm"));
    }

    [Fact]
    public async Task CacheFromPreviousAnalyzerVersion_IsRecomputedByService()
    {
        // 旧版本缓存被拒绝后，WaveformService 走真实分析，并把新版本结果写回缓存。
        var wav = _workspace.Combine("legacy-recompute.wav");
        WaveformTestAudio.WriteSine(wav, seconds: 3.0, frequency: 440);
        var directory = NewCacheDirectory();
        await CreateCache(directory, analyzerVersion: WaveformAnalyzer.AnalyzerVersion - 1)
            .SaveAsync(wav, CreatePeaks());

        var cache = CreateCache(directory);
        var service = new WaveformService(new WaveformAnalyzer(), cache, _logger);
        var peaks = await service.GetAsync(wav);

        Assert.NotNull(peaks);
        Assert.True(peaks.IsComplete);
        // 恒定电平素材在新窗口下稳定段应显示为高柱（ADR-015-A2 的核心验收点）。
        Assert.All(peaks.Height.Skip(6), height => Assert.True(height > 200, $"稳定段应有高度，实际 {height}"));
        // 重新分析的结果已按当前 AnalyzerVersion 写回；再次读取命中缓存。
        Assert.NotNull(await cache.TryLoadAsync(wav));
        var cachePath = FindCacheFile(directory);
        var analyzerVersion = BinaryPrimitives.ReadInt32LittleEndian(File.ReadAllBytes(cachePath).AsSpan(8));
        Assert.Equal(WaveformAnalyzer.AnalyzerVersion, analyzerVersion);
    }

    [Fact]
    public async Task SourceTimestampMismatch_ReturnsNullAndDeletes()
    {
        var wav = WriteTestWav("stamp.wav");
        var directory = NewCacheDirectory();
        var cache = CreateCache(directory);
        await cache.SaveAsync(wav, CreatePeaks());
        File.SetLastWriteTimeUtc(wav, DateTime.UtcNow.AddSeconds(7));

        Assert.Null(await cache.TryLoadAsync(wav));
        Assert.Empty(Directory.GetFiles(directory, "*.wfm"));
    }

    [Fact]
    public async Task InvalidColumnShares_ReturnsNullAndDeletes()
    {
        var wav = WriteTestWav("shares.wav");
        var directory = NewCacheDirectory();
        var cache = CreateCache(directory);
        await cache.SaveAsync(wav, CreatePeaks(columnCount: 3));
        var cachePath = FindCacheFile(directory);
        var data = File.ReadAllBytes(cachePath);
        var lastColumn = FileWaveformCache.HeaderSize + 2 * FileWaveformCache.BytesPerColumn;
        data[lastColumn + 1] = 254; // low
        data[lastColumn + 2] = 0;   // mid
        data[lastColumn + 3] = 0;   // high → 254 != 255
        File.WriteAllBytes(cachePath, data);

        Assert.Null(await cache.TryLoadAsync(wav));
        Assert.Empty(Directory.GetFiles(directory, "*.wfm"));
    }

    // ---- LRU（§7.5）----

    [Fact]
    public async Task Read_RefreshesLastWriteTime()
    {
        var wav = WriteTestWav("touch.wav");
        var directory = NewCacheDirectory();
        var cache = CreateCache(directory);
        await cache.SaveAsync(wav, CreatePeaks());
        var cachePath = FindCacheFile(directory);
        var old = DateTime.UtcNow.AddHours(-5);
        File.SetLastWriteTimeUtc(cachePath, old);

        Assert.NotNull(await cache.TryLoadAsync(wav));

        Assert.True(File.GetLastWriteTimeUtc(cachePath) > DateTime.UtcNow.AddMinutes(-1), "读到缓存后应刷新 LRU 时间戳");
    }

    [Fact]
    public async Task TrimAsync_DeletesOldestUntilNinetyPercent()
    {
        var directory = NewCacheDirectory();
        var cache = CreateCache(directory);
        cache.LimitMb = 1;
        var oldest = CreateDummyCacheFile(directory, "wave_oldest.wfm", 300 * 1024, DateTime.UtcNow.AddHours(-4));
        var second = CreateDummyCacheFile(directory, "wave_second.wfm", 300 * 1024, DateTime.UtcNow.AddHours(-3));
        var third = CreateDummyCacheFile(directory, "wave_third.wfm", 300 * 1024, DateTime.UtcNow.AddHours(-2));
        var newest = CreateDummyCacheFile(directory, "wave_newest.wfm", 300 * 1024, DateTime.UtcNow.AddHours(-1));

        var removed = await cache.TrimAsync();

        Assert.Equal(1, removed);
        Assert.False(File.Exists(oldest), "应删除最旧的缓存");
        Assert.True(File.Exists(second));
        Assert.True(File.Exists(third));
        Assert.True(File.Exists(newest));
    }

    [Fact]
    public async Task TrimAsync_Unlimited_DoesNothing()
    {
        var directory = NewCacheDirectory();
        var cache = CreateCache(directory);
        cache.LimitMb = 0;
        CreateDummyCacheFile(directory, "wave_a.wfm", 300 * 1024, DateTime.UtcNow.AddHours(-2));
        CreateDummyCacheFile(directory, "wave_b.wfm", 300 * 1024, DateTime.UtcNow.AddHours(-1));

        Assert.Equal(0, await cache.TrimAsync());
        Assert.Equal(2, Directory.GetFiles(directory, "*.wfm").Length);
    }

    [Fact]
    public async Task TrimAsync_EmptyDirectory_ReturnsZero()
    {
        var cache = CreateCache(NewCacheDirectory());
        cache.LimitMb = 1;

        Assert.Equal(0, await cache.TrimAsync());
    }

    [Fact]
    public async Task Save_KeepsJustWrittenFileEvenWhenItExceedsTrimTarget()
    {
        // 刚写入的缓存单文件就超过 90% 目标：LRU 不应把它自己挤掉（skip current）。
        var wav = WriteTestWav("big.wav");
        var directory = NewCacheDirectory();
        var cache = CreateCache(directory);
        cache.LimitMb = 1;
        CreateDummyCacheFile(directory, "wave_old.wfm", 200 * 1024, DateTime.UtcNow.AddHours(-5));

        await cache.SaveAsync(wav, CreatePeaks(columnCount: 250_000));

        Assert.NotNull(await cache.TryLoadAsync(wav));
        Assert.False(File.Exists(Path.Combine(directory, "wave_old.wfm")), "较旧的缓存应被清理");
    }

    // ---- 清空 / 迁移 ----

    [Fact]
    public async Task ClearAsync_DeletesWaveformFilesOnly()
    {
        var directory = NewCacheDirectory();
        var cache = CreateCache(directory);
        await cache.SaveAsync(WriteTestWav("clear-a.wav"), CreatePeaks());
        await cache.SaveAsync(WriteTestWav("clear-b.wav"), CreatePeaks());
        var keep = Path.Combine(directory, "notes.txt");
        File.WriteAllText(keep, "keep me");

        await cache.ClearAsync();

        Assert.Empty(Directory.GetFiles(directory, "*.wfm"));
        Assert.True(File.Exists(keep));
    }

    [Fact]
    public async Task ClearAsync_EmptyDirectory_DoesNotThrow()
    {
        var cache = CreateCache(NewCacheDirectory());

        await cache.ClearAsync();
    }

    [Fact]
    public async Task RelocateAsync_MovesCacheFilesToNewRoot()
    {
        var wav = WriteTestWav("relocate.wav");
        var oldDirectory = NewCacheDirectory();
        var newDirectory = _workspace.Combine("WaveformsMoved");
        var cache = CreateCache(oldDirectory);
        await cache.SaveAsync(wav, CreatePeaks());
        var fileName = Path.GetFileName(FindCacheFile(oldDirectory));

        await cache.RelocateAsync(newDirectory);

        Assert.Equal(newDirectory, cache.Directory);
        Assert.True(File.Exists(Path.Combine(newDirectory, fileName)));
        Assert.NotNull(await cache.TryLoadAsync(wav));
    }

    [Fact]
    public async Task RelocateAsync_SameDirectory_KeepsFiles()
    {
        var wav = WriteTestWav("relocate-same.wav");
        var directory = NewCacheDirectory();
        var cache = CreateCache(directory);
        await cache.SaveAsync(wav, CreatePeaks());

        await cache.RelocateAsync(directory);

        Assert.Equal(directory, cache.Directory);
        Assert.NotNull(await cache.TryLoadAsync(wav));
    }

    // ---- 并发 ----

    [Fact]
    public async Task ConcurrentWrites_DoNotThrow()
    {
        var directory = NewCacheDirectory();
        var cache = CreateCache(directory);
        var peaks = CreatePeaks(columnCount: 40);
        var writes = Enumerable.Range(0, 16)
            .Select(index =>
            {
                var wav = WriteTestWav($"concurrent-{index}.wav");
                return cache.SaveAsync(wav, peaks);
            })
            .ToArray();

        await Task.WhenAll(writes);

        Assert.Equal(16, Directory.GetFiles(directory, "*.wfm").Length);
    }

    // ---- 辅助 ----

    private string NewCacheDirectory() => _workspace.Combine("Waveforms");

    private FileWaveformCache CreateCache(string directory, int analyzerVersion = WaveformAnalyzer.AnalyzerVersion) =>
        new(directory, _logger, analyzerVersion);

    private string WriteTestWav(string name)
    {
        var path = _workspace.Combine(name);
        return WaveformTestAudio.WriteSine(path, seconds: 0.5, frequency: 440);
    }

    private static string FindCacheFile(string directory) =>
        Directory.GetFiles(directory, "*.wfm").Single();

    private static string CreateDummyCacheFile(string directory, string name, int size, DateTime lastWriteUtc)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, new byte[size]);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    private static void PatchUInt16(string path, int offset, ushort value)
    {
        var data = File.ReadAllBytes(path);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), value);
        File.WriteAllBytes(path, data);
    }

    /// <summary>构造一组合法的完成快照（low + mid + high 严格等于 255）。</summary>
    private static WaveformPeaks CreatePeaks(int columnCount = 100, TimeSpan? duration = null, int sampleRate = 44100)
    {
        var height = new byte[columnCount];
        var low = new byte[columnCount];
        var mid = new byte[columnCount];
        var high = new byte[columnCount];
        for (var i = 0; i < columnCount; i++)
        {
            var lowValue = (byte)(10 + i % 30);
            height[i] = (byte)(i % 256);
            low[i] = lowValue;
            mid[i] = 200;
            high[i] = (byte)(255 - lowValue - 200);
        }

        return new WaveformPeaks
        {
            ColumnsPerSecond = WaveformMath.ColumnsPerSecond,
            ColumnCount = columnCount,
            ValidCount = columnCount,
            Duration = duration ?? TimeSpan.FromSeconds(0.5),
            SampleRate = sampleRate,
            FloorDb = -42.5,
            CeilingDb = -3.5,
            Height = height,
            Low = low,
            Mid = mid,
            High = high,
        };
    }
}
