using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using FateAndFridge.Api.Data;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Services;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// M11-07 performance baseline (docs/TESTING.md §12, docs/M11_PLAN.md §1.28):
/// the representative household of 2,000 recipes and ten years of meal history.
/// The overview and a single history month must each respond within one second,
/// with no N+1 pattern, and the recipes list carrying the §12 metrics must stay
/// bounded. A counting <see cref="DbCommandInterceptor"/> proves the command
/// count is a small constant, not proportional to the household size.
/// </summary>
[Trait("Category", "Integration")]
[Collection(M11StatisticsCollection.Name)]
public sealed class M11StatisticsPerformanceTests(
    M11StatisticsFixture fixture,
    ITestOutputHelper output) : IAsyncLifetime
{
    private const string OverviewPath = "/api/v1/statistics/overview";
    private const string HistoryPath = "/api/v1/statistics/history";
    private const string RecipesPath = "/api/v1/recipes";

    private const int RecipeCount = 2_000;
    private const int ProteinCount = 20;
    private const int BatchSize = 500;

    private static readonly DateTimeOffset SeedTimestamp = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 15);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await fixture.ResetAsync(Ct);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task The_baseline_stays_under_one_second_without_n_plus_one_queries()
    {
        var seedWatch = Stopwatch.StartNew();
        await SeedBaselineAsync();
        seedWatch.Stop();
        output.WriteLine($"seeded {RecipeCount} recipes + 10 years of meals in {seedWatch.Elapsed.TotalSeconds:F1}s");

        var interceptor = new CountingCommandInterceptor();
        using var factory = fixture.CreateFactory(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new TestTimeProvider(Now));
            // Program registers AppDbContext against the fixture's connection
            // string; rebuild it with the same options plus the counting
            // interceptor so every request-scoped context is observed.
            services.RemoveAll<AppDbContext>();
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.AddDbContext<AppDbContext>(options => options
                .UseNpgsql(fixture.ConnectionString)
                .UseSnakeCaseNamingConvention()
                .AddInterceptors(interceptor));
        });
        using var client = factory.CreateClient();

        // Warm up the host and the EF model so the measured calls are the
        // endpoint's own latency (a cold test-host start is not the query cost).
        (await client.GetAsync(OverviewPath, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var overview = await MeasureAsync(() => client.GetAsync(OverviewPath, Ct), interceptor);
        var overviewAgain = await MeasureAsync(() => client.GetAsync(OverviewPath, Ct), interceptor);
        var overviewThird = await MeasureAsync(() => client.GetAsync(OverviewPath, Ct), interceptor);

        var history = await MeasureAsync(() => client.GetAsync($"{HistoryPath}?month=2026-10", Ct), interceptor);
        var historyOther = await MeasureAsync(() => client.GetAsync($"{HistoryPath}?month=2026-09", Ct), interceptor);

        var list = await MeasureAsync(() => client.GetAsync($"{RecipesPath}?limit=100&sort=most_eaten", Ct), interceptor);
        var listAgain = await MeasureAsync(() => client.GetAsync($"{RecipesPath}?limit=100&sort=most_eaten", Ct), interceptor);

        var maxOverviewMs = new[] { overview, overviewAgain, overviewThird }.Max(entry => entry.ElapsedMs);
        var maxHistoryMs = new[] { history, historyOther }.Max(entry => entry.ElapsedMs);
        var maxListMs = new[] { list, listAgain }.Max(entry => entry.ElapsedMs);

        output.WriteLine(
            $"overview: {maxOverviewMs:F0}ms/{overview.Commands} commands; "
            + $"history month: {maxHistoryMs:F0}ms/{history.Commands} commands; "
            + $"recipes list: {maxListMs:F0}ms/{list.Commands} commands");

        // The overview and a single history month each respond within one second.
        overview.Status.Should().Be(HttpStatusCode.OK);
        history.Status.Should().Be(HttpStatusCode.OK);
        maxOverviewMs.Should().BeLessThan(1000, "the overview must respond within the §12 one-second bar");
        maxHistoryMs.Should().BeLessThan(1000, "one history month must respond within the §12 one-second bar");

        // No N+1: a constant, small command count (an N+1 against 2,000 recipes
        // or ten years of meals would be thousands), unchanged between calls.
        overview.Commands.Should().Be(overviewAgain.Commands);
        overview.Commands.Should().Be(overviewThird.Commands);
        overview.Commands.Should().BeLessThanOrEqualTo(15);
        history.Commands.Should().Be(historyOther.Commands);
        history.Commands.Should().BeLessThanOrEqualTo(10);

        // The recipes list with the §12 metrics stays bounded (one aggregate query
        // for the page, never one per card).
        list.Status.Should().Be(HttpStatusCode.OK);
        list.Commands.Should().Be(listAgain.Commands);
        list.Commands.Should().BeLessThanOrEqualTo(15);
        maxListMs.Should().BeLessThan(1000, "the recipes list with metrics must stay responsive");

        using var listPayload = JsonDocument.Parse(list.Body);
        listPayload.RootElement.GetProperty("total").GetInt32().Should().Be(RecipeCount);
        listPayload.RootElement.GetProperty("items").GetArrayLength().Should().Be(100);
        listPayload.RootElement.GetProperty("items")[0].TryGetProperty("times_eaten", out _).Should().BeTrue();
    }

    private async Task SeedBaselineAsync()
    {
        await SeedHouseholdAsync();
        var recipeIds = await SeedRecipesAsync();
        await SeedProteinsAsync(recipeIds);
        await SeedMealHistoryAsync(recipeIds);
    }

    private async Task<List<long>> SeedRecipesAsync()
    {
        var recipeIds = new List<long>(RecipeCount);
        for (var batchStart = 0; batchStart < RecipeCount; batchStart += BatchSize)
        {
            await using var context = fixture.CreateContext();
            var batch = new List<Recipe>(BatchSize);
            for (var index = batchStart; index < Math.Min(batchStart + BatchSize, RecipeCount); index++)
            {
                var normalized = (ReferenceDataNormalizationResult.Success)
                    ReferenceDataNormalizer.Normalize($"Baseline Recipe {index:0000}")!;
                batch.Add(new Recipe
                {
                    Name = normalized.Display,
                    NormalizedName = normalized.Normalized,
                    Status = RecipeStatus.Normal,
                    CreatedAt = SeedTimestamp,
                    UpdatedAt = SeedTimestamp,
                });
            }

            context.Recipes.AddRange(batch);
            await context.SaveChangesAsync(Ct);
            recipeIds.AddRange(batch.Select(recipe => recipe.Id));
        }

        return recipeIds;
    }

    private async Task SeedProteinsAsync(IReadOnlyList<long> recipeIds)
    {
        var proteins = new List<Protein>(ProteinCount);
        await using (var context = fixture.CreateContext())
        {
            for (var index = 0; index < ProteinCount; index++)
            {
                var normalized = (ReferenceDataNormalizationResult.Success)
                    ReferenceDataNormalizer.Normalize($"Baseline Protein {index:00}")!;
                var protein = new Protein
                {
                    Name = normalized.Display,
                    NormalizedName = normalized.Normalized,
                    IsActive = true,
                };
                proteins.Add(protein);
                context.Proteins.Add(protein);
            }

            await context.SaveChangesAsync(Ct);
        }

        // Every tenth recipe lists two proteins so the distribution join has rows.
        for (var batchStart = 0; batchStart < recipeIds.Count; batchStart += BatchSize)
        {
            await using var context = fixture.CreateContext();
            var links = new List<RecipeProtein>();
            for (var index = batchStart; index < Math.Min(batchStart + BatchSize, recipeIds.Count); index++)
            {
                if (index % 10 != 0)
                {
                    continue;
                }

                links.Add(new RecipeProtein
                {
                    RecipeId = recipeIds[index],
                    ProteinId = proteins[index % ProteinCount].Id,
                });
                links.Add(new RecipeProtein
                {
                    RecipeId = recipeIds[index],
                    ProteinId = proteins[(index + 1) % ProteinCount].Id,
                });
            }

            context.RecipeProteins.AddRange(links);
            await context.SaveChangesAsync(Ct);
        }
    }

    private async Task SeedMealHistoryAsync(IReadOnlyList<long> recipeIds)
    {
        var entries = new List<CalendarEntry>(3_700);
        var start = Today.AddYears(-10);
        var day = 0;
        for (var date = start; date < Today; date = date.AddDays(1))
        {
            entries.Add(new CalendarEntry
            {
                MealDate = date,
                EntryType = CalendarEntryType.Recipe,
                RecipeId = recipeIds[day % recipeIds.Count],
                IncludeGroceries = true,
                SnapshotRecipeName = null,
                SnapshotDisplayText = "Baseline meal",
                CreatedAt = SeedTimestamp,
                UpdatedAt = SeedTimestamp,
            });
            day++;
        }

        for (var batchStart = 0; batchStart < entries.Count; batchStart += BatchSize)
        {
            await using var context = fixture.CreateContext();
            context.CalendarEntries.AddRange(entries.Skip(batchStart).Take(BatchSize));
            await context.SaveChangesAsync(Ct);
        }
    }

    private async Task SeedHouseholdAsync()
    {
        await using var context = fixture.CreateContext();
        context.HouseholdSettings.Add(new HouseholdSettings
        {
            Id = 1,
            HouseholdName = "M11 Performance Household",
            Timezone = "America/Chicago",
            DefaultDinerCount = 4,
            WeekStartsOn = 0,
            StartupPage = StartupPage.Calendar,
            AuthMode = AuthMode.SingleUser,
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
        });
        await context.SaveChangesAsync(Ct);

        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO application_settings (key, value_json, updated_at)
            VALUES ({SetupStateService.SetupCompletedKey}, 'true'::jsonb, {DateTimeOffset.UtcNow})
            ON CONFLICT (key) DO UPDATE
            SET value_json = 'true'::jsonb, updated_at = EXCLUDED.updated_at
            """,
            Ct);
    }

    private static async Task<Measurement> MeasureAsync(
        Func<Task<HttpResponseMessage>> send,
        CountingCommandInterceptor interceptor)
    {
        interceptor.Reset();
        var watch = Stopwatch.StartNew();
        using var response = await send();
        var body = await response.Content.ReadAsStringAsync(Ct);
        watch.Stop();
        return new Measurement(response.StatusCode, interceptor.Count, watch.Elapsed.TotalMilliseconds, body);
    }

    private sealed record Measurement(
        HttpStatusCode Status,
        int Commands,
        double ElapsedMs,
        string Body);

    /// <summary>
    /// Counts every executed command (reader, scalar, and non-query, sync and
    /// async) across the request-scoped contexts so an N+1 pattern shows up as a
    /// command count proportional to the seeded rows.
    /// </summary>
    private sealed class CountingCommandInterceptor : DbCommandInterceptor
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Reset() => Interlocked.Exchange(ref _count, 0);

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Interlocked.Increment(ref _count);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result)
        {
            Interlocked.Increment(ref _count);
            return result;
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            Interlocked.Increment(ref _count);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return ValueTask.FromResult(result);
        }
    }
}
