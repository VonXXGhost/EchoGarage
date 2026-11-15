using System.Net;
using System.Net.Http.Headers;
using System.Text;
using EchoGarage.Core.Abstractions;
using EchoGarage.Core.Models;
using EchoGarage.Infrastructure.WebTags;
using EchoGarage.Tests.Fixtures.WebTags;

namespace EchoGarage.Tests;

public sealed class AppleMusicHtmlParserTests
{
    private static AppleMusicHtmlParser CreateParser() => new(new RecordingLogger());

    [Fact]
    public void ParseSearchPage_MapsAlbumLockups()
    {
        var result = CreateParser().ParseSearchPage(WebTagTestFixtures.SearchPageWithAlbums, WebTagSourceIds.AppleMusicJp);

        Assert.Equal(2, result.Albums.Count);
        Assert.Empty(result.DerivedAlbumIds);

        var first = result.Albums[0];
        Assert.Equal(WebTagSourceIds.AppleMusicJp, first.SourceId);
        Assert.Equal("0000000001", first.ReleaseId);
        Assert.Equal("Demo Album A", first.Title);
        Assert.Equal("Demo Artist", first.Artist);
        Assert.Equal(3, first.TrackCount);
        Assert.Equal(WebTagTestFixtures.AlbumAArtwork1200, first.ArtworkUrl);
        Assert.Equal("https://music.apple.com/jp/album/demo-album-a/0000000001", first.SourceUrl);
        Assert.Equal(WebTagResultKind.Album, first.ResultKind);
        Assert.False(first.IsDerived);

        // 第二个条目没有 identifiers，ReleaseId 来自链接末尾的数字段。
        var second = result.Albums[1];
        Assert.Equal("0000000002", second.ReleaseId);
        Assert.Equal("Demo Album B", second.Title);
        Assert.Equal("Demo Artist B", second.Artist);
        Assert.Equal(2, second.TrackCount);
        Assert.Equal(WebTagTestFixtures.AlbumBArtworkTemplate.Replace("{w}x{h}bb.{f}", "1200x1200bb.jpg"), second.ArtworkUrl);
    }

    [Fact]
    public void ParseSearchPage_UnwrapsItemEnvelope()
    {
        var result = CreateParser().ParseSearchPage(
            WebTagTestFixtures.SearchPageWithWrappedAlbum, WebTagSourceIds.AppleMusicJp);

        var album = Assert.Single(result.Albums);
        Assert.Equal("0000000003", album.ReleaseId);
        Assert.Equal("Demo Album C", album.Title);
        Assert.Equal(4, album.TrackCount);
    }

    [Fact]
    public void ParseSearchPage_MixedTopSearchLockup_ReturnsOnlyAlbums()
    {
        var result = CreateParser().ParseSearchPage(
            WebTagTestFixtures.SearchPageMixedTopSearch, WebTagSourceIds.AppleMusicJp);

        var album = Assert.Single(result.Albums);
        Assert.Equal("0000000003", album.ReleaseId);
        Assert.Equal("Demo Album C", album.Title);
        // 真实 topSearchLockup 的专辑条目没有 subtitleLinks，艺术家要从 "アルバム · 艺术家" subtitle 解析。
        Assert.Equal("Demo Artist", album.Artist);
        Assert.Null(album.TrackCount);
        Assert.Equal(WebTagTestFixtures.AlbumCArtworkTemplate.Replace("{w}x{h}bb.{f}", "1200x1200bb.jpg"), album.ArtworkUrl);
        Assert.Empty(result.DerivedAlbumIds);
    }

    [Fact]
    public void ParseSearchPage_PrefersSquareAlbumsOverTopSearchAlbums()
    {
        var result = CreateParser().ParseSearchPage(
            WebTagTestFixtures.SearchPageTopSearchAndSquare, WebTagSourceIds.AppleMusicJp);

        // topSearchLockup 的粗略专辑条目应先被跳过，只保留 squareLockup 的完整专辑条目。
        var album = Assert.Single(result.Albums);
        Assert.Equal("0000000001", album.ReleaseId);
        Assert.Equal("Demo Album A", album.Title);
        Assert.Equal("Demo Artist", album.Artist);
        Assert.Equal(3, album.TrackCount);
        Assert.DoesNotContain(result.Albums, item => item.ReleaseId == "0000000009");
    }

    [Fact]
    public void ParseSearchPage_TopSearchOnly_ParsesArtistFromSubtitle()
    {
        var result = CreateParser().ParseSearchPage(
            WebTagTestFixtures.SearchPageTopSearchOnly, WebTagSourceIds.AppleMusicJp);

        var album = Assert.Single(result.Albums);
        Assert.Equal("0000000005", album.ReleaseId);
        Assert.Equal("Demo Album E", album.Title);
        Assert.Equal("Demo Artist E", album.Artist);
        Assert.Null(album.TrackCount);
        Assert.Equal(WebTagTestFixtures.AlbumEArtwork1200, album.ArtworkUrl);
        Assert.Equal(WebTagResultKind.Album, album.ResultKind);
        Assert.Empty(result.DerivedAlbumIds);
    }

    [Fact]
    public void ParseSearchPage_TracksOnly_ExtractsDeduplicatedDerivedIdsLimitedToFive()
    {
        var result = CreateParser().ParseSearchPage(
            WebTagTestFixtures.SearchPageTracksOnly, WebTagSourceIds.AppleMusicJp);

        Assert.Empty(result.Albums);
        Assert.Equal(
            new[] { "0000000001", "0000000002", "0000000003", "0000000004", "0000000005" },
            result.DerivedAlbumIds.ToArray());
    }

    [Fact]
    public void ParseSearchPage_EmptySections_ReturnsEmpty()
    {
        var result = CreateParser().ParseSearchPage(WebTagTestFixtures.SearchPageEmpty, WebTagSourceIds.AppleMusicJp);

        Assert.Empty(result.Albums);
        Assert.Empty(result.DerivedAlbumIds);
    }

    [Fact]
    public void ParseSearchPage_WithoutSerializedData_ThrowsFriendlyError()
    {
        var exception = Assert.Throws<WebTagSourceException>(() =>
            CreateParser().ParseSearchPage(WebTagTestFixtures.HtmlWithoutSerializedData, WebTagSourceIds.AppleMusicJp));

        Assert.Contains("结构", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseSearchPage_BrokenJson_ThrowsFriendlyError()
    {
        var exception = Assert.Throws<WebTagSourceException>(() =>
            CreateParser().ParseSearchPage(WebTagTestFixtures.SearchPageBrokenJson, WebTagSourceIds.AppleMusicJp));

        Assert.Contains("数据解析失败", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseReleasePage_MapsAlbumAndTrackFields()
    {
        var result = CreateParser().ParseReleasePage(WebTagTestFixtures.ReleasePageWithComposer);

        var release = result.Release;
        Assert.Equal(WebTagSourceIds.AppleMusicJp, release.SourceId);
        Assert.Equal("0000000001", release.ReleaseId);
        Assert.Equal("Demo Album A", release.Title);
        Assert.Equal("Demo Artist", release.Artist);
        Assert.Equal(WebTagReleaseKind.Album, release.Kind);
        Assert.Equal(WebTagTestFixtures.AlbumAArtwork1200, release.ArtworkUrl);
        Assert.Equal("https://music.apple.com/jp/album/0000000001?l=ja-JP", release.SourceUrl);
        Assert.Equal(3, result.TrackCount);

        Assert.Equal("Demo Album A", release.AlbumFields[WebTagFieldKeys.Album]);
        Assert.Equal("Demo Artist", release.AlbumFields[WebTagFieldKeys.AlbumArtist]);
        Assert.Equal("2020", release.AlbumFields[WebTagFieldKeys.Year]);
        Assert.Equal("Demo Genre A", release.AlbumFields[WebTagFieldKeys.Genre]);
        Assert.Equal(WebTagTestFixtures.AlbumAArtwork1200, release.AlbumFields[WebTagFieldKeys.Cover]);

        Assert.Equal(3, release.Tracks.Count);

        var first = release.Tracks[0];
        Assert.Equal("Demo Track 01", first.Fields[WebTagFieldKeys.Title]);
        Assert.Equal("Demo Artist", first.Fields[WebTagFieldKeys.Artist]);
        Assert.Equal("Demo Composer A", first.Fields[WebTagFieldKeys.Composer]);
        Assert.Equal("1", first.Fields[WebTagFieldKeys.Track]);
        Assert.Equal("1", first.Fields[WebTagFieldKeys.Disc]);
        Assert.Equal(1, first.TrackNumber);
        Assert.Equal(1, first.DiscNumber);
        Assert.Equal(215000, first.DurationMs);
        Assert.Equal("0000000101", first.RemoteId);

        // artistName 缺失时回落到专辑艺术家；composer 缺失时不出现在字段字典。
        var second = release.Tracks[1];
        Assert.Equal("Demo Artist", second.Fields[WebTagFieldKeys.Artist]);
        Assert.Equal("Demo Composer B", second.Fields[WebTagFieldKeys.Composer]);

        var third = release.Tracks[2];
        Assert.Equal("Demo Track 03 (Demo Version)", third.Fields[WebTagFieldKeys.Title]);
        Assert.False(third.Fields.ContainsKey(WebTagFieldKeys.Composer));
        Assert.Equal("Demo Artist", third.Fields[WebTagFieldKeys.Artist]);
    }

    [Fact]
    public void ParseReleasePage_WithoutComposer_LeavesComposerUnset()
    {
        var result = CreateParser().ParseReleasePage(WebTagTestFixtures.ReleasePageWithoutComposer);

        Assert.Equal("0000000002", result.Release.ReleaseId);
        Assert.Equal("2021", result.Release.AlbumFields[WebTagFieldKeys.Year]);
        Assert.Equal("Demo Genre B", result.Release.AlbumFields[WebTagFieldKeys.Genre]);
        Assert.Equal(2, result.Release.Tracks.Count);
        Assert.All(result.Release.Tracks, track =>
        {
            Assert.False(track.Fields.ContainsKey(WebTagFieldKeys.Composer));
            // 曲目自身没有 artistName 时回落到专辑艺术家。
            Assert.Equal("Demo Artist B", track.Fields[WebTagFieldKeys.Artist]);
        });
    }

    [Fact]
    public void ParseReleasePage_WithoutJsonLd_UsesFooterYearAndQuaternaryGenre()
    {
        var result = CreateParser().ParseReleasePage(WebTagTestFixtures.ReleasePageWithoutJsonLd);

        Assert.Equal("2021", result.Release.AlbumFields[WebTagFieldKeys.Year]);
        Assert.Equal("Demo Genre B", result.Release.AlbumFields[WebTagFieldKeys.Genre]);
    }

    [Fact]
    public void ParseReleasePage_BrokenJsonLd_StillParsesServerData()
    {
        var result = CreateParser().ParseReleasePage(WebTagTestFixtures.ReleasePageBrokenJsonLd);

        Assert.Equal("Demo Album B", result.Release.Title);
        Assert.Equal("2021", result.Release.AlbumFields[WebTagFieldKeys.Year]);
        Assert.Equal(2, result.Release.Tracks.Count);
    }

    [Fact]
    public void ParseReleasePage_MissingHeader_ThrowsFriendlyError()
    {
        var exception = Assert.Throws<WebTagSourceException>(() =>
            CreateParser().ParseReleasePage(WebTagTestFixtures.ReleasePageMissingHeader));

        Assert.Contains("专辑页", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseReleasePage_ToleratesTypeAnomaliesAndFallsBackToHeaderArtist()
    {
        var result = CreateParser().ParseReleasePage(WebTagTestFixtures.ReleasePageTypeAnomalies);

        Assert.Equal(4, result.TrackCount);
        Assert.Equal("0000000003", result.Release.ReleaseId);
        Assert.Equal("Demo Artist C", result.Release.Artist);
        Assert.False(result.Release.AlbumFields.ContainsKey(WebTagFieldKeys.Year));
        Assert.False(result.Release.AlbumFields.ContainsKey(WebTagFieldKeys.Genre));

        var track = Assert.Single(result.Release.Tracks);
        Assert.Equal(1, track.TrackNumber);
        Assert.Equal(2, track.DiscNumber);
        Assert.Equal(215000, track.DurationMs);
        Assert.Equal("Demo Artist C", track.Fields[WebTagFieldKeys.Artist]);
        Assert.False(track.Fields.ContainsKey(WebTagFieldKeys.Composer));
    }

    [Theory]
    [InlineData("0000000001", "0000000001")]
    [InlineData("  0000000001  ", "0000000001")]
    [InlineData("https://music.apple.com/jp/album/0000000001", "0000000001")]
    [InlineData("https://music.apple.com/jp/album/demo-album-a/0000000001", "0000000001")]
    [InlineData("https://music.apple.com/jp/album/demo-album-a/0000000001?i=0000000101", "0000000001")]
    [InlineData("https://music.apple.com/jp/album/demo-album-a/0000000001?l=ja-JP", "0000000001")]
    [InlineData("https://music.apple.com/us/album/demo-album-a/0000000001", "0000000001")]
    public void TryParseReleaseId_AcceptsAlbumIdsAndLinks(string input, string expected)
    {
        Assert.True(AppleMusicHtmlParser.TryParseReleaseId(input, out var releaseId));
        Assert.Equal(expected, releaseId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Demo Album A")]
    [InlineData("https://example.com/album/demo-album-a/0000000001")]
    [InlineData("https://music.apple.com/jp/artist/demo-artist/0000000009")]
    [InlineData("https://music.apple.com/jp/album/demo-album-a")]
    public void TryParseReleaseId_RejectsInvalidInput(string input)
    {
        Assert.False(AppleMusicHtmlParser.TryParseReleaseId(input, out _));
    }

    [Theory]
    [InlineData("/jp/album/demo-album-a/0000000001?i=0000000101", "0000000001")]
    [InlineData("https://music.apple.com/jp/album/demo-album-a/0000000001", "0000000001")]
    [InlineData("https://music.apple.com/jp/artist/demo-artist/0000000009", null)]
    [InlineData("https://example.com/jp/album/demo-album-a/0000000001", null)]
    public void TryExtractAlbumId_HandlesRelativeAndAbsoluteUrls(string url, string? expected)
    {
        if (expected is null)
        {
            Assert.False(AppleMusicHtmlParser.TryExtractAlbumId(url, out _));
        }
        else
        {
            Assert.True(AppleMusicHtmlParser.TryExtractAlbumId(url, out var albumId));
            Assert.Equal(expected, albumId);
        }
    }

    [Theory]
    [InlineData(WebTagTestFixtures.AlbumAArtworkTemplate, WebTagTestFixtures.AlbumAArtwork1200)]
    [InlineData("https://is1-ssl.mzstatic.com/image/thumb/demo/300x300bb.jpg", "https://is1-ssl.mzstatic.com/image/thumb/demo/300x300bb.jpg")]
    [InlineData("//is1-ssl.mzstatic.com/image/thumb/demo-album-a/{w}x{h}bb.{f}", WebTagTestFixtures.AlbumAArtwork1200)]
    [InlineData("", null)]
    [InlineData("not a url", null)]
    public void NormalizeArtworkUrl_Produces1200TemplateOrOriginal(string input, string? expected)
    {
        Assert.Equal(expected, AppleMusicHtmlParser.NormalizeArtworkUrl(input));
    }
}

public sealed class WebTagHttpClientFactoryTests
{
    [Fact]
    public void CreateHandler_SystemProxy_UsesSystemDefaults()
    {
        using var handler = new WebTagHttpClientFactory().CreateHandler(new NetworkSettings());

        Assert.True(handler.UseProxy);
        Assert.Null(handler.Proxy);
        Assert.Equal(DecompressionMethods.All, handler.AutomaticDecompression);
        Assert.Equal(TimeSpan.FromSeconds(WebTagHttpClientFactory.ConnectTimeoutSeconds), handler.ConnectTimeout);
        Assert.False(handler.UseCookies);
    }

    [Fact]
    public void CreateHandler_NullSettings_TreatedAsSystemProxy()
    {
        using var handler = new WebTagHttpClientFactory().CreateHandler(null);

        Assert.True(handler.UseProxy);
        Assert.Null(handler.Proxy);
    }

    [Fact]
    public void CreateHandler_Direct_DisablesProxy()
    {
        using var handler = new WebTagHttpClientFactory().CreateHandler(
            new NetworkSettings { ProxyMode = WebProxyMode.Direct });

        Assert.False(handler.UseProxy);
        Assert.Null(handler.Proxy);
    }

    [Fact]
    public void CreateHandler_CustomProxy_UsesConfiguredProxy()
    {
        using var handler = new WebTagHttpClientFactory().CreateHandler(
            new NetworkSettings { ProxyMode = WebProxyMode.Custom, CustomProxyUrl = "http://127.0.0.1:7890" });

        Assert.True(handler.UseProxy);
        var proxy = Assert.IsType<WebProxy>(handler.Proxy);
        Assert.Equal(new Uri("http://127.0.0.1:7890"), proxy.Address);
    }

    [Theory]
    [InlineData("socks5://127.0.0.1:7890")]
    [InlineData("ftp://127.0.0.1:7890")]
    [InlineData("not a url")]
    public void CreateHandler_InvalidCustomProxy_Throws(string url)
    {
        var settings = new NetworkSettings { ProxyMode = WebProxyMode.Custom, CustomProxyUrl = url };

        Assert.Throws<ArgumentException>(() => new WebTagHttpClientFactory().CreateHandler(settings));
    }

    [Fact]
    public async Task Create_AppliesTimeoutUserAgentAndAcceptLanguage()
    {
        var httpHandler = new FakeHttpMessageHandler(_ => HttpTestResponses.Html("<html>Demo</html>"));
        using var client = new WebTagHttpClientFactory(() => httpHandler).Create(null);

        Assert.Equal(TimeSpan.FromSeconds(WebTagHttpClientFactory.RequestTimeoutSeconds), client.Timeout);

        using var response = await client.GetAsync(new Uri("https://music.apple.com/jp/"), CancellationToken.None);
        Assert.True(response.IsSuccessStatusCode);
        Assert.Contains("Mozilla", httpHandler.UserAgents[0], StringComparison.Ordinal);
        Assert.Contains("ja-JP", httpHandler.AcceptLanguages[0], StringComparison.Ordinal);
    }
}

public sealed class AppleMusicWebTagSourceTests
{
    private static (AppleMusicWebTagSource Source, FakeHttpMessageHandler Handler, RecordingLogger Logger) CreateSource(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new FakeHttpMessageHandler(responder);
        var factory = new WebTagHttpClientFactory(() => handler);
        var settings = new FakeSettingsService();
        var logger = new RecordingLogger();
        var source = new AppleMusicWebTagSource(factory, settings, logger);
        return (source, handler, logger);
    }

    [Fact]
    public async Task SearchAsync_RequestsSearchPageAndMapsAlbums()
    {
        var (source, handler, logger) = CreateSource(_ => HttpTestResponses.Html(WebTagTestFixtures.SearchPageWithAlbums));

        var results = await source.SearchAsync(
            new WebTagSearchQuery(WebTagSourceIds.AppleMusicJp, "Demo Album A"), CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Single(handler.RequestedUris);
        Assert.Contains("term=Demo%20Album%20A", handler.RequestedUris[0], StringComparison.Ordinal);
        Assert.Contains("l=ja-JP", handler.RequestedUris[0], StringComparison.Ordinal);

        var first = results[0];
        Assert.Equal("0000000001", first.ReleaseId);
        Assert.Equal("Demo Album A", first.Title);
        Assert.Equal("Demo Artist", first.Artist);
        Assert.Equal(3, first.TrackCount);
        Assert.Equal(WebTagTestFixtures.AlbumAArtwork1200, first.ArtworkUrl);
        Assert.Equal(WebTagResultKind.Album, first.ResultKind);
        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public async Task SearchAsync_DerivesCandidatesAndSkipsFailedPages()
    {
        var (source, handler, logger) = CreateSource(request =>
        {
            var uri = request.RequestUri!.AbsoluteUri;
            if (uri.Contains("/search?", StringComparison.Ordinal))
            {
                return HttpTestResponses.Html(WebTagTestFixtures.SearchPageTracksOnly);
            }

            if (uri.Contains("/album/0000000003", StringComparison.Ordinal))
            {
                return HttpTestResponses.Empty(HttpStatusCode.InternalServerError);
            }

            return HttpTestResponses.Html(WebTagTestFixtures.ReleasePageWithComposer);
        });

        var results = await source.SearchAsync(
            new WebTagSearchQuery(WebTagSourceIds.AppleMusicJp, "Demo Album A"), CancellationToken.None);

        // 5 个派生候选中有 1 个专辑页 500，被跳过。
        Assert.Equal(4, results.Count);
        Assert.Equal(6, handler.RequestedUris.Count);
        Assert.Equal(
            new[] { "0000000001", "0000000002", "0000000004", "0000000005" },
            results.Select(item => item.ReleaseId).ToArray());
        Assert.All(results, item =>
        {
            Assert.Equal(WebTagResultKind.DerivedAlbum, item.ResultKind);
            Assert.True(item.IsDerived);
            Assert.Equal(3, item.TrackCount);
            Assert.Equal(2020, item.Year);
            Assert.False(string.IsNullOrEmpty(item.ArtworkUrl));
        });
        Assert.Contains(logger.Warnings, warning => warning.Contains("跳过", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SearchAsync_EmptyResults_ReturnsEmptyList()
    {
        var (source, handler, _) = CreateSource(_ => HttpTestResponses.Html(WebTagTestFixtures.SearchPageEmpty));

        var results = await source.SearchAsync(
            new WebTagSearchQuery(WebTagSourceIds.AppleMusicJp, "Demo Album Z"), CancellationToken.None);

        Assert.Empty(results);
        Assert.Single(handler.RequestedUris);
    }

    [Fact]
    public async Task SearchAsync_ServerError_ThrowsWithStatusCode()
    {
        var (source, _, _) = CreateSource(_ => HttpTestResponses.Empty(HttpStatusCode.InternalServerError));

        var exception = await Assert.ThrowsAsync<WebTagSourceException>(() => source.SearchAsync(
            new WebTagSearchQuery(WebTagSourceIds.AppleMusicJp, "Demo Album A"), CancellationToken.None));

        Assert.Contains("HTTP 500", exception.Message, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.InternalServerError, exception.StatusCode);
    }

    [Fact]
    public async Task SearchAsync_TooManyRequests_ReportsFriendlyMessage()
    {
        var (source, _, _) = CreateSource(_ => HttpTestResponses.Empty(HttpStatusCode.TooManyRequests));

        var exception = await Assert.ThrowsAsync<WebTagSourceException>(() => source.SearchAsync(
            new WebTagSearchQuery(WebTagSourceIds.AppleMusicJp, "Demo Album A"), CancellationToken.None));

        Assert.Contains("频繁", exception.Message, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.TooManyRequests, exception.StatusCode);
    }

    [Fact]
    public async Task SearchAsync_PageWithoutSerializedData_ThrowsFriendlyError()
    {
        var (source, _, _) = CreateSource(_ => HttpTestResponses.Html(WebTagTestFixtures.HtmlWithoutSerializedData));

        var exception = await Assert.ThrowsAsync<WebTagSourceException>(() => source.SearchAsync(
            new WebTagSearchQuery(WebTagSourceIds.AppleMusicJp, "Demo Album A"), CancellationToken.None));

        Assert.Contains("结构", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAsync_EmptyTerm_Throws()
    {
        var (source, handler, _) = CreateSource(_ => HttpTestResponses.Html(WebTagTestFixtures.SearchPageEmpty));

        var exception = await Assert.ThrowsAsync<WebTagSourceException>(() =>
            source.SearchAsync(new WebTagSearchQuery(WebTagSourceIds.AppleMusicJp, "   "), CancellationToken.None));

        Assert.Contains("搜索关键词", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.RequestedUris);
    }

    [Fact]
    public async Task SearchAsync_OversizedResponse_Throws()
    {
        var oversized = new string('x', AppleMusicWebTagSource.MaxPageBytes + 1);
        var (source, _, _) = CreateSource(_ => HttpTestResponses.Html(oversized));

        var exception = await Assert.ThrowsAsync<WebTagSourceException>(() => source.SearchAsync(
            new WebTagSearchQuery(WebTagSourceIds.AppleMusicJp, "Demo Album A"), CancellationToken.None));

        Assert.Contains("20 MB", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAsync_NetworkFailure_ThrowsFriendlyError()
    {
        var (source, _, _) = CreateSource(_ => throw new HttpRequestException("network down"));

        var exception = await Assert.ThrowsAsync<WebTagSourceException>(() => source.SearchAsync(
            new WebTagSearchQuery(WebTagSourceIds.AppleMusicJp, "Demo Album A"), CancellationToken.None));

        Assert.Contains("无法访问", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAsync_Timeout_ThrowsFriendlyError()
    {
        var (source, _, _) = CreateSource(_ => throw new TaskCanceledException("timeout"));

        var exception = await Assert.ThrowsAsync<WebTagSourceException>(() => source.SearchAsync(
            new WebTagSearchQuery(WebTagSourceIds.AppleMusicJp, "Demo Album A"), CancellationToken.None));

        Assert.Contains("超时", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetReleaseAsync_MapsReleaseFromAlbumPage()
    {
        var (source, handler, _) = CreateSource(_ => HttpTestResponses.Html(WebTagTestFixtures.ReleasePageWithComposer));

        var release = await source.GetReleaseAsync("0000000001", CancellationToken.None);

        Assert.Equal(
            "https://music.apple.com/jp/album/0000000001?l=ja-JP",
            Assert.Single(handler.RequestedUris));
        Assert.Equal("Demo Album A", release.Title);
        Assert.Equal("Demo Artist", release.Artist);
        Assert.Equal("2020", release.AlbumFields[WebTagFieldKeys.Year]);
        Assert.Equal("Demo Genre A", release.AlbumFields[WebTagFieldKeys.Genre]);
        Assert.Equal(3, release.Tracks.Count);
        Assert.Equal("Demo Composer A", release.Tracks[0].Fields[WebTagFieldKeys.Composer]);
    }

    [Fact]
    public async Task GetReleaseAsync_AcceptsFullAlbumUrl()
    {
        var (source, handler, _) = CreateSource(_ => HttpTestResponses.Html(WebTagTestFixtures.ReleasePageWithComposer));

        var release = await source.GetReleaseAsync(
            "https://music.apple.com/jp/album/demo-album-a/0000000001?i=0000000101", CancellationToken.None);

        Assert.Equal("0000000001", release.ReleaseId);
        Assert.Equal(
            "https://music.apple.com/jp/album/0000000001?l=ja-JP",
            Assert.Single(handler.RequestedUris));
    }

    [Fact]
    public async Task GetReleaseAsync_InvalidId_Throws()
    {
        var (source, handler, _) = CreateSource(_ => HttpTestResponses.Html(WebTagTestFixtures.ReleasePageWithComposer));

        var exception = await Assert.ThrowsAsync<WebTagSourceException>(() =>
            source.GetReleaseAsync("Demo Album A", CancellationToken.None));

        Assert.Contains("无法识别", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.RequestedUris);
    }

    [Fact]
    public async Task TestConnectionAsync_ProbesStorefrontHomepage()
    {
        var (source, handler, logger) = CreateSource(_ => HttpTestResponses.Html("<html>Demo</html>"));

        await source.TestConnectionAsync(CancellationToken.None);

        Assert.Equal("https://music.apple.com/jp/", Assert.Single(handler.RequestedUris));
        Assert.Contains(logger.Infos, info => info.Contains("连接测试成功", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TestConnectionAsync_ServerError_ThrowsWithStatusCode()
    {
        var (source, _, _) = CreateSource(_ => HttpTestResponses.Empty(HttpStatusCode.ServiceUnavailable));

        var exception = await Assert.ThrowsAsync<WebTagSourceException>(() =>
            source.TestConnectionAsync(CancellationToken.None));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
    }

    [Fact]
    public async Task Requests_CreateFreshHttpClientEachTime()
    {
        var createCount = 0;
        var handler = new FakeHttpMessageHandler(request =>
            request.RequestUri!.AbsoluteUri.Contains("/search?", StringComparison.Ordinal)
                ? HttpTestResponses.Html(WebTagTestFixtures.SearchPageWithAlbums)
                : HttpTestResponses.Html(WebTagTestFixtures.ReleasePageWithComposer));
        var factory = new WebTagHttpClientFactory(() =>
        {
            createCount++;
            return handler;
        });
        var source = new AppleMusicWebTagSource(factory, new FakeSettingsService(), new RecordingLogger());

        await source.SearchAsync(new WebTagSearchQuery(WebTagSourceIds.AppleMusicJp, "Demo Album A"), CancellationToken.None);
        Assert.Equal(1, createCount);

        await source.GetReleaseAsync("0000000001", CancellationToken.None);
        Assert.Equal(2, createCount);
    }
}

public sealed class WebTagServiceTests
{
    private static (WebTagService Service, RecordingLogger Logger, FakeSettingsService Settings) CreateService(
        IEnumerable<IWebTagSource> sources, FakeHttpMessageHandler? handler = null)
    {
        var logger = new RecordingLogger();
        var settings = new FakeSettingsService();
        handler ??= new FakeHttpMessageHandler(_ => HttpTestResponses.Html("<html>Demo</html>"));
        var factory = new WebTagHttpClientFactory(() => handler);
        var service = new WebTagService(sources, settings, factory, logger);
        return (service, logger, settings);
    }

    private static FakeWebTagSource CreateSource(string id, string? displayName = null) => new(id, displayName ?? $"Demo {id}");

    [Fact]
    public void EnabledSources_FiltersUnknownAndUnregisteredSources()
    {
        var sourceA = CreateSource("source-a");
        var sourceB = CreateSource("source-b");
        var (service, _, settings) = CreateService([sourceA, sourceB]);
        settings.Current.WebTags = new WebTagSettings
        {
            EnabledSourceIds = ["source-b", "unknown", "source-a"],
            DefaultSourceId = "source-a",
        };

        Assert.Equal(2, service.Sources.Count);
        Assert.Equal(new[] { "source-b", "source-a" }, service.EnabledSources.Select(source => source.Id).ToArray());
        Assert.Equal("source-a", service.DefaultSourceId);
    }

    [Fact]
    public void EnabledSources_FallsBackToAppleMusicWhenSettingsEmpty()
    {
        var sourceA = CreateSource("source-a");
        var appleMusic = CreateSource(WebTagSourceIds.AppleMusicJp);
        var (service, _, settings) = CreateService([sourceA, appleMusic]);
        settings.Current.WebTags = new WebTagSettings { EnabledSourceIds = [], DefaultSourceId = "source-a" };

        var enabled = Assert.Single(service.EnabledSources);
        Assert.Equal(WebTagSourceIds.AppleMusicJp, enabled.Id);
        Assert.Equal(WebTagSourceIds.AppleMusicJp, service.DefaultSourceId);
    }

    [Fact]
    public void EnabledSources_FallsBackToFirstRegisteredSourceWhenAppleMusicMissing()
    {
        var sourceA = CreateSource("source-a");
        var sourceB = CreateSource("source-b");
        var (service, _, settings) = CreateService([sourceA, sourceB]);
        settings.Current.WebTags = new WebTagSettings { EnabledSourceIds = [], DefaultSourceId = string.Empty };

        var enabled = Assert.Single(service.EnabledSources);
        Assert.Equal("source-a", enabled.Id);
        Assert.Equal("source-a", service.DefaultSourceId);
    }

    [Fact]
    public void DefaultSourceId_UsesConfiguredDefaultWhenEnabled()
    {
        var sourceA = CreateSource("source-a");
        var sourceB = CreateSource("source-b");
        var (service, _, settings) = CreateService([sourceA, sourceB]);
        settings.Current.WebTags = new WebTagSettings
        {
            EnabledSourceIds = ["source-a", "source-b"],
            DefaultSourceId = "source-b",
        };

        Assert.Equal("source-b", service.DefaultSourceId);
    }

    [Fact]
    public void DefaultSourceId_FallsBackToFirstEnabledSource()
    {
        var sourceA = CreateSource("source-a");
        var sourceB = CreateSource("source-b");
        var (service, _, settings) = CreateService([sourceA, sourceB]);
        settings.Current.WebTags = new WebTagSettings
        {
            EnabledSourceIds = ["source-b", "source-a"],
            DefaultSourceId = "source-missing",
        };

        Assert.Equal("source-b", service.DefaultSourceId);
    }

    [Fact]
    public void FindSource_IsCaseInsensitiveAndHandlesBlank()
    {
        var sourceA = CreateSource("source-a");
        var (service, _, _) = CreateService([sourceA]);

        Assert.Same(sourceA, service.FindSource("SOURCE-A"));
        Assert.Null(service.FindSource("source-missing"));
        Assert.Null(service.FindSource(null));
        Assert.Null(service.FindSource("   "));
    }

    [Fact]
    public async Task SearchAsync_RoutesToSource()
    {
        var sourceA = CreateSource("source-a");
        var expected = new WebTagSearchItem
        {
            SourceId = "source-a",
            ReleaseId = "0000000001",
            Title = "Demo Album A",
        };
        sourceA.SearchHandler = _ => [expected];
        var (service, _, _) = CreateService([sourceA]);

        var results = await service.SearchAsync("SOURCE-A", "Demo Album A", CancellationToken.None);

        var query = Assert.Single(sourceA.SearchQueries);
        Assert.Equal("source-a", query.SourceId);
        Assert.Equal("Demo Album A", query.Term);
        Assert.Same(expected, Assert.Single(results));
    }

    [Fact]
    public async Task SearchAsync_UnknownSource_Throws()
    {
        var (service, _, _) = CreateService([CreateSource("source-a")]);

        var exception = await Assert.ThrowsAsync<WebTagSourceException>(() =>
            service.SearchAsync("source-missing", "Demo Album A", CancellationToken.None));

        Assert.Contains("未知的 Web 标签源", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetReleaseAsync_RoutesToSource()
    {
        var sourceA = CreateSource("source-a");
        var release = new WebTagRelease { SourceId = "source-a", ReleaseId = "0000000001", Title = "Demo Album A" };
        sourceA.ReleaseHandler = _ => release;
        var (service, _, _) = CreateService([sourceA]);

        var result = await service.GetReleaseAsync("source-a", "0000000001", CancellationToken.None);

        Assert.Same(release, result);
        Assert.Equal(new[] { "0000000001" }, sourceA.RequestedReleaseIds.ToArray());
    }

    [Fact]
    public async Task GetReleaseAsync_UnknownSource_Throws()
    {
        var (service, _, _) = CreateService([CreateSource("source-a")]);

        await Assert.ThrowsAsync<WebTagSourceException>(() =>
            service.GetReleaseAsync("source-missing", "0000000001", CancellationToken.None));
    }

    [Fact]
    public void TryParseReleaseId_RoutesToSource()
    {
        var sourceA = CreateSource("source-a");
        sourceA.TryParseHandler = input => input == "demo-album" ? "0000000009" : null;
        var (service, _, _) = CreateService([sourceA]);

        Assert.True(service.TryParseReleaseId("source-a", "demo-album", out var releaseId));
        Assert.Equal("0000000009", releaseId);

        Assert.False(service.TryParseReleaseId("source-a", "nope", out _));
        Assert.False(service.TryParseReleaseId("source-missing", "demo-album", out _));
    }

    [Fact]
    public async Task DownloadCoverAsync_ReturnsJpeg()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            HttpTestResponses.Bytes(TestImageBytes.Jpeg, "image/jpeg"));
        var (service, _, _) = CreateService([], handler);

        var cover = await service.DownloadCoverAsync(
            "https://is1-ssl.mzstatic.com/image/thumb/demo-album-a/1200x1200bb.jpg", CancellationToken.None);

        Assert.Equal("image/jpeg", cover.Mime);
        Assert.Equal(TestImageBytes.Jpeg, cover.Bytes);
        Assert.Single(handler.RequestedUris);
    }

    [Fact]
    public async Task DownloadCoverAsync_ReturnsPng()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            HttpTestResponses.Bytes(TestImageBytes.Png, "image/png"));
        var (service, _, _) = CreateService([], handler);

        var cover = await service.DownloadCoverAsync(
            "https://is1-ssl.mzstatic.com/image/thumb/demo-album-a/1200x1200bb.png", CancellationToken.None);

        Assert.Equal("image/png", cover.Mime);
        Assert.Equal(TestImageBytes.Png, cover.Bytes);
    }

    [Fact]
    public async Task DownloadCoverAsync_NonImageContentType_Throws()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            HttpTestResponses.Bytes(TestImageBytes.Jpeg, "text/html"));
        var (service, _, _) = CreateService([], handler);

        var exception = await Assert.ThrowsAsync<WebTagSourceException>(() => service.DownloadCoverAsync(
            "https://is1-ssl.mzstatic.com/image/thumb/demo-album-a/1200x1200bb.jpg", CancellationToken.None));

        Assert.Contains("JPEG/PNG", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadCoverAsync_MismatchedMagicBytes_Throws()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            HttpTestResponses.Bytes(Encoding.ASCII.GetBytes("not-an-image"), "image/jpeg"));
        var (service, _, _) = CreateService([], handler);

        var exception = await Assert.ThrowsAsync<WebTagSourceException>(() => service.DownloadCoverAsync(
            "https://is1-ssl.mzstatic.com/image/thumb/demo-album-a/1200x1200bb.jpg", CancellationToken.None));

        Assert.Contains("JPEG/PNG", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadCoverAsync_NotFound_ThrowsWithStatusCode()
    {
        var handler = new FakeHttpMessageHandler(_ => HttpTestResponses.Empty(HttpStatusCode.NotFound));
        var (service, _, _) = CreateService([], handler);

        var exception = await Assert.ThrowsAsync<WebTagSourceException>(() => service.DownloadCoverAsync(
            "https://is1-ssl.mzstatic.com/image/thumb/demo-album-a/1200x1200bb.jpg", CancellationToken.None));

        Assert.Contains("HTTP 404", exception.Message, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
    }

    [Fact]
    public async Task DownloadCoverAsync_TooManyRequests_ReportsFriendlyMessage()
    {
        var handler = new FakeHttpMessageHandler(_ => HttpTestResponses.Empty(HttpStatusCode.TooManyRequests));
        var (service, _, _) = CreateService([], handler);

        var exception = await Assert.ThrowsAsync<WebTagSourceException>(() => service.DownloadCoverAsync(
            "https://is1-ssl.mzstatic.com/image/thumb/demo-album-a/1200x1200bb.jpg", CancellationToken.None));

        Assert.Contains("频繁", exception.Message, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.TooManyRequests, exception.StatusCode);
    }

    [Fact]
    public async Task DownloadCoverAsync_OversizedResponse_Throws()
    {
        var oversized = new byte[WebTagService.MaxCoverBytes + 1];
        oversized[0] = 0xFF;
        oversized[1] = 0xD8;
        oversized[2] = 0xFF;
        var handler = new FakeHttpMessageHandler(_ =>
            HttpTestResponses.Bytes(oversized, "image/jpeg"));
        var (service, _, _) = CreateService([], handler);

        var exception = await Assert.ThrowsAsync<WebTagSourceException>(() => service.DownloadCoverAsync(
            "https://is1-ssl.mzstatic.com/image/thumb/demo-album-a/1200x1200bb.jpg", CancellationToken.None));

        Assert.Contains("15 MB", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://is1-ssl.mzstatic.com/image/thumb/demo-album-a/1200x1200bb.jpg")]
    public async Task DownloadCoverAsync_InvalidUrl_Throws(string url)
    {
        var (service, _, _) = CreateService([]);

        var exception = await Assert.ThrowsAsync<WebTagSourceException>(() =>
            service.DownloadCoverAsync(url, CancellationToken.None));

        Assert.Contains("封面地址无效", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestConnectionAsync_Success_ReturnsOk()
    {
        var sourceA = CreateSource("source-a");
        var (service, logger, _) = CreateService([sourceA]);

        var result = await service.TestConnectionAsync("source-a", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("连接成功", result.Message);
        Assert.True(result.Elapsed >= TimeSpan.Zero);
        Assert.Equal(1, sourceA.ConnectionTestCalls);
        Assert.Contains(logger.Infos, info => info.Contains("连接测试成功", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TestConnectionAsync_SourceFailure_ReturnsFailAndLogs()
    {
        var sourceA = CreateSource("source-a");
        sourceA.ConnectionTestHandler = () =>
            throw new WebTagSourceException("Apple Music 请求过于频繁或暂时不可用，请稍后再试。");
        var (service, logger, _) = CreateService([sourceA]);

        var result = await service.TestConnectionAsync("source-a", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("频繁", result.Message, StringComparison.Ordinal);
        Assert.Contains(logger.Warnings, warning => warning.Contains("频繁", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TestConnectionAsync_UnknownSource_ReturnsFail()
    {
        var (service, _, _) = CreateService([CreateSource("source-a")]);

        var result = await service.TestConnectionAsync("source-missing", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("未知的 Web 标签源", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestConnectionAsync_Cancellation_Propagates()
    {
        var sourceA = CreateSource("source-a");
        var (service, _, _) = CreateService([sourceA]);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.TestConnectionAsync("source-a", cts.Token));
    }

    [Fact]
    public void EmptySources_ProduceEmptyCatalog()
    {
        var (service, _, _) = CreateService([]);

        Assert.Empty(service.Sources);
        Assert.Empty(service.EnabledSources);
        Assert.Equal(string.Empty, service.DefaultSourceId);
    }
}

internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

    public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : this((request, _) => Task.FromResult(responder(request)))
    {
    }

    public FakeHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        _responder = responder;
    }

    public List<string> RequestedUris { get; } = [];

    public List<string> UserAgents { get; } = [];

    public List<string> AcceptLanguages { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestedUris.Add(request.RequestUri?.AbsoluteUri ?? string.Empty);
        UserAgents.Add(GetHeader(request, "User-Agent"));
        AcceptLanguages.Add(GetHeader(request, "Accept-Language"));
        return await _responder(request, cancellationToken).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        // 测试里同一个 handler 会被多个 HttpClient 复用，因此不在这里释放资源。
    }

    private static string GetHeader(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? string.Join(", ", values) : string.Empty;
}

internal static class HttpTestResponses
{
    public static HttpResponseMessage Html(string html, HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode) { Content = new StringContent(html, Encoding.UTF8, "text/html") };

    public static HttpResponseMessage Bytes(
        byte[] bytes, string mediaType, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return new HttpResponseMessage(statusCode) { Content = content };
    }

    public static HttpResponseMessage Empty(HttpStatusCode statusCode) => new(statusCode);
}

internal static class TestImageBytes
{
    public static byte[] Jpeg { get; } = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01];

    public static byte[] Png { get; } = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];
}

internal sealed class FakeSettingsService : ISettingsService
{
    public AppSettings Current { get; set; } = new();

    public event EventHandler<AppSettings>? Changed;

    public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SaveAsync(CancellationToken cancellationToken = default)
    {
        Changed?.Invoke(this, Current);
        return Task.CompletedTask;
    }

    public Task ExportAsync(string path, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ImportAsync(string path, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class FakeWebTagSource : IWebTagSource
{
    public FakeWebTagSource(string id, string displayName)
    {
        Id = id;
        DisplayName = displayName;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public WebTagSourceCapabilities Capabilities { get; } = new();

    public List<WebTagSearchQuery> SearchQueries { get; } = [];

    public List<string> RequestedReleaseIds { get; } = [];

    public int ConnectionTestCalls { get; private set; }

    public Func<WebTagSearchQuery, IReadOnlyList<WebTagSearchItem>>? SearchHandler { get; set; }

    public Func<string, WebTagRelease>? ReleaseHandler { get; set; }

    public Func<Task>? ConnectionTestHandler { get; set; }

    public Func<string, string?>? TryParseHandler { get; set; }

    public Task<IReadOnlyList<WebTagSearchItem>> SearchAsync(
        WebTagSearchQuery query, CancellationToken cancellationToken)
    {
        SearchQueries.Add(query);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<IReadOnlyList<WebTagSearchItem>>(cancellationToken);
        }

        IReadOnlyList<WebTagSearchItem> results = SearchHandler?.Invoke(query) ?? [];
        return Task.FromResult(results);
    }

    public Task<WebTagRelease> GetReleaseAsync(string releaseId, CancellationToken cancellationToken)
    {
        RequestedReleaseIds.Add(releaseId);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<WebTagRelease>(cancellationToken);
        }

        if (ReleaseHandler is null)
        {
            throw new InvalidOperationException("ReleaseHandler 未配置。");
        }

        return Task.FromResult(ReleaseHandler(releaseId));
    }

    public bool TryParseReleaseId(string input, out string releaseId)
    {
        releaseId = string.Empty;
        var parsed = TryParseHandler?.Invoke(input);
        if (parsed is null)
        {
            return false;
        }

        releaseId = parsed;
        return true;
    }

    public async Task TestConnectionAsync(CancellationToken cancellationToken)
    {
        ConnectionTestCalls++;
        cancellationToken.ThrowIfCancellationRequested();
        if (ConnectionTestHandler is not null)
        {
            await ConnectionTestHandler().ConfigureAwait(false);
        }
    }
}
