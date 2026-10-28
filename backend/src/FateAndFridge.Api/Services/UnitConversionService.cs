using FateAndFridge.Api.Domain;

namespace FateAndFridge.Api.Services;

/// <summary>
/// One unit as the conversion service needs it. This is a pure input
/// record, deliberately decoupled from the EF Core <c>Unit</c> entity and
/// <c>AppDbContext</c>: consumers (M8 grocery aggregation) project unit rows
/// into it before calling the service.
/// </summary>
/// <param name="Id">Database id when the unit is persisted; null otherwise.</param>
/// <param name="Symbol">Unit symbol; decides the same-count-unit rule when either id is null.</param>
/// <param name="Dimension">Measurement family (docs/DATABASE.md §5).</param>
/// <param name="BaseFactor">
/// Conversion factor to the dimension base unit (grams for mass, millilitres for
/// volume); null for count and nonconvertible units. A null or non-positive
/// factor on a mass/volume unit is treated as missing.
/// </param>
public sealed record ConversionUnit(
    long? Id,
    string Symbol,
    UnitDimension Dimension,
    decimal? BaseFactor);

/// <summary>Why a conversion request could not be satisfied.</summary>
public enum ConversionFailureReason
{
    /// <summary>The units belong to different dimensions; only mass↔mass and volume↔volume convert.</summary>
    IncompatibleDimensions,

    /// <summary>Two different count units were combined; count units never convert (no package-size inference).</summary>
    DifferentCountUnits,

    /// <summary>The unit is nonconvertible (for example "to taste"), so every conversion request fails.</summary>
    NonConvertible,

    /// <summary>The quantity was zero or negative; only positive quantities are convertible.</summary>
    ZeroOrNegativeQuantity,

    /// <summary>A mass/volume unit had no usable (positive) base factor.</summary>
    MissingFactor,

    /// <summary>The decimal computation exceeded the representable range; no exception is thrown.</summary>
    Overflow,
}

/// <summary>Outcome of a unit conversion or base-normalization request.</summary>
public abstract record ConversionResult
{
    private ConversionResult()
    {
    }

    /// <summary>The conversion succeeded and produced the converted quantity.</summary>
    public sealed record Success(decimal Quantity) : ConversionResult;

    /// <summary>The conversion is not defined for the supplied units or quantity.</summary>
    public sealed record Failure(ConversionFailureReason Reason) : ConversionResult;
}

/// <summary>
/// Pure, dependency-free unit conversion (docs/DATABASE.md §5
/// docs/ARCHITECTURE.md §15). It operates only on <see cref="ConversionUnit"/>
/// values — never on <c>AppDbContext</c> — and is stateless/thread-safe, so the
/// application registers it as a singleton.
/// </summary>
/// <remarks>
/// <para>
/// Rules: mass↔mass and volume↔volume convert using the configured factors
/// (base: grams and millilitres); cross-dimension requests fail with
/// <see cref="ConversionFailureReason.IncompatibleDimensions"/>. Count units
/// only combine with the same unit — the same <c>Id</c> when both are
/// persisted, otherwise an equal symbol compared case-insensitively — and
/// never convert between different count units or infer package sizes.
/// Nonconvertible units ("to taste") fail every conversion with
/// <see cref="ConversionFailureReason.NonConvertible"/>; quantity-less domain
/// values are carried through aggregation without math by M8, which simply
/// does not call this service for them.
/// </para>
/// <para>
/// Failure precedence, applied consistently by <see cref="Convert"/> and
/// <see cref="ToBase"/>: a zero/negative quantity is rejected first, then
/// nonconvertible units, then incompatible dimensions, then different count
/// units, then a missing factor, and finally arithmetic overflow.
/// </para>
/// <para>
/// Rounding policy: all arithmetic uses <see cref="decimal"/> only (no
/// <c>double</c>/<c>float</c> anywhere). The service never rounds or truncates
/// on its own. Identity conversions are exact and bypass arithmetic entirely
/// the same persisted <c>Id</c>, an equal symbol when either id is absent, or
/// two equal non-null base factors return the quantity unchanged. Only when
/// factors differ does the service compute
/// <c>quantity * from.BaseFactor / to.BaseFactor</c> and inherit
/// <see cref="decimal"/> division semantics — a non-terminating quotient is
/// rounded to the nearest representable decimal by the runtime, and an
/// intermediate product may overflow even when the final quotient would fit
/// which surfaces as <see cref="ConversionFailureReason.Overflow"/>. Display
/// formatting and any presentation rounding belong to the caller.
/// </para>
/// </remarks>
public sealed class UnitConversionService
{
    /// <summary>
    /// Converts <paramref name="quantity"/> of <paramref name="from"/> into
    /// <paramref name="to"/> (for example 3 tsp → tbsp, or 1 cup → 236.5882365 mL
    /// through <see cref="ToBase"/>).
    /// </summary>
    public ConversionResult Convert(ConversionUnit from, ConversionUnit to, decimal quantity)
    {
        if (quantity <= 0m)
        {
            return Failure(ConversionFailureReason.ZeroOrNegativeQuantity);
        }

        if (from.Dimension == UnitDimension.Nonconvertible || to.Dimension == UnitDimension.Nonconvertible)
        {
            return Failure(ConversionFailureReason.NonConvertible);
        }

        if (from.Dimension != to.Dimension)
        {
            return Failure(ConversionFailureReason.IncompatibleDimensions);
        }

        if (from.Dimension == UnitDimension.Count)
        {
            return IsSameUnit(from, to)
                ? new ConversionResult.Success(quantity)
                : Failure(ConversionFailureReason.DifferentCountUnits);
        }

        if (!TryGetFactor(from, out var fromFactor) || !TryGetFactor(to, out var toFactor))
        {
            return Failure(ConversionFailureReason.MissingFactor);
        }

        if (IsSameUnit(from, to) || fromFactor == toFactor)
        {
            return new ConversionResult.Success(quantity);
        }

        return TryConvert(quantity, fromFactor, toFactor, out var converted)
            ? new ConversionResult.Success(converted)
            : Failure(ConversionFailureReason.Overflow);
    }

    /// <summary>
    /// Converts <paramref name="quantity"/> of <paramref name="unit"/> to its
    /// dimension base unit: grams for mass, millilitres for volume. Count units
    /// are an identity conversion; nonconvertible units always fail.
    /// </summary>
    public ConversionResult ToBase(ConversionUnit unit, decimal quantity)
    {
        if (quantity <= 0m)
        {
            return Failure(ConversionFailureReason.ZeroOrNegativeQuantity);
        }

        if (unit.Dimension == UnitDimension.Nonconvertible)
        {
            return Failure(ConversionFailureReason.NonConvertible);
        }

        if (unit.Dimension == UnitDimension.Count)
        {
            return new ConversionResult.Success(quantity);
        }

        if (!TryGetFactor(unit, out var factor))
        {
            return Failure(ConversionFailureReason.MissingFactor);
        }

        return TryMultiply(quantity, factor, out var baseQuantity)
            ? new ConversionResult.Success(baseQuantity)
            : Failure(ConversionFailureReason.Overflow);
    }

    /// <summary>
    /// Whether two units may be combined in one aggregation bucket: two mass
    /// units, two volume units, or the same count unit. The check is structural —
    /// it does not validate factors or arithmetic — so a factor-less mass unit
    /// still combines with another mass unit, and callers (M8) must still handle
    /// <see cref="ConversionFailureReason.MissingFactor"/> and
    /// <see cref="ConversionFailureReason.Overflow"/> failures from
    /// <see cref="Convert"/> and <see cref="ToBase"/>. Nonconvertible units never
    /// combine.
    /// </summary>
    public bool CanCombine(ConversionUnit a, ConversionUnit b)
    {
        if (a.Dimension != b.Dimension)
        {
            return false;
        }

        return a.Dimension switch
        {
            UnitDimension.Mass or UnitDimension.Volume => true,
            UnitDimension.Count => IsSameUnit(a, b),
            _ => false,
        };
    }

    private static ConversionResult.Failure Failure(ConversionFailureReason reason) => new(reason);

    private static bool IsSameUnit(ConversionUnit a, ConversionUnit b) =>
        a.Id.HasValue && b.Id.HasValue
            ? a.Id.Value == b.Id.Value
            : string.Equals(a.Symbol, b.Symbol, StringComparison.OrdinalIgnoreCase);

    private static bool TryGetFactor(ConversionUnit unit, out decimal factor)
    {
        if (unit.BaseFactor is { } value && value > 0m)
        {
            factor = value;
            return true;
        }

        factor = 0m;
        return false;
    }

    private static bool TryConvert(decimal quantity, decimal fromFactor, decimal toFactor, out decimal result)
    {
        try
        {
            result = quantity * fromFactor / toFactor;
            return true;
        }
        catch (OverflowException)
        {
            result = 0m;
            return false;
        }
    }

    private static bool TryMultiply(decimal quantity, decimal factor, out decimal result)
    {
        try
        {
            result = quantity * factor;
            return true;
        }
        catch (OverflowException)
        {
            result = 0m;
            return false;
        }
    }
}
