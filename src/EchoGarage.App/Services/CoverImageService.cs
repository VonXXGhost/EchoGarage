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
    private const int MemoryCacheLimit = 300;

    private readonly ICoverCache _cache;
    private readonly IAppLogger _logger;
    private readonly ConcurrentDictionary<string, BitmapSource?> _images = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<BitmapSource?>>> _inFlight = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _lru = new();
    private readonly object _lruGate = new();
    private readonly SemaphoreSlim _concurrency = new(3, 3);
    private bool _disposed;

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
            _lru.Clear();
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
        var lazy = _inFlight.GetOrAdd(memoryKey,
            key => new Lazy<Task<BitmapSource?>>(
                () => LoadThroughAsync(key, request, decodeWidth, cancellationToken),
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
        _images[key] = image;
        Touch(key);

        while (true)
        {
            string? toRemove = null;
            lock (_lruGate)
            {
                if (_lru.Count <= MemoryCacheLimit)
                {
                    break;
                }

                toRemove = _lru.First?.Value;
                if (toRemove is not null)
                {
                    _lru.RemoveFirst();
                }
            }

            if (toRemove is null)
            {
                break;
            }

            _images.TryRemove(toRemove, out _);
        }
    }

    private void Touch(string key)
    {
        lock (_lruGate)
        {
            var node = _lru.Find(key);
            if (node is not null)
            {
                _lru.Remove(node);
            }

            _lru.AddLast(key);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _concurrency.Dispose();
        ClearMemoryCache();
    }
}
