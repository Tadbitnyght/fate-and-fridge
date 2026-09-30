using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Domain;
using FluentAssertions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Minimal M1-07 effective-permission helper (owned and expanded by M1-09):
/// admins hold all seven keys; member grants imply their matching view key
/// (docs/DATABASE.md §2).
/// </summary>
public sealed class EffectivePermissionsTests
{
    private static readonly string[] AllSeven =
    [
        EffectivePermissions.CalendarView,
        EffectivePermissions.CalendarEdit,
        EffectivePermissions.GroceriesView,
        EffectivePermissions.GroceriesEdit,
        EffectivePermissions.RecipesView,
        EffectivePermissions.RecipesEdit,
        EffectivePermissions.ReferenceDataEdit,
    ];

    [Fact]
    public void All_contains_the_seven_documented_keys_in_canonical_order() =>
        EffectivePermissions.All.Should().Equal(AllSeven);

    [Fact]
    public void Admin_holds_all_seven_keys() =>
        EffectivePermissions.For(UserRole.Admin).Should().Equal(AllSeven);

    [Fact]
    public void Admin_grants_do_not_narrow_the_key_set() =>
        EffectivePermissions.For(UserRole.Admin, [EffectivePermissions.CalendarView]).Should().Equal(AllSeven);

    [Fact]
    public void Member_without_grants_holds_no_keys() =>
        EffectivePermissions.For(UserRole.Member).Should().BeEmpty();

    [Theory]
    [InlineData(EffectivePermissions.CalendarEdit, EffectivePermissions.CalendarView)]
    [InlineData(EffectivePermissions.GroceriesEdit, EffectivePermissions.GroceriesView)]
    [InlineData(EffectivePermissions.RecipesEdit, EffectivePermissions.RecipesView)]
    public void Member_edit_grants_imply_the_view_key(string editKey, string viewKey) =>
        EffectivePermissions.For(UserRole.Member, [editKey]).Should().Equal(viewKey, editKey);

    [Fact]
    public void Member_view_grants_pass_through_in_canonical_order() =>
        EffectivePermissions.For(
                UserRole.Member,
                [EffectivePermissions.RecipesView, EffectivePermissions.CalendarView])
            .Should().Equal(EffectivePermissions.CalendarView, EffectivePermissions.RecipesView);

    [Fact]
    public void Member_reference_data_edit_has_no_implied_view_key() =>
        EffectivePermissions.For(UserRole.Member, [EffectivePermissions.ReferenceDataEdit])
            .Should().Equal(EffectivePermissions.ReferenceDataEdit);

    [Fact]
    public void Unknown_and_duplicate_grants_are_ignored() =>
        EffectivePermissions.For(
                UserRole.Member,
                ["bogus.key", EffectivePermissions.CalendarEdit, EffectivePermissions.CalendarEdit])
            .Should().Equal(EffectivePermissions.CalendarView, EffectivePermissions.CalendarEdit);

    [Fact]
    public void Member_explicitly_granted_all_seven_keys_holds_all_seven() =>
        EffectivePermissions.For(UserRole.Member, AllSeven).Should().Equal(AllSeven);
}
