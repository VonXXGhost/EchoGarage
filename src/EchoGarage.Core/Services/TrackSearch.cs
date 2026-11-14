using System.Globalization;
using System.Text;
using EchoGarage.Core.Models;

namespace EchoGarage.Core.Services;

/// <summary>
/// In-memory search for tracks of the current playlist. A query is split into
/// whitespace-separated terms (double quotes keep spaces inside one term); every term must
/// match, so multiple keywords narrow the result (AND).
///
/// The inline search box above the list matches only title, artist, album and album artist
/// (<see cref="SearchQuery.InlineFields"/>); the advanced search dialog passes an explicit
/// field set and decides whether a keyword may appear in any selected field or must appear
/// in all of them. Matching is case-insensitive substring matching, which keeps results
/// predictable (fuzzy subsequence matching used to surface unrelated rows for short
/// queries).
/// </summary>
public static class TrackSearch
{
    /// <summary>Guards the UI against pathological paste input; longer queries are truncated.</summary>
    public const int MaxQueryLength = 200;

    /// <summary>Number of terms considered; extra terms are ignored.</summary>
    public const int MaxTerms = 8;

    /// <summary>Matches a query against the inline fields (title/artist/album/album artist).</summary>
    public static bool Matches(Track track, string? query) =>
        Matches(track, SearchQuery.InlineFields, SearchFieldMode.Any, query);

    /// <summary>
    /// Returns true when every term of the query matches the selected fields. With
    /// <see cref="SearchFieldMode.Any"/> a term may appear in any selected field; with
    /// <see cref="SearchFieldMode.All"/> it must appear in all of them.
    /// </summary>
    public static bool Matches(
        Track track,
        IReadOnlyList<SearchField> fields,
        SearchFieldMode mode,
        string? query)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(fields);

        if (fields.Count == 0)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        if (query.Length > MaxQueryLength)
        {
            query = query[..MaxQueryLength];
        }

        foreach (var term in Tokenize(query))
        {
            if (!MatchesTerm(track, fields, mode, term))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Splits a query into terms. Whitespace separates terms unless it is inside double
    /// quotes; the quote characters themselves are dropped.
    /// </summary>
    public static IReadOnlyList<string> Tokenize(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<string>();
        }

        var tokens = new List<string>();
        var builder = new StringBuilder();
        var inQuotes = false;

        foreach (var ch in query)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(ch))
            {
                if (builder.Length > 0)
                {
                    tokens.Add(builder.ToString());
                    builder.Clear();
                }

                continue;
            }

            builder.Append(ch);
        }

        if (builder.Length > 0)
        {
            tokens.Add(builder.ToString());
        }

        if (tokens.Count > MaxTerms)
        {
            tokens.RemoveRange(MaxTerms, tokens.Count - MaxTerms);
        }

        return tokens;
    }

    private static bool MatchesTerm(
        Track track,
        IReadOnlyList<SearchField> fields,
        SearchFieldMode mode,
        string term)
    {
        if (term.Length == 0)
        {
            return true;
        }

        if (mode == SearchFieldMode.All)
        {
            foreach (var field in fields)
            {
                if (!MatchesSubstring(FieldValue(track, field), term))
                {
                    return false;
                }
            }

            return true;
        }

        foreach (var field in fields)
        {
            if (MatchesSubstring(FieldValue(track, field), term))
            {
                return true;
            }
        }

        return false;
    }

    private static string? FieldValue(Track track, SearchField field) => field switch
    {
        SearchField.Title => track.DisplayTitle,
        SearchField.Artist => track.DisplayArtist,
        SearchField.Album => track.DisplayAlbum,
        SearchField.AlbumArtist => track.AlbumArtist,
        SearchField.Composer => track.Composer,
        SearchField.Genre => track.Genre,
        SearchField.Comment => track.Comment,
        SearchField.FileName => track.FileName,
        SearchField.Path => track.Path,
        SearchField.Year => track.Year?.ToString(CultureInfo.InvariantCulture),
        SearchField.Codec => track.FormatDisplay,
        _ => null
    };

    private static bool MatchesSubstring(string? text, string term) =>
        !string.IsNullOrEmpty(text) && term.Length > 0 &&
        text.Contains(term, StringComparison.OrdinalIgnoreCase);
}
