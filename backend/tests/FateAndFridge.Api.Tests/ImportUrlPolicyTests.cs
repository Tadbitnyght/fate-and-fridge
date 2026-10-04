using System.Net;
using FateAndFridge.Api.Imports;
using FluentAssertions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// SSRF URL/IP classification matrix (docs/M4_PLAN.md §1.14, §4).
/// </summary>
public sealed class ImportUrlPolicyTests
{
    [Theory]
    [InlineData("https://example.com/recipe")]
    [InlineData("http://example.com")]
    [InlineData("https://example.com:8443/path?q=1#fragment")]
    [InlineData("http://sub.example.co.uk/x")]
    [InlineData("https://Example.COM./")]
    [InlineData("http://8.8.8.8/")]
    [InlineData("http://[2606:4700:4700::1111]/")]
    public void Evaluate_allows_http_and_https_public_targets(string url)
    {
        var result = ImportUrlPolicy.Evaluate(url);

        result.IsAllowed.Should().BeTrue();
        result.Reason.Should().Be(ImportUrlBlockReason.None);
        result.Uri.Should().NotBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("example.com")]
    [InlineData("relative/path")]
    [InlineData("http://")]
    [InlineData("http://[not-an-address]/")]
    public void Evaluate_rejects_malformed_urls(string? url)
    {
        var result = ImportUrlPolicy.Evaluate(url);

        result.IsAllowed.Should().BeFalse();
        result.Reason.Should().Be(ImportUrlBlockReason.Malformed);
    }

    [Theory]
    [InlineData("/relative/path")]
    [InlineData("/")]
    public void Evaluate_rejects_rooted_paths(string url)
    {
        // On Unix a leading slash parses as a file URI, so the reason is the
        // scheme rather than "malformed"; both are rejections.
        ImportUrlPolicy.Evaluate(url).IsAllowed.Should().BeFalse();
    }

    [Theory]
    [InlineData("ftp://example.com/file")]
    [InlineData("file:///etc/passwd")]
    [InlineData("gopher://example.com/")]
    [InlineData("data:text/html,<h1>x</h1>")]
    [InlineData("javascript:alert(1)")]
    public void Evaluate_rejects_non_http_schemes(string url)
    {
        ImportUrlPolicy.Evaluate(url).Reason.Should().Be(ImportUrlBlockReason.SchemeNotAllowed);
    }

    [Theory]
    [InlineData("http://user:pass@example.com/")]
    [InlineData("http://user@example.com/")]
    [InlineData("https://token@8.8.8.8/")]
    public void Evaluate_rejects_credentials_in_url(string url)
    {
        ImportUrlPolicy.Evaluate(url).Reason.Should().Be(ImportUrlBlockReason.CredentialsNotAllowed);
    }

    [Theory]
    [InlineData("http://localhost/")]
    [InlineData("http://LOCALHOST/")]
    [InlineData("http://localhost./")]
    [InlineData("http://foo.localhost/")]
    [InlineData("http://foo.local/")]
    [InlineData("http://foo.internal/")]
    [InlineData("http://host.docker.internal/")]
    [InlineData("http://gateway.docker.internal/")]
    [InlineData("http://metadata/")]
    [InlineData("http://kubernetes.default/")]
    [InlineData("https://metadata.google.internal/")]
    public void Evaluate_rejects_blocked_host_names(string url)
    {
        ImportUrlPolicy.Evaluate(url).Reason.Should().Be(ImportUrlBlockReason.HostBlocked);
    }

    [Theory]
    [InlineData("http://0x7f000001/")]
    [InlineData("http://2130706433/")]
    [InlineData("http://0177.0.0.1/")]
    [InlineData("http://127.1/")]
    [InlineData("http://0x08080808/")]
    [InlineData("http://134744072/")]
    public void Evaluate_rejects_obfuscated_ipv4_literals(string url)
    {
        ImportUrlPolicy.Evaluate(url).Reason.Should().Be(ImportUrlBlockReason.HostBlocked);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.255.255.255")]
    [InlineData("10.0.0.1")]
    [InlineData("10.255.255.255")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.255")]
    [InlineData("0.0.0.0")]
    [InlineData("0.1.2.3")]
    [InlineData("192.0.0.1")]
    [InlineData("192.0.2.5")]
    [InlineData("192.88.99.1")]
    [InlineData("198.18.0.1")]
    [InlineData("198.51.100.5")]
    [InlineData("203.0.113.9")]
    [InlineData("224.0.0.1")]
    [InlineData("239.255.255.255")]
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.255")]
    public void IsBlockedAddress_blocks_ipv4_ssrf_ranges(string address) =>
        ImportUrlPolicy.IsBlockedAddress(IPAddress.Parse(address)).Should().BeTrue();

    [Theory]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("64:ff9b::1")]
    [InlineData("100::1")]
    [InlineData("2001::1")]
    [InlineData("2001:2::1")]
    [InlineData("2001:db8::1")]
    [InlineData("2002::1")]
    [InlineData("fc00::1")]
    [InlineData("fd00:ec2::254")]
    [InlineData("fe80::1")]
    [InlineData("ff02::1")]
    public void IsBlockedAddress_blocks_ipv6_ssrf_ranges(string address) =>
        ImportUrlPolicy.IsBlockedAddress(IPAddress.Parse(address)).Should().BeTrue();

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")]
    [InlineData("172.15.255.255")]
    [InlineData("172.32.0.1")]
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.0")]
    [InlineData("192.0.1.1")]
    public void IsBlockedAddress_allows_public_ipv4(string address) =>
        ImportUrlPolicy.IsBlockedAddress(IPAddress.Parse(address)).Should().BeFalse();

    [Theory]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("2001:4860:4860::8888")]
    [InlineData("2001:db9::1")]
    public void IsBlockedAddress_allows_public_ipv6(string address) =>
        ImportUrlPolicy.IsBlockedAddress(IPAddress.Parse(address)).Should().BeFalse();

    [Fact]
    public void IsBlockedAddress_fails_closed_for_null() =>
        ImportUrlPolicy.IsBlockedAddress(null).Should().BeTrue();

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData("localhost.", true)]
    [InlineData("a.localhost", true)]
    [InlineData("a.b.localhost", true)]
    [InlineData("a.local", true)]
    [InlineData("a.internal", true)]
    [InlineData("host.docker.internal", true)]
    [InlineData("metadata", true)]
    [InlineData("kubernetes.default", true)]
    [InlineData("", true)]
    [InlineData(null, true)]
    [InlineData("example.com", false)]
    [InlineData("notlocalhost", false)]
    [InlineData("local.example.com", false)]
    [InlineData("foo.internal.example.com", false)]
    public void IsBlockedHostName_matches_host_names(string? host, bool expected) =>
        ImportUrlPolicy.IsBlockedHostName(host).Should().Be(expected);

    [Theory]
    [InlineData("https://example.com/recipe", "https://example.com/recipe", true)]
    [InlineData("https://example.com/a/b", "/next", true)]
    [InlineData("https://example.com/a/b", "next", true)]
    [InlineData("https://example.com/a/b", "//example.com/other", true)]
    [InlineData("https://example.com/", "http://169.254.169.254/meta", false)]
    [InlineData("https://example.com/", "http://0x7f000001/", false)]
    [InlineData("https://example.com/", "file:///etc/passwd", false)]
    [InlineData("https://example.com/", "  ", false)]
    public void EvaluateLocation_resolves_and_validates(string baseUrl, string location, bool expected)
    {
        var baseUri = new Uri(baseUrl);

        var result = ImportUrlPolicy.EvaluateLocation(baseUri, location);

        result.IsAllowed.Should().Be(expected);
    }
}
