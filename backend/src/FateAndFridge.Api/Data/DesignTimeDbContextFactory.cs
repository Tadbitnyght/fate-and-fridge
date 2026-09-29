using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FateAndFridge.Api.Data;

/// <summary>
/// Allows <c>dotnet ef</c> commands to run without starting the application
/// host. Reads the same <c>ConnectionStrings__Default</c> environment variable
/// used at runtime; commands that only build the model (for example,
/// <c>migrations add</c>) never open a connection.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    private const string HostFallback =
        "Host=localhost;Port=5432;Database=fatefridge;Username=fatefridge;Password=fatefridge";

    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__Default") ?? HostFallback;

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new AppDbContext(options);
    }
}
