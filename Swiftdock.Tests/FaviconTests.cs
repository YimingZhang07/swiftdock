using System.IO;
using System.Net.Http;
using System.Net;
using System.Text.Json;
using System.Text;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using Swiftdock.Diagnostics;
using Swiftdock.Models;
using Swiftdock.Services;
using Xunit;

namespace Swiftdock.Tests;

public sealed class FaviconTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Swiftdock-favicons-" + Guid.NewGuid().ToString("N"));
    public FaviconTests() => Directory.CreateDirectory(_directory);
    private static readonly Uri Origin = new("https://large.test");
    private static LaunchItem Web(string path = "https://large.test/page?private=yes") => new("Web", path, LaunchKind.Url);
    private FaviconService Service(TestLogging log, HttpMessageHandler handler, Func<DateTimeOffset>? now = null,
        TimeSpan? requestTimeout = null, TimeSpan? siteTimeout = null) => new(_directory, log.Logger<FaviconService>(),
            (source, size) => Task.FromResult(IconService.RenderFavicon(source, size)), handler, now, requestTimeout, siteTimeout);

    [Theory]
    [InlineData("ICON")] [InlineData("shortcut icon")] [InlineData("APPLE-TOUCH-ICON")] [InlineData("apple-touch-icon-precomposed")]
    public void ParserHandlesRelQuotingCaseAndBase(string rel)
    {
        var candidates = FaviconParser.Parse($"""
            <!-- <link rel=icon href=/comment.png> -->
            <script>"<link rel=icon href=/script.png>"</script>
            <LINK HREF='tile.png?a=1&amp;b=2' SIZES="16x16 64X64 invalid 0x32" REL='{rel}' TYPE=image/png>
            <base href='/assets/'>
            <link rel=icon href='//large.test/protocol.png' sizes=any>
            <link rel=icon href='https://large.test/absolute.png' sizes=32x32>
            <link rel=icon href='https://assets.test/icon.png' sizes=256x256>
            <link rel=icon href='/vector.svg' sizes=any>
            <link rel=icon href='/vector' type='image/svg+xml'>
            """, new("https://large.test/final/index"), Origin, 48);
        Assert.Equal(5, candidates.Count);
        Assert.Contains(candidates, c => c.Url.AbsoluteUri == "https://large.test/assets/tile.png?a=1&b=2" && c.Sizes.SequenceEqual([16, 64]));
        Assert.Contains(candidates, c => c.Url.AbsoluteUri == "https://assets.test/icon.png");
        Assert.Equal("/protocol.png", candidates[0].Url.AbsolutePath); Assert.True(candidates[0].Any);
        Assert.Equal("/favicon.ico", candidates[^1].Url.AbsolutePath);
    }

    [Fact]
    public void SelectionPrefersRasterThenSmallestAdequateElseLargestAndFallbackLast()
    {
        const string html = """
            <link rel=icon href=/unknown sizes=48x48>
            <link rel=icon href=/large.png sizes=128x128>
            <link rel=apple-touch-icon href=/apple.png sizes=64x64>
            <link rel=icon href=/tiny.ico sizes=16x16>
            <link rel=icon href=/middle.gif sizes=32x32>
            <link rel=icon href=/favicon.ico sizes=48x48>
            """;
        Assert.Equal(new[] { "/apple.png", "/large.png", "/middle.gif", "/tiny.ico", "/unknown", "/favicon.ico" },
            FaviconParser.Parse(html, Origin, Origin, 48).Select(c => c.Url.AbsolutePath));
        Assert.Equal("/large.png", FaviconParser.Parse(html, Origin, Origin, 256)[0].Url.AbsolutePath);
        Assert.Equal("https://large.test:8443/favicon.ico", FaviconParser.Parse("", new("https://large.test:8443/a"), new("https://large.test:8443/a"), 48)[0].Url.AbsoluteUri);
    }

    [Fact]
    public void GarbageHtmlNeverThrowsAndRelativePathsUseFinalResponse()
    {
        var random = new Random(42);
        const string alphabet = "<>/'\" =abc\t\r\n\0";
        for (int i = 0; i < 300; i++)
        {
            string garbage = new(Enumerable.Range(0, 1024).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            Assert.NotEmpty(FaviconParser.Parse(garbage, Origin, Origin, 48));
        }
        Assert.Equal("/redirected/icon.png", FaviconParser.Parse("<link href=icon.png rel=icon>", new("https://large.test/redirected/page"), Origin, 48)[0].Url.AbsolutePath);
    }

    [Fact]
    public async Task SameOriginItemsShareOneDiscoveryAndCustomOrDisabledMakesNoRequests()
    {
        await using var log = new TestLogging();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FaviconFixtureHandler(async (request, token) =>
        {
            entered.TrySetResult(); await release.Task.WaitAsync(token); return FaviconFixtureHandler.Respond(request.RequestUri!);
        });
        using var icons = log.CreateIcons(_directory, handler);
        Assert.Null(await icons.GetFaviconAsync(Web() with { Icon = "glyph:E943:#2F6FDB" }, 48));
        Assert.Null(await icons.GetFaviconAsync(Web() with { Icon = "file:C:\\missing.ico" }, 48));
        icons.AutoFetchWebsiteIcons = false;
        Assert.Null(await icons.GetFaviconAsync(Web(), 48)); Assert.Empty(handler.Requests);
        icons.AutoFetchWebsiteIcons = true;
        var first = icons.GetFaviconAsync(Web(), 48);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = icons.GetFaviconAsync(Web("https://large.test/other"), 48);
        Assert.False(first.IsCompleted); Assert.Single(handler.Requests);
        release.SetResult();
        var images = await Task.WhenAll(first, second);
        Assert.NotNull(images[0]); Assert.Same(images[0], images[1]); Assert.Equal(2, handler.Requests.Count);
        icons.AutoFetchWebsiteIcons = false;
        Assert.Null(await icons.GetFaviconAsync(Web(), 48)); Assert.Equal(2, handler.Requests.Count);
        icons.AutoFetchWebsiteIcons = true;
        Assert.Same(images[0], await icons.GetFaviconAsync(Web(), 48)); Assert.Equal(2, handler.Requests.Count);
        icons.Refresh(Web());
        Assert.NotNull(await icons.GetFaviconAsync(Web(), 48)); Assert.Equal(4, handler.Requests.Count);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task MemoryAndDiskCachesExpireAndRefreshBypassesBoth(bool success)
    {
        await using var log = new TestLogging();
        var now = DateTimeOffset.UtcNow;
        Uri url = success ? Origin : new("https://fail.test");
        var handler = new FaviconFixtureHandler();
        using (var service = Service(log, handler, () => now))
        {
            Assert.Equal(success, await service.GetAsync(url, 48, default) is not null);
            int requests = handler.Requests.Count;
            Assert.Equal(success, await service.GetAsync(url, 48, default) is not null);
            Assert.Equal(requests, handler.Requests.Count);
        }
        handler = new();
        using (var service = Service(log, handler, () => now))
        {
            Assert.Equal(success, await service.GetAsync(url, 48, default) is not null); Assert.Empty(handler.Requests);
            service.Refresh(url);
            Assert.Equal(success, await service.GetAsync(url, 48, default) is not null); Assert.Equal(2, handler.Requests.Count);
            now = now.AddDays(success ? 30 : 1).AddSeconds(1);
            Assert.Equal(success, await service.GetAsync(url, 48, default) is not null); Assert.Equal(4, handler.Requests.Count);
        }
        handler = new(); now = now.AddDays(success ? 30 : 1).AddSeconds(1);
        using (var service = Service(log, handler, () => now))
        {
            Assert.Equal(success, await service.GetAsync(url, 48, default) is not null); Assert.Equal(2, handler.Requests.Count);
        }
    }

    [Theory]
    [InlineData("file:///C:/private.png")]
    [InlineData("ftp://large.test/icon")]
    [InlineData("http://localhost/icon.png")]
    [InlineData("http://127.0.0.1/icon.png")]
    [InlineData("http://192.168.1.1/icon.png")]
    [InlineData("https://user:password@assets.test/icon.png")]
    public async Task ForbiddenRedirectsAreNeverRequested(string location)
    {
        await using var log = new TestLogging();
        var handler = new FaviconFixtureHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new(location) } }));
        using var service = Service(log, handler);
        Assert.Null(await service.GetAsync(Origin, 48, default));
        Assert.Equal(2, handler.Requests.Count); Assert.All(handler.Requests, u => Assert.Equal("large.test", u.Host));
    }

    [Theory]
    [InlineData("http://localhost/")] [InlineData("http://LOCALHOST.:8080/")] [InlineData("http://icons.localhost/")]
    [InlineData("http://127.0.0.1/")] [InlineData("http://127.255.255.255/")] [InlineData("http://2130706433/")]
    [InlineData("http://10.0.0.1/")] [InlineData("http://172.16.0.1/")] [InlineData("http://172.31.255.255/")]
    [InlineData("http://192.168.1.1/")] [InlineData("http://169.254.1.1/")] [InlineData("http://0.0.0.0/")]
    [InlineData("http://[::1]/")] [InlineData("http://[::]/")] [InlineData("http://[::1%251]/")]
    [InlineData("http://[fc00::1]/")] [InlineData("http://[fdff:ffff::1]/")]
    [InlineData("http://[fe80::1]/")] [InlineData("http://[febf:ffff::1]/")]
    [InlineData("http://[::ffff:127.0.0.1]/")] [InlineData("http://[::ffff:192.168.1.1]/")]
    [InlineData("http://[::ffff:0.0.0.0]/")]
    public void PublicItemsRejectLocalHostsButIntranetItemsAllowThem(string target)
    {
        var uri = new Uri(target);
        Assert.False(FaviconParser.Allowed(Origin, uri));
        Assert.True(FaviconParser.Allowed(new("http://192.168.1.10/"), uri));
        Assert.True(FaviconParser.Allowed(new("http://localhost/"), uri));
        Assert.True(FaviconParser.Allowed(uri, uri));
    }

    [Theory]
    [InlineData("http://assets.test:8080/")] [InlineData("https://www.large.test/")]
    [InlineData("http://192.167.255.255/")] [InlineData("http://192.169.0.1/")]
    [InlineData("http://172.15.255.255/")] [InlineData("http://172.32.0.1/")]
    [InlineData("http://169.253.255.255/")] [InlineData("http://169.255.0.1/")]
    [InlineData("http://[2001:db8::1]/")] [InlineData("http://[fbff::1]/")] [InlineData("http://[fec0::1]/")]
    [InlineData("http://[::ffff:8.8.8.8]/")]
    public void PublicTargetsAllowCrossHostPortAndSchemeChanges(string target) => Assert.True(FaviconParser.Allowed(Origin, new(target)));

    [Fact]
    public async Task ApexRedirectUsesFinalPagesIconAndKeepsOriginalCacheIdentity()
    {
        await using var log = new TestLogging();
        var handler = new FaviconFixtureHandler((request, _) => Task.FromResult(request.RequestUri!.AbsoluteUri switch
        {
            "https://large.test/page" => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new("https://www.large.test/moved/page") } },
            "https://www.large.test/moved/page" => FaviconFixtureHandler.Html("<link rel=icon href=icon.png>"),
            "https://www.large.test/moved/icon.png" => FaviconFixtureHandler.Image(FaviconFixtureHandler.Png(64), "image/png"),
            _ => new(HttpStatusCode.NotFound)
        }));
        using var service = Service(log, handler);
        var image = await service.GetAsync(new("https://large.test/page"), 48, default);
        Assert.NotNull(image);
        Assert.Equal(new[] { "https://large.test/page", "https://www.large.test/moved/page", "https://www.large.test/moved/icon.png" },
            handler.Requests.Select(u => u.AbsoluteUri));
        Assert.Same(image, await service.GetAsync(new("https://large.test/another"), 48, default));
        Assert.Equal(3, handler.Requests.Count);
        Assert.True(File.Exists(Path.Combine(_directory, FaviconService.CacheKey("https://large.test", 48) + ".favicon")));
        Assert.False(File.Exists(Path.Combine(_directory, FaviconService.CacheKey("https://www.large.test", 48) + ".favicon")));
    }

    [Theory]
    [InlineData("https://assets.test/icon.png", "")]
    [InlineData("//assets.test/icon.png", "")]
    [InlineData("icon.png", "<base href='https://assets.test/'>")]
    public async Task PageReferencedCdnCandidateIsUsed(string href, string basis)
    {
        await using var log = new TestLogging();
        var handler = new FaviconFixtureHandler((request, _) => Task.FromResult(request.RequestUri!.AbsoluteUri switch
        {
            "https://large.test/" => FaviconFixtureHandler.Html($"{basis}<link rel=apple-touch-icon href='{href}' sizes=64x64>"),
            "https://assets.test/icon.png" => FaviconFixtureHandler.Image(FaviconFixtureHandler.Png(64), "image/png"),
            _ => new(HttpStatusCode.NotFound)
        }));
        using var service = Service(log, handler);
        Assert.NotNull(await service.GetAsync(Origin, 48, default));
        Assert.Equal(new[] { "https://large.test/", "https://assets.test/icon.png" }, handler.Requests.Select(u => u.AbsoluteUri));
    }

    [Theory]
    [InlineData("http://127.0.0.1/x.png")] [InlineData("http://192.168.1.1/x.png")]
    [InlineData("http://[fc00::1]/x.png")] [InlineData("https://user:password@assets.test/x.png")]
    public async Task PublicPageSkipsLocalOrUserinfoCandidates(string target)
    {
        await using var log = new TestLogging();
        var handler = new FaviconFixtureHandler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath == "/"
            ? FaviconFixtureHandler.Html($"<link rel=icon href='{target}'>")
            : FaviconFixtureHandler.Image(FaviconFixtureHandler.Ico(16), "image/x-icon")));
        using var service = Service(log, handler);
        Assert.NotNull(await service.GetAsync(Origin, 48, default));
        Assert.Equal(new[] { "https://large.test/", "https://large.test/favicon.ico" }, handler.Requests.Select(u => u.AbsoluteUri));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task EveryPageAndIconRedirectHopKeepsOriginalPublicHostGuard(bool iconRedirect)
    {
        await using var log = new TestLogging();
        var handler = new FaviconFixtureHandler((request, _) => Task.FromResult(request.RequestUri!.AbsoluteUri switch
        {
            "https://large.test/" when iconRedirect => FaviconFixtureHandler.Html("<link rel=icon href='https://assets.test/icon.png'>"),
            "https://large.test/" => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new("https://assets.test/page") } },
            "https://large.test/favicon.ico" => new(HttpStatusCode.NotFound),
            _ => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new("http://localhost/secret") } }
        }));
        using var service = Service(log, handler);
        Assert.Null(await service.GetAsync(Origin, 48, default));
        Assert.Equal(new[] { "https://large.test/", iconRedirect ? "https://assets.test/icon.png" : "https://assets.test/page", "https://large.test/favicon.ico" },
            handler.Requests.Select(u => u.AbsoluteUri));
    }

    [Theory]
    [InlineData("http://192.168.1.10/")] [InlineData("http://localhost/")] [InlineData("http://[fd00::10]/")]
    public async Task IntranetItemCanLoadItsOwnFallback(string address)
    {
        await using var log = new TestLogging();
        var handler = new FaviconFixtureHandler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath == "/"
            ? FaviconFixtureHandler.Html("") : FaviconFixtureHandler.Image(FaviconFixtureHandler.Ico(16), "image/x-icon")));
        using var service = Service(log, handler);
        Assert.NotNull(await service.GetAsync(new(address), 48, default));
        Assert.Equal(new[] { address, address + "favicon.ico" }, handler.Requests.Select(u => u.AbsoluteUri));
    }

    [Theory]
    [InlineData("html", true)] [InlineData("html", false)] [InlineData("text", false)] [InlineData("404", false)]
    public async Task FallbacksTryFinalOriginBeforeOriginalEvenWithoutHtml(string pageType, bool finalIconExists)
    {
        await using var log = new TestLogging();
        var handler = new FaviconFixtureHandler((request, _) => Task.FromResult(request.RequestUri!.AbsoluteUri switch
        {
            "https://large.test/" => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new("https://www.large.test/page") } },
            "https://www.large.test/page" => pageType switch
            {
                "html" => FaviconFixtureHandler.Html("<link rel=icon href='/favicon.ico'><link rel=icon href='https://large.test/favicon.ico'>"),
                "text" => FaviconFixtureHandler.Image(Encoding.UTF8.GetBytes("<link rel=icon href='/not-html.png'>"), "text/plain"),
                _ => new(HttpStatusCode.NotFound)
            },
            "https://www.large.test/favicon.ico" when !finalIconExists => new(HttpStatusCode.NotFound),
            _ => FaviconFixtureHandler.Image(FaviconFixtureHandler.Ico(16), "image/x-icon")
        }));
        using var service = Service(log, handler);
        Assert.NotNull(await service.GetAsync(Origin, 48, default));
        var expected = new[] { "https://large.test/", "https://www.large.test/page", "https://www.large.test/favicon.ico", "https://large.test/favicon.ico" };
        Assert.Equal(expected.Take(finalIconExists ? 3 : 4), handler.Requests.Select(u => u.AbsoluteUri));
    }

    [Fact]
    public async Task HttpsPageMayRedirectToHttpWithoutCookiesOrUserinfo()
    {
        await using var log = new TestLogging();
        var handler = new FaviconFixtureHandler((request, _) =>
        {
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.False(request.Headers.Contains("Authorization"));
            return Task.FromResult(request.RequestUri!.AbsoluteUri switch
            {
                "https://large.test/" => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new("http://www.large.test/page") } },
                "http://www.large.test/page" => FaviconFixtureHandler.Html("<link rel=icon href='/icon.png'>"),
                "http://www.large.test/icon.png" => FaviconFixtureHandler.Image(FaviconFixtureHandler.Png(64), "image/png"),
                _ => new(HttpStatusCode.NotFound)
            });
        });
        using var service = Service(log, handler);
        Assert.NotNull(await service.GetAsync(Origin, 48, default));
        Assert.Equal(new[] { "https://large.test/", "http://www.large.test/page", "http://www.large.test/icon.png" }, handler.Requests.Select(u => u.AbsoluteUri));
    }

    [Fact]
    public async Task RedirectLimitAndFinalResponseRelativeResolutionAreEnforced()
    {
        await using var log = new TestLogging();
        var handler = new FaviconFixtureHandler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
        { Headers = { Location = new(request.RequestUri!, "next") } }));
        using (var service = Service(log, handler))
        {
            Assert.Null(await service.GetAsync(Origin, 48, default)); Assert.Equal(12, handler.Requests.Count); // Six hops each for HTML and fallback.
        }
        handler = new((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            "/page" => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new("/moved/page", UriKind.Relative) } },
            "/moved/page" => FaviconFixtureHandler.Html("<link rel=icon href=icon.png>"),
            "/moved/icon.png" => FaviconFixtureHandler.Image(FaviconFixtureHandler.Png(64), "image/png"),
            _ => new(HttpStatusCode.NotFound)
        }));
        using (var service = Service(log, handler))
        {
            service.Refresh(Origin);
            Assert.NotNull(await service.GetAsync(new("https://large.test/page"), 48, default));
            Assert.Equal(new[] { "/page", "/moved/page", "/moved/icon.png" }, handler.Requests.Select(u => u.AbsolutePath));
        }
    }

    [Fact]
    public async Task OnlyHtmlIsParsedAndOnlyImagesAreDecoded()
    {
        await using var log = new TestLogging();
        var handler = new FaviconFixtureHandler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath == "/"
            ? FaviconFixtureHandler.Image(Encoding.UTF8.GetBytes("<link rel=icon href=/secret.png>"), "text/plain")
            : FaviconFixtureHandler.Image(FaviconFixtureHandler.Png(64), "application/octet-stream")));
        using var service = Service(log, handler);
        Assert.Null(await service.GetAsync(Origin, 48, default));
        Assert.Equal(new[] { "/", "/favicon.ico" }, handler.Requests.Select(u => u.AbsolutePath));
    }

    [Fact]
    public async Task UndecodableCandidatesFallThroughToIcoAndFirstDecodedCandidateWins()
    {
        await using var log = new TestLogging();
        var handler = new FaviconFixtureHandler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            "/" => FaviconFixtureHandler.Html("<link rel=icon href=/vector.svg sizes=any><link rel=icon href=/bad.webp sizes=48x48><link rel=icon href=/good.png sizes=64x64><link rel=icon href=/later.png sizes=128x128>"),
            "/bad.webp" => FaviconFixtureHandler.Image([0, 1, 2], "image/webp"),
            _ => FaviconFixtureHandler.Image(FaviconFixtureHandler.Png(64), "image/png")
        }));
        using (var service = Service(log, handler))
        {
            Assert.NotNull(await service.GetAsync(Origin, 48, default));
            Assert.Equal(new[] { "/", "/bad.webp", "/good.png" }, handler.Requests.Select(u => u.AbsolutePath));
        }
        handler = new((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath == "/favicon.ico"
            ? FaviconFixtureHandler.Image(FaviconFixtureHandler.Ico(16), "image/x-icon")
            : FaviconFixtureHandler.Html("<link rel=icon href=/bad.png>")));
        using (var service = Service(log, handler))
        {
            service.Refresh(Origin);
            var image = await service.GetAsync(Origin, 96, default);
            Assert.NotNull(image); Assert.Equal(96, image.PixelWidth);
            Assert.Equal(new[] { "/", "/bad.png", "/favicon.ico" }, handler.Requests.Select(u => u.AbsolutePath));
        }
    }

    [Fact]
    public async Task RefreshCancelsOldDiscoveryAndSerializesItsReplacement()
    {
        await using var log = new TestLogging();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0, active = 0, maximum = 0;
        var handler = new FaviconFixtureHandler(async (request, token) =>
        {
            int count = Interlocked.Increment(ref active); maximum = Math.Max(maximum, count);
            try
            {
                if (Interlocked.Increment(ref requests) == 1)
                { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); }
                return FaviconFixtureHandler.Respond(request.RequestUri!);
            }
            finally { Interlocked.Decrement(ref active); }
        });
        using var service = Service(log, handler);
        var first = service.GetAsync(Origin, 48, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        service.Refresh(Origin);
        var second = service.GetAsync(Origin, 48, default);
        Assert.Null(await first); Assert.NotNull(await second);
        Assert.Equal(1, maximum); Assert.Equal(3, requests);
        Assert.Same(await second, await service.GetAsync(Origin, 48, default));
    }

    [Fact]
    public async Task DiscoveryLogsOnceWithOriginAndNeverQueryOrPagePath()
    {
        var log = new TestLogging();
        var handler = new FaviconFixtureHandler((request, _) => Task.FromResult(request.RequestUri!.Host switch
        {
            "large.test" => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new("https://www.large.test/private-page?redirect-token=456") } },
            "www.large.test" => FaviconFixtureHandler.Html("<link rel=icon href='https://assets.test/icon.png?cdn-token=789'>"),
            _ => FaviconFixtureHandler.Image(FaviconFixtureHandler.Png(64), "image/png")
        }));
        using (var service = Service(log, handler))
        {
            var url = new Uri("https://large.test/private-page?secret-token=123");
            Assert.NotNull(await service.GetAsync(url, 48, default));
            Assert.NotNull(await service.GetAsync(url, 48, default));
        }
        await log.DisposeAsync();
        string line = TestLogging.Clef(Assert.Single(log.Events));
        Assert.Equal(3, handler.Requests.Count);
        Assert.DoesNotContain("private-page", line); Assert.DoesNotContain("secret-token", line);
        Assert.DoesNotContain("redirect-token", line); Assert.DoesNotContain("cdn-token", line);
        Assert.DoesNotContain("www.large.test", line); Assert.DoesNotContain("assets.test", line);
        using var json = JsonDocument.Parse(line);
        var data = json.RootElement;
        Assert.Equal("favicon.result", data.GetProperty("EventId").GetProperty("Name").GetString());
        Assert.Equal("https://large.test", data.GetProperty("origin").GetString());
        Assert.Equal("success", data.GetProperty("outcome").GetString());
        Assert.True(data.GetProperty("ms").GetInt64() >= 0);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ByteLimitsApplyEvenWithoutContentLength(bool declaredLength)
    {
        await using var log = new TestLogging();
        var html = new CountedStream(Encoding.UTF8.GetBytes(new string(' ', FaviconService.HtmlLimit) + "<link rel=icon href=/too-late.png>"));
        var image = new CountedStream(new byte[FaviconService.ImageLimit + 100]);
        var handler = new FaviconFixtureHandler((request, _) =>
        {
            bool page = request.RequestUri!.AbsolutePath == "/";
            var content = new StreamContent(page ? html : image);
            content.Headers.ContentType = new(page ? "text/html" : "image/png");
            if (declaredLength) content.Headers.ContentLength = page ? FaviconService.HtmlLimit + 45 : FaviconService.ImageLimit + 100;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        using var service = Service(log, handler);
        Assert.Null(await service.GetAsync(Origin, 48, default));
        Assert.Equal(FaviconService.HtmlLimit, html.BytesRead);
        Assert.Equal(declaredLength ? 0 : FaviconService.ImageLimit + 1, image.BytesRead);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RequestAndSiteTimeoutsAreCached(bool overall)
    {
        await using var log = new TestLogging();
        var handler = new FaviconFixtureHandler(async (_, cancellation) =>
        {
            await Task.Delay(Timeout.Infinite, cancellation); return new(HttpStatusCode.OK);
        });
        using var service = Service(log, handler, requestTimeout: TimeSpan.FromMilliseconds(overall ? 2000 : 40),
            siteTimeout: TimeSpan.FromMilliseconds(overall ? 40 : 2000));
        Assert.Null(await service.GetAsync(Origin, 48, default).WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Null(await service.GetAsync(Origin, 48, default)); Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task TurningOffCancelsDiscoveryWithoutCachingCancellation()
    {
        await using var log = new TestLogging();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FaviconFixtureHandler(async (_, token) =>
        {
            entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return new(HttpStatusCode.OK);
        });
        using var service = Service(log, handler);
        var pending = service.GetAsync(Origin, 48, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); service.Enabled = false;
        Assert.Null(await pending); Assert.Empty(Directory.GetFiles(_directory, "*.favicon"));
        Assert.Null(await service.GetAsync(Origin, 48, default)); Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task AtMostFourSitesAreActiveAndDifferentSizesSharePendingOriginDiscovery()
    {
        await using var log = new TestLogging();
        var four = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0, maximum = 0;
        var handler = new FaviconFixtureHandler(async (request, token) =>
        {
            int count = Interlocked.Increment(ref active);
            lock (four) { maximum = Math.Max(maximum, count); if (count == 4) four.TrySetResult(); }
            await release.Task.WaitAsync(token); Interlocked.Decrement(ref active);
            return request.RequestUri!.AbsolutePath == "/" ? FaviconFixtureHandler.Html("<link rel=icon href=/icon.png>")
                : FaviconFixtureHandler.Image(FaviconFixtureHandler.Png(64), "image/png");
        });
        using var service = Service(log, handler);
        var tasks = Enumerable.Range(0, 8).Select(i => service.GetAsync(new($"https://site{i}.test"), 48, default)).ToArray();
        await four.Task.WaitAsync(TimeSpan.FromSeconds(3)); Assert.Equal(4, handler.Requests.Count);
        var anotherSize = service.GetAsync(new("https://site0.test"), 96, default);
        release.SetResult();
        Assert.All(await Task.WhenAll(tasks.Append(anotherSize)), image => Assert.NotNull(image));
        Assert.InRange(maximum, 1, 4);
        // A different size either joins the pending source or runs afterwards, never concurrently for that origin.
        Assert.Equal(96, (await anotherSize)!.PixelWidth);
    }

    [Theory]
    [InlineData(48, 1)] [InlineData(64, 2)] [InlineData(96, 3)]
    public void SmallIconHasWhitePlateAndExactIntegerPixels(int size, int multiple)
    {
        byte[] solid = Enumerable.Range(0, 16 * 16).SelectMany(_ => new byte[] { 32, 24, 16, 255 }).ToArray();
        var source = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgra32, null, solid, 64);
        var image = IconService.RenderFavicon(source, size);
        Assert.Equal(size, image.PixelWidth); Assert.Equal(size, image.PixelHeight); Assert.True(image.IsFrozen);
        int start = (size - 16 * multiple) / 2;
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(image, start - 1, size / 2));
        Assert.Equal(new byte[] { 32, 24, 16, 255 }, Pixel(image, start, size / 2));
        Assert.Equal(new byte[] { 32, 24, 16, 255 }, Pixel(image, start + 16 * multiple - 1, size / 2));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(image, start + 16 * multiple, size / 2));
        Assert.Equal(0, Pixel(image, 0, 0)[3]);
    }

    [Fact]
    public void LargeSourceFillsBoxWithoutPlate()
    {
        var image = IconService.RenderFavicon(FaviconFixtureHandler.Source(128), 48);
        Assert.Equal(48, image.PixelWidth); Assert.Equal(48, image.PixelHeight);
        Assert.Equal(new byte[] { 208, 112, 48, 255 }, Pixel(image, 0, 0));
    }

    [Fact]
    public void SettingIsOptionalAndDefaultIsOmitted()
    {
        var config = AppConfig.CreateDefault();
        Assert.DoesNotContain("autoFetchWebsiteIcons", JsonSerializer.Serialize(config, ConfigJsonContext.Default.AppConfig));
        config = config with { AutoFetchWebsiteIcons = false };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(config, ConfigJsonContext.Default.AppConfig);
        Assert.False(ConfigValidation.Parse(bytes).AutoFetchWebsiteIcons);
        Assert.Null(ConfigValidation.Normalize(config with { AutoFetchWebsiteIcons = true }).AutoFetchWebsiteIcons);
    }

    private static byte[] Pixel(BitmapSource image, int x, int y)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        byte[] pixel = new byte[4]; converted.CopyPixels(new(x, y, 1, 1), pixel, 4, 0); return pixel;
    }
    private sealed class CountedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
        internal int BytesRead;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = base.Read(buffer.Span); BytesRead += read; return ValueTask.FromResult(read);
        }
    }
    public void Dispose()
    {
        if (!Directory.Exists(_directory)) return;
        foreach (string file in Directory.GetFiles(_directory)) File.Delete(file);
        Directory.Delete(_directory);
    }
}
