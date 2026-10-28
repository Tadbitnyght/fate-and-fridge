using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FateAndFridge.Api.Domain;

namespace FateAndFridge.Api.Services;

/// <summary>
/// The stable, deterministic identity shared by the grocery list projection and
/// the household check API. The identity is
/// derived only from the <b>canonical ingredient id</b> plus the
/// <b>unit dimension</b> — never from the quantity, the displayed unit of a
/// mass/volume line, or a preparation note — so a checked item survives quantity
/// changes and range expansion, and different dimensions never collapse into one
/// checkbox.
/// </summary>
/// <remarks>
/// <para>
/// Canonical string format (ordinal, culture-invariant; hashed with SHA-256 and
/// rendered as lowercase hex, 64 characters)
/// </para>
/// <list type="bullet">
/// <item><description>Mass: <c>recipe|{ingredientId}|mass</c></description></item>
/// <item><description>Volume: <c>recipe|{ingredientId}|volume</c></description></item>
/// <item><description>Count: <c>recipe|{ingredientId}|count|{unitToken}</c></description></item>
/// <item><description>Nonconvertible: <c>recipe|{ingredientId}|nonconvertible|{unitToken}</c></description></item>
/// <item><description>Manual item: <c>manual|{manualItemId}</c></description></item>
/// </list>
/// <para>
/// The <c>unitToken</c> is <c>id:{countUnitId}</c> when the unit is persisted
/// otherwise <c>sym:{lowercased symbol}</c> when only a symbol is available, and
/// otherwise <c>none</c> for a unitless count. Mass and volume deliberately omit
/// the unit: every lb/oz/g line for one ingredient shares a single identity, and
/// only the count bucket (where units never convert) is unit-specific. A unitless
/// recipe line is keyed as <c>count</c> with the <c>none</c> token, and thus stays
/// separate from an explicit <c>each</c> line. Because the canonical string is
/// culture-invariant and never calls <c>String.Normalize</c>, the hash is
/// identical under invariant globalization.
/// </para>
/// <para>
/// Persists exactly this string in
/// <c>grocery_check_state.item_identity_hash</c> and uses it in the
/// check/uncheck request body, so the list and the check API must always agree.
/// </para>
/// </remarks>
public static class GroceryItemIdentity
{
    private const string RecipePrefix = "recipe";

    private const string ManualPrefix = "manual";

    private const string NoneToken = "none";

    /// <summary>
    /// Identity for a recipe-derived line from its canonical ingredient and unit
    /// dimension. <paramref name="countUnitId"/> and
    /// <paramref name="countUnitSymbol"/> disambiguate the count bucket (and any
    /// nonconvertible unit); they are ignored for mass and volume.
    /// </summary>
    public static string ForRecipeItem(
        long ingredientId,
        UnitDimension dimension,
        long? countUnitId,
        string? countUnitSymbol) =>
        Hash(CanonicalForRecipeItem(ingredientId, dimension, countUnitId, countUnitSymbol));

    /// <summary>Identity for a manual grocery line, from the manual item id alone.</summary>
    public static string ForManualItem(long manualItemId) =>
        Hash(CanonicalForManualItem(manualItemId));

    /// <summary>
    /// The exact pre-hash canonical string for a recipe-derived line. Exposed
    /// internals-only so tests can pin the format that must reproduce.
    /// </summary>
    internal static string CanonicalForRecipeItem(
        long ingredientId,
        UnitDimension dimension,
        long? countUnitId,
        string? countUnitSymbol)
    {
        var id = ingredientId.ToString(CultureInfo.InvariantCulture);
        return dimension is UnitDimension.Count or UnitDimension.Nonconvertible
            ? $"{RecipePrefix}|{id}|{DimensionToken(dimension)}|{UnitToken(countUnitId, countUnitSymbol)}"
            : $"{RecipePrefix}|{id}|{DimensionToken(dimension)}";
    }

    /// <summary>The exact pre-hash canonical string for a manual line.</summary>
    internal static string CanonicalForManualItem(long manualItemId) =>
        $"{ManualPrefix}|{manualItemId.ToString(CultureInfo.InvariantCulture)}";

    private static string DimensionToken(UnitDimension dimension) => dimension switch
    {
        UnitDimension.Mass => "mass",
        UnitDimension.Volume => "volume",
        UnitDimension.Count => "count",
        UnitDimension.Nonconvertible => "nonconvertible",
        _ => throw new ArgumentOutOfRangeException(
            nameof(dimension),
            dimension,
            "Unknown unit dimension."),
    };

    private static string UnitToken(long? unitId, string? symbol)
    {
        if (unitId.HasValue)
        {
            return "id:" + unitId.Value.ToString(CultureInfo.InvariantCulture);
        }

        return string.IsNullOrWhiteSpace(symbol)
            ? NoneToken
            : "sym:" + symbol.ToLowerInvariant();
    }

    private static string Hash(string canonical) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
}
