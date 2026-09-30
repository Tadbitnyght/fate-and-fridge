using System.Net;
using System.Net.Sockets;
using FateAndFridge.Api.Services;
using FluentAssertions;
using Microsoft.Extensions.Primitives;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Shared address rules (docs/API.md §21): strict range parsing/normalization,
/// runtime address normalization, matching, and forwarded-header resolution.
/// </summary>
public sealed class ClientAddressTests
{
    [Theory]
    [InlineData("10.0.0.1", "10.0.0.1/32")]
    [InlineData("10.0.0.0/8", "10.0.0.0/8")]
    [InlineData("255.255.255.255/32", "255.255.255.255/32")]
    [InlineData("0.0.0.0/0", "0.0.0.0/0")]
    [InlineData("2001:db8::1", "2001:db8::1/128")]
    [InlineData("2001:db8::/32", "2001:db8::/32")]
    [InlineData("::1", "::1/128")]
    [InlineData("::ffff:10.0.0.0/104", "10.0.0.0/8")]
    [InlineData("::ffff:10.0.0.1/96", "10.0.0.1/0")]
    [InlineData("::ffff:10.0.0.0/120", "10.0.0.0/24")]
    public void TryNormalizeRange_accepts_and_canonicalizes(string input, string expected)
    {
        ClientAddress.TryNormalizeRange(input, out var cidr).Should().BeTrue();
        cidr.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("10")]
    [InlineData("10.1")]
    [InlineData("0x7f000001")]
    [InlineData("10.0.0.256")]
    [InlineData("010.0.0.1")]
    [InlineData("10.0.0.1/33")]
    [InlineData("10.0.0.1/-1")]
    [InlineData("10.0.0.1/abc")]
    [InlineData("10.0.0.1/")]
    [InlineData("10.0.0.1/24/24")]
    [InlineData("2001:db8::1/129")]
    [InlineData("fe80::1%eth0")]
    [InlineData("fe80::1%eth0/64")]
    [InlineData("::ffff:10.0.0.0/95")]
    [InlineData("::ffff:10.0.0.0/80")]
    [InlineData("not-a-range")]
    public void TryNormalizeRange_rejects_invalid_values(string? input) =>
        ClientAddress.TryNormalizeRange(input, out _).Should().BeFalse();

    [Fact]
    public void Mapped_slash_96_range_covers_every_ipv4_address()
    {
        ClientAddress.TryNormalizeRange("::ffff:10.0.0.1/96", out var cidr).Should().BeTrue();
        cidr.Should().Be("10.0.0.1/0");
        ClientAddress.IsInRange(IPAddress.Parse("8.8.8.8"), cidr).Should().BeTrue();
        ClientAddress.IsInRange(IPAddress.Parse("203.0.113.1"), cidr).Should().BeTrue();
        ClientAddress.IsInRange(IPAddress.Parse("2001:db8::1"), cidr).Should().BeFalse();
    }

    [Fact]
    public void Normalize_maps_ipv4_mapped_addresses_and_strips_scope_ids()
    {
        ClientAddress.Normalize(IPAddress.Parse("::ffff:10.0.0.1"))!.ToString()
            .Should().Be("10.0.0.1");
        ClientAddress.Normalize(null).Should().BeNull();

        var normalized = ClientAddress.Normalize(IPAddress.Parse("fe80::1%5"))!;
        normalized.ScopeId.Should().Be(0);
        normalized.AddressFamily.Should().Be(AddressFamily.InterNetworkV6);
    }

    [Fact]
    public void IsInRange_matches_by_family_and_prefix()
    {
        var ipv4 = IPAddress.Parse("192.168.4.5");
        ClientAddress.IsInRange(ipv4, "192.168.0.0/16").Should().BeTrue();
        ClientAddress.IsInRange(ipv4, "192.168.5.0/24").Should().BeFalse();
        ClientAddress.IsInRange(IPAddress.Parse("2001:db8::5"), "2001:db8::/32").Should().BeTrue();
        ClientAddress.IsInRange(IPAddress.Parse("2001:db9::5"), "2001:db8::/32").Should().BeFalse();
        ClientAddress.IsInRange(ipv4, "2001:db8::/32").Should().BeFalse();
        ClientAddress.IsInRange(null, "0.0.0.0/0").Should().BeFalse();
    }

    [Fact]
    public void Loopback_detection_covers_ipv4_ipv6_and_mapped_addresses()
    {
        ClientAddress.IsLoopback(IPAddress.Parse("127.0.0.1")).Should().BeTrue();
        ClientAddress.IsLoopback(IPAddress.Parse("127.1.2.3")).Should().BeTrue();
        ClientAddress.IsLoopback(IPAddress.Parse("::1")).Should().BeTrue();
        ClientAddress.IsLoopback(IPAddress.Parse("::ffff:127.0.0.1")).Should().BeTrue();
        ClientAddress.IsLoopback(IPAddress.Parse("10.0.0.1")).Should().BeFalse();
        ClientAddress.IsLoopback(null).Should().BeFalse();
    }

    [Fact]
    public void ResolveClientAddress_ignores_xff_from_untrusted_peers()
    {
        var resolved = ClientAddress.ResolveClientAddress(
            IPAddress.Parse("203.0.113.9"),
            ["10.0.0.0/8"],
            new StringValues("198.51.100.7"));

        resolved!.ToString().Should().Be("203.0.113.9");
    }

    [Fact]
    public void ResolveClientAddress_uses_the_rightmost_untrusted_xff_entry()
    {
        var peer = IPAddress.Parse("203.0.113.1");
        var trusted = new[] { "203.0.113.0/24" };

        // client, inner trusted proxy -> the client wins.
        ClientAddress.ResolveClientAddress(
                peer,
                trusted,
                new StringValues("198.51.100.7, 203.0.113.2"))!
            .ToString().Should().Be("198.51.100.7");

        // every entry trusted -> the socket peer wins.
        ClientAddress.ResolveClientAddress(
                peer,
                trusted,
                new StringValues("203.0.113.2, 203.0.113.1"))!
            .ToString().Should().Be("203.0.113.1");

        // malformed entries are skipped.
        ClientAddress.ResolveClientAddress(
                peer,
                trusted,
                new StringValues("bogus, 198.51.100.7, 203.0.113.2"))!
            .ToString().Should().Be("198.51.100.7");

        // multiple header lines are flattened right-to-left.
        ClientAddress.ResolveClientAddress(
                peer,
                trusted,
                new StringValues(new[] { "198.51.100.7", "203.0.113.2" }))!
            .ToString().Should().Be("198.51.100.7");
    }

    [Fact]
    public void ResolveClientAddress_normalizes_ipv4_mapped_peers()
    {
        var resolved = ClientAddress.ResolveClientAddress(
            IPAddress.Parse("::ffff:203.0.113.1"),
            ["203.0.113.0/24"],
            new StringValues("198.51.100.7"));

        resolved!.ToString().Should().Be("198.51.100.7");
    }

    [Theory]
    [InlineData("203.0.113.1", "https", true)]
    [InlineData("203.0.113.1", "http", false)]
    [InlineData("198.51.100.9", "https", false)]
    [InlineData("203.0.113.1", "", false)]
    public void IsForwardedHttps_requires_a_trusted_peer(string peer, string proto, bool expected)
    {
        var result = ClientAddress.IsForwardedHttps(
            IPAddress.Parse(peer),
            ["203.0.113.0/24"],
            new StringValues(proto));

        result.Should().Be(expected);
    }
}
