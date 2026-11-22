using System.Collections.Concurrent;
using System.Windows.Media.Imaging;
using EchoGarage.Core.Abstractions;
using EchoGarage.Core.Services;

namespace EchoGarage.App.Services;

/// <summary>
/// Decodes cached cover images for the UI and keeps a bounded in-memory LRU so scrolling
/// does not re-decode the same album art. Decoding stays off the UI thread.
///
/// Two rules keep the list reliable while the user scrolls quickly:
/// <list type="bullet">
/// <item>Concurrent requests for the same cover are coalesced into a single extraction/decode.</item>
/// <item>A transient failure (I/O, lock, decode error) is never cached as "no artwork" — only a
/// confirmed negative result from the cover cache is remembered, so a later request retries.</item>
/// </list>
/// </summary>
public sealed class CoverImageService : IDisposable
{
    /// <summary>内存缓存的硬上限：1 GB（owner 规范，2026-11-21）。</summary>
    public const long MemoryCacheByteLimit = 1024L * 1024 * 1024;

    /// <summary>
    /// 条目数上限。真正限制内存的是 <see cref="MemoryCacheByteLimit"/>；这里只防止
    /// “大量极小条目”把链表撑得过长（每条约 100 字节开销）。
    /// </summary>
    private const int MemoryCacheEntryLimit = 512;

    private readonly ICoverCache _cache;
    private readonly IAppLogger _logger;
    private readonly ConcurrentDictionary<string, BitmapSource?> _images = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<BitmapSource?>>> _inFlight = new(StringComparer.Ordinal);
    private readonly CoverCacheBudget _budget = new(MemoryCacheByteLimit, MemoryCacheEntryLimit);
    private readonly object _lruGate = new();
    private readonly SemaphoreSlim _concurrency = new(3, 3);
    private bool _disposed;

    /// <summary>内存缓存的当前条目数与估算占用（诊断 / 设置界面用）。</summary>
    public (int Entries, long Bytes) MemoryCacheStats
    {
        get
        {
            lock (_lruGate)
            {
                return (_budget.Count, _budget.Bytes);
            }
        }
    }

    public CoverImageService(ICoverCache cache, IAppLogger logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public void ClearMemoryCache()
    {
        _images.Clear();
        _inFlight.Clear();
        lock (_lruGate)
        {
            _budget.Clear();
        }
    }

    public void Invalidate(string? coverKey)
    {
        if (string.IsNullOrWhiteSpace(coverKey))
        {
            return;
        }

        foreach (var key in _images.Keys.Where(k => k.EndsWith("|" + coverKey, StringComparison.Ordinal)).ToList())
        {
            _images.TryRemove(key, out _);

            // 同步摘除 LRU 节点，否则账本里会堆积已经没有对应图片的僵尸条目。
            lock (_lruGate)
            {
                _budget.Remove(key);
            }
        }
    }

    public async Task<BitmapSource?> GetAsync(CoverRequest request, int decodeWidth, CancellationToken cancellationToken = default)
    {
        if (_disposed || string.IsNullOrWhiteSpace(request.CoverKey))
        {
            return null;
        }

        var memoryKey = $"{decodeWidth}|{request.CoverKey}";
        if (_images.TryGetValue(memoryKey, out var cached))
        {
            Touch(memoryKey);
            return cached;
        }

        // Many list rows can request the same album cover within a few milliseconds while the
        // user scrolls. Coalesce them into one extraction + decode instead of queuing duplicates.
        // The lazy factory must start on the thread pool: its synchronous prefix runs the cover
        // cache's file probes and (on a miss) TagLib cover extraction. Awaiting the lazy from the
        // UI thread would otherwise run that IO on the dispatcher and stall the cover fade.
        var lazy = _inFlight.GetOrAdd(memoryKey,
            key => new Lazy<Task<BitmapSource?>>(
                () => Task.Run(() => LoadThroughAsync(key, request, decodeWidth, cancellationToken)),
                LazyThreadSafetyMode.ExecutionAndPublication));
        return await lazy.Value.ConfigureAwait(false);
    }

    private async Task<BitmapSource?> LoadThroughAsync(
        string memoryKey,
        CoverRequest request,
        int decodeWidth,
        CancellationToken cancellationToken)
    {
        try
        {
            if (_images.TryGetValue(memoryKey, out var cached))
            {
                return cached;
            }

            await _concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_images.TryGetValue(memoryKey, out cached))
                {
                    return cached;
                }

                string? path;
                try
                {
                    path = await _cache.EnsureAsync(request, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Transient extraction failure: do not remember it, a later request may succeed.
                    _logger.Warn($"Cover extraction failed for {request.TrackPath}: {ex.Message}");
                    return null;
                }

                if (path is null)
                {
                    // The cache confirmed there is no artwork for this track (negative marker).
                    Remember(memoryKey, null);
                    return null;
                }

                BitmapSource? bitmap = null;
                try
                {
                    bitmap = await Task.Run(() => LoadBitmap(path, decodeWidth), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Cover decode failed for {path}: {ex.Message}");
                }

                if (bitmap is null)
                {
                    // A cached file may be unreadable (interrupted write, locked file…). Retry later
                    // instead of poisoning the memory cache with a permanent miss.
                    _logger.Debug($"Cover decode returned no image: {path}");
                    return null;
                }

                Remember(memoryKey, bitmap);
                return bitmap;
            }
            finally
            {
                _concurrency.Release();
            }
        }
        finally
        {
            _inFlight.TryRemove(memoryKey, out _);
        }
    }

    private static BitmapSource? LoadBitmap(string path, int decodeWidth)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            if (decodeWidth > 0)
            {
                image.DecodePixelWidth = decodeWidth;
            }

            image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            using (var stream = File.OpenRead(path))
            {
                image.StreamSource = stream;
                image.EndInit();
            }

            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private void Remember(string key, BitmapSource? image)
    {
        var bytes = EstimateBytes(image);
        _images[key] = image;

        List<string> evicted;
        lock (_lruGate)
        {
            _budget.Add(key, bytes);
            evicted = _budget.Trim();
        }

        foreach (var stale in evicted)
        {
            _images.TryRemove(stale, out _);
        }
    }

    private void Touch(string key)
    {
        lock (_lruGate)
        {
            _budget.Touch(key);
        }
    }

    /// <summary>
    /// 估算一张位图在非托管内存里的占用（WPF 的像素缓冲区不在 GC 堆上，必须自己记账）。
    /// </summary>
    private static long EstimateBytes(BitmapSource? image)
    {
        if (image is null)
        {
            return 0;
        }

        try
        {
            var stride = ((long)image.PixelWidth * image.Format.BitsPerPixel + 7) / 8;
            return stride * image.PixelHeight + 96;
        }
        catch
        {
            return 4096;
        }
    }


    public void Dispose()
    {
        _disposed = true;
        _concurrency.Dispose();
        ClearMemoryCache();
    }
}
