using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Services;
using FluentAssertions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Deterministic matrix for the M6 pure randomizer engine
/// exact-repeat windows
/// special-day contribution, protein adjacency, spacing buckets, favorites
/// secure weighted selection, and the small-pool fallback. All draws go through
/// a controlled <see cref="IRandomSource"/>, so no test depends on OS entropy.
/// </summary>
public sealed class RandomizerEngineTests
{
    private static readonly DateOnly D = new(2026, 10, 7);

    private static readonly RandomizerOptions Defaults = new(
        RepeatLookbackDays: 7,
        RepeatLookaheadDays: 14,
        RepeatMultiplier: 0.10m,
        SpecialDayMultiplier: 0.02m,
        ProteinMultiplier: 0.70m,
        ProteinNeighborDistance: 1,
        FavoriteMultiplier: 1.10m,
        SpacingNearDays: 3,
        SpacingFarDays: 8,
        SpacingSearchDays: 90);

    // Scoring helpers

    private static RandomizerCandidate Candidate(long recipeId, bool favorite = false, params string[] proteins) =>
        new(recipeId, favorite, new HashSet<string>(proteins, StringComparer.Ordinal));

    private static RandomizerNeighbor Neighbor(DateOnly date, params string[] proteins) =>
        new(date, new HashSet<string>(proteins, StringComparer.Ordinal));

    private static RandomizerRequest Single(
        RandomizerCandidate candidate,
        IReadOnlyList<RandomizerOccurrence>? occurrences = null,
        IReadOnlyList<RandomizerNeighbor>? neighbors = null) =>
        new(
            D,
            new[] { candidate },
            occurrences ?? Array.Empty<RandomizerOccurrence>(),
            neighbors ?? Array.Empty<RandomizerNeighbor>());

    private static RandomizerMetadata Score(
        RandomizerRequest request,
        RandomizerCandidate candidate,
        RandomizerOptions? options = null) =>
        new RandomizerEngine(new FixedRandomSource(0m)).ScoreCandidate(options ?? Defaults, request, candidate);

    // Normal scoring

    [Fact]
    public void A_clean_candidate_scores_the_neutral_product()
    {
        var candidate = Candidate(1);
        var metadata = Score(Single(candidate), candidate);

        metadata.CandidateCount.Should().Be(1);
        metadata.ChosenRecipeId.Should().Be(1);
        metadata.BaseWeight.Should().Be(1.0m);
        metadata.RepeatMultiplier.Should().Be(1.0m);
        metadata.RepeatOccurrences.Should().Be(0);
        metadata.ProteinMultiplier.Should().Be(1.0m);
        metadata.SharedProteins.Should().BeEmpty();
        metadata.SpacingDays.Should().BeNull();
        metadata.SpacingMultiplier.Should().Be(1.0m);
        metadata.FavoriteMultiplier.Should().Be(1.0m);
        metadata.FinalWeight.Should().Be(1.0m);
    }

    [Fact]
    public void A_favorite_candidate_uses_the_favorite_multiplier()
    {
        var candidate = Candidate(1, favorite: true);
        var metadata = Score(Single(candidate), candidate);

        metadata.FavoriteMultiplier.Should().Be(1.10m);
        metadata.FinalWeight.Should().Be(1.10m);
    }

    [Fact]
    public void Default_multipliers_compose_to_the_documented_product()
    {
        var candidate = Candidate(1, favorite: true, "chicken");
        var request = Single(
            candidate,
            new[] { new RandomizerOccurrence(1, D.AddDays(-2), false) },
            new[] { Neighbor(D.AddDays(-1), "chicken") });

        var metadata = Score(request, candidate);

        metadata.RepeatMultiplier.Should().Be(0.10m);
        metadata.ProteinMultiplier.Should().Be(0.70m);
        metadata.SpacingMultiplier.Should().Be(0.5m);
        metadata.FavoriteMultiplier.Should().Be(1.10m);
        metadata.FinalWeight.Should().Be(0.0385m);
    }

    // Exact-repeat window

    [Theory]
    [InlineData(-7, true, 0.10)]
    [InlineData(-8, false, 1.0)]
    [InlineData(14, true, 0.10)]
    [InlineData(15, false, 1.0)]
    public void Repeat_window_boundaries_are_inclusive(int offset, bool inWindow, double expected)
    {
        var candidate = Candidate(1);
        var request = Single(candidate, new[] { new RandomizerOccurrence(1, D.AddDays(offset), false) });

        var metadata = Score(request, candidate);

        metadata.RepeatOccurrences.Should().Be(inWindow ? 1 : 0);
        metadata.RepeatMultiplier.Should().Be((decimal)expected);
    }

    [Fact]
    public void A_special_day_occurrence_uses_the_special_day_multiplier()
    {
        var candidate = Candidate(1);
        var request = Single(candidate, new[] { new RandomizerOccurrence(1, D.AddDays(-3), true) });

        var metadata = Score(request, candidate);

        metadata.RepeatOccurrences.Should().Be(1);
        metadata.RepeatMultiplier.Should().Be(0.02m);
    }

    [Fact]
    public void A_normal_occurrence_wins_over_a_special_day_occurrence()
    {
        var candidate = Candidate(1);
        var request = Single(candidate, new[]
        {
            new RandomizerOccurrence(1, D.AddDays(2), true),
            new RandomizerOccurrence(1, D.AddDays(-2), false),
        });

        var metadata = Score(request, candidate);

        metadata.RepeatOccurrences.Should().Be(2);
        metadata.RepeatMultiplier.Should().Be(0.10m);
    }

    [Fact]
    public void Past_and_future_occurrences_count_and_the_candidate_date_is_excluded()
    {
        var candidate = Candidate(1);
        var request = Single(candidate, new[]
        {
            new RandomizerOccurrence(1, D.AddDays(-1), false),
            new RandomizerOccurrence(1, D, false),
            new RandomizerOccurrence(1, D.AddDays(1), false),
        });

        var metadata = Score(request, candidate);

        metadata.RepeatOccurrences.Should().Be(2);
        metadata.RepeatMultiplier.Should().Be(0.10m);
    }

    [Fact]
    public void Occurrences_of_other_recipes_do_not_affect_the_candidate()
    {
        var candidate = Candidate(1);
        var request = Single(candidate, new[] { new RandomizerOccurrence(2, D.AddDays(-2), false) });

        Score(request, candidate).RepeatMultiplier.Should().Be(1.0m);
    }

    // Protein adjacency

    [Fact]
    public void A_shared_protein_with_the_previous_day_is_penalized_once()
    {
        var candidate = Candidate(1, false, "chicken");
        var request = Single(candidate, neighbors: new[] { Neighbor(D.AddDays(-1), "chicken") });

        var metadata = Score(request, candidate);

        metadata.ProteinMultiplier.Should().Be(0.70m);
        metadata.SharedProteins.Should().Equal("chicken");
    }

    [Fact]
    public void A_shared_protein_with_the_next_day_is_penalized_once()
    {
        var candidate = Candidate(1, false, "beef");
        var request = Single(candidate, neighbors: new[] { Neighbor(D.AddDays(1), "beef") });

        Score(request, candidate).ProteinMultiplier.Should().Be(0.70m);
    }

    [Fact]
    public void Multiple_shared_proteins_are_penalized_once_and_reported_sorted()
    {
        var candidate = Candidate(1, false, "pork", "chicken");
        var request = Single(candidate, neighbors: new[] { Neighbor(D.AddDays(-1), "chicken", "pork") });

        var metadata = Score(request, candidate);

        metadata.ProteinMultiplier.Should().Be(0.70m);
        metadata.SharedProteins.Should().Equal("chicken", "pork");
    }

    [Fact]
    public void Two_matching_neighbors_still_penalize_only_once()
    {
        var candidate = Candidate(1, false, "chicken");
        var request = Single(candidate, neighbors: new[]
        {
            Neighbor(D.AddDays(-1), "chicken"),
            Neighbor(D.AddDays(1), "chicken"),
        });

        Score(request, candidate).ProteinMultiplier.Should().Be(0.70m);
    }

    [Fact]
    public void Unrelated_proteins_are_not_penalized()
    {
        var candidate = Candidate(1, false, "chicken");
        var request = Single(candidate, neighbors: new[] { Neighbor(D.AddDays(-1), "beef") });

        var metadata = Score(request, candidate);

        metadata.ProteinMultiplier.Should().Be(1.0m);
        metadata.SharedProteins.Should().BeEmpty();
    }

    [Fact]
    public void A_neighbor_outside_the_neighbor_distance_is_ignored()
    {
        var candidate = Candidate(1, false, "chicken");
        var request = Single(candidate, neighbors: new[] { Neighbor(D.AddDays(-2), "chicken") });

        Score(request, candidate).ProteinMultiplier.Should().Be(1.0m);
    }

    [Fact]
    public void A_neighbor_on_the_candidate_date_is_ignored()
    {
        var candidate = Candidate(1, false, "chicken");
        var request = Single(candidate, neighbors: new[] { Neighbor(D, "chicken") });

        Score(request, candidate).ProteinMultiplier.Should().Be(1.0m);
    }

    // Spacing

    [Theory]
    [InlineData(3, 0.5)]
    [InlineData(5, 0.75)]
    [InlineData(8, 1.0)]
    public void Spacing_buckets_map_to_the_documented_factors(int offset, double expected)
    {
        var candidate = Candidate(1);
        var request = Single(candidate, new[] { new RandomizerOccurrence(1, D.AddDays(offset), false) });

        var metadata = Score(request, candidate);

        metadata.SpacingDays.Should().Be(offset);
        metadata.SpacingMultiplier.Should().Be((decimal)expected);
    }

    [Fact]
    public void Spacing_factors_are_the_documented_constants()
    {
        RandomizerOptions.SpacingNearFactor.Should().Be(0.5m);
        RandomizerOptions.SpacingMiddleFactor.Should().Be(0.75m);
        RandomizerOptions.SpacingFarFactor.Should().Be(1.0m);
    }

    [Fact]
    public void No_nearby_occurrence_scores_spacing_as_neutral()
    {
        var candidate = Candidate(1);
        var metadata = Score(Single(candidate), candidate);

        metadata.SpacingDays.Should().BeNull();
        metadata.SpacingMultiplier.Should().Be(1.0m);
    }

    [Fact]
    public void An_occurrence_outside_the_spacing_search_window_is_ignored()
    {
        var candidate = Candidate(1);
        var request = Single(candidate, new[] { new RandomizerOccurrence(1, D.AddDays(-91), false) });

        var metadata = Score(request, candidate);

        metadata.SpacingDays.Should().BeNull();
        metadata.SpacingMultiplier.Should().Be(1.0m);
    }

    [Fact]
    public void The_nearest_of_several_occurrences_drives_spacing()
    {
        var candidate = Candidate(1);
        var request = Single(candidate, new[]
        {
            new RandomizerOccurrence(1, D.AddDays(-5), false),
            new RandomizerOccurrence(1, D.AddDays(-2), false),
        });

        var metadata = Score(request, candidate);

        metadata.SpacingDays.Should().Be(2);
        metadata.SpacingMultiplier.Should().Be(0.5m);
    }

    // Weighted selection

    [Fact]
    public void A_forced_repeat_prefers_the_candidate_with_the_larger_nearest_spacing()
    {
        var nearCandidate = Candidate(1);
        var farCandidate = Candidate(2);
        var request = new RandomizerRequest(
            D,
            new[] { nearCandidate, farCandidate },
            new[]
            {
                new RandomizerOccurrence(1, D.AddDays(-2), false),
                new RandomizerOccurrence(2, D.AddDays(-5), false),
            },
            Array.Empty<RandomizerNeighbor>());

        // Both repeat (0.10); spacing 2 -> 0.5 vs 5 -> 0.75, so the weights are
        // 0.05 and 0.075. A draw in the upper part of the wheel picks recipe 2.
        var result = new RandomizerEngine(new FixedRandomSource(0.9m)).Select(Defaults, request);

        result.RecipeId.Should().Be(2);
        result.Metadata.SpacingMultiplier.Should().Be(0.75m);
    }

    [Fact]
    public void A_much_larger_weight_wins_across_the_random_range()
    {
        var heavy = Candidate(1);
        var light = Candidate(2);
        var request = new RandomizerRequest(
            D,
            new[] { heavy, light },
            new[] { new RandomizerOccurrence(2, D.AddDays(-7), false) },
            Array.Empty<RandomizerNeighbor>());

        foreach (var draw in new[] { 0m, 0.5m, 0.9m })
        {
            var result = new RandomizerEngine(new FixedRandomSource(draw)).Select(Defaults, request);
            result.RecipeId.Should().Be(1);
        }
    }

    [Fact]
    public void The_roulette_boundary_selects_by_cumulative_weight()
    {
        var first = Candidate(1);
        var second = Candidate(2);
        var request = new RandomizerRequest(
            D,
            new[] { first, second },
            new[] { new RandomizerOccurrence(2, D.AddDays(-7), false) },
            Array.Empty<RandomizerNeighbor>());

        // Weights 1.0 and 0.075 (total 1.075): below the boundary picks recipe 1
        // above it picks recipe 2.
        new RandomizerEngine(new FixedRandomSource(0.9m)).Select(Defaults, request).RecipeId.Should().Be(1);
        new RandomizerEngine(new FixedRandomSource(0.96m)).Select(Defaults, request).RecipeId.Should().Be(2);
    }

    [Fact]
    public void An_all_zero_weight_pool_falls_back_to_a_uniform_successful_draw()
    {
        var options = Defaults with { SpecialDayMultiplier = 0.0m };
        var first = Candidate(1);
        var second = Candidate(2);
        var request = new RandomizerRequest(
            D,
            new[] { first, second },
            new[]
            {
                new RandomizerOccurrence(1, D.AddDays(-2), true),
                new RandomizerOccurrence(2, D.AddDays(2), true),
            },
            Array.Empty<RandomizerNeighbor>());

        var firstDraw = new RandomizerEngine(new FixedRandomSource(0m)).Select(options, request);
        var lastDraw = new RandomizerEngine(new FixedRandomSource(0.999999999m)).Select(options, request);

        firstDraw.RecipeId.Should().Be(1);
        lastDraw.RecipeId.Should().Be(2);
        firstDraw.Metadata.FinalWeight.Should().Be(0.0m);
        lastDraw.Metadata.FinalWeight.Should().Be(0.0m);
    }

    [Fact]
    public void An_empty_candidate_pool_throws()
    {
        var request = new RandomizerRequest(
            D,
            Array.Empty<RandomizerCandidate>(),
            Array.Empty<RandomizerOccurrence>(),
            Array.Empty<RandomizerNeighbor>());

        var act = () => new RandomizerEngine(new FixedRandomSource(0m)).Select(Defaults, request);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Selection_is_deterministic_for_a_given_source_value()
    {
        var request = new RandomizerRequest(
            D,
            new[] { Candidate(1), Candidate(2), Candidate(3) },
            Array.Empty<RandomizerOccurrence>(),
            Array.Empty<RandomizerNeighbor>());

        var first = new RandomizerEngine(new FixedRandomSource(0.42m)).Select(Defaults, request);
        var second = new RandomizerEngine(new FixedRandomSource(0.42m)).Select(Defaults, request);

        first.RecipeId.Should().Be(second.RecipeId);
        first.Metadata.FinalWeight.Should().Be(second.Metadata.FinalWeight);
    }

    // Options projection

    [Fact]
    public void Options_from_maps_every_household_field()
    {
        var settings = new HouseholdSettings
        {
            HouseholdName = "Test Household",
            Timezone = "America/New_York",
            RandomizerRepeatLookbackDays = 1,
            RandomizerRepeatLookaheadDays = 2,
            RandomizerRepeatMultiplier = 0.11m,
            RandomizerSpecialDayMultiplier = 0.22m,
            RandomizerProteinMultiplier = 0.33m,
            RandomizerProteinNeighborDistance = 4,
            RandomizerFavoriteMultiplier = 1.44m,
            RandomizerSpacingNearDays = 5,
            RandomizerSpacingFarDays = 6,
            RandomizerSpacingSearchDays = 7,
        };

        var options = RandomizerOptions.From(settings);

        options.Should().Be(new RandomizerOptions(1, 2, 0.11m, 0.22m, 0.33m, 4, 1.44m, 5, 6, 7));
    }

    private sealed class FixedRandomSource(decimal value) : IRandomSource
    {
        public decimal NextUnitInterval() => value;
    }
}
