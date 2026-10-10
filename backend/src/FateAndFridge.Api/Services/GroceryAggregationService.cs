using System.Globalization;
using FateAndFridge.Api.Domain;

namespace FateAndFridge.Api.Services;

/// <summary>
/// One recipe ingredient line supplied to <see cref="GroceryAggregationService"/>
/// (docs/M8_PLAN.md §1.4, §2.1). The caller resolves aliases to the canonical
/// <paramref name="IngredientId"/> first; aggregation never sees aliases. There is
/// deliberately no preparation-note field: a note can therefore never influence
/// identity or math, and any representative note shown for a combined line is a
/// display concern owned by the list service.
/// </summary>
/// <param name="IngredientId">Canonical ingredient id (aliases already resolved).</param>
/// <param name="IngredientName">Canonical ingredient display name.</param>
/// <param name="QuantityNumeric">Precise quantity, or null for a quantity-less line.</param>
/// <param name="Unit">Projected unit, or null for a unitless count.</param>
/// <param name="RecipeId">Contributing recipe.</param>
/// <param name="RecipeName">Contributing recipe name (for attribution).</param>
/// <param name="MealDate">Household-local calendar date of the contributing meal.</param>
public sealed record GroceryIngredientInput(
    long IngredientId,
    string IngredientName,
    decimal? QuantityNumeric,
    ConversionUnit? Unit,
    long RecipeId,
    string RecipeName,
    DateOnly MealDate);

/// <summary>
/// One contributing calendar meal for a recipe-derived line (docs/M8_PLAN.md §1.6).
/// Identical <c>(RecipeId, MealDate)</c> pairs are deduplicated, so a recipe that
/// lists the same ingredient twice still contributes once per meal.
/// </summary>
public sealed record GroceryItemSource(long RecipeId, string RecipeName, DateOnly MealDate);

/// <summary>
/// One aggregated grocery line (docs/M8_PLAN.md §1.4–§1.6, §2.1). The
/// <see cref="IdentityHash"/> is the shared
/// <see cref="GroceryItemIdentity"/> value the check API (M8-06) keys on.
/// <see cref="Quantity"/> and <see cref="UnitSymbol"/> expose the numeric value
/// and display unit; <see cref="QuantityText"/> is the ready-to-render text
/// (exact common fraction, else up to two decimals, else empty for a
/// quantity-less line).
/// </summary>
public sealed record GroceryAggregatedItem(
    string IdentityHash,
    long IngredientId,
    string Name,
    UnitDimension? Dimension,
    decimal? Quantity,
    string? UnitSymbol,
    string QuantityText,
    IReadOnlyList<GroceryItemSource> Sources);

/// <summary>
/// Pure, stateless grocery aggregation over supplied recipe ingredients
/// (docs/M8_PLAN.md §1.4–§1.6, §2.1; docs/PRODUCT_SPEC.md §20–§22). It performs
/// no database access and no I/O: callers project calendar/recipe/unit rows into
/// <see cref="GroceryIngredientInput"/> and receive aggregated lines. The only
/// dependency is the pure <see cref="UnitConversionService"/>, so the application
/// registers this as a singleton.
/// </summary>
/// <remarks>
/// <para>
/// Buckets are keyed by <see cref="GroceryItemIdentity"/> — canonical ingredient
/// plus unit dimension, and (for count and nonconvertible units) the unit — so
/// compatible quantities in one dimension combine, while incompatible dimensions
/// (1 onion vs ½ cup chopped onion) and different count units stay separate.
/// Package-size inference is never attempted.
/// </para>
/// <para>
/// Mass and volume combine through <see cref="UnitConversionService"/>, and the
/// combined total is shown in the largest compatible unit that appears among the
/// bucket's contributions (1 lb + 8 oz → 1.5 lb, measured by
/// <see cref="ConversionUnit.BaseFactor"/>). A bucket with a single present unit
/// uses that unit unchanged. Counts (and unitless lines) combine by direct
/// addition within the same unit; count units never convert.
/// </para>
/// <para>
/// Defensive conversion handling: if a quantity-bearing mass/volume contribution
/// cannot be converted to the chosen display unit (a missing factor or decimal
/// overflow), it is never dropped and never guessed at. It is emitted as its own
/// line carrying its own unit, with the reason documented here. That line reuses
/// the dimension identity on purpose, so household check state stays stable even
/// if the missing factor is later configured (the line then merges back and the
/// checkbox is preserved). Reference data normally guarantees factors, so this is
/// a defensive path; a quantity-less contribution needs no conversion and joins
/// the main line.
/// </para>
/// <para>
/// Output is deterministic: items are ordered by ingredient name, then identity,
/// then display unit; attribution rows are chronological, then by recipe id.
/// Final department/checked ordering belongs to the list service (M8-03).
/// </para>
/// </remarks>
public sealed class GroceryAggregationService
{
    private const decimal FractionTolerance = 0.000001m;

    /// <summary>
    /// Cooking fractions up to eighths, checked in the same ascending "nicest
    /// first" order as the frontend <c>formatQuantity</c> helper
    /// (docs/PRODUCT_SPEC.md §37).
    /// </summary>
    private static readonly (int Numerator, int Denominator)[] Fractions =
    [
        (1, 8),
        (1, 6),
        (1, 5),
        (1, 4),
        (1, 3),
        (3, 8),
        (2, 5),
        (1, 2),
        (3, 5),
        (5, 8),
        (2, 3),
        (3, 4),
        (4, 5),
        (5, 6),
        (7, 8),
    ];

    private readonly UnitConversionService _conversion;

    public GroceryAggregationService(UnitConversionService conversion)
    {
        ArgumentNullException.ThrowIfNull(conversion);
        _conversion = conversion;
    }

    /// <summary>
    /// Aggregates the supplied recipe ingredient lines into display lines.
    /// </summary>
    public IReadOnlyList<GroceryAggregatedItem> Aggregate(
        IEnumerable<GroceryIngredientInput> ingredients)
    {
        ArgumentNullException.ThrowIfNull(ingredients);

        var items = new List<GroceryAggregatedItem>();
        foreach (var group in ingredients.GroupBy(IdentityOf))
        {
            items.AddRange(AggregateGroup(group));
        }

        return items
            .OrderBy(item => item.Name, StringComparer.Ordinal)
            .ThenBy(item => item.IdentityHash, StringComparer.Ordinal)
            .ThenBy(item => item.UnitSymbol ?? string.Empty, StringComparer.Ordinal)
            .ToList();
    }

    private static string IdentityOf(GroceryIngredientInput input)
    {
        var dimension = input.Unit?.Dimension ?? UnitDimension.Count;
        return GroceryItemIdentity.ForRecipeItem(
            input.IngredientId,
            dimension,
            input.Unit?.Id,
            input.Unit?.Symbol);
    }

    private IEnumerable<GroceryAggregatedItem> AggregateGroup(
        IGrouping<string, GroceryIngredientInput> group)
    {
        var first = group.First();
        var dimension = first.Unit?.Dimension ?? UnitDimension.Count;
        var name = PickName(group);

        return dimension is UnitDimension.Mass or UnitDimension.Volume
            ? AggregateMeasured(group, first.IngredientId, name, dimension)
            : [AggregateCounted(group, first.IngredientId, name, dimension)];
    }

    private static string PickName(IEnumerable<GroceryIngredientInput> group) =>
        group
            .Select(input => input.IngredientName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .FirstOrDefault() ?? string.Empty;

    private static GroceryAggregatedItem AggregateCounted(
        IEnumerable<GroceryIngredientInput> group,
        long ingredientId,
        string name,
        UnitDimension dimension)
    {
        var inputs = group.ToList();
        var unit = inputs[0].Unit;
        decimal? quantity = null;

        if (dimension == UnitDimension.Count)
        {
            foreach (var input in inputs)
            {
                if (input.QuantityNumeric is { } value)
                {
                    quantity = quantity is null ? value : quantity.Value + value;
                }
            }
        }

        return new GroceryAggregatedItem(
            GroceryItemIdentity.ForRecipeItem(ingredientId, dimension, unit?.Id, unit?.Symbol),
            ingredientId,
            name,
            dimension,
            quantity,
            unit?.Symbol,
            FormatQuantityText(quantity),
            BuildSources(inputs));
    }

    private IEnumerable<GroceryAggregatedItem> AggregateMeasured(
        IEnumerable<GroceryIngredientInput> group,
        long ingredientId,
        string name,
        UnitDimension dimension)
    {
        var inputs = group.ToList();
        var identity = GroceryItemIdentity.ForRecipeItem(ingredientId, dimension, null, null);
        var displayUnit = SelectDisplayUnit(inputs);
        var main = new Bucket(displayUnit);
        var fallbacks = new Dictionary<string, Bucket>(StringComparer.Ordinal);

        foreach (var input in inputs)
        {
            // Mass/volume lines always carry a unit, so the null-forgiving use is safe.
            var unit = input.Unit!;
            var quantity = input.QuantityNumeric;

            if (quantity is null)
            {
                // A quantity-less contribution needs no math and keeps its place.
                (displayUnit is null ? GetOrAdd(fallbacks, unit) : main).AddSource(input);
                continue;
            }

            if (displayUnit is not null && SameUnit(unit, displayUnit))
            {
                main.Add(input, quantity.Value);
                continue;
            }

            if (displayUnit is not null && TryConvert(unit, displayUnit, quantity.Value) is { } converted)
            {
                main.Add(input, converted);
                continue;
            }

            GetOrAdd(fallbacks, unit).Add(input, quantity.Value);
        }

        var results = new List<GroceryAggregatedItem>();

        if (!main.IsEmpty)
        {
            results.Add(new GroceryAggregatedItem(
                identity,
                ingredientId,
                name,
                dimension,
                main.Quantity,
                main.Unit?.Symbol,
                FormatQuantityText(main.Quantity),
                BuildSources(main.Inputs)));
        }

        foreach (var bucket in fallbacks.Values.OrderBy(bucket => bucket.Unit!.Symbol, StringComparer.Ordinal))
        {
            results.Add(new GroceryAggregatedItem(
                identity,
                ingredientId,
                name,
                dimension,
                bucket.Quantity,
                bucket.Unit!.Symbol,
                FormatQuantityText(bucket.Quantity),
                BuildSources(bucket.Inputs)));
        }

        return results;
    }

    private static ConversionUnit? SelectDisplayUnit(IReadOnlyCollection<GroceryIngredientInput> inputs)
    {
        var units = new Dictionary<string, ConversionUnit>(StringComparer.Ordinal);
        foreach (var input in inputs)
        {
            if (input.Unit is { } unit)
            {
                units.TryAdd(UnitKey(unit), unit);
            }
        }

        if (units.Count == 0)
        {
            return null;
        }

        if (units.Count == 1)
        {
            return units.Values.First();
        }

        ConversionUnit? largest = null;
        foreach (var unit in units.Values)
        {
            if (unit.BaseFactor is not { } factor || factor <= 0m)
            {
                continue;
            }

            if (largest is null
                || factor > largest.BaseFactor!.Value
                || (factor == largest.BaseFactor!.Value
                    && string.CompareOrdinal(unit.Symbol, largest.Symbol) < 0))
            {
                largest = unit;
            }
        }

        return largest;
    }

    private static Bucket GetOrAdd(Dictionary<string, Bucket> fallbacks, ConversionUnit unit)
    {
        var key = UnitKey(unit);
        if (!fallbacks.TryGetValue(key, out var bucket))
        {
            bucket = new Bucket(unit);
            fallbacks[key] = bucket;
        }

        return bucket;
    }

    private decimal? TryConvert(ConversionUnit from, ConversionUnit to, decimal quantity) =>
        _conversion.Convert(from, to, quantity) is ConversionResult.Success success
            ? success.Quantity
            : null;

    private static bool SameUnit(ConversionUnit a, ConversionUnit b) =>
        string.Equals(UnitKey(a), UnitKey(b), StringComparison.Ordinal);

    private static string UnitKey(ConversionUnit unit) =>
        unit.Id.HasValue
            ? "id:" + unit.Id.Value.ToString(CultureInfo.InvariantCulture)
            : "sym:" + unit.Symbol.ToLowerInvariant();

    private static IReadOnlyList<GroceryItemSource> BuildSources(
        IEnumerable<GroceryIngredientInput> inputs) =>
        inputs
            .GroupBy(input => (input.RecipeId, input.MealDate))
            .Select(group => new GroceryItemSource(
                group.Key.RecipeId,
                group.Select(input => input.RecipeName)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .First(),
                group.Key.MealDate))
            .OrderBy(source => source.MealDate)
            .ThenBy(source => source.RecipeId)
            .ToList();

    private static string FormatQuantityText(decimal? quantity) =>
        quantity is { } value ? FormatQuantity(value) : string.Empty;

    /// <summary>
    /// Renders a quantity as the nearest common cooking fraction when the value is
    /// exact within a small tolerance (0.5 → "1/2", 1.5 → "1 1/2", 2.25 → "2 1/4",
    /// matching docs/PRODUCT_SPEC.md §37 and the frontend helper), otherwise as a
    /// decimal with up to two places (1.234 → "1.23", 0.3 → "0.3").
    /// </summary>
    private static string FormatQuantity(decimal value)
    {
        var negative = value < 0m;
        var absolute = Math.Abs(value);
        var whole = decimal.Floor(absolute);
        var remainder = absolute - whole;
        var sign = negative ? "-" : string.Empty;

        if (remainder > 1m - FractionTolerance)
        {
            whole += 1m;
            remainder = 0m;
        }

        if (remainder <= FractionTolerance)
        {
            return sign + whole.ToString("0", CultureInfo.InvariantCulture);
        }

        foreach (var (numerator, denominator) in Fractions)
        {
            var candidate = (decimal)numerator / denominator;
            if (Math.Abs(remainder - candidate) > FractionTolerance)
            {
                continue;
            }

            var fraction = numerator.ToString(CultureInfo.InvariantCulture)
                + "/"
                + denominator.ToString(CultureInfo.InvariantCulture);

            return whole == 0m
                ? sign + fraction
                : sign + whole.ToString("0", CultureInfo.InvariantCulture) + " " + fraction;
        }

        return sign + absolute.ToString("0.##", CultureInfo.InvariantCulture);
    }

    /// <summary>Mutable accumulator for one output line during aggregation.</summary>
    private sealed class Bucket
    {
        private readonly List<GroceryIngredientInput> _inputs = [];

        private decimal? _quantity;

        public Bucket(ConversionUnit? unit)
        {
            Unit = unit;
        }

        public ConversionUnit? Unit { get; }

        public bool IsEmpty => _inputs.Count == 0;

        public decimal? Quantity => _quantity;

        public IReadOnlyList<GroceryIngredientInput> Inputs => _inputs;

        public void AddSource(GroceryIngredientInput input) => _inputs.Add(input);

        public void Add(GroceryIngredientInput input, decimal quantity)
        {
            _inputs.Add(input);
            _quantity = _quantity is null ? quantity : _quantity.Value + quantity;
        }
    }
}
