using FateAndFridge.Api.Data;
using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace FateAndFridge.Api.Services;

/// <summary>
/// Idempotent startup seeder for the default reference data
/// (docs/M2_PLAN.md §1.1, §2). The seeder runs only while
/// <c>application_settings.reference_data_seeded</c> is absent; it inserts what
/// is missing, never updates or deletes an existing row, and records the flag
/// in the same transaction as the inserts. Concurrent invocations serialize on
/// a transaction-scoped PostgreSQL advisory lock, and the unique indexes remain
/// the backstop.
/// </summary>
public sealed class ReferenceDataSeeder(AppDbContext dbContext)
{
    /// <summary>Key of the JSON boolean flag in <c>application_settings.key</c>.</summary>
    public const string SeededKey = "reference_data_seeded";

    /// <summary>
    /// Fixed, transaction-scoped advisory-lock key: the ASCII bytes of
    /// <c>"FAFRDF01"</c> (Fate &amp; Fridge reference data, format 01). Every
    /// seeder invocation takes exactly this key, and PostgreSQL releases it on
    /// commit or rollback.
    /// </summary>
    public const long AdvisoryLockKey = 0x4641465244463031L;

    /// <summary>
    /// Startup hook used by <c>Program</c> after migrations: runs the seeder in
    /// its own scope, logs the outcome, and rethrows failures so an unseeded
    /// reference dataset never becomes a valid boot state.
    /// </summary>
    public static async Task SeedAtStartupAsync(
        IServiceProvider services,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = services.CreateAsyncScope();
            var seeder = scope.ServiceProvider.GetRequiredService<ReferenceDataSeeder>();
            var seeded = await seeder.SeedAsync(cancellationToken);
            if (seeded)
            {
                logger.LogInformation("Default reference data seeded");
            }
            else
            {
                logger.LogInformation("Reference data already seeded; skipping");
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Reference-data seeding failed; aborting startup");
            throw;
        }
    }

    /// <summary>
    /// Inserts the missing default reference data and the completion flag in one
    /// transaction. Returns <see langword="true"/> when this call inserted the
    /// defaults, or <see langword="false"/> when the flag already existed.
    /// </summary>
    public async Task<bool> SeedAsync(CancellationToken cancellationToken = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        // Transaction-scoped lock: concurrent seeders (for example two app
        // instances starting together) queue here until the winner commits.
        await dbContext.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock({AdvisoryLockKey})",
            cancellationToken);

        // Re-check under the lock so the losing seeder observes the winner's
        // committed flag instead of inserting a second dataset.
        var alreadySeeded = await dbContext.ApplicationSettings
            .AsNoTracking()
            .AnyAsync(setting => setting.Key == SeededKey, cancellationToken);
        if (alreadySeeded)
        {
            return false;
        }

        var seededAt = DateTimeOffset.UtcNow;
        await InsertMissingDepartmentsAsync(seededAt, cancellationToken);
        await InsertMissingProteinsAsync(cancellationToken);
        await InsertMissingUnitsAsync(cancellationToken);

        // Assigns generated ids so the aliases below can reference new units.
        await dbContext.SaveChangesAsync(cancellationToken);

        await InsertMissingUnitAliasesAsync(cancellationToken);

        dbContext.ApplicationSettings.Add(new ApplicationSetting
        {
            Key = SeededKey,
            ValueJson = "true",
            UpdatedAt = seededAt,
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task InsertMissingDepartmentsAsync(
        DateTimeOffset seededAt,
        CancellationToken cancellationToken)
    {
        var existingNames = (await dbContext.GroceryDepartments
                .AsNoTracking()
                .Select(department => department.NormalizedName)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);

        var missing = new List<GroceryDepartment>();
        for (var index = 0; index < ReferenceDataSeedCatalog.DepartmentNames.Count; index++)
        {
            var (display, normalized) = NormalizeSeedText(ReferenceDataSeedCatalog.DepartmentNames[index]);
            if (!existingNames.Add(normalized))
            {
                // A pre-existing row (user-created or an earlier partial seed)
                // keeps its name, sort order, and timestamps.
                continue;
            }

            missing.Add(new GroceryDepartment
            {
                Name = display,
                NormalizedName = normalized,
                SortOrder = index + 1,
                CreatedAt = seededAt,
                UpdatedAt = seededAt,
            });
        }

        dbContext.GroceryDepartments.AddRange(missing);
    }

    private async Task InsertMissingProteinsAsync(CancellationToken cancellationToken)
    {
        var existingNames = (await dbContext.Proteins
                .AsNoTracking()
                .Select(protein => protein.NormalizedName)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);

        var missing = new List<Protein>();
        foreach (var name in ReferenceDataSeedCatalog.ProteinNames)
        {
            var (display, normalized) = NormalizeSeedText(name);
            if (!existingNames.Add(normalized))
            {
                continue;
            }

            missing.Add(new Protein
            {
                Name = display,
                NormalizedName = normalized,
            });
        }

        dbContext.Proteins.AddRange(missing);
    }

    private async Task InsertMissingUnitsAsync(CancellationToken cancellationToken)
    {
        var existingUnits = await dbContext.Units
            .AsNoTracking()
            .Select(unit => new { unit.Symbol, unit.NormalizedName })
            .ToListAsync(cancellationToken);

        var existingSymbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var existingNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var unit in existingUnits)
        {
            existingSymbols.Add(unit.Symbol);
            existingNames.Add(unit.NormalizedName);
        }

        var missing = new List<Unit>();
        foreach (var seed in ReferenceDataSeedCatalog.Units)
        {
            var (displayName, normalizedName) = NormalizeSeedText(seed.DisplayName);
            if (existingSymbols.Contains(seed.Symbol) || existingNames.Contains(normalizedName))
            {
                // Both the symbol and the normalized display name are unique, so
                // either match means the unit already exists and user data wins.
                continue;
            }

            existingSymbols.Add(seed.Symbol);
            existingNames.Add(normalizedName);
            missing.Add(new Unit
            {
                Symbol = seed.Symbol,
                DisplayName = displayName,
                NormalizedName = normalizedName,
                Dimension = seed.Dimension,
                BaseFactor = seed.BaseFactor,
            });
        }

        dbContext.Units.AddRange(missing);
    }

    private async Task InsertMissingUnitAliasesAsync(CancellationToken cancellationToken)
    {
        var units = await dbContext.Units
            .AsNoTracking()
            .Select(unit => new { unit.Id, unit.Symbol, unit.NormalizedName })
            .ToListAsync(cancellationToken);

        var idsBySymbol = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var idsByName = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var unit in units)
        {
            idsBySymbol.TryAdd(unit.Symbol, unit.Id);
            idsByName.TryAdd(unit.NormalizedName, unit.Id);
        }

        var knownAliases = (await dbContext.UnitAliases
                .AsNoTracking()
                .Select(alias => new { alias.UnitId, alias.NormalizedAlias })
                .ToListAsync(cancellationToken))
            .Select(alias => (alias.UnitId, alias.NormalizedAlias))
            .ToHashSet();

        var missing = new List<UnitAlias>();
        foreach (var seed in ReferenceDataSeedCatalog.Units)
        {
            var (_, normalizedName) = NormalizeSeedText(seed.DisplayName);
            if (!idsBySymbol.TryGetValue(seed.Symbol, out var unitId)
                && !idsByName.TryGetValue(normalizedName, out unitId))
            {
                throw new InvalidOperationException(
                    $"Seed unit '{seed.Symbol}' is missing after the unit insert.");
            }

            // Per-unit uniqueness: "t" (tsp) and "T" (tbsp) normalize to the same
            // text but belong to different units, so both are inserted. Aliases
            // are added even for pre-existing units when the exact normalized
            // alias is not there yet; existing alias rows are never touched.
            foreach (var alias in seed.Aliases)
            {
                var (aliasDisplay, aliasNormalized) = NormalizeSeedText(alias);
                if (knownAliases.Add((unitId, aliasNormalized)))
                {
                    missing.Add(new UnitAlias
                    {
                        UnitId = unitId,
                        Alias = aliasDisplay,
                        NormalizedAlias = aliasNormalized,
                    });
                }
            }
        }

        dbContext.UnitAliases.AddRange(missing);
    }

    private static (string Display, string Normalized) NormalizeSeedText(string value)
    {
        var result = ReferenceDataNormalizer.Normalize(value);
        if (result is not ReferenceDataNormalizationResult.Success success)
        {
            throw new InvalidOperationException(
                $"Reference-data seed value \"{value}\" is not a valid name or alias.");
        }

        return (success.Display, success.Normalized);
    }
}
