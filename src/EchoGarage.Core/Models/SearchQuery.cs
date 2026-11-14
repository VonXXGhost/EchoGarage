namespace EchoGarage.Core.Models;

/// <summary>Fields that a search query can be matched against.</summary>
public enum SearchField
{
    Title,
    Artist,
    Album,
    AlbumArtist,
    Composer,
    Genre,
    Comment,
    FileName,
    Path,
    Year,
    Codec
}

/// <summary>How one keyword combines across the selected fields of an advanced search.</summary>
public enum SearchFieldMode
{
    /// <summary>The keyword may appear in any of the selected fields.</summary>
    Any,

    /// <summary>The keyword must appear in every selected field.</summary>
    All
}

/// <summary>
/// An advanced search: keyword text plus the fields it is matched against. The inline
/// search box only matches <see cref="InlineFields"/>; the advanced search dialog lets the
/// user pick a different field set (e.g. composer + artist) and how the fields combine.
/// </summary>
public sealed record SearchQuery(
    string Text,
    IReadOnlyList<SearchField> Fields,
    SearchFieldMode Mode = SearchFieldMode.Any)
{
    /// <summary>Fields matched by the inline search box above the track list.</summary>
    public static IReadOnlyList<SearchField> InlineFields { get; } =
        [SearchField.Title, SearchField.Artist, SearchField.Album, SearchField.AlbumArtist];

    /// <summary>Every field the advanced search dialog offers.</summary>
    public static IReadOnlyList<SearchField> AllFields { get; } = Enum.GetValues<SearchField>();

    /// <summary>True when the query cannot narrow anything (no text or no field selected).</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(Text) || Fields.Count == 0;
}
