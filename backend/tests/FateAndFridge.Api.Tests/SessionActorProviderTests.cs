using System.Security.Claims;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Data.Configurations;
using FateAndFridge.Api.Domain;
using FluentAssertions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// The session-principal → actor mapping (M1-06 / M9-01). The mapping must
/// trust only the session authentication scheme, exactly like
/// <c>CsrfMiddleware.IsSessionAuthenticated</c> and
/// <c>MustChangePasswordMiddleware.HasForcedChange</c>, so a principal
/// authenticated by any other scheme can never become a session actor.
/// </summary>
public sealed class SessionActorProviderTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FromPrincipal_maps_a_session_principal_to_the_actor()
    {
        var user = CreateUser(UserRole.Admin);

        var actor = SessionActorProvider.FromPrincipal(SessionClaims.Create(user, sessionId: 7));

        actor.Should().NotBeNull();
        actor!.UserId.Should().Be(user.Id);
        actor.IsSystem.Should().BeFalse();
        actor.Role.Should().Be(UserRole.Admin);
    }

    [Fact]
    public void FromPrincipal_rejects_an_identity_authenticated_by_another_scheme()
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "42"),
                new Claim(ClaimTypes.Role, EnumTextConverters.UserRoleToText(UserRole.Admin)),
            ],
            authenticationType: "SomeOtherScheme");

        SessionActorProvider.FromPrincipal(new ClaimsPrincipal(identity)).Should().BeNull();
    }

    [Fact]
    public void FromPrincipal_rejects_an_anonymous_principal() =>
        SessionActorProvider.FromPrincipal(new ClaimsPrincipal(new ClaimsIdentity())).Should().BeNull();

    [Fact]
    public void FromPrincipal_rejects_null() => SessionActorProvider.FromPrincipal(null).Should().BeNull();

    private static User CreateUser(UserRole role) => new()
    {
        Id = 42,
        Username = "alice",
        NormalizedUsername = "alice",
        PasswordHash = "phc-placeholder",
        Role = role,
        IsActive = true,
        CreatedAt = Timestamp,
        UpdatedAt = Timestamp,
    };
}
