using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using FateAndFridge.Api.Data;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Services;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// PostgreSQL-backed verification of the M2-02 audit writer (docs/DATABASE.md §10):
/// row contents, the injected clock, transaction atomicity, and secret-free
/// payloads. Requires a Docker daemon; run with
/// <c>dotnet test --filter "Category!=Integration"</c> to skip locally.
/// </summary>
[Trait("Category", "Integration")]
[Collection(M1SetupCollection.Name)]
public sealed class AuditServiceIntegrationTests(M1SetupFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public ValueTask InitializeAsync() =>
        new(fixture.ResetAsync(TestContext.Current.CancellationToken));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Round_trips_actor_entity_payloads_reason_ip_and_injected_timestamp()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var userId = await SeedUserAsync(cancellationToken);
        const string BeforeJson = """{"canonical_name":"Tomato","is_active":true}""";
        const string AfterJson = """{"canonical_name":"Tomato","is_active":false}""";

        await using (var context = fixture.CreateContext())
        {
            var service = CreateService(context, new TestTimeProvider(Start));
            await service.WriteAsync(
                new AuditEventEntry(
                    AuditEventTypes.Retired,
                    AuditEntityTypes.Ingredient,
                    EntityId: userId.ToString(CultureInfo.InvariantCulture),
                    BeforeJson: BeforeJson,
                    AfterJson: AfterJson,
                    Reason: "duplicate of Roma",
                    IpAddress: "203.0.113.7",
                    ActorUserId: userId),
                cancellationToken);

            await context.SaveChangesAsync(cancellationToken);
        }

        await using var verification = fixture.CreateContext();
        var row = await verification.AuditEvents.AsNoTracking().SingleAsync(cancellationToken);
        row.ActorUserId.Should().Be(userId);
        row.EventType.Should().Be("retired");
        row.EntityType.Should().Be("ingredient");
        row.EntityId.Should().Be(userId.ToString(CultureInfo.InvariantCulture));
        AssertJsonEquals(row.BeforeJson, BeforeJson);
        AssertJsonEquals(row.AfterJson, AfterJson);
        row.Reason.Should().Be("duplicate of Roma");
        row.IpAddress.Should().Be("203.0.113.7");
        row.CreatedAt.Should().Be(Start);
        row.MetadataJson.Should().Be("{}");
    }

    [Fact]
    public async Task System_actor_writes_a_null_actor_with_empty_optionals()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using (var context = fixture.CreateContext())
        {
            var service = CreateService(context, new TestTimeProvider(Start));
            await service.WriteAsync(
                new AuditEventEntry(
                    AuditEventTypes.Reordered,
                    AuditEntityTypes.Department,
                    EntityId: "8",
                    Reason: "admin reorder"),
                cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
        }

        await using var verification = fixture.CreateContext();
        var row = await verification.AuditEvents.AsNoTracking().SingleAsync(cancellationToken);
        row.ActorUserId.Should().BeNull();
        row.EventType.Should().Be("reordered");
        row.EntityType.Should().Be("department");
        row.EntityId.Should().Be("8");
        row.BeforeJson.Should().BeNull();
        row.AfterJson.Should().BeNull();
        row.Reason.Should().Be("admin reorder");
        row.IpAddress.Should().BeNull();
        row.CreatedAt.Should().Be(Start);
        row.MetadataJson.Should().Be("{}");
    }

    [Fact]
    public async Task Transaction_rollback_discards_the_audit_row_with_the_primary_change()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using (var context = fixture.CreateContext())
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            var service = CreateService(context, new TestTimeProvider(Start));

            await service.WriteAsync(
                new AuditEventEntry(
                    AuditEventTypes.Created,
                    AuditEntityTypes.Tag,
                    EntityId: "rollback-tag",
                    AfterJson: """{"name":"Rollback Tag"}"""),
                cancellationToken);
            context.Tags.Add(new Tag { Name = "Rollback Tag", NormalizedName = "rollback tag" });

            await context.SaveChangesAsync(cancellationToken);
            (await context.AuditEvents.CountAsync(cancellationToken)).Should().Be(1);

            await transaction.RollbackAsync(cancellationToken);
        }

        await using var verification = fixture.CreateContext();
        (await verification.AuditEvents.CountAsync(cancellationToken)).Should().Be(0);
        (await verification.Tags.CountAsync(
                tag => tag.NormalizedName == "rollback tag",
                cancellationToken))
            .Should().Be(0);
    }

    [Fact]
    public async Task Failed_primary_change_discards_the_staged_audit_row()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using (var context = fixture.CreateContext())
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            var service = CreateService(context, new TestTimeProvider(Start));

            await service.WriteAsync(
                new AuditEventEntry(
                    AuditEventTypes.Created,
                    AuditEntityTypes.Tag,
                    EntityId: "duplicate-tag",
                    AfterJson: """{"name":"Duplicate Tag"}"""),
                cancellationToken);

            // The unique normalized-name index rejects the second insert, so the
            // SaveChanges that carries the staged audit row fails as a unit.
            context.Tags.Add(new Tag { Name = "Duplicate Tag", NormalizedName = "duplicate tag" });
            context.Tags.Add(new Tag { Name = "Duplicate Tag", NormalizedName = "duplicate tag" });

            await FluentActions.Awaiting(() => context.SaveChangesAsync(cancellationToken))
                .Should().ThrowAsync<DbUpdateException>();

            await transaction.RollbackAsync(cancellationToken);
        }

        await using var verification = fixture.CreateContext();
        (await verification.AuditEvents.CountAsync(cancellationToken)).Should().Be(0);
        (await verification.Tags.CountAsync(
                tag => tag.NormalizedName == "duplicate tag",
                cancellationToken))
            .Should().Be(0);
    }

    [Fact]
    public async Task Representative_payload_writes_only_the_supplied_json_without_secret_material()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const string BeforeJson = """{"canonical_name":"Tomato","department_id":1,"is_active":true}""";
        const string AfterJson = """{"canonical_name":"Tomato","department_id":2,"is_active":true}""";
        var metadata = new Dictionary<string, object?>
        {
            ["changed_fields"] = new[] { "department_id" },
        };

        await using (var context = fixture.CreateContext())
        {
            var service = CreateService(context, new TestTimeProvider(Start));
            await service.WriteAsync(
                new AuditEventEntry(
                    AuditEventTypes.Updated,
                    AuditEntityTypes.Ingredient,
                    EntityId: "17",
                    BeforeJson: BeforeJson,
                    AfterJson: AfterJson,
                    Reason: "recategorize",
                    IpAddress: "203.0.113.7",
                    Metadata: metadata),
                cancellationToken);

            await context.SaveChangesAsync(cancellationToken);
        }

        await using var verification = fixture.CreateContext();
        var row = await verification.AuditEvents.AsNoTracking().SingleAsync(cancellationToken);
        AssertJsonEquals(row.BeforeJson, BeforeJson);
        AssertJsonEquals(row.AfterJson, AfterJson);
        AssertJsonEquals(row.MetadataJson, JsonSerializer.Serialize(metadata));

        var written = string.Join(
            '\n',
            row.EventType,
            row.EntityType,
            row.EntityId,
            row.BeforeJson,
            row.AfterJson,
            row.Reason,
            row.IpAddress,
            row.MetadataJson);
        written.Should().NotContain("password");
        written.Should().NotContain("token");
        written.Should().NotContain("session");
        written.Should().NotContain("recovery");
    }

    private static void AssertJsonEquals(string? actual, string expected)
    {
        actual.Should().NotBeNull();
        JsonNode.DeepEquals(JsonNode.Parse(actual!), JsonNode.Parse(expected))
            .Should().BeTrue($"expected JSON {expected} but found {actual}");
    }

    private static AuditService CreateService(AppDbContext context, TestTimeProvider clock) =>
        new(context, clock, NullLogger<AuditService>.Instance);

    private async Task<long> SeedUserAsync(CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        var user = new User
        {
            Username = "auditor",
            NormalizedUsername = "auditor",
            PasswordHash = "test-only-hash",
            Role = UserRole.Admin,
            IsActive = true,
            CreatedAt = Start,
            UpdatedAt = Start,
        };

        context.Users.Add(user);
        await context.SaveChangesAsync(cancellationToken);
        return user.Id;
    }
}
