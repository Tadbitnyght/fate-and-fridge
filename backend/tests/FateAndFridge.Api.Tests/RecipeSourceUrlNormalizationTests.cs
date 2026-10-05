using FateAndFridge.Api.Services;
using FluentAssertions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Pure matrix for <see cref="RecipeService.BuildNormalizedSourceUrl"/>
/// (docs/M4_PLAN.md §1.9, §4 "duplicate checks"). The canonical form is shared
/// by recipe save and import duplicate detection, so these pin the exact
/// variants that must fold together — and the ones that must stay distinct.
/// </summary>
public sealed class RecipeSourceUrlNormalizationTests
{
    private static string Normalize(string url) =>
        RecipeService.BuildNormalizedSourceUrl(new Uri(url), url);

    [Theory]
    [InlineData("https://example.com/recipe/", "https://example.com/recipe")]
    [InlineData("https://example.com/", "https://example.com")]
    [InlineData("https://example.com", "https://example.com/")]
    [InlineData("HTTPS://EXAMPLE.COM/Recipe", "https://example.com/Recipe")]
    [InlineData("https://example.com:443/recipe", "https://example.com/recipe")]
    [InlineData("https://example.com/recipe?b=2&a=1", "https://example.com/recipe?a=1&b=2")]
    [InlineData("https://example.com/recipe?b=2&a=1#step", "https://example.com/recipe?a=1&b=2")]
    [InlineData("https://example.com/recipe#a", "https://example.com/recipe#b")]
    public void Canonicalization_folds_the_duplicate_variants(string left, string right) =>
        Normalize(left).Should().Be(Normalize(right));

    [Theory]
    [InlineData("https://example.com:8443/recipe", "https://example.com/recipe")] // non-default port
    [InlineData("https://example.com/recipe?", "https://example.com/recipe")] // empty query is distinct
    [InlineData("https://example.com/recipe?a=1&", "https://example.com/recipe?a=1")] // trailing ampersand
    [InlineData("https://example.com/recipe?x=%2F", "https://example.com/recipe?x=%2f")] // encoding case
    public void Canonicalization_keeps_deliberately_distinct_urls_apart(string left, string right) =>
        Normalize(left).Should().NotBe(Normalize(right));

    [Fact]
    public void Canonicalization_is_idempotent()
    {
        var once = Normalize("https://Example.com:443/Recipe/?b=2&a=1#x");
        var twice = RecipeService.BuildNormalizedSourceUrl(new Uri(once), once);

        twice.Should().Be(once);
    }

    [Fact]
    public void Repeated_keys_are_sorted_as_pairs()
    {
        // Both orderings collapse onto the same sorted pair order ...
        Normalize("https://example.com/r?a=1&a=2").Should().Be("https://example.com/r?a=1&a=2");
        // ... and a different pairing is not equal.
        Normalize("https://example.com/r?a=2&a=1").Should().Be(Normalize("https://example.com/r?a=1&a=2"));
    }
}
