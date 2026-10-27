using System.Net;
using System.Text;
using FateAndFridge.Api.Imports;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Fetch pipeline tests (docs/M4_PLAN.md §1.13–§1.14). All transports are
/// stubbed or loopback; no public network access occurs.
/// </summary>
public sealed class SafeUrlFetcherTests
{
    private static readonly IPAddress PublicAddress = IPAddress.Parse("93.184.216.34");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ImportFetchOptions DefaultOptions() => new()
    {
        ReadTimeout = TimeSpan.FromSeconds(2),
        ConnectTimeout = TimeSpan.FromSeconds(1),
    };

    private static SafeUrlFetcher CreateFetcher(
        StubHttpMessageHandler handler,
        IDnsResolver? resolver = null,
        ImportFetchOptions? options = null) =>
        new(
            NullLogger<SafeUrlFetcher>.Instance,
            options ?? DefaultOptions(),
            resolver ?? new FakeDnsResolver(PublicAddress),
            () => handler);

    private static StubHttpMessageHandler HandlerFor(
        Dictionary<string, Func<HttpResponseMessage>> routes) =>
        new(request => routes.TryGetValue(request.RequestUri!.AbsolutePath, out var factory)
            ? factory()
            : ImportTestResponses.Content(HttpStatusCode.NotFound, "text/plain", []));

    [Fact]
    public async Task FetchPageAsync_returns_html_content_and_final_url()
    {
        var resolver = new FakeDnsResolver(PublicAddress);
        var handler = new StubHttpMessageHandler(_ => ImportTestResponses.Html("<html>hello</html>"));
        using var fetcher = CreateFetcher(handler, resolver);

        var result = await fetcher.FetchPageAsync("https://example.com/recipe", Ct);

        result.IsSuccess.Should().BeTrue();
        result.FailureReason.Should().Be(ImportFetchFailureReason.None);
        result.StatusCode.Should().Be(200);
        result.ContentType.Should().Be("text/html");
        Encoding.UTF8.GetString(result.Content!).Should().Contain("hello");
        result.FinalUrl!.AbsoluteUri.Should().Be("https://example.com/recipe");
        resolver.QueriedHosts.Should().ContainSingle().Which.Should().Be("example.com");
    }

    [Fact]
    public async Task FetchImageAsync_returns_image_content()
    {
        var body = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var handler = new StubHttpMessageHandler(_ =>
            ImportTestResponses.Content(HttpStatusCode.OK, "image/png", body));
        using var fetcher = CreateFetcher(handler);

        var result = await fetcher.FetchImageAsync("https://cdn.example.com/cat.png", Ct);

        result.IsSuccess.Should().BeTrue();
        result.ContentType.Should().Be("image/png");
        result.Content.Should().Equal(body);
    }

    [Fact]
    public async Task FetchPageAsync_happy_path_over_loopback_server()
    {
        await using var server = new LoopbackHttpServer();
        server.MapHtml("/recipe", "<html><body>loopback ok</body></html>");
        using var fetcher = new SafeUrlFetcher(
            NullLogger<SafeUrlFetcher>.Instance,
            DefaultOptions(),
            new FakeDnsResolver(PublicAddress),
            () => new LoopbackForwardingHandler(server.BaseUri));

        var result = await fetcher.FetchPageAsync("http://example.com/recipe", Ct);

        result.IsSuccess.Should().BeTrue();
        result.FinalUrl!.AbsolutePath.Should().Be("/recipe");
        Encoding.UTF8.GetString(result.Content!).Should().Contain("loopback ok");
    }

    [Fact]
    public async Task FetchPageAsync_uses_validated_ip_and_sends_identifying_user_agent_without_credentials()
    {
        var handler = new StubHttpMessageHandler(_ => ImportTestResponses.Html("ok"));
        using var fetcher = CreateFetcher(handler);

        await fetcher.FetchPageAsync("https://example.com/recipe", Ct);

        handler.Requests.Should().ContainSingle();
        var request = handler.Requests[0];
        request.UserAgent.Should().Contain("FateAndFridge");
        request.Authorization.Should().BeNullOrEmpty();
        request.Cookie.Should().BeNullOrEmpty();
        request.AcceptEncoding.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task FetchPageAsync_does_not_resolve_dns_for_public_ip_literal()
    {
        var resolver = new FakeDnsResolver(_ => throw new InvalidOperationException("DNS must not be used"));
        var handler = new StubHttpMessageHandler(_ => ImportTestResponses.Html("ok"));
        using var fetcher = CreateFetcher(handler, resolver);

        var result = await fetcher.FetchPageAsync("http://8.8.8.8/recipe", Ct);

        result.IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData("ftp://example.com/recipe")]
    [InlineData("file:///etc/passwd")]
    [InlineData("not-a-url")]
    public async Task FetchPageAsync_rejects_invalid_url_without_requesting(string url)
    {
        var handler = new StubHttpMessageHandler(_ => ImportTestResponses.Html("ok"));
        using var fetcher = CreateFetcher(handler);

        var result = await fetcher.FetchPageAsync(url, Ct);

        result.FailureReason.Should().Be(ImportFetchFailureReason.InvalidUrl);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task FetchPageAsync_maps_dns_failure()
    {
        var handler = new StubHttpMessageHandler(_ => ImportTestResponses.Html("ok"));
        using var fetcher = CreateFetcher(handler, new FailingDnsResolver());

        var result = await fetcher.FetchPageAsync("https://unresolvable.example.com/", Ct);

        result.FailureReason.Should().Be(ImportFetchFailureReason.DnsFailure);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task FetchPageAsync_blocks_resolved_private_address()
    {
        var handler = new StubHttpMessageHandler(_ => ImportTestResponses.Html("ok"));
        using var fetcher = CreateFetcher(handler, new FakeDnsResolver(IPAddress.Parse("10.0.0.1")));

        var result = await fetcher.FetchPageAsync("https://internal.example.com/", Ct);

        result.FailureReason.Should().Be(ImportFetchFailureReason.BlockedTarget);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task FetchPageAsync_fails_closed_when_resolution_mixes_public_and_private()
    {
        var handler = new StubHttpMessageHandler(_ => ImportTestResponses.Html("ok"));
        using var fetcher = CreateFetcher(
            handler,
            new FakeDnsResolver(IPAddress.Parse("8.8.8.8"), IPAddress.Parse("127.0.0.1")));

        var result = await fetcher.FetchPageAsync("https://example.com/", Ct);

        result.FailureReason.Should().Be(ImportFetchFailureReason.BlockedTarget);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task FetchPageAsync_enforces_html_size_ceiling_from_content_length()
    {
        var handler = new StubHttpMessageHandler(_ =>
            ImportTestResponses.Html(new string('x', 512)));
        using var fetcher = CreateFetcher(handler, options: new ImportFetchOptions
        {
            ReadTimeout = TimeSpan.FromSeconds(2),
            MaxHtmlBytes = 128,
        });

        var result = await fetcher.FetchPageAsync("https://example.com/big", Ct);

        result.FailureReason.Should().Be(ImportFetchFailureReason.TooLarge);
    }

    [Fact]
    public async Task FetchPageAsync_enforces_html_size_ceiling_without_content_length()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnknownLengthContent("text/html", Encoding.UTF8.GetBytes(new string('x', 512))),
        });
        using var fetcher = CreateFetcher(handler, options: new ImportFetchOptions
        {
            ReadTimeout = TimeSpan.FromSeconds(2),
            MaxHtmlBytes = 128,
        });

        var result = await fetcher.FetchPageAsync("https://example.com/chunked", Ct);

        result.FailureReason.Should().Be(ImportFetchFailureReason.TooLarge);
    }

    [Fact]
    public async Task FetchPageAsync_enforces_size_ceiling_when_content_length_lies()
    {
        var handler = new StubHttpMessageHandler(_ => ImportTestResponses.Content(
            HttpStatusCode.OK,
            "text/html",
            Encoding.UTF8.GetBytes(new string('x', 512)),
            declaredLength: 10));
        using var fetcher = CreateFetcher(handler, options: new ImportFetchOptions
        {
            ReadTimeout = TimeSpan.FromSeconds(2),
            MaxHtmlBytes = 128,
        });

        var result = await fetcher.FetchPageAsync("https://example.com/lying", Ct);

        result.FailureReason.Should().Be(ImportFetchFailureReason.TooLarge);
    }

    [Fact]
    public async Task FetchImageAsync_enforces_image_size_ceiling()
    {
        var handler = new StubHttpMessageHandler(_ => ImportTestResponses.Content(
            HttpStatusCode.OK,
            "image/png",
            new byte[512]));
        using var fetcher = CreateFetcher(handler, options: new ImportFetchOptions
        {
            ReadTimeout = TimeSpan.FromSeconds(2),
            MaxImageBytes = 256,
        });

        var result = await fetcher.FetchImageAsync("https://example.com/big.png", Ct);

        result.FailureReason.Should().Be(ImportFetchFailureReason.TooLarge);
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("text/plain")]
    public async Task FetchPageAsync_rejects_disallowed_content_types(string contentType)
    {
        var handler = new StubHttpMessageHandler(_ =>
            ImportTestResponses.Content(HttpStatusCode.OK, contentType, Encoding.UTF8.GetBytes("{}")));
        using var fetcher = CreateFetcher(handler);

        var result = await fetcher.FetchPageAsync("https://example.com/data", Ct);

        result.FailureReason.Should().Be(ImportFetchFailureReason.UnsupportedContentType);
        result.ContentType.Should().Be(contentType);
    }

    [Fact]
    public async Task FetchPageAsync_rejects_missing_content_type()
    {
        var handler = new StubHttpMessageHandler(_ =>
            ImportTestResponses.Content(HttpStatusCode.OK, null, Encoding.UTF8.GetBytes("<html></html>")));
        using var fetcher = CreateFetcher(handler);

        var result = await fetcher.FetchPageAsync("https://example.com/no-type", Ct);

        result.FailureReason.Should().Be(ImportFetchFailureReason.UnsupportedContentType);
    }

    [Fact]
    public async Task FetchImageAsync_rejects_html_content_type()
    {
        var handler = new StubHttpMessageHandler(_ => ImportTestResponses.Html("<html></html>"));
        using var fetcher = CreateFetcher(handler);

        var result = await fetcher.FetchImageAsync("https://example.com/not-an-image", Ct);

        result.FailureReason.Should().Be(ImportFetchFailureReason.UnsupportedContentType);
    }

    [Fact]
    public async Task FetchPageAsync_follows_relative_redirect()
    {
        var handler = HandlerFor(new Dictionary<string, Func<HttpResponseMessage>>
        {
            ["/start"] = () => ImportTestResponses.Redirect(HttpStatusCode.Found, "/final"),
            ["/final"] = () => ImportTestResponses.Html("final"),
        });
        using var fetcher = CreateFetcher(handler);

        var result = await fetcher.FetchPageAsync("https://example.com/start", Ct);

        result.IsSuccess.Should().BeTrue();
        result.FinalUrl!.AbsolutePath.Should().Be("/final");
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task FetchPageAsync_revalidates_redirect_targets_against_the_policy()
    {
        var handler = HandlerFor(new Dictionary<string, Func<HttpResponseMessage>>
        {
            ["/start"] = () => ImportTestResponses.Redirect(
                HttpStatusCode.Found,
                "http://169.254.169.254/latest/meta-data"),
        });
        using var fetcher = CreateFetcher(handler);

        var result = await fetcher.FetchPageAsync("https://example.com/start", Ct);

        result.FailureReason.Should().Be(ImportFetchFailureReason.BlockedTarget);
        handler.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task FetchPageAsync_rejects_redirect_to_blocked_host_name()
    {
        var handler = HandlerFor(new Dictionary<string, Func<HttpResponseMessage>>
        {
            ["/start"] = () => ImportTestResponses.Redirect(HttpStatusCode.Found, "http://localhost/"),
        });
        using var fetcher = CreateFetcher(handler);

        var result = await fetcher.FetchPageAsync("https://example.com/start", Ct);

        result.FailureReason.Should().Be(ImportFetchFailureReason.BlockedTarget);
    }

    [Fact]
    public async Task FetchPageAsync_rejects_redirect_to_other_scheme()
    {
        var handler = HandlerFor(new Dictionary<string, Func<HttpResponseMessage>>
        {
            ["/start"] = () => ImportTestResponses.Redirect(HttpStatusCode.Found, "file:///etc/passwd"),
        });
        using var fetcher = CreateFetcher(handler);

        var result = await fetcher.FetchPageAsync("https://example.com/start", Ct);

        result.FailureReason.Should().Be(ImportFetchFailureReason.InvalidUrl);
    }

    [Fact]
    public async Task FetchPageAsync_enforces_max_redirects()
    {
        var handler = HandlerFor(new Dictionary<string, Func<HttpResponseMessage>>
        {
            ["/start"] = () => ImportTestResponses.Redirect(HttpStatusCode.Found, "/loop"),
            ["/loop"] = () => ImportTestResponses.Redirect(HttpStatusCode.Found, "/loop"),
        });
        using var fetcher = CreateFetcher(handler, options: new ImportFetchOptions
        {
            ReadTimeout = TimeSpan.FromSeconds(2),
            MaxRedirects = 5,
        });

        var result = await fetcher.FetchPageAsync("https://example.com/start", Ct);

        result.FailureReason.Should().Be(ImportFetchFailureReason.TooManyRedirects);
    }

    [Fact]
    public async Task FetchPageAsync_rejects_redirect_without_location()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Found));
        using var fetcher = CreateFetcher(handler);

        var result = await fetcher.FetchPageAsync("https://example.com/start", Ct);

        result.FailureReason.Should().Be(ImportFetchFailureReason.HttpError);
    }

    [Fact]
    public async Task FetchPageAsync_maps_non_success_status()
    {
        var handler = new StubHttpMessageHandler(_ =>
            ImportTestResponses.Content(HttpStatusCode.NotFound, "text/html", Encoding.UTF8.GetBytes("nope")));
        using var fetcher = CreateFetcher(handler);

        var result = await fetcher.FetchPageAsync("https://example.com/missing", Ct);

        result.FailureReason.Should().Be(ImportFetchFailureReason.HttpError);
        result.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task FetchPageAsync_maps_read_timeout()
    {
        var handler = new StubHttpMessageHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            return ImportTestResponses.Html("late");
        });
        using var fetcher = CreateFetcher(handler, options: new ImportFetchOptions
        {
            ReadTimeout = TimeSpan.FromMilliseconds(150),
        });

        var result = await fetcher.FetchPageAsync("https://example.com/slow", Ct);

        result.FailureReason.Should().Be(ImportFetchFailureReason.Timeout);
    }

    [Fact]
    public async Task FetchPageAsync_maps_timeout_across_multiple_hops()
    {
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.AbsolutePath == "/start")
            {
                return ImportTestResponses.Redirect(HttpStatusCode.Found, "/slow");
            }

            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            return ImportTestResponses.Html("late");
        });
        using var fetcher = CreateFetcher(handler, options: new ImportFetchOptions
        {
            ReadTimeout = TimeSpan.FromMilliseconds(200),
        });

        var result = await fetcher.FetchPageAsync("https://example.com/start", Ct);

        result.FailureReason.Should().Be(ImportFetchFailureReason.Timeout);
    }

    [Fact]
    public async Task FetchPageAsync_maps_connect_timeout_to_timeout_reason()
    {
        var handler = new StubHttpMessageHandler(_ =>
            throw new HttpRequestException("connect failed", new TimeoutException("connect timeout")));
        using var fetcher = CreateFetcher(handler);

        var result = await fetcher.FetchPageAsync("https://example.com/slow", Ct);

        result.FailureReason.Should().Be(ImportFetchFailureReason.Timeout);
    }

    [Fact]
    public async Task FetchPageAsync_rejects_redirect_to_hostname_resolving_to_private_address()
    {
        var handler = HandlerFor(new Dictionary<string, Func<HttpResponseMessage>>
        {
            ["/start"] = () => ImportTestResponses.Redirect(
                HttpStatusCode.Found,
                "http://internal.example.com/secret"),
        });
        var resolver = new FakeDnsResolver(host => host == "internal.example.com"
            ? [IPAddress.Parse("10.0.0.7")]
            : [PublicAddress]);
        using var fetcher = CreateFetcher(handler, resolver);

        var result = await fetcher.FetchPageAsync("https://example.com/start", Ct);

        result.FailureReason.Should().Be(ImportFetchFailureReason.BlockedTarget);
        handler.Requests.Should().HaveCount(1);
    }

    [Theory]
    [InlineData("TEXT/HTML; charset=UTF-8")]
    [InlineData("Application/XHTML+XML; charset=utf-8")]
    public async Task FetchPageAsync_accepts_content_type_with_parameters_and_case(string contentType)
    {
        var handler = new StubHttpMessageHandler(_ =>
            ImportTestResponses.Content(
                HttpStatusCode.OK,
                contentType,
                Encoding.UTF8.GetBytes("<html></html>")));
        using var fetcher = CreateFetcher(handler);

        var result = await fetcher.FetchPageAsync("https://example.com/", Ct);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task FetchImageAsync_accepts_uppercase_image_content_type()
    {
        var handler = new StubHttpMessageHandler(_ =>
            ImportTestResponses.Content(HttpStatusCode.OK, "IMAGE/PNG", new byte[] { 1, 2, 3 }));
        using var fetcher = CreateFetcher(handler);

        var result = await fetcher.FetchImageAsync("https://example.com/x.png", Ct);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task FetchPageAsync_fails_closed_when_dns_rebinds_to_private_at_connect_time()
    {
        // Public at the fail-fast pre-check, private when the production pinned
        // transport re-resolves at connect time. No injected handler is used,
        // so this exercises CreatePinnedHandler/ConnectAsync directly.
        var resolver = new StatefulDnsResolver(
            [IPAddress.Parse("93.184.216.34")],
            [IPAddress.Parse("127.0.0.1")]);
        using var fetcher = new SafeUrlFetcher(
            NullLogger<SafeUrlFetcher>.Instance,
            DefaultOptions(),
            resolver,
            handlerFactory: null);

        var result = await fetcher.FetchPageAsync("https://evil.example.com/recipe", Ct);

        result.FailureReason.Should().Be(ImportFetchFailureReason.BlockedTarget);
        resolver.Calls.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void Pinned_handler_disables_proxy_redirects_and_cookies()
    {
        using var fetcher = new SafeUrlFetcher(
            NullLogger<SafeUrlFetcher>.Instance,
            DefaultOptions(),
            new FakeDnsResolver(PublicAddress),
            handlerFactory: null);

        using var handler = fetcher.CreatePinnedHandler();

        handler.UseProxy.Should().BeFalse();
        handler.AllowAutoRedirect.Should().BeFalse();
        handler.UseCookies.Should().BeFalse();
        handler.AutomaticDecompression.Should().Be(DecompressionMethods.None);
        handler.ConnectTimeout.Should().Be(DefaultOptions().ConnectTimeout);
        handler.ConnectCallback.Should().NotBeNull();
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        var handler = new StubHttpMessageHandler(_ => ImportTestResponses.Html("ok"));
        var fetcher = CreateFetcher(handler);

        fetcher.Dispose();
        var act = () => fetcher.Dispose();

        act.Should().NotThrow();
    }
}
