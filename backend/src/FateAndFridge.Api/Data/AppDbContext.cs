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

    public DbSet<GroceryDepartment> GroceryDepartments => Set<GroceryDepartment>();

    public DbSet<Ingredient> Ingredients => Set<Ingredient>();

    public DbSet<IngredientAlias> IngredientAliases => Set<IngredientAlias>();

    public DbSet<Unit> Units => Set<Unit>();

    public DbSet<UnitAlias> UnitAliases => Set<UnitAlias>();

    public DbSet<Protein> Proteins => Set<Protein>();

    public DbSet<Tag> Tags => Set<Tag>();

    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    public DbSet<Recipe> Recipes => Set<Recipe>();

    public DbSet<RecipeImage> RecipeImages => Set<RecipeImage>();

    public DbSet<RecipeIngredientGroup> RecipeIngredientGroups => Set<RecipeIngredientGroup>();

    public DbSet<RecipeIngredient> RecipeIngredients => Set<RecipeIngredient>();

    public DbSet<RecipeInstructionSection> RecipeInstructionSections => Set<RecipeInstructionSection>();

    public DbSet<RecipeInstructionStep> RecipeInstructionSteps => Set<RecipeInstructionStep>();

    public DbSet<RecipeProtein> RecipeProteins => Set<RecipeProtein>();

    public DbSet<RecipeTag> RecipeTags => Set<RecipeTag>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfiguration(new HouseholdSettingsConfiguration());
        modelBuilder.ApplyConfiguration(new ApplicationSettingConfiguration());
        modelBuilder.ApplyConfiguration(new UserConfiguration());
        modelBuilder.ApplyConfiguration(new UserPermissionConfiguration());
        modelBuilder.ApplyConfiguration(new AuthEventConfiguration());
        modelBuilder.ApplyConfiguration(new UserSessionConfiguration());

        // M2-01 reference-data and audit baseline (docs/M2_PLAN.md §3).
        modelBuilder.ApplyConfiguration(new GroceryDepartmentConfiguration());
        modelBuilder.ApplyConfiguration(new IngredientConfiguration());
        modelBuilder.ApplyConfiguration(new IngredientAliasConfiguration());
        modelBuilder.ApplyConfiguration(new UnitConfiguration());
        modelBuilder.ApplyConfiguration(new UnitAliasConfiguration());
        modelBuilder.ApplyConfiguration(new ProteinConfiguration());
        modelBuilder.ApplyConfiguration(new TagConfiguration());
        modelBuilder.ApplyConfiguration(new AuditEventConfiguration());

        // M3-01 recipe schema baseline (docs/M3_PLAN.md §2/§3).
        modelBuilder.ApplyConfiguration(new RecipeConfiguration());
        modelBuilder.ApplyConfiguration(new RecipeImageConfiguration());
        modelBuilder.ApplyConfiguration(new RecipeIngredientGroupConfiguration());
        modelBuilder.ApplyConfiguration(new RecipeIngredientConfiguration());
        modelBuilder.ApplyConfiguration(new RecipeInstructionSectionConfiguration());
        modelBuilder.ApplyConfiguration(new RecipeInstructionStepConfiguration());
        modelBuilder.ApplyConfiguration(new RecipeProteinConfiguration());
        modelBuilder.ApplyConfiguration(new RecipeTagConfiguration());
    }
}
