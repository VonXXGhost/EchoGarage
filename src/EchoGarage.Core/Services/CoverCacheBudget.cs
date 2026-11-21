namespace EchoGarage.Core.Services;

/// <summary>
/// 封面内存缓存的容量账本：以字节数为主、条目数为辅的 LRU 顺序表。
/// 只记录“键 + 占用字节”，不持有任何图像对象，因此可以在单元测试里覆盖全部容量与淘汰规则。
/// 本类型不是线程安全的，由调用方加锁（<c>CoverImageService</c> 用 <c>_lruGate</c>）。
/// </summary>
public sealed class CoverCacheBudget
{
    private readonly LinkedList<Entry> _order = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _nodes = new(StringComparer.Ordinal);
    private long _bytes;

    public CoverCacheBudget(long byteLimit, int entryLimit)
    {
        if (byteLimit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteLimit));
        }

        if (entryLimit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(entryLimit));
        }

        ByteLimit = byteLimit;
        EntryLimit = entryLimit;
    }

    public long ByteLimit { get; }

    public int EntryLimit { get; }

    public int Count => _nodes.Count;

    public long Bytes => _bytes;

    /// <summary>按 LRU 顺序登记；再次登记同一键视为一次访问：移到队尾并更新占用。</summary>
    public void Add(string key, long bytes)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (bytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes));
        }

        if (_nodes.TryGetValue(key, out var existing))
        {
            _bytes -= existing.Value.Bytes;
            _order.Remove(existing);
            _nodes.Remove(key);
        }

        _nodes[key] = _order.AddLast(new Entry(key, bytes));
        _bytes += bytes;
    }

    /// <summary>标记一次访问（命中内存缓存时调用），返回该键是否在账本中。</summary>
    public bool Touch(string key)
    {
        if (!_nodes.TryGetValue(key, out var node))
        {
            return false;
        }

        _order.Remove(node);
        _order.AddLast(node);
        return true;
    }

    public bool Remove(string key)
    {
        if (!_nodes.TryGetValue(key, out var node))
        {
            return false;
        }

        _order.Remove(node);
        _nodes.Remove(key);
        _bytes = Math.Max(0, _bytes - node.Value.Bytes);
        return true;
    }

    public void Clear()
    {
        _order.Clear();
        _nodes.Clear();
        _bytes = 0;
    }

    public bool Contains(string key) => _nodes.ContainsKey(key);

    /// <summary>当前最久未使用的键（队列头部），用于诊断与测试。</summary>
    public bool TryPeekOldest(out string key)
    {
        if (_order.First is { } first)
        {
            key = first.Value.Key;
            return true;
        }

        key = string.Empty;
        return false;
    }

    /// <summary>
    /// 淘汰最久未使用的条目，直到同时满足字节与条目上限；至少保留一条，
    /// 否则一张超大的封面会被自己挤掉、随即被重新解码，反而更慢。
    /// 返回被淘汰的键（调用方负责从实际缓存里移除）。
    /// </summary>
    public List<string> Trim()
    {
        var evicted = new List<string>();
        while ((_nodes.Count > EntryLimit || _bytes > ByteLimit) && _order.First is { } oldest)
        {
            if (_nodes.Count == 1)
            {
                break;
            }

            _order.RemoveFirst();
            _nodes.Remove(oldest.Value.Key);
            _bytes = Math.Max(0, _bytes - oldest.Value.Bytes);
            evicted.Add(oldest.Value.Key);
        }

        return evicted;
    }

    private readonly record struct Entry(string Key, long Bytes);
}
