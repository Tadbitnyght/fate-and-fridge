using Microsoft.EntityFrameworkCore;

namespace FateAndFridge.Api.Data;

/// <summary>
/// Application database context. Entity configuration arrives with each
/// milestone (docs/MILESTONES.md); snake_case naming is configured through the
/// Npgsql provider options in Program.cs.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
    }
}
