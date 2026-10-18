using System.Net;
using System.Text;
using System.Text.Json;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Auth.Permissions;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.Middleware;
using FateAndFridge.Api.Services;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// HTTP-level verification of M1-10 user management (docs/API.md §6/§21):
/// authorization matrix, CRUD, username/password validation, permission
/// replacement, disable/enable semantics, temporary-password handling, and
/// last-active-Admin safety including the concurrent race. Requires a Docker
/// daemon; run with <c>dotnet test --filter "Category!=Integration"</c> to skip
/// locally.
/// </summary>
[Trait("Category", "Integration")]
[Collection(M1SetupCollection.Name)]
public sealed class UserManagementIntegrationTests(M1SetupFixture fixture) : IAsyncLifetime
{
    private const string AdminPassword = "root horse battery staple";
    private const string MemberPassword = "correct horse battery staple";
    private const string TestUserAgent = "user-management-test";
    private const string AntiforgeryCookieName = "faf_csrf";

    private static readonly DateTimeOffset SeedTimestamp =
        new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public ValueTask InitializeAsync() =>
        new(fixture.ResetAsync(TestContext.Current.CancellationToken));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Theory]
    [InlineData("GET", "/api/v1/users")]
    [InlineData("POST", "/api/v1/users")]
    [InlineData("GET", "/api/v1/users/1")]
    [InlineData("PATCH", "/api/v1/users/1")]
    [InlineData("PATCH", "/api/v1/users/1/permissions")]
    [InlineData("POST", "/api/v1/users/1/disable")]
    [InlineData("POST", "/api/v1/users/1/enable")]
    [InlineData("POST", "/api/v1/users/1/reset-password")]
    [InlineData("POST", "/api/v1/settings/auth-mode")]
    public async Task Member_is_forbidden_and_unauthenticated_is_auth_required(string method, string path)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("jane", MemberPassword, cancellationToken, UserRole.Member);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var member = await SignInAsync(factory, "jane", MemberPassword, cancellationToken);

        var memberResponse = await SendJsonAsync(
            member.Client,
            new HttpMethod(method),
            path,
            json: "{}",
            member.CsrfToken,
            cancellationToken);
        memberResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadErrorCodeAsync(memberResponse, cancellationToken)).Should().Be(ErrorCodes.Forbidden);

        using var anonymous = factory.CreateDefaultClient();
        var anonymousResponse = await SendJsonAsync(
            anonymous,
            new HttpMethod(method),
            path,
            json: "{}",
            csrfToken: null,
            cancellationToken);
        anonymousResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ReadErrorCodeAsync(anonymousResponse, cancellationToken)).Should().Be(ErrorCodes.AuthRequired);
    }

    [Fact]
    public async Task Single_user_system_actor_can_manage_users()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.SingleUser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();

        var list = await client.GetAsync("/api/v1/users", cancellationToken);
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(list, cancellationToken)).GetArrayLength().Should().Be(0);

        var created = await SendJsonAsync(
            client,
            HttpMethod.Post,
            "/api/v1/users",
            """{"username":"first"}""",
            csrfToken: null,
            cancellationToken);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var createdRoot = await ReadJsonAsync(created, cancellationToken);
        var userId = createdRoot.GetProperty("id").GetInt64();

        (await client.GetAsync($"/api/v1/users/{userId}", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        var disabled = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/v1/users/{userId}/disable",
            json: null,
            csrfToken: null,
            cancellationToken);
        disabled.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(disabled, cancellationToken)).GetProperty("is_active").GetBoolean()
            .Should().BeFalse();

        var enabled = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/v1/users/{userId}/enable",
            json: null,
            csrfToken: null,
            cancellationToken);
        enabled.StatusCode.Should().Be(HttpStatusCode.OK);

        (await client.GetAsync("/api/v1/users/999999", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Create_without_password_returns_a_one_time_temporary_password()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var admin = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var response = await SendJsonAsync(
            admin.Client,
            HttpMethod.Post,
            "/api/v1/users",
            """{"username":"newbie"}""",
            admin.CsrfToken,
            cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var payload = JsonDocument.Parse(body);
        var root = payload.RootElement;
        root.GetProperty("username").GetString().Should().Be("newbie");
        root.GetProperty("role").GetString().Should().Be("member");
        root.GetProperty("is_active").GetBoolean().Should().BeTrue();
        root.GetProperty("must_change_password").GetBoolean().Should().BeTrue();
        root.GetProperty("permissions").EnumerateArray().Select(element => element.GetString()!)
            .Should().Equal(PermissionKeys.MemberViewOnly);

        var temporaryPassword = root.GetProperty("temporary_password").GetString()!;
        PasswordPolicy.IsValid(temporaryPassword).Should().BeTrue();
        CountOccurrences(body, temporaryPassword).Should().Be(1, "the temporary password is returned once");

        await using (var context = fixture.CreateContext())
        {
            var stored = await context.Users.AsNoTracking().SingleAsync(
                candidate => candidate.Username == "newbie",
                cancellationToken);
            stored.MustChangePassword.Should().BeTrue();
            stored.PasswordHash.Should().NotBe(temporaryPassword);
            new Argon2idPasswordHasher().Verify(stored.PasswordHash, temporaryPassword).Should().BeTrue();
        }

        using var anonymousClient = factory.CreateDefaultClient();
        var login = await LoginAsync(anonymousClient, "newbie", temporaryPassword, cancellationToken);
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var loginRoot = await ReadJsonAsync(login, cancellationToken);
        loginRoot.GetProperty("must_change_password").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Create_with_an_explicit_password_still_requires_a_change()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var admin = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var response = await SendJsonAsync(
            admin.Client,
            HttpMethod.Post,
            "/api/v1/users",
            """{"username":"second","role":"admin","password":"second admin password"}""",
            admin.CsrfToken,
            cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var root = await ReadJsonAsync(response, cancellationToken);
        root.GetProperty("role").GetString().Should().Be("admin");
        root.GetProperty("must_change_password").GetBoolean().Should().BeTrue();
        root.TryGetProperty("temporary_password", out _).Should().BeFalse();

        using var anonymousClient = factory.CreateDefaultClient();
        (await LoginAsync(anonymousClient, "second", "second admin password", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("{}", "username")]
    [InlineData("""{"username":"ab"}""", "username")]
    [InlineData("""{"username":"bad name"}""", "username")]
    [InlineData("""{"username":"bad@name"}""", "username")]
    [InlineData("""{"username":123}""", "username")]
    [InlineData("""{"username":"okname","role":"boss"}""", "role")]
    [InlineData("""{"username":"okname","password":"short"}""", "password")]
    [InlineData("""{"username":"okname","password":42}""", "password")]
    [InlineData("not json", "body")]
    [InlineData("[]", "body")]
    public async Task Create_validation_failures_return_400_with_the_field(string json, string field)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var admin = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var response = await SendJsonAsync(
            admin.Client,
            HttpMethod.Post,
            "/api/v1/users",
            json,
            admin.CsrfToken,
            cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var root = await ReadJsonAsync(response, cancellationToken);
        var error = root.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be(ErrorCodes.ValidationFailed);
        error.GetProperty("details").GetProperty("field_errors").TryGetProperty(field, out _).Should().BeTrue();
    }

    [Fact]
    public async Task Create_rejects_a_duplicate_username_case_insensitively()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var admin = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        (await SendJsonAsync(
            admin.Client,
            HttpMethod.Post,
            "/api/v1/users",
            """{"username":"newbie","password":"newbie password 1"}""",
            admin.CsrfToken,
            cancellationToken)).StatusCode.Should().Be(HttpStatusCode.Created);

        var duplicate = await SendJsonAsync(
            admin.Client,
            HttpMethod.Post,
            "/api/v1/users",
            """{"username":"NEWBIE","password":"newbie password 2"}""",
            admin.CsrfToken,
            cancellationToken);

        duplicate.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var root = await ReadJsonAsync(duplicate, cancellationToken);
        root.GetProperty("error").GetProperty("details").GetProperty("field_errors")
            .TryGetProperty("username", out _).Should().BeTrue();

        await using var context = fixture.CreateContext();
        (await context.Users.CountAsync(cancellationToken)).Should().Be(2);
    }

    [Fact]
    public async Task Rename_revalidates_the_username_and_rejects_conflicts()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        var member = await SeedUserAsync("jane", MemberPassword, cancellationToken, UserRole.Member);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var admin = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var rename = await SendJsonAsync(
            admin.Client,
            HttpMethod.Patch,
            $"/api/v1/users/{member.Id}",
            """{"username":"janet"}""",
            admin.CsrfToken,
            cancellationToken);
        rename.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(rename, cancellationToken)).GetProperty("username").GetString()
            .Should().Be("janet");

        var conflict = await SendJsonAsync(
            admin.Client,
            HttpMethod.Patch,
            $"/api/v1/users/{member.Id}",
            """{"username":"root"}""",
            admin.CsrfToken,
            cancellationToken);
        conflict.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(conflict, cancellationToken))
            .GetProperty("error").GetProperty("details").GetProperty("field_errors")
            .TryGetProperty("username", out _).Should().BeTrue();

        var invalid = await SendJsonAsync(
            admin.Client,
            HttpMethod.Patch,
            $"/api/v1/users/{member.Id}",
            """{"username":"bad name"}""",
            admin.CsrfToken,
            cancellationToken);
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var empty = await SendJsonAsync(
            admin.Client,
            HttpMethod.Patch,
            $"/api/v1/users/{member.Id}",
            """{}""",
            admin.CsrfToken,
            cancellationToken);
        empty.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Role_change_revokes_the_targets_sessions()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        var member = await SeedUserAsync("jane", MemberPassword, cancellationToken, UserRole.Member);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var admin = await SignInAsync(factory, "root", AdminPassword, cancellationToken);
        using var memberSession = await CreateSessionClientAsync(factory, member, cancellationToken);

        var promote = await SendJsonAsync(
            admin.Client,
            HttpMethod.Patch,
            $"/api/v1/users/{member.Id}",
            """{"role":"admin"}""",
            admin.CsrfToken,
            cancellationToken);
        promote.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(promote, cancellationToken)).GetProperty("role").GetString().Should().Be("admin");

        // The old member session is revoked (role claim cannot outlive the role).
        (await memberSession.Client.GetAsync("/api/v1/auth/session", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
        await using (var context = fixture.CreateContext())
        {
            var sessions = await context.UserSessions
                .AsNoTracking()
                .Where(session => session.UserId == member.Id)
                .ToListAsync(cancellationToken);
            sessions.Should().NotBeEmpty();
            sessions.Should().OnlyContain(session =>
                session.RevokedAt != null
                && session.RevokedReason == UserManagementService.RoleChangedRevocationReason);
        }

        // Demoting back is allowed because root is still an active Admin.
        var demote = await SendJsonAsync(
            admin.Client,
            HttpMethod.Patch,
            $"/api/v1/users/{member.Id}",
            """{"role":"member"}""",
            admin.CsrfToken,
            cancellationToken);
        demote.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Last_active_admin_cannot_be_disabled_or_demoted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        var root = await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var admin = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var disable = await SendJsonAsync(
            admin.Client,
            HttpMethod.Post,
            $"/api/v1/users/{root.Id}/disable",
            json: null,
            admin.CsrfToken,
            cancellationToken);
        disable.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadErrorCodeAsync(disable, cancellationToken)).Should().Be(ErrorCodes.LastAdminProtected);

        var demote = await SendJsonAsync(
            admin.Client,
            HttpMethod.Patch,
            $"/api/v1/users/{root.Id}",
            """{"role":"member"}""",
            admin.CsrfToken,
            cancellationToken);
        demote.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadErrorCodeAsync(demote, cancellationToken)).Should().Be(ErrorCodes.LastAdminProtected);

        await using var context = fixture.CreateContext();
        var stored = await context.Users.AsNoTracking().SingleAsync(
            candidate => candidate.Id == root.Id,
            cancellationToken);
        stored.IsActive.Should().BeTrue();
        stored.Role.Should().Be(UserRole.Admin);
    }

    [Fact]
    public async Task Inactive_admins_do_not_satisfy_last_admin_safety()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        var root = await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await SeedUserAsync(
            "retired",
            MemberPassword,
            cancellationToken,
            UserRole.Admin,
            isActive: false);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var admin = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var disable = await SendJsonAsync(
            admin.Client,
            HttpMethod.Post,
            $"/api/v1/users/{root.Id}/disable",
            json: null,
            admin.CsrfToken,
            cancellationToken);

        disable.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadErrorCodeAsync(disable, cancellationToken)).Should().Be(ErrorCodes.LastAdminProtected);
    }

    [Fact]
    public async Task Concurrent_demotions_leave_at_least_one_active_admin()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        var root = await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        var second = await SeedUserAsync("second", MemberPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var rootSession = await CreateSessionClientAsync(factory, root, cancellationToken);
        using var secondSession = await CreateSessionClientAsync(factory, second, cancellationToken);

        var responses = await Task.WhenAll(
            SendJsonAsync(
                rootSession.Client,
                HttpMethod.Patch,
                $"/api/v1/users/{second.Id}",
                """{"role":"member"}""",
                rootSession.CsrfToken,
                cancellationToken),
            SendJsonAsync(
                secondSession.Client,
                HttpMethod.Patch,
                $"/api/v1/users/{root.Id}",
                """{"role":"member"}""",
                secondSession.CsrfToken,
                cancellationToken));

        try
        {
            responses.Count(response => response.StatusCode == HttpStatusCode.OK).Should().Be(1);
            var loser = responses.Single(response => response.StatusCode != HttpStatusCode.OK);

            // The loser either hits the last-admin guard (409) or had its
            // session revoked by the winner's role change before it was
            // authenticated (401); either way no zero-admin state is possible.
            if (loser.StatusCode == HttpStatusCode.Conflict)
            {
                (await ReadErrorCodeAsync(loser, cancellationToken)).Should().Be(ErrorCodes.LastAdminProtected);
            }
            else
            {
                loser.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
                (await ReadErrorCodeAsync(loser, cancellationToken)).Should().Be(ErrorCodes.AuthRequired);
            }
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        await using var context = fixture.CreateContext();
        (await context.Users.CountAsync(
                candidate => candidate.IsActive && candidate.Role == UserRole.Admin,
                cancellationToken))
            .Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Concurrent_service_demotions_are_serialized_by_the_admin_lock()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        var root = await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        var second = await SeedUserAsync("second", MemberPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        await using var scopeA = factory.Services.CreateAsyncScope();
        await using var scopeB = factory.Services.CreateAsyncScope();
        var serviceA = scopeA.ServiceProvider.GetRequiredService<UserManagementService>();
        var serviceB = scopeB.ServiceProvider.GetRequiredService<UserManagementService>();

        // Two genuinely concurrent demotions through separate DI scopes: the
        // advisory lock serializes them, so exactly one succeeds and the other
        // is rejected by the last-admin guard.
        var results = await Task.WhenAll(
            serviceA.UpdateAsync(
                second.Id,
                username: null,
                renameRequested: false,
                "member",
                roleRequested: true,
                cancellationToken: cancellationToken),
            serviceB.UpdateAsync(
                root.Id,
                username: null,
                renameRequested: false,
                "member",
                roleRequested: true,
                cancellationToken: cancellationToken));

        results.Count(result => result is UserMutationResult.Success).Should().Be(1);
        results.Count(result => result is UserMutationResult.LastAdminProtected).Should().Be(1);

        await using var context = fixture.CreateContext();
        (await context.Users.CountAsync(
                candidate => candidate.IsActive && candidate.Role == UserRole.Admin,
                cancellationToken))
            .Should().Be(1);
    }

    [Fact]
    public async Task Permissions_replace_validates_keys_rejects_admins_and_has_immediate_effect()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        var root = await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        var member = await SeedUserAsync("jane", MemberPassword, cancellationToken, UserRole.Member);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var admin = await SignInAsync(factory, "root", AdminPassword, cancellationToken);
        using var memberSession = await CreateSessionClientAsync(factory, member, cancellationToken);

        var unknown = await SendJsonAsync(
            admin.Client,
            HttpMethod.Patch,
            $"/api/v1/users/{member.Id}/permissions",
            """{"permissions":["calendar.edit","bogus.key"]}""",
            admin.CsrfToken,
            cancellationToken);
        unknown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(unknown, cancellationToken))
            .GetProperty("error").GetProperty("details").GetProperty("field_errors")
            .TryGetProperty("permissions", out _).Should().BeTrue();

        var adminTarget = await SendJsonAsync(
            admin.Client,
            HttpMethod.Patch,
            $"/api/v1/users/{root.Id}/permissions",
            """{"permissions":["calendar.view"]}""",
            admin.CsrfToken,
            cancellationToken);
        adminTarget.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var replace = await SendJsonAsync(
            admin.Client,
            HttpMethod.Patch,
            $"/api/v1/users/{member.Id}/permissions",
            """{"permissions":["recipes.edit","recipes.view","recipes.edit"]}""",
            admin.CsrfToken,
            cancellationToken);
        replace.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(replace, cancellationToken)).GetProperty("permissions").EnumerateArray()
            .Select(element => element.GetString()!)
            .Should().Equal(PermissionKeys.RecipesView, PermissionKeys.RecipesEdit);

        // Immediate effect for the member's existing session.
        (await memberSession.Client.GetAsync(
            PermissionMatrix.ProbePath(PermissionKeys.RecipesView),
            cancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await memberSession.Client.GetAsync(
            PermissionMatrix.ProbePath(PermissionKeys.CalendarView),
            cancellationToken)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var sessionResponse = await memberSession.Client.GetAsync("/api/v1/auth/session", cancellationToken);
        (await ReadJsonAsync(sessionResponse, cancellationToken)).GetProperty("permissions").EnumerateArray()
            .Select(element => element.GetString()!)
            .Should().Equal(PermissionKeys.RecipesView, PermissionKeys.RecipesEdit);

        // Replacing with an empty set revokes access again.
        var clear = await SendJsonAsync(
            admin.Client,
            HttpMethod.Patch,
            $"/api/v1/users/{member.Id}/permissions",
            """{"permissions":[]}""",
            admin.CsrfToken,
            cancellationToken);
        clear.StatusCode.Should().Be(HttpStatusCode.OK);
        (await memberSession.Client.GetAsync(
            PermissionMatrix.ProbePath(PermissionKeys.RecipesView),
            cancellationToken)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Disable_revokes_sessions_and_blocks_login_until_enabled()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        var member = await SeedUserAsync("jane", MemberPassword, cancellationToken, UserRole.Member);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var admin = await SignInAsync(factory, "root", AdminPassword, cancellationToken);
        using var memberSession = await CreateSessionClientAsync(factory, member, cancellationToken);

        var disable = await SendJsonAsync(
            admin.Client,
            HttpMethod.Post,
            $"/api/v1/users/{member.Id}/disable",
            json: null,
            admin.CsrfToken,
            cancellationToken);
        disable.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(disable, cancellationToken)).GetProperty("is_active").GetBoolean()
            .Should().BeFalse();

        (await memberSession.Client.GetAsync("/api/v1/auth/session", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);

        using (var anonymousClient = factory.CreateDefaultClient())
        {
            var login = await LoginAsync(anonymousClient, "jane", MemberPassword, cancellationToken);
            login.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await ReadErrorCodeAsync(login, cancellationToken)).Should().Be(ErrorCodes.InvalidCredentials);
        }

        var enable = await SendJsonAsync(
            admin.Client,
            HttpMethod.Post,
            $"/api/v1/users/{member.Id}/enable",
            json: null,
            admin.CsrfToken,
            cancellationToken);
        enable.StatusCode.Should().Be(HttpStatusCode.OK);

        using var anonymousClient2 = factory.CreateDefaultClient();
        (await LoginAsync(anonymousClient2, "jane", MemberPassword, cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Enable_clears_lockout_state_and_unblocks_login()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        var member = await SeedUserAsync(
            "jane",
            MemberPassword,
            cancellationToken,
            UserRole.Member,
            lockedUntil: DateTimeOffset.UtcNow.AddMinutes(15),
            failedLoginCount: 10,
            lockoutCount: 2,
            lockoutWindowStartedAt: DateTimeOffset.UtcNow.AddHours(-1));
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var admin = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        // The account is locked before the enable.
        using (var lockedClient = factory.CreateDefaultClient())
        {
            (await LoginAsync(lockedClient, "jane", MemberPassword, cancellationToken)).StatusCode
                .Should().Be(HttpStatusCode.Locked);
        }

        var enable = await SendJsonAsync(
            admin.Client,
            HttpMethod.Post,
            $"/api/v1/users/{member.Id}/enable",
            json: null,
            admin.CsrfToken,
            cancellationToken);
        enable.StatusCode.Should().Be(HttpStatusCode.OK);

        await using (var context = fixture.CreateContext())
        {
            var stored = await context.Users.AsNoTracking().SingleAsync(
                candidate => candidate.Id == member.Id,
                cancellationToken);
            stored.FailedLoginCount.Should().Be(0);
            stored.LastFailedLoginAt.Should().BeNull();
            stored.LockedUntil.Should().BeNull();
            stored.LockoutCount.Should().Be(0);
            stored.LockoutWindowStartedAt.Should().BeNull();
        }

        using var anonymousClient = factory.CreateDefaultClient();
        (await LoginAsync(anonymousClient, "jane", MemberPassword, cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Temporary_passwords_never_reach_logs_or_the_database()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);

        var recorder = new RecordingLoggerProvider();
        using var factory = fixture.CreateFactory().WithWebHostBuilder(builder =>
            builder.ConfigureLogging(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Trace);
                logging.AddProvider(recorder);
            }));
        using var admin = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var response = await SendJsonAsync(
            admin.Client,
            HttpMethod.Post,
            "/api/v1/users",
            """{"username":"newbie"}""",
            admin.CsrfToken,
            cancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var temporaryPassword = (await ReadJsonAsync(response, cancellationToken))
            .GetProperty("temporary_password").GetString()!;

        await using (var context = fixture.CreateContext())
        {
            var stored = await context.Users.AsNoTracking().SingleAsync(
                candidate => candidate.Username == "newbie",
                cancellationToken);
            stored.PasswordHash.Should().NotBe(temporaryPassword);

            var events = await context.AuthEvents.AsNoTracking().ToListAsync(cancellationToken);
            var eventText = string.Join(
                '\n',
                events.Select(authEvent => $"{authEvent.MetadataJson}|{authEvent.UsernameAttempted}"));
            eventText.Should().NotContain(temporaryPassword);
        }

        recorder.Messages.Should().NotBeEmpty();
        foreach (var secret in new[] { temporaryPassword, AdminPassword })
        {
            recorder.AllText.Should().NotContain(
                text => text.Contains(secret, StringComparison.Ordinal),
                "user-management secrets must never reach logs");
        }
    }

    [Fact]
    public async Task Created_member_defaults_to_view_only_permissions()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var admin = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var response = await SendJsonAsync(
            admin.Client,
            HttpMethod.Post,
            "/api/v1/users",
            """{"username":"newbie","password":"newbie password 1"}""",
            admin.CsrfToken,
            cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await ReadJsonAsync(response, cancellationToken)).GetProperty("permissions").EnumerateArray()
            .Select(element => element.GetString()!)
            .Should().Equal(PermissionKeys.MemberViewOnly);

        await using (var context = fixture.CreateContext())
        {
            var memberId = await context.Users
                .AsNoTracking()
                .Where(candidate => candidate.Username == "newbie")
                .Select(candidate => candidate.Id)
                .SingleAsync(cancellationToken);

            var grantedKeys = await context.UserPermissions
                .AsNoTracking()
                .Where(permission => permission.UserId == memberId)
                .Select(permission => permission.PermissionKey)
                .ToListAsync(cancellationToken);
            grantedKeys.Should().BeEquivalentTo(PermissionKeys.MemberViewOnly);
        }

        using var memberClient = factory.CreateDefaultClient();
        var login = await LoginAsync(memberClient, "newbie", "newbie password 1", cancellationToken);
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(login, cancellationToken)).GetProperty("permissions").EnumerateArray()
            .Select(element => element.GetString()!)
            .Should().Equal(PermissionKeys.MemberViewOnly);
    }

    [Fact]
    public async Task Concurrent_rename_and_create_on_the_same_username_do_not_conflict()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        var member = await SeedUserAsync("jane", MemberPassword, cancellationToken, UserRole.Member);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var admin = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var responses = await Task.WhenAll(
            SendJsonAsync(
                admin.Client,
                HttpMethod.Patch,
                $"/api/v1/users/{member.Id}",
                """{"username":"newbie"}""",
                admin.CsrfToken,
                cancellationToken),
            SendJsonAsync(
                admin.Client,
                HttpMethod.Post,
                "/api/v1/users",
                """{"username":"newbie","password":"newbie password 1"}""",
                admin.CsrfToken,
                cancellationToken));

        try
        {
            responses.Count(response =>
                    response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
                .Should().Be(1);
            var failed = responses.Single(response =>
                response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Created));
            failed.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await ReadErrorCodeAsync(failed, cancellationToken)).Should().Be(ErrorCodes.ValidationFailed);
            (await ReadJsonAsync(failed, cancellationToken))
                .GetProperty("error").GetProperty("details").GetProperty("field_errors")
                .TryGetProperty("username", out _).Should().BeTrue();
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        await using var context = fixture.CreateContext();
        // The winner either renamed jane or inserted a new row; either way
        // exactly one user holds the new username.
        (await context.Users.CountAsync(
                candidate => candidate.NormalizedUsername == "newbie",
                cancellationToken))
            .Should().Be(1);
        (await context.Users.CountAsync(cancellationToken)).Should().BeInRange(2, 3);
    }

    [Fact]
    public async Task Demoting_an_admin_without_grants_seeds_the_view_only_defaults()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        var second = await SeedUserAsync("second", MemberPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var admin = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var demote = await SendJsonAsync(
            admin.Client,
            HttpMethod.Patch,
            $"/api/v1/users/{second.Id}",
            """{"role":"member"}""",
            admin.CsrfToken,
            cancellationToken);

        demote.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(demote, cancellationToken)).GetProperty("permissions").EnumerateArray()
            .Select(element => element.GetString()!)
            .Should().Equal(PermissionKeys.MemberViewOnly);

        await using (var context = fixture.CreateContext())
        {
            var rows = await context.UserPermissions
                .AsNoTracking()
                .Where(permission => permission.UserId == second.Id)
                .Select(permission => permission.PermissionKey)
                .ToListAsync(cancellationToken);
            rows.Should().BeEquivalentTo(PermissionKeys.MemberViewOnly);
        }

        using var secondClient = factory.CreateDefaultClient();
        var login = await LoginAsync(secondClient, "second", MemberPassword, cancellationToken);
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(login, cancellationToken)).GetProperty("permissions").EnumerateArray()
            .Select(element => element.GetString()!)
            .Should().Equal(PermissionKeys.MemberViewOnly);
    }

    [Fact]
    public async Task Demoting_an_admin_with_existing_grants_keeps_them()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        var jane = await SeedUserAsync("jane", MemberPassword, cancellationToken, UserRole.Member);
        await SeedPermissionAsync(jane.Id, PermissionKeys.RecipesEdit, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var admin = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var promote = await SendJsonAsync(
            admin.Client,
            HttpMethod.Patch,
            $"/api/v1/users/{jane.Id}",
            """{"role":"admin"}""",
            admin.CsrfToken,
            cancellationToken);
        promote.StatusCode.Should().Be(HttpStatusCode.OK);

        var demote = await SendJsonAsync(
            admin.Client,
            HttpMethod.Patch,
            $"/api/v1/users/{jane.Id}",
            """{"role":"member"}""",
            admin.CsrfToken,
            cancellationToken);

        demote.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(demote, cancellationToken)).GetProperty("permissions").EnumerateArray()
            .Select(element => element.GetString()!)
            .Should().Equal(PermissionKeys.RecipesView, PermissionKeys.RecipesEdit);

        await using (var context = fixture.CreateContext())
        {
            var rows = await context.UserPermissions
                .AsNoTracking()
                .Where(permission => permission.UserId == jane.Id)
                .Select(permission => permission.PermissionKey)
                .ToListAsync(cancellationToken);
            rows.Should().Equal(PermissionKeys.RecipesEdit);
        }

        using var janeClient = factory.CreateDefaultClient();
        var login = await LoginAsync(janeClient, "jane", MemberPassword, cancellationToken);
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(login, cancellationToken)).GetProperty("permissions").EnumerateArray()
            .Select(element => element.GetString()!)
            .Should().Equal(PermissionKeys.RecipesView, PermissionKeys.RecipesEdit);
    }

    private sealed record SignedInClient(HttpClient Client, string CsrfToken, string RawToken) : IDisposable
    {
        public void Dispose() => Client.Dispose();
    }

    private static async Task<SignedInClient> SignInAsync(
        WebApplicationFactory<Program> factory,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        var cookies = new CookieContainer();
        var client = factory.CreateDefaultClient(new CookieContainerHandler(cookies));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { username, password }),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation("User-Agent", TestUserAgent);

        var login = await client.SendAsync(request, cancellationToken);
        login.StatusCode.Should().Be(HttpStatusCode.OK);

        return new SignedInClient(
            client,
            login.Headers.GetValues(CsrfMiddleware.HeaderName).Single(),
            ExtractSessionToken(GetSessionCookieHeader(login)));
    }

    private async Task<SignedInClient> CreateSessionClientAsync(
        WebApplicationFactory<Program> factory,
        User user,
        CancellationToken cancellationToken)
    {
        var cookies = new CookieContainer();
        var client = factory.CreateDefaultClient(new CookieContainerHandler(cookies));
        using var scope = factory.Services.CreateScope();
        var (_, rawToken) = await scope.ServiceProvider
            .GetRequiredService<SessionService>()
            .CreateAsync(user.Id, "127.0.0.1", TestUserAgent, cancellationToken);
        cookies.Add(
            new Uri("http://localhost/"),
            new Cookie(SessionAuthenticationDefaults.CookieName, rawToken) { Path = "/" });

        var bootstrap = await client.GetAsync("/api/v1/auth/session", cancellationToken);
        bootstrap.StatusCode.Should().Be(HttpStatusCode.OK);
        var csrfToken = bootstrap.Headers.GetValues(CsrfMiddleware.HeaderName).Single();

        return new SignedInClient(client, csrfToken, rawToken);
    }

    private static async Task<HttpResponseMessage> SendJsonAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string? json,
        string? csrfToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        if (csrfToken is not null)
        {
            request.Headers.Add(CsrfMiddleware.HeaderName, csrfToken);
        }

        return await client.SendAsync(request, cancellationToken);
    }

    private static async Task<HttpResponseMessage> LoginAsync(
        HttpClient client,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { username, password }),
                Encoding.UTF8,
                "application/json"),
        };

        return await client.SendAsync(request, cancellationToken);
    }

    private async Task SeedHouseholdAsync(AuthMode authMode, CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        context.HouseholdSettings.Add(new HouseholdSettings
        {
            Id = 1,
            HouseholdName = "User Management Household",
            Timezone = "America/Chicago",
            DefaultDinerCount = 4,
            WeekStartsOn = 0,
            StartupPage = StartupPage.Calendar,
            AuthMode = authMode,
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
        });
        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task MarkSetupCompletedAsync(CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO application_settings (key, value_json, updated_at)
            VALUES ({SetupStateService.SetupCompletedKey}, 'true'::jsonb, {DateTimeOffset.UtcNow})
            ON CONFLICT (key) DO UPDATE
            SET value_json = 'true'::jsonb, updated_at = EXCLUDED.updated_at
            """,
            cancellationToken);
    }

    private async Task<User> SeedUserAsync(
        string username,
        string password,
        CancellationToken cancellationToken,
        UserRole role = UserRole.Member,
        bool isActive = true,
        bool mustChangePassword = false,
        DateTimeOffset? lockedUntil = null,
        int failedLoginCount = 0,
        int lockoutCount = 0,
        DateTimeOffset? lockoutWindowStartedAt = null)
    {
        await using var context = fixture.CreateContext();
        var user = new User
        {
            Username = username,
            NormalizedUsername = UsernameNormalizer.Normalize(username),
            PasswordHash = new Argon2idPasswordHasher().Hash(password),
            Role = role,
            IsActive = isActive,
            MustChangePassword = mustChangePassword,
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
            LockedUntil = lockedUntil,
            FailedLoginCount = failedLoginCount,
            LastFailedLoginAt = failedLoginCount > 0 ? SeedTimestamp : null,
            LockoutCount = lockoutCount,
            LockoutWindowStartedAt = lockoutWindowStartedAt,
        };

        context.Users.Add(user);
        await context.SaveChangesAsync(cancellationToken);
        return user;
    }

    private async Task SeedPermissionAsync(
        long userId,
        string permissionKey,
        CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        context.UserPermissions.Add(new UserPermission
        {
            UserId = userId,
            PermissionKey = permissionKey,
        });
        await context.SaveChangesAsync(cancellationToken);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = text.IndexOf(value, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal);
        }

        return count;
    }

    private static async Task<JsonElement> ReadJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        using var payload = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));
        return payload.RootElement.Clone();
    }

    private static async Task<string> ReadErrorCodeAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken) =>
        (await ReadJsonAsync(response, cancellationToken))
            .GetProperty("error").GetProperty("code").GetString()!;

    private static string GetSessionCookieHeader(HttpResponseMessage response)
    {
        response.Headers.TryGetValues("Set-Cookie", out var headers).Should().BeTrue();
        return headers!
            .Single(header => header.StartsWith(
                $"{SessionAuthenticationDefaults.CookieName}=",
                StringComparison.Ordinal));
    }

    private static string ExtractSessionToken(string setCookieHeader)
    {
        var value = setCookieHeader[$"{SessionAuthenticationDefaults.CookieName}=".Length..];
        var terminator = value.IndexOf(';');
        return terminator < 0 ? value : value[..terminator];
    }
}
