using System.Collections.Concurrent;
using System.Windows.Media.Imaging;
using EchoGarage.Core.Abstractions;
using EchoGarage.Core.Services;

namespace EchoGarage.App.Services;

/// <summary>
/// Decodes cached cover images for the UI and keeps a bounded in-memory LRU so scrolling
/// does not re-decode the same album art. Decoding stays off the UI thread.
/// </summary>
public sealed class CoverImageService : IDisposable
{
    private const int MemoryCacheLimit = 300;

    private readonly ICoverCache _cache;
    private readonly IAppLogger _logger;
    private readonly ConcurrentDictionary<string, BitmapSource?> _images = new(StringComparer.Ordinal);
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

        await _concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_images.TryGetValue(memoryKey, out cached))
            {
                return cached;
            }

            var path = _cache.Resolve(request.CoverKey);
            if (path is null)
            {
                path = await _cache.EnsureAsync(request, cancellationToken).ConfigureAwait(false);
            }

            BitmapSource? bitmap = null;
            if (path is not null)
            {
                bitmap = await Task.Run(() => LoadBitmap(path, decodeWidth), cancellationToken).ConfigureAwait(false);
            }

            Remember(memoryKey, bitmap);
            return bitmap;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Debug($"Cover load failed for {request.TrackPath}: {ex.Message}");
            Remember(memoryKey, null);
            return null;
        }
        finally
        {
            _concurrency.Release();
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
