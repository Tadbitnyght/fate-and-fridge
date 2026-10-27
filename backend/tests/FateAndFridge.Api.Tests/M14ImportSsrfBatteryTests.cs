using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FateAndFridge.Api.Imports;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// M14-06 adversarial SSRF battery (docs/M14_PLAN.md §3 M14-06 and §4
/// "M14-05/06 upload/SSRF", docs/SECURITY.md §6–§7). Extends
/// <see cref="ImportUrlPolicyTests"/> and <see cref="SafeUrlFetcherTests"/> with
/// the full loopback/private/link-local/metadata/IPv6-embedded/numeric-form
/// matrix, DNS rebinding across redirect hops, the redirect matrix (blocked
/// targets, cross-scheme, limit, loop), the size/time/content-type ceilings, and
/// content-encoding abuse.
/// </summary>
/// <remarks>
/// Every case asserts two things: the exact structured failure reason, and that
/// the recording handler/resolver never reached the blocked target (no request
/// bytes were sent). All transports are stubbed; no test touches a network.
/// </remarks>
public sealed class M14ImportSsrfBatteryTests
{
    private static readonly IPAddress PublicAddress = IPAddress.Parse("93.184.216.34");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ImportFetchOptions Options(
        int maxRedirects = 5,
        int maxHtmlBytes = 2 * 1024 * 1024,
        int maxImageBytes = 5 * 1024 * 1024,
        TimeSpan? readTimeout = null) => new()
        {
            MaxRedirects = maxRedirects,
            MaxHtmlBytes = maxHtmlBytes,
            MaxImageBytes = maxImageBytes,
            ConnectTimeout = TimeSpan.FromSeconds(1),
            ReadTimeout = readTimeout ?? TimeSpan.FromSeconds(2),
        };

    private static SafeUrlFetcher CreateFetcher(
        StubHttpMessageHandler handler,
        IDnsResolver? resolver = null,
        ImportFetchOptions? options = null) =>
        new(
            NullLogger<SafeUrlFetcher>.Instance,
            options ?? Options(),
            resolver ?? new FakeDnsResolver(PublicAddress),
            () => handler);

    private static StubHttpMessageHandler RedirectHandler(params string[] locations)
    {
        var index = 0;
        return new StubHttpMessageHandler(_ =>
        {
            var location = locations[Math.Min(index, locations.Length - 1)];
            index++;
            return ImportTestResponses.Redirect(HttpStatusCode.Found, location);
        });
    }

    // ---------------------------------------------------------------------
    // 1. Shared policy matrix: host forms and embedded addresses
    // ---------------------------------------------------------------------

    [Theory]
    // Loopback, bare and subdomain names.
    [InlineData("http://127.0.0.1/", ImportUrlBlockReason.AddressBlocked)]
    [InlineData("http://127.1.2.3/", ImportUrlBlockReason.AddressBlocked)]
    [InlineData("http://127.255.255.254/", ImportUrlBlockReason.AddressBlocked)]
    [InlineData("http://[::1]/", ImportUrlBlockReason.AddressBlocked)]
    [InlineData("http://[0:0:0:0:0:0:0:1]/", ImportUrlBlockReason.AddressBlocked)]
    [InlineData("http://localhost/", ImportUrlBlockReason.HostBlocked)]
    [InlineData("http://localhost:8080/recipe", ImportUrlBlockReason.HostBlocked)]
    [InlineData("http://api.localhost/recipe", ImportUrlBlockReason.HostBlocked)]
    [InlineData("http://a.b.localhost/recipe", ImportUrlBlockReason.HostBlocked)]
    // RFC1918.
    [InlineData("http://10.0.0.1/", ImportUrlBlockReason.AddressBlocked)]
    [InlineData("http://172.16.0.1/", ImportUrlBlockReason.AddressBlocked)]
    [InlineData("http://172.31.255.254/", ImportUrlBlockReason.AddressBlocked)]
    [InlineData("http://192.168.1.1/", ImportUrlBlockReason.AddressBlocked)]
    // CGNAT 100.64/10 boundaries.
    [InlineData("http://100.64.0.1/", ImportUrlBlockReason.AddressBlocked)]
    [InlineData("http://100.127.255.255/", ImportUrlBlockReason.AddressBlocked)]
    // Link-local 169.254/16 and fe80::/10.
    [InlineData("http://169.254.1.1/", ImportUrlBlockReason.AddressBlocked)]
    [InlineData("http://169.254.169.254/latest/meta-data/", ImportUrlBlockReason.AddressBlocked)]
    [InlineData("http://[fe80::1]/", ImportUrlBlockReason.AddressBlocked)]
    [InlineData("http://[fe80::abcd:1]/", ImportUrlBlockReason.AddressBlocked)]
    // Cloud metadata names/addresses.
    [InlineData("http://metadata.google.internal/computeMetadata/v1/", ImportUrlBlockReason.HostBlocked)]
    [InlineData("http://metadata/", ImportUrlBlockReason.HostBlocked)]
    [InlineData("http://[fd00:ec2::254]/", ImportUrlBlockReason.AddressBlocked)]
    [InlineData("http://[fd00:ec2::254]/latest/meta-data/", ImportUrlBlockReason.AddressBlocked)]
    // IPv4 embedded in IPv6 and translation/tunnelling prefixes.
    [InlineData("http://[::ffff:127.0.0.1]/", ImportUrlBlockReason.AddressBlocked)]
    [InlineData("http://[::ffff:169.254.169.254]/", ImportUrlBlockReason.AddressBlocked)]
    [InlineData("http://[::ffff:10.1.2.3]/", ImportUrlBlockReason.AddressBlocked)]
    [InlineData("http://[::ffff:192.168.0.1]/", ImportUrlBlockReason.AddressBlocked)]
    [InlineData("http://[64:ff9b::7f00:1]/", ImportUrlBlockReason.AddressBlocked)]
    [InlineData("http://[64:ff9b::a9fe:a9fe]/", ImportUrlBlockReason.AddressBlocked)]
    [InlineData("http://[64:ff9b:1::7f00:1]/", ImportUrlBlockReason.AddressBlocked)]
    [InlineData("http://[2002:7f00:1::]/", ImportUrlBlockReason.AddressBlocked)]
    // Alternate numeric host forms.
    [InlineData("http://2130706433/", ImportUrlBlockReason.HostBlocked)]
    [InlineData("http://2130706433./", ImportUrlBlockReason.HostBlocked)]
    [InlineData("http://0177.0.0.1/", ImportUrlBlockReason.HostBlocked)]
    [InlineData("http://0177.0.0.1./", ImportUrlBlockReason.HostBlocked)]
    [InlineData("http://0x7f.0.0.1/", ImportUrlBlockReason.HostBlocked)]
    [InlineData("http://0x7f000001/", ImportUrlBlockReason.HostBlocked)]
    [InlineData("http://0x7f.0x0.0x0.0x1/", ImportUrlBlockReason.HostBlocked)]
    [InlineData("http://127.0.1/", ImportUrlBlockReason.HostBlocked)]
    [InlineData("http://127.1/", ImportUrlBlockReason.HostBlocked)]
    [InlineData("http://0/", ImportUrlBlockReason.HostBlocked)]
    // Trailing dot.
    [InlineData("http://127.0.0.1./", ImportUrlBlockReason.AddressBlocked)]
    [InlineData("http://10.0.0.1./", ImportUrlBlockReason.AddressBlocked)]
    [InlineData("http://[::1]./", ImportUrlBlockReason.AddressBlocked)]
    // Credentials in the userinfo component. A literal blocked host is
    // classified by address first; a name host reports the credentials.
    [InlineData("http://user@127.0.0.1/", ImportUrlBlockReason.AddressBlocked)]
    [InlineData("http://user:pass@example.com/", ImportUrlBlockReason.CredentialsNotAllowed)]
    [InlineData("http://127.0.0.1@example.com/", ImportUrlBlockReason.CredentialsNotAllowed)]
    // IDN form whose Unicode dots IDN-map to a blocked literal. The transport
    // resolves Uri.IdnHost ("127.0.0.1"), so the policy must reject it.
    [InlineData("http://127\u30020\u30020\u30021/", ImportUrlBlockReason.AddressBlocked)]
    [InlineData("http://localhost\u3002/", ImportUrlBlockReason.HostBlocked)]
    [InlineData("http://169\u3002254\u3002169\u3002254/", ImportUrlBlockReason.AddressBlocked)]
    public void Policy_rejects_blocked_hosts_and_addresses_with_an_exact_reason(
        string url,
        ImportUrlBlockReason expected)
    {
        var result = ImportUrlPolicy.Evaluate(url);

        result.IsAllowed.Should().BeFalse();
        result.Reason.Should().Be(expected);
        result.Uri.Should().BeNull();
    }

    [Theory]
    [InlineData("ftp://example.com/recipe")]
    [InlineData("file:///etc/passwd")]
    [InlineData("gopher://example.com:70/1")]
    [InlineData("data:text/html,<h1>x</h1>")]
    [InlineData("javascript:alert(document.domain)")]
    [InlineData("dict://example.com:2628/")]
    [InlineData("ldap://example.com/")]
    public void Policy_rejects_every_non_http_scheme(string url) =>
        ImportUrlPolicy.Evaluate(url).Reason.Should().Be(ImportUrlBlockReason.SchemeNotAllowed);

    [Theory]
    [InlineData("http://8.8.8.8/")]
    [InlineData("https://1.1.1.1/")]
    [InlineData("http://100.63.255.255/")]
    [InlineData("http://100.128.0.0/")]
    [InlineData("http://172.15.255.255/")]
    [InlineData("http://172.32.0.1/")]
    [InlineData("http://[2606:4700:4700::1111]/")]
    [InlineData("https://example.com./recipe")]
    public void Policy_still_allows_public_targets(string url) =>
        ImportUrlPolicy.Evaluate(url).IsAllowed.Should().BeTrue(url);

    [Theory]
    [InlineData("https://example.com/a/b", "http://127.0.0.1/", ImportUrlBlockReason.AddressBlocked)]
    [InlineData("https://example.com/a/b", "//127.0.0.1/", ImportUrlBlockReason.AddressBlocked)]
    [InlineData("https://example.com/a/b", "//2130706433/", ImportUrlBlockReason.HostBlocked)]
    [InlineData("https://example.com/a/b", "http://127\u30020\u30020\u30021/", ImportUrlBlockReason.AddressBlocked)]
    [InlineData("https://example.com/a/b", "http://[fd00:ec2::254]/", ImportUrlBlockReason.AddressBlocked)]
    [InlineData("https://example.com/a/b", "http://user@example.com/", ImportUrlBlockReason.CredentialsNotAllowed)]
    [InlineData("https://example.com/a/b", "file:///etc/passwd", ImportUrlBlockReason.SchemeNotAllowed)]
    [InlineData("https://example.com/a/b", "gopher://example.com/", ImportUrlBlockReason.SchemeNotAllowed)]
    [InlineData("https://example.com/a/b", "javascript:alert(1)", ImportUrlBlockReason.SchemeNotAllowed)]
    [InlineData("https://example.com/a/b", "next", ImportUrlBlockReason.None)]
    [InlineData("https://example.com/a/b", "/next", ImportUrlBlockReason.None)]
    public void Policy_rejects_blocked_redirect_locations_with_an_exact_reason(
        string baseUrl,
        string location,
        ImportUrlBlockReason expected)
    {
        var result = ImportUrlPolicy.EvaluateLocation(new Uri(baseUrl), location);

        result.Reason.Should().Be(expected);
        result.IsAllowed.Should().Be(expected == ImportUrlBlockReason.None);
    }

    // ---------------------------------------------------------------------
    // 2. Fetch pipeline: a blocked URL never produces a request
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("http://127.0.0.1/recipe", ImportFetchFailureReason.BlockedTarget)]
    [InlineData("http://127.1.2.3/recipe", ImportFetchFailureReason.BlockedTarget)]
    [InlineData("http://[::1]/recipe", ImportFetchFailureReason.BlockedTarget)]
    [InlineData("http://localhost/recipe", ImportFetchFailureReason.BlockedTarget)]
    [InlineData("http://a.localhost/recipe", ImportFetchFailureReason.BlockedTarget)]
    [InlineData("http://10.1.2.3/recipe", ImportFetchFailureReason.BlockedTarget)]
    [InlineData("http://100.64.0.1/recipe", ImportFetchFailureReason.BlockedTarget)]
    [InlineData("http://169.254.169.254/recipe", ImportFetchFailureReason.BlockedTarget)]
    [InlineData("http://[fe80::1]/recipe", ImportFetchFailureReason.BlockedTarget)]
    [InlineData("http://[fd00:ec2::254]/recipe", ImportFetchFailureReason.BlockedTarget)]
    [InlineData("http://metadata.google.internal/recipe", ImportFetchFailureReason.BlockedTarget)]
    [InlineData("http://[::ffff:127.0.0.1]/recipe", ImportFetchFailureReason.BlockedTarget)]
    [InlineData("http://[64:ff9b::7f00:1]/recipe", ImportFetchFailureReason.BlockedTarget)]
    [InlineData("http://[2002:7f00:1::]/recipe", ImportFetchFailureReason.BlockedTarget)]
    [InlineData("http://2130706433/recipe", ImportFetchFailureReason.BlockedTarget)]
    [InlineData("http://0177.0.0.1/recipe", ImportFetchFailureReason.BlockedTarget)]
    [InlineData("http://0x7f.0.0.1/recipe", ImportFetchFailureReason.BlockedTarget)]
    [InlineData("http://127\u30020\u30020\u30021/recipe", ImportFetchFailureReason.BlockedTarget)]
    [InlineData("http://user@example.com/recipe", ImportFetchFailureReason.InvalidUrl)]
    [InlineData("file:///etc/passwd", ImportFetchFailureReason.InvalidUrl)]
    [InlineData("gopher://example.com/1", ImportFetchFailureReason.InvalidUrl)]
    [InlineData("data:text/html,<h1>x</h1>", ImportFetchFailureReason.InvalidUrl)]
    [InlineData("javascript:alert(1)", ImportFetchFailureReason.InvalidUrl)]
    public async Task Fetch_rejects_blocked_targets_before_any_request_is_sent(
        string url,
        ImportFetchFailureReason expected)
    {
        var handler = new StubHttpMessageHandler(_ => ImportTestResponses.Html("must never be served"));
        var resolver = new FakeDnsResolver(_ => throw new InvalidOperationException("DNS must not be used"));
        using var fetcher = CreateFetcher(handler, resolver);

        var result = await fetcher.FetchPageAsync(url, Ct);

        result.IsSuccess.Should().BeFalse();
        result.FailureReason.Should().Be(expected);
        result.Content.Should().BeNull();
        handler.Requests.Should().BeEmpty("no request bytes may reach a blocked target");
        resolver.QueriedHosts.Should().BeEmpty("a blocked literal host needs no DNS lookup");
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.255.255.254")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.5.5")]
    [InlineData("192.168.42.1")]
    [InlineData("100.64.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd00:ec2::254")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("64:ff9b::7f00:1")]
    [InlineData("2002:7f00:1::")]
    public async Task Fetch_rejects_a_public_host_name_that_resolves_to_a_blocked_address(
        string resolvedAddress)
    {
        var handler = new StubHttpMessageHandler(_ => ImportTestResponses.Html("must never be served"));
        var resolver = new FakeDnsResolver(IPAddress.Parse(resolvedAddress));
        using var fetcher = CreateFetcher(handler, resolver);

        var result = await fetcher.FetchPageAsync("https://cdn.example.com/recipe", Ct);

        result.FailureReason.Should().Be(ImportFetchFailureReason.BlockedTarget);
        handler.Requests.Should().BeEmpty("the resolved address is blocked, so no request may be sent");
        resolver.QueriedHosts.Should().ContainSingle().Which.Should().Be("cdn.example.com");
    }

    [Fact]
    public async Task Fetch_resolves_the_idn_form_the_transport_actually_connects_to()
    {
        // SocketsHttpHandler keys its connection on Uri.IdnHost, so the
        // pre-connect resolution must ask for the IDN (punycode) name: a
        // resolver answer for the Unicode spelling would validate a different
        // name than the one the socket targets.
        var handler = new StubHttpMessageHandler(_ => ImportTestResponses.Html("ok"));
        var resolver = new FakeDnsResolver(IPAddress.Parse("127.0.0.1"));
        using var fetcher = CreateFetcher(handler, resolver);

        var result = await fetcher.FetchPageAsync("http://b\u00fccher.example/recipe", Ct);

        result.FailureReason.Should().Be(ImportFetchFailureReason.BlockedTarget);
        handler.Requests.Should().BeEmpty();
        resolver.QueriedHosts.Should().ContainSingle().Which.Should().Be("xn--bcher-kva.example");
    }

    [Fact]
    public async Task Fetch_fails_closed_when_a_redirect_hop_rebinds_to_a_private_address()
    {
        var resolver = new FakeDnsResolver(host => host switch
        {
            "public.example.com" => [PublicAddress],
            "rebind.example.com" => [IPAddress.Parse("10.20.30.40")],
            _ => [IPAddress.Parse("127.0.0.1")],
        });
        var handler = new StubHttpMessageHandler(request =>
            request.RequestUri!.Host == "public.example.com"
                ? ImportTestResponses.Redirect(HttpStatusCode.Found, "http://rebind.example.com/secret")
                : ImportTestResponses.Html("must never be served"));
        using var fetcher = CreateFetcher(handler, resolver);

        var result = await fetcher.FetchPageAsync("https://public.example.com/start", Ct);

        result.FailureReason.Should().Be(ImportFetchFailureReason.BlockedTarget);
        handler.Requests.Should().ContainSingle();
        handler.Requests.Should().OnlyContain(request => request.Uri.Host == "public.example.com");
        resolver.QueriedHosts.Should().Equal("public.example.com", "rebind.example.com");
    }

    [Fact]
    public async Task Fetch_revalidates_every_hop_of_a_multi_hop_redirect_chain()
    {
        var resolver = new FakeDnsResolver(host => host switch
        {
            "first.example.com" or "second.example.com" => [PublicAddress],
            _ => [IPAddress.Parse("169.254.169.254")],
        });
        var handler = new StubHttpMessageHandler(request => request.RequestUri!.Host switch
        {
            "first.example.com" => ImportTestResponses.Redirect(HttpStatusCode.Found, "http://second.example.com/two"),
            "second.example.com" => ImportTestResponses.Redirect(
                HttpStatusCode.Found,
                "http://metadata.example.com/three"),
            _ => ImportTestResponses.Html("must never be served"),
        });
        using var fetcher = CreateFetcher(handler, resolver);

        var result = await fetcher.FetchPageAsync("https://first.example.com/one", Ct);

        result.FailureReason.Should().Be(ImportFetchFailureReason.BlockedTarget);
        handler.Requests.Select(request => request.Uri.Host).Should().Equal(
            "first.example.com",
            "second.example.com");
    }

    // ---------------------------------------------------------------------
    // 3. Redirect matrix
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("http://127.0.0.1/", ImportFetchFailureReason.BlockedTarget)]
    [InlineData("http://127.1.2.3/", ImportFetchFailureReason.BlockedTarget)]
    [InlineData("http://[::1]/", ImportFetchFailureReason.BlockedTarget)]
    [InlineData("http://localhost/", ImportFetchFailureReason.BlockedTarget)]
    [InlineData("//localhost/", ImportFetchFailureReason.BlockedTarget)]
    [InlineData("http://10.0.0.1/", ImportFetchFailureReason.BlockedTarget)]
    [InlineData("http://100.64.0.1/", ImportFetchFailureReason.BlockedTarget)]
    [InlineData("http://169.254.1.1/", ImportFetchFailureReason.BlockedTarget)]
    [InlineData("http://[fd00:ec2::254]/", ImportFetchFailureReason.BlockedTarget)]
    [InlineData("//2130706433/", ImportFetchFailureReason.BlockedTarget)]
    [InlineData("http://0x7f.0.0.1/", ImportFetchFailureReason.BlockedTarget)]
    [InlineData("http://127\u30020\u30020\u30021/", ImportFetchFailureReason.BlockedTarget)]
    [InlineData("file:///etc/passwd", ImportFetchFailureReason.InvalidUrl)]
    [InlineData("ftp://example.com/", ImportFetchFailureReason.InvalidUrl)]
    [InlineData("gopher://example.com/1", ImportFetchFailureReason.InvalidUrl)]
    [InlineData("data:text/html,<h1>x</h1>", ImportFetchFailureReason.InvalidUrl)]
    [InlineData("javascript:alert(1)", ImportFetchFailureReason.InvalidUrl)]
    [InlineData("http://user@example.com/", ImportFetchFailureReason.InvalidUrl)]
    public async Task Fetch_rejects_redirects_to_blocked_or_disallowed_targets(
        string location,
        ImportFetchFailureReason expected)
    {
        var handler = new StubHttpMessageHandler(_ =>
            ImportTestResponses.Redirect(HttpStatusCode.Found, location));
        using var fetcher = CreateFetcher(handler);

        var result = await fetcher.FetchPageAsync("https://example.com/start", Ct);

        result.FailureReason.Should().Be(expected);
        handler.Requests.Should().ContainSingle("only the first, allowed hop may be requested");
        handler.Requests[0].Uri.Host.Should().Be("example.com");
    }

    [Fact]
    public async Task Fetch_follows_a_same_host_https_to_http_downgrade_after_revalidation()
    {
        // Only http/https are allowed, and both are policy-checked per hop; the
        // fetcher never sends credentials, so a scheme downgrade is not a
        // data-exposure path and is documented here rather than rejected.
        var handler = new StubHttpMessageHandler(request =>
            request.RequestUri!.Scheme == "https"
                ? ImportTestResponses.Redirect(HttpStatusCode.Found, "http://example.com/plain")
                : ImportTestResponses.Html("plain"));
        using var fetcher = CreateFetcher(handler);

        var result = await fetcher.FetchPageAsync("https://example.com/start", Ct);

        result.IsSuccess.Should().BeTrue();
        result.FinalUrl!.Scheme.Should().Be("http");
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Fetch_enforces_the_redirect_limit_and_stops_requesting()
    {
        var handler = RedirectHandler("/r1", "/r2", "/r3", "/r4", "/r5", "/r6");
        using var fetcher = CreateFetcher(handler, options: Options(maxRedirects: 2));

        var result = await fetcher.FetchPageAsync("https://example.com/r0", Ct);

        result.FailureReason.Should().Be(ImportFetchFailureReason.TooManyRedirects);
        handler.Requests.Should().HaveCount(3, "one request per allowed hop, then the limit trips");
    }

    [Fact]
    public async Task Fetch_breaks_a_redirect_loop_at_the_hop_limit()
    {
        var count = 0;
        var handler = new StubHttpMessageHandler(request =>
            ImportTestResponses.Redirect(
                HttpStatusCode.Found,
                ++count % 2 == 1 ? "https://example.com/b" : "https://example.com/a"));
        using var fetcher = CreateFetcher(handler, options: Options(maxRedirects: 3));

        var result = await fetcher.FetchPageAsync("https://example.com/a", Ct);

        result.FailureReason.Should().Be(ImportFetchFailureReason.TooManyRedirects);
        handler.Requests.Should().HaveCount(4);
    }

    // ---------------------------------------------------------------------
    // 4. Size, time, content-type and content-encoding limits
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Fetch_rejects_a_declared_html_length_above_the_cap_without_reading_the_body()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnreadableContent("text/html", Options().MaxHtmlBytes + 1),
        });
        using var fetcher = CreateFetcher(handler);

        var result = await fetcher.FetchPageAsync("https://example.com/big", Ct);

        result.FailureReason.Should().Be(ImportFetchFailureReason.TooLarge);
    }

    [Fact]
    public async Task Fetch_rejects_a_declared_image_length_above_the_cap_without_reading_the_body()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnreadableContent("image/png", Options().MaxImageBytes + 1),
        });
        using var fetcher = CreateFetcher(handler);

        var result = await fetcher.FetchImageAsync("https://example.com/big.png", Ct);

        result.FailureReason.Should().Be(ImportFetchFailureReason.TooLarge);
    }

    [Fact]
    public async Task Fetch_times_out_on_a_never_ending_body()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamedContent("text/html", () => new NeverEndingStream(byteCountPerRead: 8)),
        });
        using var fetcher = CreateFetcher(handler, options: Options(readTimeout: TimeSpan.FromMilliseconds(200)));

        var result = await fetcher.FetchPageAsync("https://example.com/endless", Ct);

        result.FailureReason.Should().Be(ImportFetchFailureReason.Timeout);
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Fetch_times_out_on_a_body_that_trickles_below_the_cap()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamedContent("image/png", () => new NeverEndingStream(byteCountPerRead: 1)),
        });
        using var fetcher = CreateFetcher(
            handler,
            options: Options(maxImageBytes: 1_000_000, readTimeout: TimeSpan.FromMilliseconds(250)));

        var result = await fetcher.FetchImageAsync("https://example.com/trickle.png", Ct);

        result.FailureReason.Should().Be(ImportFetchFailureReason.Timeout);
    }

    [Fact]
    public async Task Fetch_never_inflates_a_gzip_body_the_server_sent_unasked()
    {
        var plaintext = Encoding.UTF8.GetBytes(new string('x', 256 * 1024));
        var compressed = Gzip(plaintext);
        compressed.Length.Should().BeLessThan(plaintext.Length / 10);
        var handler = new StubHttpMessageHandler(_ => ImportTestResponses.Content(
            HttpStatusCode.OK,
            "text/html",
            compressed,
            declaredLength: compressed.Length,
            contentEncoding: "gzip"));
        using var fetcher = CreateFetcher(handler);

        var result = await fetcher.FetchPageAsync("https://example.com/zipped", Ct);

        result.IsSuccess.Should().BeTrue();
        result.Content!.Length.Should().Be(compressed.Length, "the fetcher must not decompress wire bytes");
        result.Content[0].Should().Be(0x1F);
        result.Content[1].Should().Be(0x8B);
    }

    [Fact]
    public async Task Fetch_applies_the_size_cap_to_content_encoded_wire_bytes()
    {
        var plaintext = Encoding.UTF8.GetBytes(new string('y', 512 * 1024));
        var compressed = Gzip(plaintext);
        var handler = new StubHttpMessageHandler(_ => ImportTestResponses.Content(
            HttpStatusCode.OK,
            "text/html",
            compressed,
            contentEncoding: "gzip"));
        using var fetcher = CreateFetcher(handler, options: Options(maxHtmlBytes: 256));

        var result = await fetcher.FetchPageAsync("https://example.com/zipped", Ct);

        result.FailureReason.Should().Be(ImportFetchFailureReason.TooLarge);
    }

    [Fact]
    public async Task Fetch_never_inflates_a_deflate_body_the_server_sent_unasked()
    {
        var plaintext = Encoding.UTF8.GetBytes(new string('z', 128 * 1024));
        var compressed = Deflate(plaintext);
        var handler = new StubHttpMessageHandler(_ => ImportTestResponses.Content(
            HttpStatusCode.OK,
            "text/html",
            compressed,
            contentEncoding: "deflate"));
        using var fetcher = CreateFetcher(handler);

        var result = await fetcher.FetchPageAsync("https://example.com/deflated", Ct);

        result.IsSuccess.Should().BeTrue();
        result.Content!.Length.Should().Be(compressed.Length);
        Encoding.UTF8.GetString(result.Content).Should().NotContain("zzzzzzzz");
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("text/plain")]
    [InlineData("text/xml")]
    [InlineData("application/octet-stream")]
    public async Task Fetch_rejects_a_non_html_recipe_page_content_type(string contentType)
    {
        var handler = new StubHttpMessageHandler(_ => ImportTestResponses.Content(
            HttpStatusCode.OK,
            contentType,
            Encoding.UTF8.GetBytes("<html><body>not html</body></html>")));
        using var fetcher = CreateFetcher(handler);

        var result = await fetcher.FetchPageAsync("https://example.com/page", Ct);

        result.FailureReason.Should().Be(ImportFetchFailureReason.UnsupportedContentType);
        result.Content.Should().BeNull();
    }

    [Theory]
    [InlineData("image/svg+xml")]
    [InlineData("image/gif")]
    [InlineData("text/html")]
    [InlineData("application/pdf")]
    public async Task Fetch_rejects_a_disallowed_import_image_content_type(string contentType)
    {
        var handler = new StubHttpMessageHandler(_ => ImportTestResponses.Content(
            HttpStatusCode.OK,
            contentType,
            Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"/>")));
        using var fetcher = CreateFetcher(handler);

        var result = await fetcher.FetchImageAsync("https://example.com/pic", Ct);

        result.FailureReason.Should().Be(ImportFetchFailureReason.UnsupportedContentType);
    }

    private static byte[] Gzip(byte[] plaintext)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(plaintext);
        }

        return output.ToArray();
    }

    private static byte[] Deflate(byte[] plaintext)
    {
        using var output = new MemoryStream();
        using (var deflate = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            deflate.Write(plaintext);
        }

        return output.ToArray();
    }

    /// <summary>
    /// A response body that throws when read, used to prove a limit is applied
    /// from the declared headers before any bytes are buffered.
    /// </summary>
    private sealed class UnreadableContent : HttpContent
    {
        public UnreadableContent(string contentType, long declaredLength)
        {
            Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            Headers.ContentLength = declaredLength;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new InvalidOperationException("The response body must not be read.");

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>Response body backed by a caller-supplied stream.</summary>
    private sealed class StreamedContent : HttpContent
    {
        private readonly Func<Stream> _open;

        public StreamedContent(string contentType, Func<Stream> open)
        {
            _open = open;
            Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_open());

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new NotSupportedException();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>A body that never ends, emitting a few bytes per short delay.</summary>
    private sealed class NeverEndingStream(int byteCountPerRead) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("The fetcher must read asynchronously.");

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(20, cancellationToken);
            var count = Math.Min(byteCountPerRead, buffer.Length);
            buffer.Span[..count].Fill(0x42);
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
