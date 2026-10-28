using FateAndFridge.Api.Domain;

namespace FateAndFridge.Api.Services;

/// <summary>
/// Immutable projection of the household randomizer settings
/// Scoring is pure: the caller reads the stored row once
/// per operation and passes these values to <see cref="RandomizerEngine"/>.
/// </summary>
public sealed record RandomizerOptions(
    int RepeatLookbackDays,
    int RepeatLookaheadDays,
    decimal RepeatMultiplier,
    decimal SpecialDayMultiplier,
    decimal ProteinMultiplier,
    int ProteinNeighborDistance,
    decimal FavoriteMultiplier,
    int SpacingNearDays,
    int SpacingFarDays,
    int SpacingSearchDays)
{
    /// <summary>Factor applied when the nearest occurrence is within the near window.</summary>
    public const decimal SpacingNearFactor = 0.5m;

    /// <summary>Factor applied when the nearest occurrence sits between near and far.</summary>
    public const decimal SpacingMiddleFactor = 0.75m;

    /// <summary>Factor applied when the nearest occurrence is at or beyond the far window.</summary>
    public const decimal SpacingFarFactor = 1.0m;

    /// <summary>Projects the persisted household settings onto the scoring options.</summary>
    public static RandomizerOptions From(HouseholdSettings settings) => new(
        settings.RandomizerRepeatLookbackDays,
        settings.RandomizerRepeatLookaheadDays,
        settings.RandomizerRepeatMultiplier,
        settings.RandomizerSpecialDayMultiplier,
        settings.RandomizerProteinMultiplier,
        settings.RandomizerProteinNeighborDistance,
        settings.RandomizerFavoriteMultiplier,
        settings.RandomizerSpacingNearDays,
        settings.RandomizerSpacingFarDays,
        settings.RandomizerSpacingSearchDays);
}
