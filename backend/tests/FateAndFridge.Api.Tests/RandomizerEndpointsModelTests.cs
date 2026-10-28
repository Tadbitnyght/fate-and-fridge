using System.Text.Json;
using FateAndFridge.Api.Services;
using FluentAssertions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Unit coverage for the reroll operation metadata serialization
/// the documented snake_case keys, in order, including
/// the nullable spacing fields and the <c>"kind"</c> discriminator.
/// </summary>
public sealed class RandomizerEndpointsModelTests
{
    private static readonly string[] DocumentedKeys =
    [
        "kind",
        "candidate_count",
        "chosen_recipe_id",
        "base_weight",
        "repeat_multiplier",
        "repeat_occurrences",
        "protein_multiplier",
        "shared_proteins",
        "spacing_days",
        "spacing_multiplier",
        "favorite_multiplier",
        "final_weight",
    ];

    [Fact]
    public void SerializeMetadata_emits_the_documented_snake_case_keys()
    {
        var metadata = new RandomizerMetadata(
            CandidateCount: 42,
            ChosenRecipeId: 5,
            BaseWeight: 1.0m,
            RepeatMultiplier: 0.10m,
            RepeatOccurrences: 2,
            ProteinMultiplier: 0.70m,
            SharedProteins: new[] { "chicken" },
            SpacingDays: 9,
            SpacingMultiplier: 1.0m,
            FavoriteMultiplier: 1.10m,
            FinalWeight: 0.077m);

        using var document = JsonDocument.Parse(CalendarRandomizer.SerializeMetadata(metadata));
        var root = document.RootElement;

        root.EnumerateObject().Select(property => property.Name).Should().Equal(DocumentedKeys);
        root.GetProperty("kind").GetString().Should().Be("reroll");
        root.GetProperty("candidate_count").GetInt32().Should().Be(42);
        root.GetProperty("chosen_recipe_id").GetInt64().Should().Be(5);
        root.GetProperty("base_weight").GetDecimal().Should().Be(1.0m);
        root.GetProperty("repeat_multiplier").GetDecimal().Should().Be(0.10m);
        root.GetProperty("repeat_occurrences").GetInt32().Should().Be(2);
        root.GetProperty("protein_multiplier").GetDecimal().Should().Be(0.70m);
        root.GetProperty("shared_proteins").EnumerateArray()
            .Select(item => item.GetString()).Should().Equal("chicken");
        root.GetProperty("spacing_days").GetInt32().Should().Be(9);
        root.GetProperty("spacing_multiplier").GetDecimal().Should().Be(1.0m);
        root.GetProperty("favorite_multiplier").GetDecimal().Should().Be(1.10m);
        root.GetProperty("final_weight").GetDecimal().Should().Be(0.077m);
    }

    [Fact]
    public void SerializeMetadata_emits_null_and_empty_collections_when_absent()
    {
        var metadata = new RandomizerMetadata(
            CandidateCount: 1,
            ChosenRecipeId: 2,
            BaseWeight: 1.0m,
            RepeatMultiplier: 1.0m,
            RepeatOccurrences: 0,
            ProteinMultiplier: 1.0m,
            SharedProteins: Array.Empty<string>(),
            SpacingDays: null,
            SpacingMultiplier: 1.0m,
            FavoriteMultiplier: 1.0m,
            FinalWeight: 1.0m);

        using var document = JsonDocument.Parse(CalendarRandomizer.SerializeMetadata(metadata));
        var root = document.RootElement;

        root.GetProperty("spacing_days").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("shared_proteins").GetArrayLength().Should().Be(0);
    }
}
