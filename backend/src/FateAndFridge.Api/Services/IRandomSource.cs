using System.Security.Cryptography;

namespace FateAndFridge.Api.Services;

/// <summary>
/// Injectable randomness seam for the randomizer (docs/RANDOMIZER.md §7 Phase F).
/// Tests substitute a controlled implementation so selection is deterministic.
/// </summary>
public interface IRandomSource
{
    /// <summary>Uniform decimal in [0, 1).</summary>
    decimal NextUnitInterval();
}

/// <summary>
/// Cryptographically secure <see cref="IRandomSource"/> backed by the operating
/// system entropy pool (<see cref="RandomNumberGenerator"/>). Production code
/// never derives randomness from the date, recipe count, or database ids.
/// </summary>
public sealed class SystemRandomSource : IRandomSource
{
    private const int Scale = 1_000_000_000;

    /// <inheritdoc />
    public decimal NextUnitInterval() =>
        RandomNumberGenerator.GetInt32(0, Scale) / (decimal)Scale;
}
