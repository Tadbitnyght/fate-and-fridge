using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Domain;
using FluentAssertions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Authorization seam matrix for the household settings endpoints
/// (docs/API.md §21). Null is an unauthenticated caller; the system actor is
/// the single-user identity; Admin/Member arrive with sessions in M1-06/M1-07
/// and the full matrix is re-tested in M1-09.
/// </summary>
public sealed class HouseholdSettingsPolicyTests
{
    [Fact]
    public void Unauthenticated_caller_can_neither_read_nor_write()
    {
        HouseholdSettingsPolicy.CanRead(null).Should().BeFalse();
        HouseholdSettingsPolicy.CanWrite(null).Should().BeFalse();
    }

    [Fact]
    public void Member_can_read_but_not_write()
    {
        var member = new CurrentActor(IsSystem: false, Role: UserRole.Member);

        HouseholdSettingsPolicy.CanRead(member).Should().BeTrue();
        HouseholdSettingsPolicy.CanWrite(member).Should().BeFalse();
    }

    [Fact]
    public void Admin_can_read_and_write()
    {
        var admin = new CurrentActor(IsSystem: false, Role: UserRole.Admin);

        HouseholdSettingsPolicy.CanRead(admin).Should().BeTrue();
        HouseholdSettingsPolicy.CanWrite(admin).Should().BeTrue();
    }

    [Theory]
    [InlineData(UserRole.Admin)]
    [InlineData(UserRole.Member)]
    public void System_actor_can_read_and_write_regardless_of_role(UserRole role)
    {
        var systemActor = new CurrentActor(IsSystem: true, Role: role);

        HouseholdSettingsPolicy.CanRead(systemActor).Should().BeTrue();
        HouseholdSettingsPolicy.CanWrite(systemActor).Should().BeTrue();
    }
}
