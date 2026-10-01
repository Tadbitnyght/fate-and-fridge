using FateAndFridge.Api.Data;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Services;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Docker-free checks for the M2-02 audit writer: constants, argument guards,
/// entry defaults, and the row staged before the caller's SaveChanges.
/// </summary>
public sealed class AuditServiceUnitTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Event_type_constants_use_the_documented_snake_case_values()
    {
        AuditEventTypes.Created.Should().Be("created");
        AuditEventTypes.Updated.Should().Be("updated");
        AuditEventTypes.Retired.Should().Be("retired");
        AuditEventTypes.Deleted.Should().Be("deleted");
        AuditEventTypes.Merged.Should().Be("merged");
        AuditEventTypes.FactorChanged.Should().Be("factor_changed");
        AuditEventTypes.Reordered.Should().Be("reordered");
    }

    [Fact]
    public void Entity_type_constants_cover_the_M2_reference_data()
    {
        AuditEntityTypes.Department.Should().Be("department");
        AuditEntityTypes.Unit.Should().Be("unit");
        AuditEntityTypes.Ingredient.Should().Be("ingredient");
        AuditEntityTypes.Protein.Should().Be("protein");
        AuditEntityTypes.Tag.Should().Be("tag");
    }

    [Fact]
    public async Task WriteAsync_stages_the_row_on_the_caller_context_without_saving()
    {
        await using var context = CreateContext();
        var service = CreateService(context, new TestTimeProvider(Start));

        await service.WriteAsync(new AuditEventEntry(
            AuditEventTypes.FactorChanged,
            AuditEntityTypes.Unit,
            EntityId: "12",
            BeforeJson: """{"base_factor":28.349523125}""",
            AfterJson: """{"base_factor":30}""",
            Reason: "package accuracy",
            IpAddress: "203.0.113.7",
            ActorUserId: 41,
            Metadata: new Dictionary<string, object?> { ["field"] = "base_factor" }), TestContext.Current.CancellationToken);

        var staged = context.ChangeTracker.Entries<AuditEvent>().Should().ContainSingle().Subject;
        staged.State.Should().Be(EntityState.Added);

        var row = staged.Entity;
        row.ActorUserId.Should().Be(41);
        row.EventType.Should().Be("factor_changed");
        row.EntityType.Should().Be("unit");
        row.EntityId.Should().Be("12");
        row.BeforeJson.Should().Be("""{"base_factor":28.349523125}""");
        row.AfterJson.Should().Be("""{"base_factor":30}""");
        row.Reason.Should().Be("package accuracy");
        row.IpAddress.Should().Be("203.0.113.7");
        row.CreatedAt.Should().Be(Start);
        row.MetadataJson.Should().Be("""{"field":"base_factor"}""");
    }

    [Fact]
    public async Task WriteAsync_defaults_metadata_to_an_empty_object_for_a_system_actor()
    {
        await using var context = CreateContext();
        var service = CreateService(context, new TestTimeProvider(Start));

        await service.WriteAsync(new AuditEventEntry(
            AuditEventTypes.Reordered,
            AuditEntityTypes.Department), TestContext.Current.CancellationToken);

        var row = context.ChangeTracker.Entries<AuditEvent>().Single().Entity;
        row.ActorUserId.Should().BeNull();
        row.EntityId.Should().BeNull();
        row.BeforeJson.Should().BeNull();
        row.AfterJson.Should().BeNull();
        row.Reason.Should().BeNull();
        row.IpAddress.Should().BeNull();
        row.MetadataJson.Should().Be("{}");
    }

    [Fact]
    public async Task WriteAsync_rejects_a_null_entry()
    {
        using var context = CreateContext();
        var service = CreateService(context, new TestTimeProvider(Start));

        await FluentActions.Awaiting(() => service.WriteAsync(null!))
            .Should().ThrowAsync<ArgumentNullException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task WriteAsync_rejects_a_blank_event_type(string? eventType)
    {
        using var context = CreateContext();
        var service = CreateService(context, new TestTimeProvider(Start));

        await FluentActions.Awaiting(() => service.WriteAsync(new AuditEventEntry(eventType!, AuditEntityTypes.Tag)))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task WriteAsync_rejects_a_blank_entity_type()
    {
        using var context = CreateContext();
        var service = CreateService(context, new TestTimeProvider(Start));

        await FluentActions.Awaiting(() => service.WriteAsync(new AuditEventEntry(AuditEventTypes.Updated, " ")))
            .Should().ThrowAsync<ArgumentException>();
    }

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=audit-unit-tests;Username=unit;Password=unit")
            .Options);

    private static AuditService CreateService(AppDbContext context, TestTimeProvider clock) =>
        new(context, clock, NullLogger<AuditService>.Instance);
}
