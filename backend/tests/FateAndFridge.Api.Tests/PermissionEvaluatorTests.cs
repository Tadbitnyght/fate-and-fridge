using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Auth.Permissions;
using FateAndFridge.Api.Domain;
using FluentAssertions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Effective-set matrix (docs/DATABASE.md §2): system/Admin hold all seven
/// keys; members hold granted keys plus edit-implies-view; unknown keys are
/// ignored; results follow the canonical order.
/// </summary>
public sealed class PermissionEvaluatorTests
{
    private static readonly CurrentActor SystemActor = new(IsSystem: true, Role: UserRole.Admin);

    private static readonly CurrentActor Admin = new(IsSystem: false, Role: UserRole.Admin);

    private static readonly CurrentActor Member = new(IsSystem: false, Role: UserRole.Member);

    [Fact]
    public void System_actor_holds_all_seven_keys() =>
        PermissionEvaluator.For(SystemActor, ["bogus.key"]).Should().Equal(PermissionKeys.All);

    [Fact]
    public void Admin_holds_all_seven_keys() =>
        PermissionEvaluator.For(Admin, [PermissionKeys.CalendarView]).Should().Equal(PermissionKeys.All);

    [Fact]
    public void Member_without_grants_holds_no_keys() =>
        PermissionEvaluator.For(Member).Should().BeEmpty();

    [Theory]
    [InlineData(PermissionKeys.CalendarEdit, PermissionKeys.CalendarView)]
    [InlineData(PermissionKeys.GroceriesEdit, PermissionKeys.GroceriesView)]
    [InlineData(PermissionKeys.RecipesEdit, PermissionKeys.RecipesView)]
    public void Member_edit_grants_imply_the_view_key(string editKey, string viewKey) =>
        PermissionEvaluator.For(Member, [editKey]).Should().Equal(viewKey, editKey);

    [Fact]
    public void Member_explicit_view_grants_are_retained() =>
        PermissionEvaluator.For(Member, [PermissionKeys.GroceriesView]).Should()
            .Equal(PermissionKeys.GroceriesView);

    [Fact]
    public void Member_reference_data_edit_has_no_implied_view_key() =>
        PermissionEvaluator.For(Member, [PermissionKeys.ReferenceDataEdit]).Should()
            .Equal(PermissionKeys.ReferenceDataEdit);

    [Fact]
    public void Unknown_and_duplicate_grants_are_ignored() =>
        PermissionEvaluator.ForMember(
                ["bogus.key", PermissionKeys.CalendarEdit, PermissionKeys.CalendarEdit])
            .Should().Equal(PermissionKeys.CalendarView, PermissionKeys.CalendarEdit);

    [Fact]
    public void Member_grants_follow_canonical_order() =>
        PermissionEvaluator.For(
                Member,
                [PermissionKeys.RecipesView, PermissionKeys.GroceriesEdit, PermissionKeys.CalendarView])
            .Should().Equal(
                PermissionKeys.CalendarView,
                PermissionKeys.GroceriesView,
                PermissionKeys.GroceriesEdit,
                PermissionKeys.RecipesView);

    [Fact]
    public void Member_explicitly_granted_all_seven_keys_holds_all_seven() =>
        PermissionEvaluator.For(Member, PermissionKeys.All).Should().Equal(PermissionKeys.All);
}
