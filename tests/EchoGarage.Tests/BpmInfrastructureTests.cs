using System.Text;
using EchoGarage.Core.Models;
using EchoGarage.Core.Services;
using EchoGarage.Infrastructure.Audio;
using EchoGarage.Infrastructure.Configuration;
using EchoGarage.Infrastructure.Conversion;
using EchoGarage.Infrastructure.Data;
using EchoGarage.Infrastructure.Logging;
using EchoGarage.Infrastructure.Tagging;
using Microsoft.Data.Sqlite;

namespace EchoGarage.Tests;

public sealed class BpmInfrastructureTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();
    private readonly AppPaths _paths;
    private readonly FileLogger _logger;
    private readonly SqliteDatabase _database;
    private readonly SqliteLibraryRepository _library;
    private readonly SqliteBpmCache _bpmCache;
    private readonly TagLibTagService _tags;

    public BpmInfrastructureTests()
    {
        _paths = new AppPaths(_workspace.Combine("state"));
        _paths.EnsureCreated();
        _logger = new FileLogger(_paths);
        _database = new SqliteDatabase(_paths, _logger);
        _library = new SqliteLibraryRepository(_database, _logger);
        _bpmCache = new SqliteBpmCache(_database, _logger);
        _tags = new TagLibTagService(_logger);
        _library.InitializeAsync().GetAwaiter().GetResult();
    }

    // =====================================================================
    // SQLite Schema v5 migration & SqliteBpmCache
    // =====================================================================

    [Fact]
    public async Task Database_MigratesVersion4ToVersion5_CreatesBpmCacheTableAndPreservesTracks()
    {
        var paths = new AppPaths(_workspace.Combine("migrate-v4"));
        paths.EnsureCreated();
        var connStr = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();

        await using (var conn = new SqliteConnection(connStr))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE tracks (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Path TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    FolderId INTEGER NULL,
                    Title TEXT NOT NULL DEFAULT '',
                    DurationMs INTEGER NOT NULL DEFAULT 0);
                INSERT INTO tracks (Path, Title, DurationMs)
                VALUES ('demo/track01.wav', 'Demo Track 01', 120000);
                PRAGMA user_version = 4;
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var db = new SqliteDatabase(paths, new FileLogger(paths));
        await db.InitializeAsync();

        await using var check = new SqliteConnection(connStr);
        await check.OpenAsync();

        await using (var verCmd = check.CreateCommand())
        {
            verCmd.CommandText = "PRAGMA user_version;";
            Assert.Equal(6, Convert.ToInt64(await verCmd.ExecuteScalarAsync()));
        }

        await using (var tblCmd = check.CreateCommand())
        {
            tblCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='track_bpm_cache';";
            Assert.Equal("track_bpm_cache", await tblCmd.ExecuteScalarAsync());
        }

        await using (var rowCmd = check.CreateCommand())
        {
            rowCmd.CommandText = "SELECT Title, DurationMs FROM tracks WHERE Path='demo/track01.wav';";
            await using var reader = await rowCmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("Demo Track 01", reader.GetString(0));
            Assert.Equal(120000, reader.GetInt64(1));
        }
    }

    [Fact]
    public async Task Database_MigratesVersion5ToVersion6_AddsSegmentColumnsAndPreservesExistingBpmCache()
    {
        var paths = new AppPaths(_workspace.Combine("migrate-v5"));
        paths.EnsureCreated();
        var connStr = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();

        var existingTrackPath = _workspace.Combine("media", "legacy-v5.wav");
        await using (var conn = new SqliteConnection(connStr))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE tracks (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Path TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    FolderId INTEGER NULL,
                    Title TEXT NOT NULL DEFAULT '',
                    DurationMs INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE track_bpm_cache (
                    Path TEXT NOT NULL COLLATE NOCASE PRIMARY KEY,
                    FileSize INTEGER NOT NULL,
                    ModifiedAtUtc TEXT NOT NULL,
                    DurationMs INTEGER NOT NULL,
                    MeasuredBpm REAL NULL,
                    Confidence REAL NULL,
                    IsVariableTempo INTEGER NOT NULL DEFAULT 0,
                    CandidatesJson TEXT NULL,
                    AnalyzerId TEXT NULL,
                    AnalyzerVersion INTEGER NOT NULL DEFAULT 0,
                    ManualBpm REAL NULL,
                    UpdatedAtUtc TEXT NOT NULL);
                INSERT INTO track_bpm_cache (Path, FileSize, ModifiedAtUtc, DurationMs, MeasuredBpm, Confidence, IsVariableTempo, ManualBpm, UpdatedAtUtc)
                VALUES (@p, 4096, '2026-11-25T00:00:00.0000000Z', 180000, 124.0, 0.91, 1, 125.0, '2026-11-25T00:00:00.0000000Z');
                PRAGMA user_version = 5;
                """;
            cmd.Parameters.AddWithValue("@p", PathUtil.Normalize(existingTrackPath));
            await cmd.ExecuteNonQueryAsync();
        }

        var db = new SqliteDatabase(paths, new FileLogger(paths));
        await db.InitializeAsync();

        await using (var check = new SqliteConnection(connStr))
        {
            await check.OpenAsync();
            await using var verCmd = check.CreateCommand();
            verCmd.CommandText = "PRAGMA user_version;";
            Assert.Equal(6, Convert.ToInt64(await verCmd.ExecuteScalarAsync()));
        }

        var cache = new SqliteBpmCache(db, new FileLogger(paths));
        var entry = await cache.TryGetAsync(existingTrackPath);
        Assert.NotNull(entry);
        Assert.Equal(124.0, entry!.MeasuredBpm);
        Assert.Equal(125.0, entry.ManualBpm);
        Assert.True(entry.IsVariableTempo);
        Assert.Empty(entry.Segments);
        Assert.Equal(0, entry.DetectedSegmentCount);
    }

    [Fact]
    public async Task SqliteBpmCache_RoundTripsSegmentsAndTooManyFlag_AndClearMeasuredClearsSegments()
    {
        var pathSegmented = _workspace.Combine("media", "demo-segments.wav");
        var pathTooMany = _workspace.Combine("media", "demo-toomany.wav");
        var now = new DateTime(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);

        IReadOnlyList<BpmSegment> segs =
        [
            new(0, 30, 96.0, 0.93, IsPrimary: false),
            new(30, 110, 128.0, 0.96, IsPrimary: true),
            new(110, 150, 144.0, 0.91, IsPrimary: false)
        ];

        await _bpmCache.SaveMeasuredAsync(new BpmCacheEntry
        {
            Path = pathSegmented,
            FileSize = 3000,
            ModifiedAtUtc = now,
            DurationMs = 150_000,
            MeasuredBpm = 128.0,
            Confidence = 0.94,
            IsVariableTempo = true,
            Segments = segs,
            DetectedSegmentCount = 3,
            AnalyzerId = "beat-this-small0-onnx",
            AnalyzerVersion = 1,
            UpdatedAtUtc = now
        });
        await _bpmCache.SaveManualAsync(pathSegmented, 3000, now, 150_000, 128.5);

        var loaded = await _bpmCache.TryGetAsync(pathSegmented);
        Assert.NotNull(loaded);
        Assert.Equal(3, loaded!.Segments.Count);
        Assert.Equal(3, loaded.DetectedSegmentCount);
        Assert.Equal(96.0, loaded.Segments[0].Bpm);
        Assert.True(loaded.Segments[1].IsPrimary);

        await _bpmCache.SaveMeasuredAsync(new BpmCacheEntry
        {
            Path = pathTooMany,
            FileSize = 4000,
            ModifiedAtUtc = now,
            DurationMs = 200_000,
            MeasuredBpm = 120.0,
            Confidence = 0.72,
            IsVariableTempo = true,
            Segments = [],
            DetectedSegmentCount = 7,
            AnalyzerId = "beat-this-small0-onnx",
            AnalyzerVersion = 1,
            UpdatedAtUtc = now
        });

        var tooMany = await _bpmCache.TryGetAsync(pathTooMany);
        Assert.NotNull(tooMany);
        Assert.Empty(tooMany!.Segments);
        Assert.Equal(7, tooMany.DetectedSegmentCount);
        var resolvedTooMany = BpmResolver.Resolve(null, tooMany, 4000, now, 200_000).DisplayState;
        Assert.True(resolvedTooMany.TooManyTempoChanges);
        Assert.Empty(resolvedTooMany.Segments);

        // Clear measured cache -> clears SegmentsJson & DetectedSegmentCount while preserving ManualBpm
        await _bpmCache.ClearMeasuredAsync();
        var afterClear = await _bpmCache.TryGetAsync(pathSegmented);
        Assert.NotNull(afterClear);
        Assert.Equal(128.5, afterClear!.ManualBpm);
        Assert.Empty(afterClear.Segments);
        Assert.Equal(0, afterClear.DetectedSegmentCount);
    }

    [Fact]
    public async Task SqliteBpmCache_ClearMeasuredPreservesManualOverrides_AndMovePathUpdatesKey()
    {
        var pathMeasuredOnly = _workspace.Combine("media", "demo-01.wav");
        var pathManualAndMeasured = _workspace.Combine("media", "demo-02.wav");
        var pathMoved = _workspace.Combine("media", "demo-02-renamed.wav");
        var now = new DateTime(2026, 11, 25, 10, 0, 0, DateTimeKind.Utc);

        await _bpmCache.SaveMeasuredAsync(new BpmCacheEntry
        {
            Path = pathMeasuredOnly,
            FileSize = 1000,
            ModifiedAtUtc = now,
            DurationMs = 120_000,
            MeasuredBpm = 120.0,
            Confidence = 0.95,
            Candidates = [new BpmCandidate(120.0, 1.0), new BpmCandidate(60.0, 0.35)],
            AnalyzerId = "beat_this_small0",
            AnalyzerVersion = 1,
            UpdatedAtUtc = now
        });

        await _bpmCache.SaveMeasuredAsync(new BpmCacheEntry
        {
            Path = pathManualAndMeasured,
            FileSize = 2000,
            ModifiedAtUtc = now,
            DurationMs = 150_000,
            MeasuredBpm = 140.0,
            Confidence = 0.88,
            AnalyzerId = "beat_this_small0",
            AnalyzerVersion = 1,
            UpdatedAtUtc = now
        });
        await _bpmCache.SaveManualAsync(pathManualAndMeasured, 2000, now, 150_000, 138.5);

        var statsBefore = await _bpmCache.GetStatsAsync();
        Assert.Equal(2, statsBefore.TotalCount);
        Assert.Equal(2, statsBefore.MeasuredCount);
        Assert.Equal(1, statsBefore.ManualCount);

        // Clear measured cache -> measured-only row deleted; manual row keeps ManualBpm but clears MeasuredBpm
        var cleared = await _bpmCache.ClearMeasuredAsync();
        Assert.Equal(2, cleared);

        Assert.Null(await _bpmCache.TryGetAsync(pathMeasuredOnly));

        var preserved = await _bpmCache.TryGetAsync(pathManualAndMeasured);
        Assert.NotNull(preserved);
        Assert.Null(preserved!.MeasuredBpm);
        Assert.Equal(138.5, preserved.ManualBpm);

        var statsAfter = await _bpmCache.GetStatsAsync();
        Assert.Equal(1, statsAfter.TotalCount);
        Assert.Equal(0, statsAfter.MeasuredCount);
        Assert.Equal(1, statsAfter.ManualCount);

        // Moving a track path migrates track_bpm_cache inside UpdateTrackPathAsync
        var folder = await _library.AddFolderAsync(_workspace.Combine("media"));
        var track = new Track
        {
            Path = pathManualAndMeasured,
            FolderId = folder.Id,
            IsInLibrary = true,
            Title = "Demo Track 02",
            DurationMs = 150_000,
            FileSize = 2000,
            ModifiedAtUtc = now,
            AddedAtUtc = now,
            Codec = "WAV"
        };
        await _library.UpsertTrackAsync(track);
        await _library.UpdateTrackPathAsync(pathManualAndMeasured, pathMoved, folder.Id);

        Assert.Null(await _bpmCache.TryGetAsync(pathManualAndMeasured));
        var movedEntry = await _bpmCache.TryGetAsync(pathMoved);
        Assert.NotNull(movedEntry);
        Assert.Equal(138.5, movedEntry!.ManualBpm);

        await _bpmCache.DeletePathsAsync([pathMoved]);
        Assert.Null(await _bpmCache.TryGetAsync(pathMoved));
    }

    // =====================================================================
    // BpmModelLocator
    // =====================================================================

    [Fact]
    public void BpmModelLocator_PrefersUserModelsDirectoryForFinal0_AndRejectsTinyFiles()
    {
        var appBase = _workspace.CreateFolder("app");
        var dataRoot = _workspace.Combine("data-root");
        var paths = new AppPaths(dataRoot);
        paths.EnsureCreated();

        // Put a valid-sized (> 1 MB) dummy small0 in appBase\tools\models
        var bundledDir = Path.Combine(appBase, "tools", "models");
        Directory.CreateDirectory(bundledDir);
        var smallPath = Path.Combine(bundledDir, BpmFormatter.Small0FileName);
        WriteDummyModelFile(smallPath, 1_100_000);

        // Put an invalid tiny (< 1 MB) final0 in user ModelsDirectory -> must be rejected
        Directory.CreateDirectory(paths.ModelsDirectory);
        var userFinalPath = Path.Combine(paths.ModelsDirectory, BpmFormatter.Final0FileName);
        File.WriteAllText(userFinalPath, "<html>404 Not Found</html>");

        var locator = new BpmModelLocator(paths, appBase);
        var status1 = locator.GetStatus(BpmModelKind.Final0);
        Assert.True(status1.Small0.Installed);
        Assert.False(status1.Final0.Installed);
        Assert.Equal(BpmModelKind.Small0, status1.EffectiveModel); // falls back to small0

        // Now place a valid-sized final0 in both bundled and user ModelsDirectory -> user wins
        var bundledFinalPath = Path.Combine(bundledDir, BpmFormatter.Final0FileName);
        WriteDummyModelFile(bundledFinalPath, 1_100_000);
        WriteDummyModelFile(userFinalPath, 1_200_000);

        var status2 = locator.GetStatus(BpmModelKind.Final0);
        Assert.True(status2.Final0.Installed);
        Assert.Equal(BpmModelKind.Final0, status2.EffectiveModel);
        Assert.Equal(Path.GetFullPath(userFinalPath), status2.Final0.ResolvedPath);
    }

    // =====================================================================
    // TagLibTagService BPM round-trip & non-interrupting write
    // =====================================================================

    [Fact]
    public void TagLibTagService_RoundTripsBpmDecimalIntegerClear_AndPreservesCoverAndYear()
    {
        var path = TestAudio.CreateWav(_workspace.Combine("media", "bpm-tags.wav"), seconds: 2);
        _tags.Write(path, new TagEditModel
        {
            Title = "Demo Track 01",
            Artist = "Demo Artist",
            Album = "Demo Album A",
            Year = 2025,
            YearText = "2025-06-15",
            YearChanged = true,
            NewCoverBytes = TestAudio.CoverBytes(),
            NewCoverMime = "image/png",
            Bpm = 128.5,
            BpmChanged = true
        });

        var read1 = _tags.Read(path, includeCover: true);
        Assert.Equal(128.5, read1.Bpm);
        Assert.Equal("2025-06-15", read1.YearText);
        Assert.NotNull(read1.CoverBytes);
        // TBPM must not duplicate inside AdditionalTags
        Assert.DoesNotContain(read1.AdditionalTags, kv => kv.Key.Equals("TBPM", StringComparison.OrdinalIgnoreCase));

        // RoundId3BpmToInteger compatibility option
        _tags.Write(path, new TagEditModel
        {
            Title = "Demo Track 01",
            Artist = "Demo Artist",
            Album = "Demo Album A",
            Year = 2025,
            YearChanged = false,
            Bpm = 128.5,
            BpmChanged = true,
            RoundId3BpmToInteger = true
        });
        var read2 = _tags.Read(path, includeCover: true);
        Assert.Equal(129.0, read2.Bpm);
        Assert.Equal("2025-06-15", read2.YearText);
        Assert.NotNull(read2.CoverBytes);

        // Clear BPM
        _tags.Write(path, new TagEditModel
        {
            Title = "Demo Track 01",
            YearChanged = false,
            Bpm = null,
            BpmChanged = true
        });
        Assert.Null(_tags.Read(path, includeCover: false).Bpm);
    }

    [Fact]
    public void AudioConverter_CreateTagCopy_PreservesBpm()
    {
        var tags = new AudioTagData(
            "Demo Track 01", "Demo Artist", "Demo Album A", "Demo Artist", "", "", "",
            2026, 1, 1, 180_000, 320, 44100, 2, "FLAC", null, null)
        {
            Bpm = 122.4
        };

        var copy = AudioConverter.CreateTagCopy(tags, copyCover: true);
        Assert.True(copy.BpmChanged);
        Assert.Equal(122.4, copy.Bpm);
    }

    [Fact]
    public async Task BpmService_WriteToFileWhilePlaying_DoesNotInterruptSharedAudioFileReader()
    {
        var path = TestAudio.CreateWav(_workspace.Combine("media", "playing-write.wav"), seconds: 2);
        _tags.Write(path, new TagEditModel
        {
            Title = "Demo Track 01",
            Year = 2024,
            YearText = "2024-03-01",
            YearChanged = true,
            NewCoverBytes = TestAudio.CoverBytes(),
            NewCoverMime = "image/png"
        });

        var settings = new BpmSettings();
        using var analyzer = new OnnxBeatThisBpmAnalyzer(new BpmModelLocator(_paths), () => settings, _logger);
        using var service = new BpmService(analyzer, _bpmCache, _tags, () => settings, _logger);

        // Simulate active playback holding a shared read handle (ADR-020)
        using (var reader = new SharedAudioFileReader(path))
        {
            var probe = new float[256];
            Assert.True(reader.Read(probe, 0, probe.Length) > 0);

            // SaveManualAsync with writeToFile: true must succeed atomically without throwing IOException
            var state = await service.SaveManualAsync(path, 126.5, writeToFile: true);
            Assert.Equal(BpmSource.FileTag, state.Source);
            Assert.Equal(126.5, state.EffectiveBpm);
            Assert.Null(state.ManualBpm);

            // Playback handle continues reading smoothly
            Assert.True(reader.Read(probe, 0, probe.Length) > 0);
        }

        // Verify cover and YearText stayed intact
        var after = _tags.Read(path, includeCover: true);
        Assert.Equal(126.5, after.Bpm);
        Assert.Equal("2024-03-01", after.YearText);
        Assert.NotNull(after.CoverBytes);
    }

    // =====================================================================
    // End-to-End ONNX Beat This! Analyzer (Lazy load, GPU/CPU, accuracy, unload)
    // =====================================================================

    [Fact]
    public async Task OnnxBeatThisBpmAnalyzer_EndToEnd_LazyLoadAccuracyAndImmediateUnload()
    {
        var locator = new BpmModelLocator(_paths);
        var status = locator.GetStatus(BpmModelKind.Small0);
        Assert.True(status.Small0.Installed, "Built-in tools/models/beat_this_small0.onnx must be present in the workspace.");

        var wav128 = CreateBeatPatternWav(_workspace.Combine("media", "demo-128bpm.wav"), bpm: 128.0, seconds: 16);
        var wav120Long = CreateBeatPatternWav(_workspace.Combine("media", "demo-120bpm-long.wav"), bpm: 120.0, seconds: 36);

        var settings = new BpmSettings
        {
            PreferredModel = BpmModelKind.Small0,
            UseGpuAcceleration = true,
            IdleUnloadSeconds = 60
        };

        using var analyzer = new OnnxBeatThisBpmAnalyzer(locator, () => settings, _logger);

        // Strict lazy load: constructing + checking status must NOT load ONNX Runtime
        Assert.False(analyzer.IsRuntimeLoaded);
        Assert.True(analyzer.GetModelStatus().Small0.Installed);
        Assert.False(analyzer.IsRuntimeLoaded);

        // 1. Run with GPU preferred (DirectML on DX12 or CPU fallback) on 128.0 BPM
        var progressStages = new List<double>();
        var progress = new Progress<BpmAnalysisProgress>(p => progressStages.Add(p.Fraction));
        var gpuResult = await analyzer.AnalyzeAsync(wav128, progress);

        Assert.True(gpuResult.IsReliable);
        Assert.InRange(Math.Abs(gpuResult.Bpm - 128.0), 0.0, 0.8);
        Assert.True(gpuResult.Confidence >= 0.70);
        Assert.Equal("beat-this-small0-onnx", gpuResult.AlgorithmId);
        Assert.True(
            gpuResult.ExecutionProvider is "DirectML (GPU)" or "CPU",
            $"Unexpected EP: {gpuResult.ExecutionProvider}");
        Assert.True(analyzer.IsRuntimeLoaded); // IdleUnloadSeconds = 60 keeps warm session

        // Also check GPU vs CPU consistency on the same 128 BPM track
        analyzer.UnloadRuntime();
        Assert.False(analyzer.IsRuntimeLoaded);

        settings.UseGpuAcceleration = false;
        settings.IdleUnloadSeconds = 0;

        var cpu128 = await analyzer.AnalyzeAsync(wav128);
        Assert.True(cpu128.IsReliable);
        Assert.Equal("CPU", cpu128.ExecutionProvider);
        Assert.InRange(Math.Abs(cpu128.Bpm - gpuResult.Bpm), 0.0, 0.5);
        Assert.False(analyzer.IsRuntimeLoaded);

        // 2. Run on CPU with IdleUnloadSeconds = 0 on a 36 s track (exercises multi-chunk 1500-frame overlap!)
        var cpuResult = await analyzer.AnalyzeAsync(wav120Long);
        Assert.True(cpuResult.IsReliable);
        Assert.Equal("CPU", cpuResult.ExecutionProvider);
        Assert.InRange(Math.Abs(cpuResult.Bpm - 120.0), 0.0, 0.8);
        // IdleUnloadSeconds = 0 must release session right after analysis completes
        Assert.False(analyzer.IsRuntimeLoaded);

        // 3. Cancellation & short track behavior
        var shortWav = TestAudio.CreateWav(_workspace.Combine("media", "short.wav"), seconds: 1);
        var shortResult = await analyzer.AnalyzeAsync(shortWav);
        Assert.False(shortResult.IsReliable);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => analyzer.AnalyzeAsync(wav128, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task BpmService_FullFlow_MeasureCacheManualAndSmartInvalidation()
    {
        var wav = CreateBeatPatternWav(_workspace.Combine("media", "demo-flow.wav"), bpm: 120.0, seconds: 14);
        var settings = new BpmSettings
        {
            PreferredModel = BpmModelKind.Small0,
            UseGpuAcceleration = false,
            IdleUnloadSeconds = 0
        };

        using var analyzer = new OnnxBeatThisBpmAnalyzer(new BpmModelLocator(_paths), () => settings, _logger);
        using var service = new BpmService(analyzer, _bpmCache, _tags, () => settings, _logger);

        // Initially no BPM
        var initial = await service.GetDisplayStateAsync(wav);
        Assert.Equal(BpmSource.None, initial.Source);

        // Measure -> stored in SQLite cache without touching audio file
        var infoBefore = new FileInfo(wav);
        var (result, measuredState) = await service.MeasureAsync(wav);
        var infoAfter = new FileInfo(wav);
        Assert.Equal(infoBefore.Length, infoAfter.Length);
        Assert.Equal(infoBefore.LastWriteTimeUtc, infoAfter.LastWriteTimeUtc);
        Assert.True(result.IsReliable);
        Assert.Equal(BpmSource.CachedMeasured, measuredState.Source);
        Assert.InRange(Math.Abs(measuredState.EffectiveBpm!.Value - 120.0), 0.0, 0.8);

        // Editing Title/Cover changes file stamp, but duration is unchanged -> BPM cache survives!
        _tags.Write(wav, new TagEditModel
        {
            Title = "Demo Track Renamed",
            NewCoverBytes = TestAudio.CoverBytes(),
            NewCoverMime = "image/png"
        });
        var afterTitleEdit = await service.GetDisplayStateAsync(wav);
        Assert.Equal(BpmSource.CachedMeasured, afterTitleEdit.Source);
        Assert.Equal(measuredState.EffectiveBpm, afterTitleEdit.EffectiveBpm);

        // Manual override (cache-only)
        var manualState = await service.SaveManualAsync(wav, 121.5, writeToFile: false);
        Assert.Equal(BpmSource.CachedManual, manualState.Source);
        Assert.Equal(121.5, manualState.EffectiveBpm);
        Assert.Null(_tags.Read(wav, includeCover: false).Bpm);

        // Clear measured cache -> manual override stays intact!
        await service.ClearMeasuredCacheAsync();
        var afterClear = await service.GetDisplayStateAsync(wav);
        Assert.Equal(BpmSource.CachedManual, afterClear.Source);
        Assert.Equal(121.5, afterClear.EffectiveBpm);
        Assert.Null(afterClear.MeasuredBpm);
    }

    [Fact]
    public async Task OnnxBeatThisBpmAnalyzer_MultiTempoTrack_DetectsSegmentsEndToEnd()
    {
        // Real end-to-end neural inference on a 3-section variable-tempo WAV:
        // [0–28s] 108 BPM -> [28–64s] 132 BPM (primary) -> [64–92s] 148 BPM
        var wav = CreateMultiTempoBeatPatternWav(
            _workspace.Combine("media", "demo-3segments.wav"),
            [(28, 108.0), (36, 132.0), (28, 148.0)]);

        var settings = new BpmSettings
        {
            PreferredModel = BpmModelKind.Small0,
            UseGpuAcceleration = true,
            IdleUnloadSeconds = 0
        };
        using var analyzer = new OnnxBeatThisBpmAnalyzer(new BpmModelLocator(_paths), () => settings, _logger);
        using var service = new BpmService(analyzer, _bpmCache, _tags, () => settings, _logger);

        var (result, state) = await service.MeasureAsync(wav);
        Assert.True(result.IsReliable);
        Assert.True(result.IsVariableTempoSuspected);
        Assert.False(result.TooManyTempoChanges);
        Assert.Equal(3, result.Segments.Count);
        Assert.InRange(Math.Abs(result.Segments[0].Bpm - 108.0), 0.0, 0.8);
        Assert.InRange(Math.Abs(result.Segments[1].Bpm - 132.0), 0.0, 0.8);
        Assert.InRange(Math.Abs(result.Segments[2].Bpm - 148.0), 0.0, 0.8);
        Assert.True(result.Segments[1].IsPrimary);

        // Reloading from SQLite cache must return the exact persisted segments
        var reloaded = await service.GetDisplayStateAsync(wav);
        Assert.Equal(3, reloaded.Segments.Count);
        Assert.False(reloaded.TooManyTempoChanges);
        Assert.Equal(state.Segments[0].Bpm, reloaded.Segments[0].Bpm);
        Assert.Equal(state.Segments[1].Bpm, reloaded.Segments[1].Bpm);
        Assert.Equal(state.Segments[2].Bpm, reloaded.Segments[2].Bpm);
    }

    public void Dispose() => _workspace.Dispose();

    private static void WriteDummyModelFile(string path, int bytes)
    {
        using var fs = File.Create(path);
        fs.SetLength(bytes);
    }

    private static string CreateMultiTempoBeatPatternWav(
        string path,
        IReadOnlyList<(int Seconds, double Bpm)> sections,
        int sampleRate = 44100)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var totalSeconds = sections.Sum(s => s.Seconds);
        var totalSamples = sampleRate * totalSeconds;
        var buf = new double[totalSamples];
        var rng = new Random(42);

        var cursorSec = 0.0;
        foreach (var (secLen, bpm) in sections)
        {
            var beatPeriod = 60.0 / bpm;
            var beatIdx = 0;
            for (var localTime = 0.15; localTime < secLen - 0.15; localTime += beatPeriod, beatIdx++)
            {
                var beatTime = cursorSec + localTime;
                var startSample = (int)(beatTime * sampleRate);

                var isStrongKick = beatIdx % 2 == 0;
                var kickLen = (int)(0.16 * sampleRate);
                var kickAmp = isStrongKick ? 0.65 : 0.40;
                for (var i = 0; i < kickLen && startSample + i < totalSamples; i++)
                {
                    var t = i / (double)sampleRate;
                    var freq = 48.0 + 100.0 * Math.Exp(-t * 28.0);
                    var env = Math.Exp(-t * 16.0);
                    buf[startSample + i] += kickAmp * env * Math.Sin(2.0 * Math.PI * freq * t);
                }

                if (beatIdx % 2 == 1)
                {
                    var snareLen = (int)(0.12 * sampleRate);
                    for (var i = 0; i < snareLen && startSample + i < totalSamples; i++)
                    {
                        var t = i / (double)sampleRate;
                        var env = Math.Exp(-t * 26.0);
                        var noise = rng.NextDouble() * 2.0 - 1.0;
                        var body = Math.Sin(2.0 * Math.PI * 190.0 * t);
                        buf[startSample + i] += 0.42 * env * (0.65 * noise + 0.35 * body);
                    }
                }

                // Subtle offbeat hi-hat so quarter-note pulse stays unambiguous
                var hhStart = (int)((beatTime + 0.5 * beatPeriod) * sampleRate);
                var hhLen = (int)(0.03 * sampleRate);
                var prev = 0.0;
                for (var i = 0; i < hhLen && hhStart + i < totalSamples; i++)
                {
                    var t = i / (double)sampleRate;
                    var env = Math.Exp(-t * 100.0);
                    var white = rng.NextDouble() * 2.0 - 1.0;
                    var hp = white - prev;
                    prev = white;
                    buf[hhStart + i] += 0.08 * env * hp;
                }

                var bassFreq = (beatIdx % 4) switch { 0 => 55.0, 1 => 55.0, 2 => 65.41, _ => 73.42 };
                var bassLen = (int)(beatPeriod * 0.75 * sampleRate);
                for (var i = 0; i < bassLen && startSample + i < totalSamples; i++)
                {
                    var t = i / (double)sampleRate;
                    var env = Math.Min(1.0, t / 0.01) * Math.Exp(-t * 4.5);
                    buf[startSample + i] += 0.18 * env * Math.Sin(2.0 * Math.PI * bassFreq * t);
                }
            }

            cursorSec += secLen;
        }

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: false);
        var dataSize = totalSamples * 2;
        writer.Write("RIFF"u8);
        writer.Write(36 + dataSize);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataSize);

        for (var i = 0; i < totalSamples; i++)
        {
            var clamped = Math.Clamp(buf[i], -0.98, 0.98);
            writer.Write((short)(clamped * 32767.0));
        }

        writer.Flush();
        return path;
    }

    /// <summary>
    /// 合成带底鼓（1/3 拍）、军鼓（2/4 拍）、踩镲（八分音符）与贝斯律动的 44.1 kHz WAV，
    /// 供 Beat This! 端到端集成测试验证真实神经网络推理精度（完全使用合成信号，不依赖任何真实歌曲）。
    /// </summary>
    private static string CreateBeatPatternWav(string path, double bpm, int seconds, int sampleRate = 44100)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var totalSamples = sampleRate * seconds;
        var buf = new double[totalSamples];
        var rng = new Random(42);
        var beatPeriod = 60.0 / bpm;

        var beatIdx = 0;
        for (var beatTime = 0.25; beatTime < seconds - 0.2; beatTime += beatPeriod, beatIdx++)
        {
            var startSample = (int)(beatTime * sampleRate);

            // Kick on beats 0, 2 (every 2 beats) + soft kick on 1, 3
            var isStrongKick = beatIdx % 2 == 0;
            var kickLen = (int)(0.16 * sampleRate);
            var kickAmp = isStrongKick ? 0.65 : 0.22;
            for (var i = 0; i < kickLen && startSample + i < totalSamples; i++)
            {
                var t = i / (double)sampleRate;
                var freq = 48.0 + 100.0 * Math.Exp(-t * 28.0);
                var env = Math.Exp(-t * 16.0);
                buf[startSample + i] += kickAmp * env * Math.Sin(2.0 * Math.PI * freq * t);
            }

            // Snare on beats 1, 3
            if (beatIdx % 2 == 1)
            {
                var snareLen = (int)(0.12 * sampleRate);
                for (var i = 0; i < snareLen && startSample + i < totalSamples; i++)
                {
                    var t = i / (double)sampleRate;
                    var env = Math.Exp(-t * 26.0);
                    var noise = rng.NextDouble() * 2.0 - 1.0;
                    var body = Math.Sin(2.0 * Math.PI * 190.0 * t);
                    buf[startSample + i] += 0.42 * env * (0.65 * noise + 0.35 * body);
                }
            }

            // Eighth-note hi-hats
            for (var sub = 0; sub < 2; sub++)
            {
                var hhStart = (int)((beatTime + sub * 0.5 * beatPeriod) * sampleRate);
                var hhLen = (int)(0.035 * sampleRate);
                var prev = 0.0;
                for (var i = 0; i < hhLen && hhStart + i < totalSamples; i++)
                {
                    var t = i / (double)sampleRate;
                    var env = Math.Exp(-t * 95.0);
                    var white = rng.NextDouble() * 2.0 - 1.0;
                    var hp = white - prev;
                    prev = white;
                    buf[hhStart + i] += (sub == 0 ? 0.16 : 0.11) * env * hp;
                }
            }

            // Subtle bass note on each beat
            var bassFreq = (beatIdx % 4) switch { 0 => 55.0, 1 => 55.0, 2 => 65.41, _ => 73.42 };
            var bassLen = (int)(beatPeriod * 0.75 * sampleRate);
            for (var i = 0; i < bassLen && startSample + i < totalSamples; i++)
            {
                var t = i / (double)sampleRate;
                var env = Math.Min(1.0, t / 0.01) * Math.Exp(-t * 4.5);
                buf[startSample + i] += 0.18 * env * Math.Sin(2.0 * Math.PI * bassFreq * t);
            }
        }

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: false);
        var dataSize = totalSamples * 2;
        writer.Write("RIFF"u8);
        writer.Write(36 + dataSize);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataSize);

        for (var i = 0; i < totalSamples; i++)
        {
            var clamped = Math.Clamp(buf[i], -0.98, 0.98);
            writer.Write((short)(clamped * 32767.0));
        }

        writer.Flush();
        return path;
    }
}
