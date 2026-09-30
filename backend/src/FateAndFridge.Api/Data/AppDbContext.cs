using FateAndFridge.Api.Data.Configurations;
using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace FateAndFridge.Api.Data;

/// <summary>
/// Application database context. Entity configuration arrives with each
/// milestone (docs/MILESTONES.md); snake_case naming is configured through the
/// Npgsql provider options in Program.cs.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<HouseholdSettings> HouseholdSettings => Set<HouseholdSettings>();

    public DbSet<ApplicationSetting> ApplicationSettings => Set<ApplicationSetting>();

    public DbSet<User> Users => Set<User>();

    public DbSet<UserPermission> UserPermissions => Set<UserPermission>();

    public DbSet<AuthEvent> AuthEvents => Set<AuthEvent>();

    public DbSet<UserSession> UserSessions => Set<UserSession>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfiguration(new HouseholdSettingsConfiguration());
        modelBuilder.ApplyConfiguration(new ApplicationSettingConfiguration());
        modelBuilder.ApplyConfiguration(new UserConfiguration());
        modelBuilder.ApplyConfiguration(new UserPermissionConfiguration());
        modelBuilder.ApplyConfiguration(new AuthEventConfiguration());
        modelBuilder.ApplyConfiguration(new UserSessionConfiguration());
    }
}
