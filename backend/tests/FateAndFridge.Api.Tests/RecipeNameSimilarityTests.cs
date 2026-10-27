using FateAndFridge.Api.Imports;
using FluentAssertions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Pure unit matrix for the M4-12 similar-name scorer (docs/M4_PLAN.md §1.9).
/// No database and no network; inputs are already-folded names.
/// </summary>
public sealed class RecipeNameSimilarityTests
{
    [Fact]
    public void Folded_equality_is_the_exact_kind()
    {
        var result = RecipeNameSimilarity.Evaluate("lasagna", "lasagna");

        result.Should().NotBeNull();
        result!.Kind.Should().Be(RecipeNameSimilarity.ExactKind);
    }

    [Theory]
    [InlineData("lasagna", "best lasagna ever")] // query is a whole-word run in the candidate
    [InlineData("grandma's lasagna", "lasagna")] // candidate is a whole-word run in the query
    [InlineData("chicken curry", "easy chicken curry")] // multi-word run at the start
    public void Whole_word_containment_matches_in_either_direction(string query, string candidate)
    {
        var result = RecipeNameSimilarity.Evaluate(query, candidate);

        result.Should().NotBeNull();
        result!.Kind.Should().Be(RecipeNameSimilarity.ContainmentKind);
    }

    [Theory]
    [InlineData("lasagna", "lasagnabake")] // raw substring, not a word boundary
    [InlineData("curry", "currywurst")] // prefix of a longer word
    public void Raw_substring_without_a_word_boundary_does_not_match(string query, string candidate)
    {
        RecipeNameSimilarity.Evaluate(query, candidate).Should().BeNull();
    }

    [Fact]
    public void A_two_character_edit_on_a_short_name_is_still_the_edit_distance_kind()
    {
        // "stew" -> "stewed" is two insertions, within MaxEditDistance.
        var result = RecipeNameSimilarity.Evaluate("stew", "stewed");

        result.Should().NotBeNull();
        result!.Kind.Should().Be(RecipeNameSimilarity.EditDistanceKind);
    }

    [Fact]
    public void Shared_tokens_are_the_token_overlap_kind()
    {
        var result = RecipeNameSimilarity.Evaluate("chicken curry", "chicken soup");

        result.Should().NotBeNull();
        result!.Kind.Should().Be(RecipeNameSimilarity.TokenOverlapKind);
    }

    [Fact]
    public void A_single_character_token_does_not_create_an_overlap_by_itself()
    {
        // "grandma's" splits into grandma + s; the lone "s" must not match.
        RecipeNameSimilarity.Evaluate("grandma's lasagna", "mama's pudding").Should().BeNull();
    }

    [Theory]
    [InlineData("lasagna", "lasagnia")] // one insertion
    [InlineData("brownie", "browny")] // one substitution
    [InlineData("chili", "chilis")] // one deletion
    public void A_small_edit_distance_is_the_edit_distance_kind(string query, string candidate)
    {
        var result = RecipeNameSimilarity.Evaluate(query, candidate);

        result.Should().NotBeNull();
        result!.Kind.Should().Be(RecipeNameSimilarity.EditDistanceKind);
    }

    [Theory]
    [InlineData("pasta", "pizza")]
    [InlineData("lasagna", "enchiladas")]
    public void A_large_edit_distance_does_not_match(string query, string candidate)
    {
        RecipeNameSimilarity.Evaluate(query, candidate).Should().BeNull();
    }

    [Fact]
    public void Scores_rank_exact_above_containment_above_overlap_above_edit_distance()
    {
        var exact = RecipeNameSimilarity.Evaluate("lasagna", "lasagna")!;
        var containment = RecipeNameSimilarity.Evaluate("lasagna", "best lasagna ever")!;
        var overlap = RecipeNameSimilarity.Evaluate("chicken curry", "chicken soup")!;
        var editDistance = RecipeNameSimilarity.Evaluate("lasagna", "lasagnia")!;

        exact.Score.Should().BeGreaterThan(containment.Score);
        containment.Score.Should().BeGreaterThan(overlap.Score);
        overlap.Score.Should().BeGreaterThan(editDistance.Score);
    }

    [Fact]
    public void More_shared_signals_score_higher()
    {
        var one = RecipeNameSimilarity.Evaluate("chicken curry", "chicken soup")!;
        var two = RecipeNameSimilarity.Evaluate("chicken curry", "curry chicken bake")!;

        two.Score.Should().BeGreaterThan(one.Score);
    }

    [Theory]
    [InlineData("", "lasagna")]
    [InlineData("lasagna", "")]
    [InlineData("   ", "lasagna")]
    public void Blank_inputs_never_match(string query, string candidate)
    {
        RecipeNameSimilarity.Evaluate(query, candidate).Should().BeNull();
    }

    [Fact]
    public void Evaluation_is_deterministic()
    {
        var first = RecipeNameSimilarity.Evaluate("chicken curry", "easy chicken curry bake");
        var second = RecipeNameSimilarity.Evaluate("chicken curry", "easy chicken curry bake");

        first.Should().Be(second);
    }

    [Fact]
    public void Tokenize_splits_on_punctuation()
    {
        RecipeNameSimilarity.Tokenize("grandma's banana-bread 2.0")
            .Should().Equal("grandma", "s", "banana", "bread", "2", "0");
    }
}
