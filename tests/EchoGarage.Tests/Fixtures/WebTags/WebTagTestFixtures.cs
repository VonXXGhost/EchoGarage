namespace EchoGarage.Tests.Fixtures.WebTags;

/// <summary>
/// ADR-022 Web 标签解析测试用的合成 HTML / JSON。全部使用 Demo 值（Demo Album A / Demo Artist /
/// Demo Track 01 …），不含任何真实歌曲、专辑或艺术家名称；不来自真实网络响应。
/// section 统一使用真实页面的 itemKind 字段（不再提供 sectionType），专辑页 header / footer 数据
/// 放在 section.items[0]，搜索页 topSearchLockup 的专辑条目只有形如 "アルバム · 艺术家" 的 subtitle。
/// </summary>
internal static class WebTagTestFixtures
{
    public const string AlbumAArtworkTemplate =
        "https://is1-ssl.mzstatic.com/image/thumb/demo-album-a/{w}x{h}bb.{f}";

    public const string AlbumAArtwork1200 =
        "https://is1-ssl.mzstatic.com/image/thumb/demo-album-a/1200x1200bb.jpg";

    public const string AlbumBArtworkTemplate =
        "https://is1-ssl.mzstatic.com/image/thumb/demo-album-b/{w}x{h}bb.{f}";

    public const string AlbumCArtworkTemplate =
        "https://is1-ssl.mzstatic.com/image/thumb/demo-album-c/{w}x{h}bb.{f}";

    public const string AlbumEArtworkTemplate =
        "https://is1-ssl.mzstatic.com/image/thumb/demo-album-e/{w}x{h}bb.{f}";

    public const string AlbumEArtwork1200 =
        "https://is1-ssl.mzstatic.com/image/thumb/demo-album-e/1200x1200bb.jpg";

    /// <summary>不含 serialized-server-data 的普通 HTML。</summary>
    public const string HtmlWithoutSerializedData =
        "<!DOCTYPE html><html><head><title>Demo</title></head><body><p>Demo Album A</p></body></html>";

    public static string SearchPageWithAlbums { get; } = BuildPage(SearchWithAlbumsJson);

    public static string SearchPageWithWrappedAlbum { get; } = BuildPage(WrappedAlbumJson);

    public static string SearchPageMixedTopSearch { get; } = BuildPage(MixedTopSearchJson);

    public static string SearchPageTopSearchAndSquare { get; } = BuildPage(TopSearchAndSquareJson);

    public static string SearchPageTopSearchOnly { get; } = BuildPage(TopSearchOnlyJson);

    public static string SearchPageTracksOnly { get; } = BuildPage(TracksOnlyJson);

    public static string SearchPageEmpty { get; } = BuildPage(EmptySearchJson);

    public static string SearchPageBrokenJson { get; } = BuildPage("{ this is not valid json");

    public static string ReleasePageWithComposer { get; } = BuildPage(ReleaseComposerJson, ReleaseComposerJsonLd);

    public static string ReleasePageWithoutComposer { get; } = BuildPage(ReleaseNoComposerJson, ReleaseNoComposerJsonLd);

    public static string ReleasePageWithoutJsonLd { get; } = BuildPage(ReleaseNoJsonLdJson);

    public static string ReleasePageBrokenJsonLd { get; } = BuildPage(ReleaseNoJsonLdJson, "{ this is not valid json");

    public static string ReleasePageMissingHeader { get; } = BuildPage(MissingHeaderJson);

    public static string ReleasePageTypeAnomalies { get; } = BuildPage(TypeAnomaliesJson);

    /// <summary>把内嵌 JSON 包进最小可用 HTML 页面；可选附加 JSON-LD 块。</summary>
    public static string BuildPage(string serializedServerData, string? jsonLd = null)
    {
        var jsonLdScript = jsonLd is null
            ? string.Empty
            : $"<script type=\"application/ld+json\">{jsonLd}</script>";
        return "<!DOCTYPE html>\n<html lang=\"ja\">\n<head>\n<meta charset=\"utf-8\">\n<title>Demo</title>\n"
            + jsonLdScript
            + "\n</head>\n<body>\n<div id=\"page\">Demo</div>\n"
            + "<script type=\"application/json\" id=\"serialized-server-data\">"
            + serializedServerData
            + "</script>\n</body>\n</html>";
    }

    private const string SearchWithAlbumsJson =
        """
        {
          "data": [
            {
              "id": "search",
              "type": "page",
              "data": {
                "sections": [
                  {
                    "id": "square-section - album",
                    "itemKind": "squareLockup",
                    "items": [
                      {
                        "contentDescriptor": {
                          "kind": "album",
                          "url": "/jp/album/demo-album-a/0000000001",
                          "identifiers": { "storeAdamID": "0000000001" }
                        },
                        "titleLinks": [ { "title": "Demo Album A" } ],
                        "subtitleLinks": [ { "title": "Demo Artist" } ],
                        "trackCount": 3,
                        "artwork": {
                          "dictionary": {
                            "url": "https://is1-ssl.mzstatic.com/image/thumb/demo-album-a/{w}x{h}bb.{f}"
                          }
                        }
                      },
                      {
                        "contentDescriptor": {
                          "kind": "album",
                          "url": "/jp/album/demo-album-b/0000000002"
                        },
                        "titleLinks": [ { "title": "Demo Album B" } ],
                        "subtitleLinks": [ { "title": "Demo Artist B" } ],
                        "trackCount": 2,
                        "artwork": {
                          "dictionary": {
                            "url": "https://is1-ssl.mzstatic.com/image/thumb/demo-album-b/{w}x{h}bb.{f}"
                          }
                        }
                      }
                    ]
                  }
                ]
              }
            }
          ]
        }
        """;

    private const string WrappedAlbumJson =
        """
        {
          "data": [
            {
              "data": {
                "sections": [
                  {
                    "itemKind": "squareLockup",
                    "items": [
                      {
                        "item": {
                          "contentDescriptor": {
                            "kind": "album",
                            "url": "/jp/album/demo-album-c/0000000003",
                            "identifiers": { "storeAdamID": "0000000003" }
                          },
                          "titleLinks": [ { "title": "Demo Album C" } ],
                          "subtitleLinks": [ { "title": "Demo Artist C" } ],
                          "trackCount": 4
                        }
                      }
                    ]
                  }
                ]
              }
            }
          ]
        }
        """;

    private const string MixedTopSearchJson =
        """
        {
          "data": [
            {
              "data": {
                "sections": [
                  {
                    "itemKind": "topSearchLockup",
                    "items": [
                      {
                        "contentDescriptor": {
                          "kind": "album",
                          "url": "/jp/album/demo-album-c/0000000003"
                        },
                        "title": "Demo Album C",
                        "subtitle": "アルバム · Demo Artist",
                        "artwork": {
                          "dictionary": {
                            "url": "https://is1-ssl.mzstatic.com/image/thumb/demo-album-c/{w}x{h}bb.{f}"
                          }
                        }
                      },
                      {
                        "contentDescriptor": {
                          "kind": "song",
                          "url": "/jp/album/demo-album-d/0000000004?i=0000000401"
                        },
                        "title": "Demo Track 04",
                        "artistName": "Demo Artist"
                      },
                      {
                        "contentDescriptor": {
                          "kind": "artist",
                          "url": "/jp/artist/demo-artist/0000000009"
                        },
                        "title": "Demo Artist"
                      }
                    ]
                  },
                  {
                    "itemKind": "bubbleLockup",
                    "items": [
                      {
                        "contentDescriptor": { "kind": "artist" },
                        "title": "Demo Artist B"
                      }
                    ]
                  }
                ]
              }
            }
          ]
        }
        """;

    private const string TopSearchAndSquareJson =
        """
        {
          "data": [
            {
              "data": {
                "sections": [
                  {
                    "itemKind": "topSearchLockup",
                    "items": [
                      {
                        "contentDescriptor": {
                          "kind": "album",
                          "url": "/jp/album/demo-album-old/0000000009"
                        },
                        "title": "Demo Album Old",
                        "subtitle": "アルバム · Demo Artist Old"
                      }
                    ]
                  },
                  {
                    "itemKind": "squareLockup",
                    "items": [
                      {
                        "contentDescriptor": {
                          "kind": "album",
                          "url": "/jp/album/demo-album-a/0000000001"
                        },
                        "titleLinks": [ { "title": "Demo Album A" } ],
                        "subtitleLinks": [ { "title": "Demo Artist" } ],
                        "trackCount": 3
                      }
                    ]
                  }
                ]
              }
            }
          ]
        }
        """;

    private const string TopSearchOnlyJson =
        """
        {
          "data": [
            {
              "data": {
                "sections": [
                  {
                    "itemKind": "topSearchLockup",
                    "items": [
                      {
                        "contentDescriptor": {
                          "kind": "album",
                          "url": "/jp/album/demo-album-e/0000000005"
                        },
                        "title": "Demo Album E",
                        "subtitle": "アルバム · Demo Artist E",
                        "artwork": {
                          "dictionary": {
                            "url": "https://is1-ssl.mzstatic.com/image/thumb/demo-album-e/{w}x{h}bb.{f}"
                          }
                        }
                      }
                    ]
                  }
                ]
              }
            }
          ]
        }
        """;

    private const string TracksOnlyJson =
        """
        {
          "data": [
            {
              "data": {
                "sections": [
                  {
                    "itemKind": "trackLockup",
                    "items": [
                      {
                        "contentDescriptor": {
                          "kind": "song",
                          "url": "/jp/album/demo-album-a/0000000001?i=0000000101"
                        },
                        "title": "Demo Track 01",
                        "artistName": "Demo Artist"
                      },
                      {
                        "contentDescriptor": {
                          "kind": "song",
                          "url": "/jp/album/demo-album-a/0000000001?i=0000000102"
                        },
                        "title": "Demo Track 02",
                        "artistName": "Demo Artist"
                      },
                      {
                        "contentDescriptor": { "kind": "song" },
                        "cdUrl": "https://music.apple.com/jp/album/demo-album-b/0000000002?i=0000000201",
                        "title": "Demo Track 11",
                        "artistName": "Demo Artist B"
                      },
                      {
                        "contentDescriptor": {
                          "kind": "song",
                          "url": "/jp/album/demo-album-c/0000000003?i=0000000301"
                        },
                        "title": "Demo Track 21"
                      },
                      {
                        "contentDescriptor": {
                          "kind": "song",
                          "url": "/jp/album/demo-album-d/0000000004?i=0000000401"
                        },
                        "title": "Demo Track 31"
                      },
                      {
                        "contentDescriptor": {
                          "kind": "song",
                          "url": "/jp/album/demo-album-e/0000000005?i=0000000501"
                        },
                        "title": "Demo Track 41"
                      },
                      {
                        "contentDescriptor": {
                          "kind": "song",
                          "url": "/jp/album/demo-album-f/0000000006?i=0000000601"
                        },
                        "title": "Demo Track 51"
                      },
                      {
                        "contentDescriptor": { "kind": "song" },
                        "title": "Demo Track 61"
                      }
                    ]
                  }
                ]
              }
            }
          ]
        }
        """;

    private const string EmptySearchJson =
        """
        {
          "data": [
            {
              "data": {
                "sections": []
              }
            }
          ]
        }
        """;

    private const string ReleaseComposerJson =
        """
        {
          "data": [
            {
              "id": "album-page",
              "data": {
                "sections": [
                  {
                    "id": "album-header",
                    "itemKind": "containerDetailHeaderLockup",
                    "items": [
                      {
                        "title": "Demo Album A",
                        "subtitleLinks": [ { "title": "Demo Artist" } ],
                        "trackCount": 3,
                        "quaternaryTitle": "Demo Genre A · 2020年",
                        "contentDescriptor": {
                          "kind": "album",
                          "url": "/jp/album/demo-album-a/0000000001",
                          "identifiers": { "storeAdamID": "0000000001" }
                        },
                        "artwork": {
                          "dictionary": {
                            "url": "https://is1-ssl.mzstatic.com/image/thumb/demo-album-a/{w}x{h}bb.{f}"
                          }
                        }
                      }
                    ]
                  },
                  {
                    "id": "album-track-list",
                    "itemKind": "trackLockup",
                    "items": [
                      {
                        "title": "Demo Track 01",
                        "trackNumber": 1,
                        "discNumber": 1,
                        "duration": 215000,
                        "artistName": "Demo Artist",
                        "composer": "Demo Composer A",
                        "contentDescriptor": {
                          "kind": "song",
                          "identifiers": { "storeAdamID": "0000000101" }
                        }
                      },
                      {
                        "title": "Demo Track 02",
                        "trackNumber": 2,
                        "discNumber": 1,
                        "duration": 198000,
                        "composer": "Demo Composer B",
                        "contentDescriptor": {
                          "kind": "song",
                          "identifiers": { "storeAdamID": "0000000102" }
                        }
                      },
                      {
                        "title": "Demo Track 03 (Demo Version)",
                        "trackNumber": 3,
                        "discNumber": 1,
                        "duration": 260000,
                        "artistName": "Demo Artist",
                        "composer": null,
                        "contentDescriptor": {
                          "kind": "song",
                          "identifiers": { "storeAdamID": "0000000103" }
                        }
                      }
                    ]
                  },
                  {
                    "id": "album-footer",
                    "itemKind": "containerDetailTracklistFooterLockup",
                    "items": [
                      {
                        "description": "2020年5月1日 3 首歌曲、10 分钟 ℗ 2020 Demo Records"
                      }
                    ]
                  },
                  {
                    "itemKind": "smallVideoLockup",
                    "items": [ { "title": "Demo Related Video" } ]
                  }
                ]
              }
            }
          ]
        }
        """;

    private const string ReleaseComposerJsonLd =
        """
        {
          "@context": "https://schema.org",
          "@type": "MusicAlbum",
          "name": "Demo Album A",
          "byArtist": [ { "@type": "MusicGroup", "name": "Demo Artist" } ],
          "datePublished": "2020-05-01",
          "genre": [ "Demo Genre A" ],
          "image": "https://is1-ssl.mzstatic.com/image/thumb/demo-jsonld/{w}x{h}bb.{f}"
        }
        """;

    private const string ReleaseNoComposerJson =
        """
        {
          "data": [
            {
              "data": {
                "sections": [
                  {
                    "itemKind": "containerDetailHeaderLockup",
                    "items": [
                      {
                        "title": "Demo Album B",
                        "subtitleLinks": [ { "title": "Demo Artist B" } ],
                        "trackCount": 2,
                        "quaternaryTitle": "Demo Genre B · 2021年",
                        "contentDescriptor": {
                          "kind": "album",
                          "url": "/jp/album/demo-album-b/0000000002",
                          "identifiers": { "storeAdamID": "0000000002" }
                        },
                        "artwork": {
                          "dictionary": {
                            "url": "https://is1-ssl.mzstatic.com/image/thumb/demo-album-b/{w}x{h}bb.{f}"
                          }
                        }
                      }
                    ]
                  },
                  {
                    "itemKind": "trackLockup",
                    "items": [
                      {
                        "title": "Demo Track 11",
                        "trackNumber": 1,
                        "discNumber": 1,
                        "duration": 180000,
                        "contentDescriptor": { "kind": "song" }
                      },
                      {
                        "title": "Demo Track 12",
                        "trackNumber": 2,
                        "discNumber": 1,
                        "duration": 190000,
                        "contentDescriptor": { "kind": "song" }
                      }
                    ]
                  }
                ]
              }
            }
          ]
        }
        """;

    private const string ReleaseNoComposerJsonLd =
        """
        {
          "@context": "https://schema.org",
          "@type": "MusicAlbum",
          "name": "Demo Album B",
          "byArtist": [ { "@type": "MusicGroup", "name": "Demo Artist B" } ],
          "datePublished": "2021-03-05",
          "genre": [ "Demo Genre B" ]
        }
        """;

    private const string ReleaseNoJsonLdJson =
        """
        {
          "data": [
            {
              "data": {
                "sections": [
                  {
                    "itemKind": "containerDetailHeaderLockup",
                    "items": [
                      {
                        "title": "Demo Album B",
                        "subtitleLinks": [ { "title": "Demo Artist B" } ],
                        "trackCount": 2,
                        "quaternaryTitle": "Demo Genre B · 2021年",
                        "contentDescriptor": {
                          "kind": "album",
                          "url": "/jp/album/demo-album-b/0000000002",
                          "identifiers": { "storeAdamID": "0000000002" }
                        },
                        "artwork": {
                          "dictionary": {
                            "url": "https://is1-ssl.mzstatic.com/image/thumb/demo-album-b/{w}x{h}bb.{f}"
                          }
                        }
                      }
                    ]
                  },
                  {
                    "itemKind": "trackLockup",
                    "items": [
                      {
                        "title": "Demo Track 11",
                        "trackNumber": 1,
                        "discNumber": 1,
                        "duration": 180000,
                        "contentDescriptor": { "kind": "song" }
                      },
                      {
                        "title": "Demo Track 12",
                        "trackNumber": 2,
                        "discNumber": 1,
                        "duration": 190000,
                        "contentDescriptor": { "kind": "song" }
                      }
                    ]
                  },
                  {
                    "itemKind": "containerDetailTracklistFooterLockup",
                    "items": [
                      {
                        "description": "2021年5月1日 2 首歌曲 ℗ 2021 Demo Records"
                      }
                    ]
                  }
                ]
              }
            }
          ]
        }
        """;

    private const string MissingHeaderJson =
        """
        {
          "data": [
            {
              "data": {
                "sections": [
                  {
                    "itemKind": "trackLockup",
                    "items": []
                  }
                ]
              }
            }
          ]
        }
        """;

    private const string TypeAnomaliesJson =
        """
        {
          "data": [
            {
              "data": {
                "sections": [
                  {
                    "itemKind": "containerDetailHeaderLockup",
                    "items": [
                      {
                        "title": "Demo Album C",
                        "subtitleLinks": [ { "title": "Demo Artist C" } ],
                        "trackCount": "4",
                        "contentDescriptor": {
                          "kind": "album",
                          "url": "/jp/album/demo-album-c/0000000003"
                        },
                        "artwork": {
                          "dictionary": {
                            "url": "https://is1-ssl.mzstatic.com/image/thumb/demo-album-c/{w}x{h}bb.{f}"
                          }
                        }
                      }
                    ]
                  },
                  {
                    "itemKind": "trackLockup",
                    "items": [
                      {
                        "title": "Demo Track 21",
                        "trackNumber": "1",
                        "discNumber": "2",
                        "duration": "215000",
                        "artistName": 42,
                        "composer": 42,
                        "contentDescriptor": { "kind": "song" }
                      }
                    ]
                  }
                ]
              }
            }
          ]
        }
        """;
}
