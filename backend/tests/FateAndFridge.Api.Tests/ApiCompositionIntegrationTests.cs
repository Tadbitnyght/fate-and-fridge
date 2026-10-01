using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Host-composition checks that need the real Program DI container. Booting the
/// host seeds reference data on every startup, so a reachable PostgreSQL
/// container is required.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ApiCompositionIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:17-alpine").Build();

    public ValueTask InitializeAsync() =>
        new(_postgres.StartAsync(TestContext.Current.CancellationToken));

    public ValueTask DisposeAsync() => new(_postgres.DisposeAsync().AsTask());

    [Fact]
    public void Password_hasher_is_registered_as_a_singleton_in_di()
    {
        using var factory = new ApiFactory(
            _postgres.GetConnectionString(),
            migrateOnStartup: true,
            Path.Combine(Path.GetTempPath(), "fate-and-fridge-tests", Guid.NewGuid().ToString("N")));

        var first = factory.Services.GetRequiredService<IPasswordHasher>();
        var second = factory.Services.GetRequiredService<IPasswordHasher>();

        first.Should().BeOfType<Argon2idPasswordHasher>();
        second.Should().BeSameAs(first);
    }
}
