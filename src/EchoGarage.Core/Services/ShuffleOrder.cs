namespace EchoGarage.Core.Services;

/// <summary>
/// Builds the random playback order. Kept in Core (no WPF dependency) so the round logic can
/// be unit-tested with a seeded <see cref="Random"/>.
/// </summary>
public static class ShuffleOrder
{
    /// <summary>Shuffles the list in place using Fisher–Yates.</summary>
    public static void Shuffle<T>(IList<T> items, Random? random = null)
    {
        var rng = random ?? Random.Shared;
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    /// <summary>
    /// Builds one shuffled round. The currently playing track (matched by reference) stays
    /// first, so pressing "next" right after switching to shuffle continues with the rest of
    /// the list instead of replaying the current song.
    /// </summary>
    public static List<T> BuildRound<T>(IReadOnlyList<T> items, T? current, Random? random = null)
        where T : class
    {
        var currentIncluded = current is not null && items.Any(item => ReferenceEquals(item, current));
        var pool = new List<T>(items.Count);
        foreach (var item in items)
        {
            if (!ReferenceEquals(item, current))
            {
                pool.Add(item);
            }
        }

        Shuffle(pool, random);

        var result = new List<T>(items.Count);
        if (currentIncluded)
        {
            result.Add(current!);
        }

        result.AddRange(pool);
        return result;
    }
}
