using FateAndFridge.Api.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Regression coverage for the M1-08 session-rotation re-mint: the antiforgery
/// cookie is hidden from the framework view without re-serializing the raw
/// <c>Cookie</c> header, so unrelated cookies (percent-encoded values included)
/// are neither mutated nor dropped.
/// </summary>
public sealed class FilteredRequestCookieCollectionTests
{
    [Fact]
    public void Hides_only_the_excluded_cookie_and_keeps_the_raw_header_intact()
    {
        const string rawCookieHeader = "keepme=hello%2Fworld; quoted=\"a b\"; faf_csrf=token-value";
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = rawCookieHeader;
        var parsed = context.Request.Cookies;

        var filtered = new FilteredRequestCookieCollection(parsed, "faf_csrf");

        // The raw header is never re-serialized from the parsed collection.
        context.Request.Headers.Cookie.ToString().Should().Be(rawCookieHeader);

        // Only the antiforgery cookie is hidden.
        filtered.ContainsKey("faf_csrf").Should().BeFalse();
        filtered["faf_csrf"].Should().BeNull();
        filtered.TryGetValue("faf_csrf", out _).Should().BeFalse();
        filtered.Keys.Should().NotContain("faf_csrf");
        filtered.Select(pair => pair.Key).Should().NotContain("faf_csrf");
        filtered.Count.Should().Be(parsed.Count - 1);
        filtered.Count.Should().Be(
            filtered.Count(),
            "the count must agree with the enumeration predicate");

        // Everything the framework parsed survives unchanged.
        filtered.Select(pair => pair.Key).Should().BeEquivalentTo(
            parsed.Keys.Where(key => key != "faf_csrf"));
        foreach (var (key, value) in parsed.Where(pair => pair.Key != "faf_csrf"))
        {
            filtered[key].Should().Be(value);
            filtered.ContainsKey(key).Should().BeTrue();
            filtered.TryGetValue(key, out var found).Should().BeTrue();
            found.Should().Be(value);
        }

        parsed["keepme"].Should().Be("hello/world");
        filtered["keepme"].Should().Be("hello/world");
    }

    [Theory]
    [InlineData("FAF_CSRF")]
    [InlineData("Faf_Csrf")]
    [InlineData("faf_csrf")]
    public void Excluded_cookie_is_hidden_case_insensitively(string excludedName)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = "keepme=hello%2Fworld; FAF_CSRF=mixed-case-token";
        var parsed = context.Request.Cookies;

        var filtered = new FilteredRequestCookieCollection(parsed, excludedName);

        filtered.ContainsKey("faf_csrf").Should().BeFalse();
        filtered["faf_csrf"].Should().BeNull();
        filtered.TryGetValue("faf_csrf", out _).Should().BeFalse();
        filtered.ContainsKey("FAF_CSRF").Should().BeFalse();
        filtered["Faf_Csrf"].Should().BeNull();
        filtered.TryGetValue("FAF_CSRF", out _).Should().BeFalse();
        filtered.Keys.Should().NotContain(key =>
            string.Equals(key, "faf_csrf", StringComparison.OrdinalIgnoreCase));
        filtered.Should().NotContain(pair =>
            string.Equals(pair.Key, "faf_csrf", StringComparison.OrdinalIgnoreCase));
        filtered.Count.Should().Be(filtered.Count());
        filtered.Count.Should().Be(parsed.Count - 1);

        // Mixed-case lookups for a surviving cookie stay consistent.
        filtered.ContainsKey("KEEPME").Should().BeTrue();
        filtered["KEEPME"].Should().Be("hello/world");
        filtered.TryGetValue("KeepMe", out var value).Should().BeTrue();
        value.Should().Be("hello/world");
    }

    [Fact]
    public void An_absent_cookie_leaves_the_collection_unchanged()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = "keepme=hello";
        var parsed = context.Request.Cookies;

        var filtered = new FilteredRequestCookieCollection(parsed, "faf_csrf");

        filtered.Count.Should().Be(parsed.Count);
        filtered["keepme"].Should().Be(parsed["keepme"]);
        filtered.Select(pair => pair.Key).Should().BeEquivalentTo(parsed.Keys);
    }
}
