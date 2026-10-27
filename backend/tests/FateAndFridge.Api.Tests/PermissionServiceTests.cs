using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Auth.Permissions;
using FateAndFridge.Api.Domain;
using FluentAssertions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Loader/service behavior (docs/DATABASE.md §2): system/Admin bypass without
/// a database read, members are evaluated from fresh grants on every call, and
/// permission changes therefore apply on the next request.
/// </summary>
public sealed class PermissionServiceTests
{
    private static readonly CurrentActor SystemActor = new(IsSystem: true, Role: UserRole.Admin);

    private static readonly CurrentActor Admin = new(IsSystem: false, Role: UserRole.Admin);

    [Fact]
    public async Task System_actor_and_admin_bypass_the_grant_reader()
    {
        var reader = new FakeGrantReader { GrantedKeys = [] };
        var service = new PermissionService(reader);

        (await service.GetEffectiveKeysAsync(SystemActor, TestContext.Current.CancellationToken))
            .Should().Equal(PermissionKeys.All);
        (await service.GetEffectiveKeysAsync(Admin, TestContext.Current.CancellationToken))
            .Should().Equal(PermissionKeys.All);

        reader.ReadCount.Should().Be(0);
    }

    [Fact]
    public async Task Member_grants_are_evaluated_with_edit_implies_view()
    {
        var reader = new FakeGrantReader
        {
            GrantedKeys = [PermissionKeys.CalendarEdit, "bogus.key"],
        };
        var service = new PermissionService(reader);
        var member = MemberWithId(7);

        var effective = await service.GetEffectiveKeysAsync(member, TestContext.Current.CancellationToken);

        effective.Should().Equal(PermissionKeys.CalendarView, PermissionKeys.CalendarEdit);
        reader.ReadCount.Should().Be(1);
        reader.LastUserId.Should().Be(7);
    }

    [Fact]
    public async Task Member_without_a_user_id_holds_no_permissions()
    {
        var reader = new FakeGrantReader { GrantedKeys = [PermissionKeys.CalendarEdit] };
        var service = new PermissionService(reader);

        var effective = await service.GetEffectiveKeysAsync(
            new CurrentActor(IsSystem: false, Role: UserRole.Member),
            TestContext.Current.CancellationToken);

        effective.Should().BeEmpty();
        reader.ReadCount.Should().Be(0);
    }

    [Fact]
    public async Task Grants_are_read_fresh_on_every_call()
    {
        var reader = new FakeGrantReader { GrantedKeys = [PermissionKeys.RecipesView] };
        var service = new PermissionService(reader);
        var member = MemberWithId(9);

        (await service.GetEffectiveKeysAsync(member, TestContext.Current.CancellationToken))
            .Should().Equal(PermissionKeys.RecipesView);

        // Row changes take effect on the next call: no cross-request caching.
        reader.GrantedKeys = [PermissionKeys.GroceriesEdit];

        (await service.GetEffectiveKeysAsync(member, TestContext.Current.CancellationToken))
            .Should().Equal(PermissionKeys.GroceriesView, PermissionKeys.GroceriesEdit);
        reader.ReadCount.Should().Be(2);
    }

    [Fact]
    public async Task HasPermission_matches_granted_and_implied_keys_and_denies_the_rest()
    {
        var reader = new FakeGrantReader { GrantedKeys = [PermissionKeys.CalendarEdit] };
        var service = new PermissionService(reader);
        var member = MemberWithId(11);
        var cancellationToken = TestContext.Current.CancellationToken;

        (await service.HasPermissionAsync(member, PermissionKeys.CalendarEdit, cancellationToken))
            .Should().BeTrue();
        (await service.HasPermissionAsync(member, PermissionKeys.CalendarView, cancellationToken))
            .Should().BeTrue("edit implies view");
        (await service.HasPermissionAsync(member, PermissionKeys.RecipesEdit, cancellationToken))
            .Should().BeFalse();
        (await service.HasPermissionAsync(member, "unknown.key", cancellationToken))
            .Should().BeFalse("unknown keys never match");

        reader.ReadCount.Should().Be(3, "unknown keys short-circuit before the reader");
    }

    private static CurrentActor MemberWithId(long userId) =>
        new(IsSystem: false, Role: UserRole.Member) { UserId = userId };

    private sealed class FakeGrantReader : IPermissionGrantReader
    {
        public List<string> GrantedKeys { get; set; } = [];

        public int ReadCount { get; private set; }

        public long? LastUserId { get; private set; }

        public Task<IReadOnlyList<string>> ReadGrantedKeysAsync(
            long userId,
            CancellationToken cancellationToken = default)
        {
            ReadCount++;
            LastUserId = userId;
            return Task.FromResult<IReadOnlyList<string>>(GrantedKeys.ToArray());
        }
    }
}
