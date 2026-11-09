using System.Collections.Concurrent;
using System.Diagnostics;
using EchoGarage.Core.Abstractions;
using EchoGarage.Core.Models;
using EchoGarage.Core.Services;

namespace EchoGarage.Infrastructure.Library;

public sealed class LibraryScanner : ILibraryScanner
{
    private readonly ILibraryRepository _repository;
    private readonly ITagService _tagService;
    private readonly IAppLogger _logger;

    public LibraryScanner(ILibraryRepository repository, ITagService tagService, IAppLogger logger)
    {
        _repository = repository;
        _tagService = tagService;
        _logger = logger;
    }

    public async Task<ScanResult> ScanFolderAsync(LibraryFolder folder, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var existing = (await _repository.GetTrackStatesAsync(folder.Id, cancellationToken))
            .ToDictionary(s => s.Path, StringComparer.OrdinalIgnoreCase);

        var files = EnumerateAudioFiles(folder.Path);
        var fileSet = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        progress?.Report(new ScanProgress(folder.Path, 0, files.Count, null, "比对文件"));

        var changed = new List<string>(Math.Max(16, files.Count / 10));
        var skipped = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (existing.TryGetValue(file, out var state) && !state.IsMissing && IsUnchanged(file, state))
            {
                skipped++;
                continue;
            }

            changed.Add(file);
        }

        var removed = existing
            .Where(kv => !kv.Value.IsMissing && !fileSet.Contains(kv.Key))
            .Select(kv => kv.Key)
            .ToList();

        var indexed = new ConcurrentBag<Track>();
        var failed = 0;
        var processed = 0;
        var parallelism = Math.Clamp(Environment.ProcessorCount, 2, 8);

        await Parallel.ForEachAsync(changed, new ParallelOptions
        {
            MaxDegreeOfParallelism = parallelism,
            CancellationToken = cancellationToken
        }, async (path, token) =>
        {
            try
            {
                var track = BuildTrack(path, inLibrary: true, folderId: folder.Id, existingTrack: null);
                if (track is not null)
                {
                    indexed.Add(track);
                }
                else
                {
                    Interlocked.Increment(ref failed);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Interlocked.Increment(ref failed);
                _logger.Warn($"Failed to index {path}: {ex.Message}");
            }

            var current = Interlocked.Increment(ref processed);
            if (current % 25 == 0 || current == changed.Count)
            {
                progress?.Report(new ScanProgress(folder.Path, current, changed.Count, path, "读取标签"));
            }

            await Task.CompletedTask;
        }).ConfigureAwait(false);

        if (!indexed.IsEmpty)
        {
            await _repository.UpsertTracksAsync(indexed.ToList(), cancellationToken);
        }

        if (removed.Count > 0)
        {
            await _repository.MarkTracksMissingAsync(removed, cancellationToken);
        }

        await _repository.UpdateFolderScanTimeAsync(folder.Id, DateTime.UtcNow, cancellationToken);
        stopwatch.Stop();
        var result = new ScanResult(folder.Path, indexed.Count, changed.Count, removed.Count, skipped, failed, stopwatch.Elapsed);
        _logger.Info($"Scan finished for {folder.Path}: added/updated={result.Added}, removed={result.Removed}, unchanged={result.Skipped}, failed={result.Failed}, elapsed={result.Elapsed.TotalSeconds:F1}s");
        return result;
    }

    public async Task<Track?> IndexFileAsync(string path, bool inLibrary, long? folderId = null,
        CancellationToken cancellationToken = default)
    {
        var normalized = PathUtil.Normalize(path);
        if (!File.Exists(normalized))
        {
            return null;
        }

        var existing = await _repository.GetTrackByPathAsync(normalized, cancellationToken);
        var track = BuildTrack(normalized, inLibrary, folderId, existing);
        if (track is null)
        {
            return null;
        }

        await _repository.UpsertTrackAsync(track, cancellationToken);
        return track;
    }

    public async Task<IReadOnlyList<Track>> IndexFilesAsync(IEnumerable<string> paths, bool inLibrary,
        IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var list = paths
            .Where(PathUtil.IsSupportedAudio)
            .Select(p => PathUtil.TryNormalize(p, out var normalized) ? normalized : null)
            .Where(p => p is not null)
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var results = new ConcurrentBag<Track>();
        var processed = 0;
        await Parallel.ForEachAsync(list, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8),
            CancellationToken = cancellationToken
        }, async (path, _) =>
        {
            try
            {
                if (!File.Exists(path))
                {
                    return;
                }

                var existing = await _repository.GetTrackByPathAsync(path, cancellationToken).ConfigureAwait(false);
                var track = BuildTrack(path, inLibrary, null, existing);
                if (track is not null)
                {
                    results.Add(track);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Warn($"Failed to index {path}: {ex.Message}");
            }

            var current = Interlocked.Increment(ref processed);
            progress?.Report(new ScanProgress(string.Empty, current, list.Count, path, "读取标签"));
        }).ConfigureAwait(false);

        if (!results.IsEmpty)
        {
            await _repository.UpsertTracksAsync(results.ToList(), cancellationToken);
        }

        return results.OrderBy(t => t.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private Track? BuildTrack(string path, bool inLibrary, long? folderId, Track? existingTrack)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"Cannot stat {path}: {ex.Message}");
            return null;
        }

        AudioTagData? tags = null;
        try
        {
            tags = _tagService.Read(path);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Unable to read tags from {path} ({ex.GetType().Name}: {ex.Message}); indexing file name only.");
        }

        var title = string.IsNullOrWhiteSpace(tags?.Title)
            ? System.IO.Path.GetFileNameWithoutExtension(path)
            : tags!.Title;
        var album = tags?.Album ?? string.Empty;
        var albumArtist = string.IsNullOrWhiteSpace(tags?.AlbumArtist) ? tags?.Artist ?? string.Empty : tags!.AlbumArtist;

        var track = new Track
        {
            Path = path,
            FolderId = folderId ?? (inLibrary ? existingTrack?.FolderId : null),
            IsInLibrary = inLibrary || existingTrack?.IsInLibrary == true,
            IsMissing = false,
            Title = title,
            Artist = tags?.Artist ?? string.Empty,
            Album = album,
            AlbumArtist = albumArtist,
            Genre = tags?.Genre ?? string.Empty,
            Comment = tags?.Comment ?? string.Empty,
            Year = tags?.Year,
            TrackNumber = tags?.TrackNumber ?? 0,
            DiscNumber = tags?.DiscNumber ?? 0,
            DurationMs = tags?.DurationMs ?? 0,
            Bitrate = tags?.Bitrate ?? 0,
            SampleRate = tags?.SampleRate ?? 0,
            Channels = tags?.Channels ?? 0,
            Codec = tags?.Codec ?? PathUtil.ExtensionDisplay(path),
            FileSize = info.Length,
            ModifiedAtUtc = info.LastWriteTimeUtc,
            AddedAtUtc = existingTrack?.AddedAtUtc ?? DateTime.UtcNow,
            LastPlayedAtUtc = existingTrack?.LastPlayedAtUtc,
            PlayCount = existingTrack?.PlayCount ?? 0,
            Rating = existingTrack?.Rating ?? 0,
            CoverKey = CoverKeyFactory.ForTrack(path, album, albumArtist)
        };

        if (existingTrack is not null && existingTrack.CoverKey != track.CoverKey)
        {
            // Album identity changed; the old cover cache entry is simply left unreferenced.
        }

        return track;
    }

    private static bool IsUnchanged(string path, TrackState state)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists &&
                   info.Length == state.FileSize &&
                   info.LastWriteTimeUtc == state.ModifiedAtUtc;
        }
        catch
        {
            return false;
        }
    }

    private static List<string> EnumerateAudioFiles(string folderPath)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
        };

        try
        {
            return Directory.EnumerateFiles(folderPath, "*", options)
                .Where(PathUtil.IsSupportedAudio)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }
}
