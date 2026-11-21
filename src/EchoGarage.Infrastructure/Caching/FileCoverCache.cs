using EchoGarage.Core.Abstractions;
using EchoGarage.Core.Services;

namespace EchoGarage.Infrastructure.Caching;

/// <summary>
/// Stores extracted cover art as plain image files on disk, keyed by album identity or file path.
/// A <c>.none</c> marker records a negative result together with the source file's
/// modification stamp so the file is not parsed again until it actually changes.
/// </summary>
public sealed class FileCoverCache : ICoverCache
{
    private static readonly string[] FolderCoverNames =
        ["cover", "folder", "front", "album", "albumart", "folderart"];

    private static readonly string[] SupportedCoverExtensions =
        [".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif"];

    private readonly IAppLogger _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private string _coversDirectory;

    /// <summary>裁剪后保留的比例：删到上限的 90%，避免每次启动都为几 MB 反复扫描。</summary>
    private const double TrimTargetRatio = 0.9;

    public FileCoverCache(string coversDirectory, IAppLogger logger)
    {
        _coversDirectory = coversDirectory;
        _logger = logger;
        Directory.CreateDirectory(_coversDirectory);
    }

    public string CoversDirectory => _coversDirectory;

    /// <summary>封面缓存目录的容量上限（MB）；0 表示不限制。与波形缓存同一套 LRU 规则。</summary>
    public int LimitMb { get; set; } = 2048;

    public string? Resolve(string? coverKey)
    {
        if (string.IsNullOrWhiteSpace(coverKey))
        {
            return null;
        }

        foreach (var extension in SupportedCoverExtensions)
        {
            var candidate = System.IO.Path.Combine(_coversDirectory, coverKey + extension);
            // A zero-length file is a leftover from an interrupted write; treat it as missing
            // so the artwork is extracted again instead of being decoded (and failing forever).
            if (File.Exists(candidate) && new FileInfo(candidate).Length > 0)
            {
                return candidate;
            }
        }

        return null;
    }

    public async Task<string?> EnsureAsync(CoverRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.CoverKey))
        {
            return null;
        }

        var stamp = $"{request.ModifiedAtUtc.Ticks}:{request.FileSize}";
        var stampPath = System.IO.Path.Combine(_coversDirectory, request.CoverKey + ".stamp");
        var marker = System.IO.Path.Combine(_coversDirectory, request.CoverKey + ".none");
        var isAlbumKey = request.CoverKey.StartsWith("album_", StringComparison.Ordinal);

        var existing = Resolve(request.CoverKey);
        var cachedStamp = await ReadTextIfExistsAsync(stampPath, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            // Album covers are shared by every track of the album, so an individual track's
            // modification stamp must not invalidate artwork extracted from a sibling file.
            if (isAlbumKey || cachedStamp == stamp)
            {
                return existing;
            }
        }

        if (existing is null &&
            await ReadTextIfExistsAsync(marker, cancellationToken).ConfigureAwait(false) == stamp)
        {
            return null;
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            existing = Resolve(request.CoverKey);
            cachedStamp = await ReadTextIfExistsAsync(stampPath, cancellationToken).ConfigureAwait(false);
            if (existing is not null && (isAlbumKey || cachedStamp == stamp))
            {
                return existing;
            }

            if (!isAlbumKey && existing is not null && cachedStamp != stamp)
            {
                // A per-file cover is stale: the source file changed since it was extracted.
                DeleteCachedEntry(request.CoverKey);
            }

            if (File.Exists(marker) &&
                await File.ReadAllTextAsync(marker, cancellationToken).ConfigureAwait(false) == stamp)
            {
                return null;
            }

            var (bytes, mime) = ExtractFromFile(request.TrackPath);
            if (bytes is null)
            {
                var folderCover = FindFolderCover(request.TrackPath);
                if (folderCover is not null)
                {
                    bytes = await File.ReadAllBytesAsync(folderCover, cancellationToken).ConfigureAwait(false);
                    mime = MimeFromExtension(folderCover);
                }
            }

            var path = bytes is null ? null : await WriteAsync(request.CoverKey, bytes, mime, cancellationToken);
            if (path is null)
            {
                await File.WriteAllTextAsync(marker, stamp, cancellationToken);
                return null;
            }

            if (File.Exists(marker))
            {
                File.Delete(marker);
            }

            await File.WriteAllTextAsync(stampPath, stamp, cancellationToken);
            return path;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Do not swallow the failure: the image service must not remember this as
            // "no artwork", otherwise one transient I/O problem would blank the cover for the
            // rest of the session.
            _logger.Warn($"Cover extraction failed for {request.TrackPath}: {ex.Message}");
            throw;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public void Invalidate(string? coverKey)
    {
        if (string.IsNullOrWhiteSpace(coverKey))
        {
            return;
        }

        DeleteCachedEntry(coverKey);
    }

    private void DeleteCachedEntry(string coverKey)
    {
        foreach (var extension in SupportedCoverExtensions)
        {
            TryDelete(System.IO.Path.Combine(_coversDirectory, coverKey + extension));
        }

        TryDelete(System.IO.Path.Combine(_coversDirectory, coverKey + ".none"));
        TryDelete(System.IO.Path.Combine(_coversDirectory, coverKey + ".stamp"));
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.Debug($"Could not delete cached file {path}: {ex.Message}");
        }
    }

    private static async Task<string?> ReadTextIfExistsAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// 按 <see cref="LimitMb"/> 裁剪封面缓存：按最后写入时间从旧到新删除，删到上限的 90%。
    /// 同时删除对应的 <c>.stamp</c> / <c>.none</c> 伴生文件，避免留下指向已删封面的标记。
    /// </summary>
    public async Task<int> TrimAsync(CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var removed = await Task.Run(() => TrimToLimit(cancellationToken), cancellationToken).ConfigureAwait(false);
            if (removed > 0)
            {
                _logger.Info($"封面缓存已按上限裁剪 files={removed} limitMb={LimitMb}");
            }

            return removed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.Warn($"封面缓存裁剪失败 directory={_coversDirectory} error={exception.Message}");
            return 0;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private int TrimToLimit(CancellationToken cancellationToken)
    {
        if (LimitMb <= 0 || !Directory.Exists(_coversDirectory))
        {
            return 0;
        }

        var limitBytes = (long)LimitMb * 1024 * 1024;
        var files = new List<FileInfo>();
        long total = 0;
        foreach (var path in Directory.EnumerateFiles(_coversDirectory))
        {
            var extension = System.IO.Path.GetExtension(path);
            if (!SupportedCoverExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var info = new FileInfo(path);
                files.Add(info);
                total += info.Length;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.Debug($"封面缓存统计失败 path={path} error={exception.Message}");
            }
        }

        if (total <= limitBytes)
        {
            return 0;
        }

        var target = (long)(limitBytes * TrimTargetRatio);
        var removed = 0;
        foreach (var file in files.OrderBy(info => info.LastWriteTimeUtc))
        {
            if (total <= target)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var key = System.IO.Path.GetFileNameWithoutExtension(file.FullName);
                var length = file.Length;
                file.Delete();
                total -= length;
                removed++;

                // 伴生标记必须一起删：否则 .stamp 会指向一个不存在的封面，.none 会挡住重新提取。
                TryDelete(System.IO.Path.Combine(_coversDirectory, key + ".stamp"));
                TryDelete(System.IO.Path.Combine(_coversDirectory, key + ".none"));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.Debug($"封面缓存 LRU 删除失败 path={file.FullName} error={exception.Message}");
            }
        }

        return removed;
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await Task.Run(() =>
        {
            if (!Directory.Exists(_coversDirectory))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(_coversDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Could not delete cached cover {file}: {ex.Message}");
                }
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task RelocateAsync(string newRoot, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(newRoot);
        var oldRoot = _coversDirectory;
        _coversDirectory = newRoot;

        if (string.Equals(oldRoot, newRoot, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(oldRoot))
        {
            return Task.CompletedTask;
        }

        return Task.Run(() =>
        {
            foreach (var file in Directory.EnumerateFiles(oldRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    File.Move(file, System.IO.Path.Combine(newRoot, System.IO.Path.GetFileName(file)), false);
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Could not move cached cover {file}: {ex.Message}");
                }
            }
        }, cancellationToken);
    }

    private (byte[]? Bytes, string? Mime) ExtractFromFile(string trackPath)
    {
        try
        {
            using var file = TagLib.File.Create(trackPath, TagLib.ReadStyle.None);
            var pictures = file.Tag.Pictures;
            if (pictures is { Length: > 0 })
            {
                var picture = pictures.FirstOrDefault(p => p.Type == TagLib.PictureType.FrontCover) ?? pictures[0];
                if (picture.Data.Count > 0)
                {
                    return (picture.Data.Data, picture.MimeType ?? GuessMime(picture.Data.Data));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Debug($"TagLib cover read failed for {trackPath}: {ex.Message}");
        }

        return (null, null);
    }

    private static string? FindFolderCover(string trackPath)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(trackPath);
            if (directory is null || !Directory.Exists(directory))
            {
                return null;
            }

            var candidates = Directory.EnumerateFiles(directory)
                .Where(f => SupportedCoverExtensions.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant()))
                .ToList();

            foreach (var name in FolderCoverNames)
            {
                var match = candidates.FirstOrDefault(f =>
                    string.Equals(System.IO.Path.GetFileNameWithoutExtension(f), name, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    return match;
                }
            }

            return candidates.Count == 1 ? candidates[0] : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> WriteAsync(string coverKey, byte[] bytes, string? mime, CancellationToken cancellationToken)
    {
        var extension = ExtensionFor(mime, bytes);
        if (extension is null)
        {
            return null;
        }

        var path = System.IO.Path.Combine(_coversDirectory, coverKey + extension);
        Directory.CreateDirectory(_coversDirectory);

        // Write to a temporary file and move it into place atomically: a concurrent reader
        // must never observe a half-written image file (decoding one would fail permanently
        // in the old implementation that remembered the miss).
        var tempPath = path + ".tmp" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                TryDelete(tempPath);
            }
        }

        return path;
    }

    private static string? ExtensionFor(string? mime, byte[] bytes)
    {
        switch (mime?.ToLowerInvariant())
        {
            case "image/jpeg" or "image/jpg":
                return ".jpg";
            case "image/png":
                return ".png";
            case "image/webp":
                return ".webp";
            case "image/bmp":
                return ".bmp";
            case "image/gif":
                return ".gif";
        }

        if (bytes.Length > 3 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            return ".png";
        }

        if (bytes.Length > 2 && bytes[0] == 0xFF && bytes[1] == 0xD8)
        {
            return ".jpg";
        }

        if (bytes.Length > 11 && bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F')
        {
            return ".webp";
        }

        return null;
    }

    private static string? MimeFromExtension(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".gif" => "image/gif",
        _ => null
    };

    private static string? GuessMime(byte[] bytes)
    {
        if (bytes.Length > 3 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            return "image/png";
        }

        return "image/jpeg";
    }
}
