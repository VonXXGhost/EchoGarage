using EchoGarage.Core.Abstractions;
using EchoGarage.Core.Models;

namespace EchoGarage.Infrastructure.Tagging;

/// <summary>
/// 标签编辑候选项的进程内缓存：首次使用时从媒体库缓存查询一次，之后复用，
/// 直到应用层在媒体库数据变化后调用 <see cref="Invalidate"/>。
/// 缓存只在 UI 打开标签属性窗口时读取，查询本身不阻塞 UI 线程；失败时返回空快照，
/// 且不会把失败结果缓存下来，下一次会重试。
/// </summary>
public sealed class TagSuggestionService : ITagSuggestionService
{
    private readonly ILibraryRepository _repository;
    private readonly IAppLogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private TagSuggestionValues? _cached;
    private long _generation;

    public TagSuggestionService(ILibraryRepository repository, IAppLogger logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task<TagSuggestionValues> GetAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _cached) is { } cached)
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _cached) is { } cachedInside)
            {
                return cachedInside;
            }

            var generation = Volatile.Read(ref _generation);
            try
            {
                var values = await _repository
                    .GetTagSuggestionValuesAsync(cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                // 查询期间可能刚好发生了 Invalidate（例如扫描完成），此时不要把旧快照写回缓存。
                if (Volatile.Read(ref _generation) == generation)
                {
                    Volatile.Write(ref _cached, values);
                }

                _logger.Debug(
                    "Tag suggestions loaded: " +
                    $"artist={values.Get(TagFieldKeys.Artist).Count}, " +
                    $"albumArtist={values.Get(TagFieldKeys.AlbumArtist).Count}, " +
                    $"composer={values.Get(TagFieldKeys.Composer).Count}, " +
                    $"genre={values.Get(TagFieldKeys.Genre).Count}.");
                return values;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 候选项只是编辑辅助，读取失败不应阻止用户编辑标签。
                _logger.Warn($"Loading tag suggestions failed: {ex.Message}");
                return TagSuggestionValues.Empty;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate()
    {
        Interlocked.Increment(ref _generation);
        Volatile.Write(ref _cached, null);
    }
}
