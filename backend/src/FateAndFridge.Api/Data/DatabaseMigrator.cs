using Microsoft.EntityFrameworkCore;

namespace FateAndFridge.Api.Data;

/// <summary>Applies pending EF Core migrations, retrying while PostgreSQL warms up.</summary>
internal static class DatabaseMigrator
{
    private const int MaxAttempts = 30;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    public static async Task MigrateWithRetryAsync(
        IServiceProvider services,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var scope = services.CreateAsyncScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await dbContext.Database.MigrateAsync(cancellationToken);
                logger.LogInformation("Database migrations applied");
                return;
            }
            catch (Exception ex) when (attempt < MaxAttempts)
            {
                logger.LogWarning(
                    ex,
                    "Database not ready (attempt {Attempt}/{MaxAttempts}); retrying in {Delay}s",
                    attempt,
                    MaxAttempts,
                    RetryDelay.TotalSeconds);
                await Task.Delay(RetryDelay, cancellationToken);
            }
        }
    }
}
