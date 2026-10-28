using FateAndFridge.Api.Imports;
using FluentAssertions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Pure unit verification of canonical/alias ingredient matching
/// exact canonical, exact alias, folding, bounded
/// deterministic suggestions, retired exclusion, ambiguity, and adversarial
/// input. Reads no database and touches no network.
/// </summary>
public sealed class IngredientMatcherTests
{
    private static IngredientCandidate Candidate(
        long id,
        string name,
        bool active = true,
        params string[] aliases) =>
        new(id, name, active, aliases);

    [Fact]
    public void Exact_canonical_name_matches_with_exact_kind()
    {
        var matcher = new IngredientMatcher([Candidate(1, "Flour")]);

        var result = matcher.Match("Flour");

        result.MatchedIngredientId.Should().Be(1);
        result.MatchKind.Should().Be(IngredientMatcher.ExactKind);
        result.IsUnknown.Should().BeFalse();
        result.Suggestions.Should().ContainSingle().Which.Should().Be(new ImportDraftReference(1, "Flour"));
    }

    [Fact]
    public void Exact_alias_matches_with_alias_kind()
    {
        var matcher = new IngredientMatcher([Candidate(7, "Scallion", aliases: ["Green Onion", "Spring Onion"])]);

        var result = matcher.Match("Green Onion");

        result.MatchedIngredientId.Should().Be(7);
        result.MatchKind.Should().Be(IngredientMatcher.AliasKind);
        result.IsUnknown.Should().BeFalse();
    }

    [Fact]
    public void Canonical_name_wins_over_another_ingredients_alias()
    {
        var matcher = new IngredientMatcher(
        [
            Candidate(1, "Scallion", aliases: ["Green Onion"]),
            Candidate(2, "Green Onion"),
        ]);

        var result = matcher.Match("Green Onion");

        result.MatchedIngredientId.Should().Be(2);
        result.MatchKind.Should().Be(IngredientMatcher.ExactKind);
    }

    [Theory]
    [InlineData("FLOUR")]
    [InlineData("  flour  ")]
    [InlineData("Flour")]
    public void Folding_ignores_case_and_surrounding_whitespace(string input)
    {
        var matcher = new IngredientMatcher([Candidate(1, "Flour")]);

        matcher.Match(input).MatchedIngredientId.Should().Be(1);
    }

    [Fact]
    public void Folding_ignores_internal_whitespace_runs_and_diacritics()
    {
        var matcher = new IngredientMatcher(
        [
            Candidate(1, "Jalapeño"),
            Candidate(2, "Extra   Virgin Olive Oil"),
        ]);

        matcher.Match("JALAPENO").MatchedIngredientId.Should().Be(1);
        matcher.Match("jalapeño").MatchedIngredientId.Should().Be(1);
        matcher.Match("extra virgin olive oil").MatchedIngredientId.Should().Be(2);
    }

    [Fact]
    public void No_match_flags_unknown_with_bound_suggestions()
    {
        var matcher = new IngredientMatcher([Candidate(1, "Flour")]);

        var result = matcher.Match("flour tortillas");

        result.MatchedIngredientId.Should().BeNull();
        result.MatchKind.Should().BeNull();
        result.IsUnknown.Should().BeTrue();
        result.Suggestions.Should().ContainSingle().Which.Id.Should().Be(1);
    }

    [Fact]
    public void Blank_and_null_names_are_unknown_without_suggestions()
    {
        var matcher = new IngredientMatcher([Candidate(1, "Flour")]);

        foreach (var input in new string?[] { null, "", "   ", "\t\n" })
        {
            var result = matcher.Match(input);
            result.IsUnknown.Should().BeTrue();
            result.MatchedIngredientId.Should().BeNull();
            result.Suggestions.Should().BeEmpty();
        }
    }

    [Fact]
    public void Suggestions_are_bounded_to_five_and_ordered_by_prefix_then_length_then_id()
    {
        var matcher = new IngredientMatcher(
        [
            Candidate(30, "Flour tortillas"),   // substring, longer
            Candidate(20, "Flour"),             // exact-prefix, shortest
            Candidate(40, "Flour mix"),         // exact-prefix, medium
            Candidate(10, "All-purpose flour"), // substring
            Candidate(50, "Flour blend"),       // exact-prefix
            Candidate(60, "Floury grain"),      // exact-prefix (starts with "flour")
            Candidate(70, "Cake flour"),        // substring
            Candidate(80, "Bread flour"),       // substring (not selected: >5)
        ]);

        var result = matcher.Match("flou");

        result.IsUnknown.Should().BeTrue();
        result.Suggestions.Should().HaveCount(IngredientMatcher.MaxSuggestions);
        result.Suggestions.Select(s => s.Id).Should().Equal(20L, 40L, 50L, 60L, 30L);
        result.Suggestions.Select(s => s.Name).Should().NotContainNulls();
    }

    [Fact]
    public void Prefix_matches_rank_before_substring_matches()
    {
        var matcher = new IngredientMatcher(
        [
            Candidate(1, "Bread flour"),
            Candidate(2, "Flour"),
        ]);

        matcher.Match("flo").Suggestions.Select(s => s.Id).Should().Equal(2, 1);
    }

    [Fact]
    public void Retired_ingredients_are_excluded_from_matches_and_suggestions()
    {
        var matcher = new IngredientMatcher(
        [
            Candidate(1, "Flour", active: false),
            Candidate(2, "Sugar", active: true),
        ]);

        var exact = matcher.Match("Flour");
        exact.MatchedIngredientId.Should().BeNull("a retired ingredient is never matched");
        exact.Suggestions.Should().NotContain(s => s.Id == 1);

        var suggestion = matcher.Match("fl");
        suggestion.Suggestions.Should().NotContain(s => s.Id == 1);
        matcher.ActiveCount.Should().Be(1);
    }

    [Fact]
    public void Retired_alias_is_excluded_from_suggestions()
    {
        var matcher = new IngredientMatcher(
        [
            Candidate(1, "Scallion", active: false, aliases: ["Green Onion"]),
            Candidate(2, "Onion", active: true),
        ]);

        matcher.Match("green").Suggestions.Should().BeEmpty();
    }

    [Fact]
    public void Ambiguous_folded_key_is_unknown_rather_than_guessed()
    {
        // Two active ingredients whose folded names collide (defensive: the
        // database unique index normally prevents this).
        var matcher = new IngredientMatcher(
        [
            Candidate(1, "Jalapeño"),
            Candidate(2, "Jalapeno"),
        ]);

        var result = matcher.Match("jalapeno");

        result.MatchedIngredientId.Should().BeNull();
        result.MatchKind.Should().BeNull();
        result.IsUnknown.Should().BeTrue("an ambiguous key must never be guessed");
        result.Suggestions.Should().HaveCount(2);
    }

    [Fact]
    public void Inactive_and_duplicate_candidates_are_ignored_by_construction()
    {
        var candidate = Candidate(1, "Flour");
        var matcher = new IngredientMatcher(
        [
            candidate,
            candidate,
            Candidate(2, "Sugar", active: false),
        ]);

        matcher.ActiveCount.Should().Be(1);
        matcher.Match("flour").MatchedIngredientId.Should().Be(1);
        matcher.Match("sugar").IsUnknown.Should().BeTrue();
    }

    [Fact]
    public void Empty_catalog_never_matches()
    {
        var matcher = new IngredientMatcher([]);

        var result = matcher.Match("anything");

        result.IsUnknown.Should().BeTrue();
        result.Suggestions.Should().BeEmpty();
        matcher.ActiveCount.Should().Be(0);
    }

    [Fact]
    public void Adversarial_names_never_throw()
    {
        var matcher = new IngredientMatcher([Candidate(1, "Flour")]);

        var adversarial = new[]
        {
            new string('a', 100_000),
            "flour\u0000\u0007\u001b",
            "\ud800",                 // lone high surrogate
            "\udfff\ud800",           // lone low + high surrogate
            "\ud83d\ude00",           // valid surrogate pair (emoji)
            "flour\ud83d\ude00",
            new string('\ud800', 1_000),
            "µ⅛½🍅",
            null,
        };

        foreach (var input in adversarial)
        {
            var act = () => matcher.Match(input);
            act.Should().NotThrow();
        }
    }

    [Fact]
    public void Matched_result_suggestion_carries_the_canonical_name()
    {
        var matcher = new IngredientMatcher([Candidate(4, "All-Purpose Flour")]);

        var result = matcher.Match("all-purpose flour");

        result.Suggestions.Should().ContainSingle()
            .Which.Should().Be(new ImportDraftReference(4, "All-Purpose Flour"));
    }
}
