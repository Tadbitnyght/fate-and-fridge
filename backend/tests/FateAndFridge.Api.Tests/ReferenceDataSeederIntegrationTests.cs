using System.Data.Common;
using System.Text.Json;
using FateAndFridge.Api.Data;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Services;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// PostgreSQL-backed verification of the M2-04 idempotent reference-data seeder
/// (docs/M2_PLAN.md §1.1, §2). Requires a Docker daemon; run with
/// <c>dotnet test --filter "Category!=Integration"</c> to skip locally.
/// </summary>
[Trait("Category", "Integration")]
[Collection(ReferenceDataSeederCollection.Name)]
public sealed class ReferenceDataSeederIntegrationTests(ReferenceDataSeederFixture fixture)
{
    private const int ExpectedAliasCount = 38;

    private static readonly string[] ExpectedDepartments =
    [
        "Produce",
        "Meat & Seafood",
        "Dairy & Eggs",
        "Bakery",
        "Deli",
        "Frozen",
        "Canned Goods",
        "Dry Goods / Pantry",
        "Spices & Seasonings",
        "Condiments & Sauces",
        "Beverages",
        "Household",
        "Personal Care",
        "Other",
    ];

    private static readonly (string Symbol, string DisplayName, UnitDimension Dimension, decimal? BaseFactor, string[] Aliases)[] ExpectedUnits =
    [
        ("tsp", "teaspoon", UnitDimension.Volume, 4.92892159375m, ["teaspoon", "teaspoons", "t"]),
        ("tbsp", "tablespoon", UnitDimension.Volume, 14.78676478125m, ["tablespoon", "tablespoons", "Tbsp", "T"]),
        ("fl oz", "fluid ounce", UnitDimension.Volume, 29.5735295625m, ["floz", "fl. oz.", "fluid ounce", "fluid ounces"]),
        ("cup", "cup", UnitDimension.Volume, 236.5882365m, ["cups"]),
        ("pint", "pint", UnitDimension.Volume, 473.176473m, ["pints", "pt"]),
        ("quart", "quart", UnitDimension.Volume, 946.352946m, ["quarts", "qt"]),
        ("gallon", "gallon", UnitDimension.Volume, 3785.411784m, ["gallons", "gal"]),
        ("oz", "ounce", UnitDimension.Mass, 28.349523125m, ["ounce", "ounces"]),
        ("lb", "pound", UnitDimension.Mass, 453.59237m, ["pound", "pounds", "lbs"]),
        ("g", "gram", UnitDimension.Mass, 1m, ["gram", "grams"]),
        ("kg", "kilogram", UnitDimension.Mass, 1000m, ["kilogram", "kilograms"]),
        ("each", "each", UnitDimension.Count, null, ["ea"]),
        ("package", "package", UnitDimension.Count, null, ["packages", "pkg"]),
        ("can", "can", UnitDimension.Count, null, ["cans"]),
        ("bottle", "bottle", UnitDimension.Count, null, ["bottles"]),
        ("jar", "jar", UnitDimension.Count, null, ["jars"]),
        ("bunch", "bunch", UnitDimension.Count, null, ["bunches"]),
        ("clove", "clove", UnitDimension.Count, null, ["cloves"]),
        ("slice", "slice", UnitDimension.Count, null, ["slices"]),
        ("pinch", "pinch", UnitDimension.Count, null, ["pinches"]),
        ("to taste", "to taste", UnitDimension.Nonconvertible, null, ["as needed"]),
    ];

    private static readonly string[] ExpectedProteins =
    [
        "Beef",
        "Chicken",
        "Pork",
        "Turkey",
        "Seafood",
        "Lamb",
        "Venison",
        "Duck",
        "Other",
    ];

    [Fact]
    public async Task Fresh_database_is_seeded_exactly_once_with_the_documented_defaults()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetReferenceDataAsync(cancellationToken);

        await using (var context = fixture.CreateContext())
        {
            var seeded = await new ReferenceDataSeeder(context).SeedAsync(cancellationToken);
            seeded.Should().BeTrue("a fresh database has neither reference data nor the seed flag");
        }

        await using var verification = fixture.CreateContext();
        await AssertSeededAsync(verification, cancellationToken);
    }

    [Fact]
    public async Task A_second_run_is_a_no_op_and_leaves_every_row_unchanged()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetReferenceDataAsync(cancellationToken);

        await using (var context = fixture.CreateContext())
        {
            (await new ReferenceDataSeeder(context).SeedAsync(cancellationToken)).Should().BeTrue();
        }

        ReferenceDataSnapshot before;
        await using (var context = fixture.CreateContext())
        {
            before = await LoadSnapshotAsync(context, cancellationToken);
        }

        await using (var context = fixture.CreateContext())
        {
            var seeded = await new ReferenceDataSeeder(context).SeedAsync(cancellationToken);
            seeded.Should().BeFalse("the seed flag already exists");
        }

        await using (var context = fixture.CreateContext())
        {
            var after = await LoadSnapshotAsync(context, cancellationToken);
            after.Should().BeEquivalentTo(before);
        }

        await using (var verification = fixture.CreateContext())
        {
            await AssertSeededAsync(verification, cancellationToken);
        }
    }

    [Fact]
    public async Task An_M1_baseline_database_is_backfilled_after_upgrade_and_seed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetReferenceDataAsync(cancellationToken);
        await fixture.MigrateToM1BaselineAsync(cancellationToken);

        await using (var context = fixture.CreateContext())
        {
            var tables = await GetTableNamesAsync(context, cancellationToken);
            tables.Should().Contain("application_settings");
            tables.Should().NotContain("grocery_departments");
            tables.Should().NotContain("units");
            tables.Should().NotContain("proteins");
        }

        // Startup order is migrate, then seed; upgrading the M1-era database
        // creates the reference-data tables the seeder fills.
        await fixture.MigrateToLatestAsync(cancellationToken);

        await using (var context = fixture.CreateContext())
        {
            (await new ReferenceDataSeeder(context).SeedAsync(cancellationToken)).Should().BeTrue();
        }

        await using var verification = fixture.CreateContext();
        await AssertSeededAsync(verification, cancellationToken);
    }

    [Fact]
    public async Task Existing_user_rows_survive_and_missing_defaults_are_inserted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetReferenceDataAsync(cancellationToken);

        var customTimestamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        await using (var context = fixture.CreateContext())
        {
            context.GroceryDepartments.Add(new GroceryDepartment
            {
                Name = "Other",
                NormalizedName = "other",
                SortOrder = 99,
                CreatedAt = customTimestamp,
                UpdatedAt = customTimestamp,
            });
            context.Proteins.Add(new Protein
            {
                Name = "Other",
                NormalizedName = "other",
            });
            var customUnit = new Unit
            {
                Symbol = "oz",
                DisplayName = "Ounce",
                NormalizedName = "ounce",
                Dimension = UnitDimension.Mass,
                BaseFactor = 99m,
                IsCustom = true,
            };
            context.Units.Add(customUnit);
            await context.SaveChangesAsync(cancellationToken);

            context.UnitAliases.Add(new UnitAlias
            {
                UnitId = customUnit.Id,
                Alias = "ounce",
                NormalizedAlias = "ounce",
            });
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = fixture.CreateContext())
        {
            (await new ReferenceDataSeeder(context).SeedAsync(cancellationToken)).Should().BeTrue();
        }

        await using var verification = fixture.CreateContext();

        var department = await verification.GroceryDepartments
            .AsNoTracking()
            .SingleAsync(row => row.NormalizedName == "other", cancellationToken);
        department.Name.Should().Be("Other");
        department.SortOrder.Should().Be(99);
        department.IsActive.Should().BeTrue();
        department.CreatedAt.Should().Be(customTimestamp);
        department.UpdatedAt.Should().Be(customTimestamp);
        (await verification.GroceryDepartments.CountAsync(cancellationToken)).Should().Be(14);

        var protein = await verification.Proteins
            .AsNoTracking()
            .SingleAsync(row => row.NormalizedName == "other", cancellationToken);
        protein.Name.Should().Be("Other");
        (await verification.Proteins.CountAsync(cancellationToken)).Should().Be(9);

        var unit = await verification.Units
            .AsNoTracking()
            .SingleAsync(row => row.NormalizedName == "ounce", cancellationToken);
        unit.Symbol.Should().Be("oz");
        unit.DisplayName.Should().Be("Ounce");
        unit.Dimension.Should().Be(UnitDimension.Mass);
        unit.BaseFactor.Should().Be(99m);
        unit.IsCustom.Should().BeTrue();
        (await verification.Units.CountAsync(cancellationToken)).Should().Be(21);

        // The user's alias survives; the still-missing seeded alias is added.
        var aliasDisplayNames = await verification.UnitAliases
            .AsNoTracking()
            .Where(alias => alias.UnitId == unit.Id)
            .Select(alias => alias.Alias)
            .ToListAsync(cancellationToken);
        aliasDisplayNames.Should().BeEquivalentTo(new[] { "ounce", "ounces" });
        (await verification.UnitAliases.CountAsync(cancellationToken)).Should().Be(ExpectedAliasCount);
        await AssertFlagAsync(verification, cancellationToken);
    }

    [Fact]
    public async Task A_user_renamed_department_is_preserved_when_the_normalized_name_matches()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetReferenceDataAsync(cancellationToken);

        var customTimestamp = new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero);
        await using (var context = fixture.CreateContext())
        {
            context.GroceryDepartments.Add(new GroceryDepartment
            {
                Name = "PRODUCE",
                NormalizedName = "produce",
                SortOrder = 42,
                CreatedAt = customTimestamp,
                UpdatedAt = customTimestamp,
            });
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = fixture.CreateContext())
        {
            (await new ReferenceDataSeeder(context).SeedAsync(cancellationToken)).Should().BeTrue();
        }

        await using var verification = fixture.CreateContext();
        var department = await verification.GroceryDepartments
            .AsNoTracking()
            .SingleAsync(row => row.NormalizedName == "produce", cancellationToken);
        department.Name.Should().Be("PRODUCE");
        department.SortOrder.Should().Be(42);
        department.UpdatedAt.Should().Be(customTimestamp);
        (await verification.GroceryDepartments.CountAsync(cancellationToken)).Should().Be(14);
        await AssertFlagAsync(verification, cancellationToken);
    }

    [Fact]
    public async Task Concurrent_seeders_produce_exactly_one_dataset_without_errors()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetReferenceDataAsync(cancellationToken);

        await using var firstContext = fixture.CreateContext();
        await using var secondContext = fixture.CreateContext();

        var results = await Task.WhenAll(
            new ReferenceDataSeeder(firstContext).SeedAsync(cancellationToken),
            new ReferenceDataSeeder(secondContext).SeedAsync(cancellationToken));

        results.Should().ContainSingle(seeded => seeded);
        results.Should().ContainSingle(seeded => !seeded);

        await using var verification = fixture.CreateContext();
        await AssertSeededAsync(verification, cancellationToken);

        var aliasKeys = await verification.UnitAliases
            .AsNoTracking()
            .Select(alias => new { alias.UnitId, alias.NormalizedAlias })
            .ToListAsync(cancellationToken);
        aliasKeys.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task A_failed_seed_rolls_back_rows_and_flag_then_a_clean_retry_succeeds()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetReferenceDataAsync(cancellationToken);

        await using (var poisoned = fixture.CreateContextWithInterceptor(new FailOnUnitAliasInsertInterceptor()))
        {
            var seeder = new ReferenceDataSeeder(poisoned);
            var act = async () => await seeder.SeedAsync(cancellationToken);
            var failure = await act.Should().ThrowAsync<DbUpdateException>();
            failure.Which.InnerException.Should().BeOfType<InvalidOperationException>(
                "the poisoned unit-alias insert must surface as the save failure");
        }

        await using (var verification = fixture.CreateContext())
        {
            (await verification.GroceryDepartments.CountAsync(cancellationToken)).Should().Be(0);
            (await verification.Proteins.CountAsync(cancellationToken)).Should().Be(0);
            (await verification.Units.CountAsync(cancellationToken)).Should().Be(0);
            (await verification.UnitAliases.CountAsync(cancellationToken)).Should().Be(0);
            (await verification.ApplicationSettings.CountAsync(cancellationToken)).Should().Be(0);
        }

        await using (var context = fixture.CreateContext())
        {
            (await new ReferenceDataSeeder(context).SeedAsync(cancellationToken)).Should().BeTrue();
        }

        await using var recovered = fixture.CreateContext();
        await AssertSeededAsync(recovered, cancellationToken);
    }

    [Fact]
    public async Task Startup_runs_the_seeder_after_migrations()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetReferenceDataAsync(cancellationToken);

        var dataDir = Path.Combine(Path.GetTempPath(), "fate-and-fridge-tests", Guid.NewGuid().ToString("N"));
        using (var factory = new ApiFactory(fixture.ConnectionString, migrateOnStartup: true, dataDir))
        {
            // Building the host runs Program's migrate-then-seed startup hook.
            _ = factory.Services;
        }

        await using var verification = fixture.CreateContext();
        await AssertSeededAsync(verification, cancellationToken);
    }

    private static async Task AssertSeededAsync(AppDbContext context, CancellationToken cancellationToken)
    {
        var departments = await context.GroceryDepartments
            .AsNoTracking()
            .OrderBy(department => department.SortOrder)
            .ToListAsync(cancellationToken);
        departments.Should().HaveCount(14);
        departments.Select(department => department.Name).Should().Equal(ExpectedDepartments);
        departments.Select(department => department.SortOrder).Should().Equal(Enumerable.Range(1, 14));
        departments.Should().OnlyContain(department => department.IsActive);
        departments.Should().OnlyContain(department => department.NormalizedName == Normalize(department.Name));

        var proteins = await context.Proteins
            .AsNoTracking()
            .OrderBy(protein => protein.Id)
            .ToListAsync(cancellationToken);
        proteins.Should().HaveCount(9);
        proteins.Select(protein => protein.Name).Should().Equal(ExpectedProteins);
        proteins.Should().OnlyContain(protein => protein.IsActive);
        proteins.Should().OnlyContain(protein => protein.NormalizedName == Normalize(protein.Name));

        var units = await context.Units
            .AsNoTracking()
            .OrderBy(unit => unit.Id)
            .ToListAsync(cancellationToken);
        units.Should().HaveCount(21);
        units.Select(unit => unit.Symbol).Should().Equal(ExpectedUnits.Select(expected => expected.Symbol));

        var unitsBySymbol = units.ToDictionary(unit => unit.Symbol, StringComparer.Ordinal);
        foreach (var expected in ExpectedUnits)
        {
            var unit = unitsBySymbol[expected.Symbol];
            unit.DisplayName.Should().Be(expected.DisplayName);
            unit.NormalizedName.Should().Be(Normalize(expected.DisplayName));
            unit.Dimension.Should().Be(expected.Dimension);
            unit.BaseFactor.Should().Be(expected.BaseFactor);
            unit.IsCustom.Should().BeFalse();
            unit.IsActive.Should().BeTrue();
        }

        var aliases = await context.UnitAliases
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        aliases.Should().HaveCount(ExpectedAliasCount);
        aliases.Should().OnlyContain(alias => alias.NormalizedAlias == Normalize(alias.Alias));
        foreach (var expected in ExpectedUnits)
        {
            var unitId = unitsBySymbol[expected.Symbol].Id;
            aliases
                .Where(alias => alias.UnitId == unitId)
                .Select(alias => alias.Alias)
                .Should().BeEquivalentTo(expected.Aliases);
        }

        // The per-unit alias rule lets teaspoon "t" and tablespoon "T" coexist.
        var teaspoon = unitsBySymbol["tsp"];
        var tablespoon = unitsBySymbol["tbsp"];
        tablespoon.Id.Should().NotBe(teaspoon.Id);
        aliases.Should().Contain(alias =>
            alias.UnitId == teaspoon.Id && alias.Alias == "t" && alias.NormalizedAlias == "t");
        aliases.Should().Contain(alias =>
            alias.UnitId == tablespoon.Id && alias.Alias == "T" && alias.NormalizedAlias == "t");

        await AssertFlagAsync(context, cancellationToken);
    }

    private static async Task AssertFlagAsync(AppDbContext context, CancellationToken cancellationToken)
    {
        var flag = await context.ApplicationSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(setting => setting.Key == ReferenceDataSeeder.SeededKey, cancellationToken);
        flag.Should().NotBeNull("the seeder records reference_data_seeded in the same transaction");
        flag!.ValueJson.Should().NotBeNull();

        using var document = JsonDocument.Parse(flag.ValueJson!);
        document.RootElement.ValueKind.Should().Be(JsonValueKind.True);
    }

    private static async Task<ReferenceDataSnapshot> LoadSnapshotAsync(
        AppDbContext context,
        CancellationToken cancellationToken) =>
        new(
            await context.GroceryDepartments.AsNoTracking().OrderBy(row => row.Id).ToListAsync(cancellationToken),
            await context.Units.AsNoTracking().OrderBy(row => row.Id).ToListAsync(cancellationToken),
            await context.UnitAliases.AsNoTracking().OrderBy(row => row.Id).ToListAsync(cancellationToken),
            await context.Proteins.AsNoTracking().OrderBy(row => row.Id).ToListAsync(cancellationToken),
            await context.ApplicationSettings.AsNoTracking().OrderBy(row => row.Key).ToListAsync(cancellationToken));

    private static async Task<List<string>> GetTableNamesAsync(
        AppDbContext context,
        CancellationToken cancellationToken) =>
        (await context.Database
            .SqlQueryRaw<TableInfo>(
                """
                SELECT table_name AS table_name
                FROM information_schema.tables
                WHERE table_schema = 'public' AND table_type = 'BASE TABLE'
                """)
            .ToListAsync(cancellationToken))
        .Select(table => table.TableName)
        .ToList();

    private static string Normalize(string value)
    {
        var result = ReferenceDataNormalizer.Normalize(value);
        result.Should().BeOfType<ReferenceDataNormalizationResult.Success>();
        return ((ReferenceDataNormalizationResult.Success)result).Normalized;
    }

    private sealed record ReferenceDataSnapshot(
        IReadOnlyList<GroceryDepartment> Departments,
        IReadOnlyList<Unit> Units,
        IReadOnlyList<UnitAlias> UnitAliases,
        IReadOnlyList<Protein> Proteins,
        IReadOnlyList<ApplicationSetting> ApplicationSettings);

    private sealed record TableInfo(string TableName);

    /// <summary>Throws while EF inserts seeded unit aliases, after earlier seed rows were already written.</summary>
    private sealed class FailOnUnitAliasInsertInterceptor : DbCommandInterceptor
    {
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            ThrowIfSeedingUnitAliases(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfSeedingUnitAliases(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            ThrowIfSeedingUnitAliases(command);
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfSeedingUnitAliases(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private static void ThrowIfSeedingUnitAliases(DbCommand command)
        {
            if (command.CommandText.Contains("INSERT INTO unit_aliases", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Simulated failure while inserting seeded unit aliases.");
            }
        }
    }
}
