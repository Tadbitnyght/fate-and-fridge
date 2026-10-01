using System.Net;
using System.Text;
using System.Text.Json;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Auth.Permissions;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Endpoints;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.Services;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Server-side permission matrix for the test-only probe routes (M1-09,
/// docs/API.md §3): the enforced result for every key is compared against the
/// spec oracle in <see cref="PermissionMatrix"/>. Covers Admin, member with /
/// without grants, unauthenticated, single-user system actor, immediate effect
/// of grant changes, forced-change gate precedence, and pre-setup gating.
/// Requires a Docker daemon; run with
/// <c>dotnet test --filter "Category!=Integration"</c> to skip locally.
/// </summary>
[Trait("Category", "Integration")]
[Collection(M1SetupCollection.Name)]
public sealed class PermissionMatrixIntegrationTests(M1SetupFixture fixture) : IAsyncLifetime
{
    private const string Password = "correct horse battery staple";

    private static readonly DateTimeOffset SeedTimestamp =
        new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public ValueTask InitializeAsync() =>
        new(fixture.ResetAsync(TestContext.Current.CancellationToken));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Admin_holds_every_key_and_passes_the_admin_requirement()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", Password, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var session = await SignInAsync(factory, "root", Password, cancellationToken);

        foreach (var key in PermissionMatrix.AllKeys)
        {
            var response = await session.Client.GetAsync(PermissionMatrix.ProbePath(key), cancellationToken);
            response.StatusCode.Should().Be(HttpStatusCode.OK, "an Admin holds {0}", key);
        }

        (await session.Client.GetAsync(PermissionMatrix.AdminProbePath, cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await session.Client.GetAsync(PermissionMatrix.AuthenticatedProbePath, cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Member_permissions_match_the_specification_matrix()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        var member = await SeedUserAsync("jane", Password, cancellationToken, UserRole.Member);
        await SeedPermissionAsync(member.Id, PermissionKeys.CalendarEdit, cancellationToken);
        await SeedPermissionAsync(member.Id, PermissionKeys.RecipesView, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var session = await SignInAsync(factory, "jane", Password, cancellationToken);

        var expected = PermissionMatrix.MemberKeys(
            PermissionKeys.CalendarEdit,
            PermissionKeys.RecipesView);

        foreach (var key in PermissionMatrix.AllKeys)
        {
            var response = await session.Client.GetAsync(PermissionMatrix.ProbePath(key), cancellationToken);
            if (expected.Contains(key, StringComparer.Ordinal))
            {
                response.StatusCode.Should().Be(HttpStatusCode.OK, "the member holds {0}", key);
            }
            else
            {
                response.StatusCode.Should().Be(HttpStatusCode.Forbidden, "the member lacks {0}", key);
                (await ReadErrorCodeAsync(response, cancellationToken)).Should().Be(ErrorCodes.Forbidden);
            }
        }

        // Admin-only routes deny the member.
        var adminProbe = await session.Client.GetAsync(PermissionMatrix.AdminProbePath, cancellationToken);
        adminProbe.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadErrorCodeAsync(adminProbe, cancellationToken)).Should().Be(ErrorCodes.Forbidden);

        // GET /auth/session reports the same evaluator output.
        var sessionResponse = await session.Client.GetAsync("/api/v1/auth/session", cancellationToken);
        var sessionRoot = await ReadJsonAsync(sessionResponse, cancellationToken);
        sessionRoot.GetProperty("permissions").EnumerateArray()
            .Select(element => element.GetString()!)
            .Should().Equal(expected);
    }

    [Fact]
    public async Task Member_without_grants_is_denied_every_key()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("jane", Password, cancellationToken, UserRole.Member);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var session = await SignInAsync(factory, "jane", Password, cancellationToken);

        foreach (var key in PermissionMatrix.AllKeys)
        {
            var response = await session.Client.GetAsync(PermissionMatrix.ProbePath(key), cancellationToken);
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, "no grants mean no key {0}", key);
        }

        // Any authenticated caller is still allowed by the authenticated-only helper.
        (await session.Client.GetAsync(PermissionMatrix.AuthenticatedProbePath, cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Unauthenticated_probe_returns_401_envelope_with_no_store()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            PermissionMatrix.ProbePath(PermissionKeys.CalendarView),
            cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var root = await ReadJsonAsync(response, cancellationToken);
        var error = root.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be(ErrorCodes.AuthRequired);
        error.GetProperty("details").EnumerateObject().Should().BeEmpty();
        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Fact]
    public async Task Single_user_system_actor_holds_every_key()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.SingleUser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();

        foreach (var key in PermissionMatrix.AllKeys)
        {
            var response = await client.GetAsync(PermissionMatrix.ProbePath(key), cancellationToken);
            response.StatusCode.Should().Be(HttpStatusCode.OK, "the system actor holds {0}", key);
        }

        (await client.GetAsync(PermissionMatrix.AdminProbePath, cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Permission_changes_take_effect_on_the_next_request()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        var member = await SeedUserAsync("jane", Password, cancellationToken, UserRole.Member);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var session = await SignInAsync(factory, "jane", Password, cancellationToken);

        (await ProbeAsync(session.Client, PermissionKeys.CalendarEdit, cancellationToken)).Should()
            .Be(HttpStatusCode.Forbidden);

        // Grant mid-test: the next request must observe it, including edit-implies-view.
        await SeedPermissionAsync(member.Id, PermissionKeys.CalendarEdit, cancellationToken);
        (await ProbeAsync(session.Client, PermissionKeys.CalendarEdit, cancellationToken)).Should()
            .Be(HttpStatusCode.OK);
        (await ProbeAsync(session.Client, PermissionKeys.CalendarView, cancellationToken)).Should()
            .Be(HttpStatusCode.OK);

        var sessionResponse = await session.Client.GetAsync("/api/v1/auth/session", cancellationToken);
        var sessionRoot = await ReadJsonAsync(sessionResponse, cancellationToken);
        sessionRoot.GetProperty("permissions").EnumerateArray()
            .Select(element => element.GetString()!)
            .Should().Equal(PermissionMatrix.MemberKeys(PermissionKeys.CalendarEdit));

        // Revoke mid-test: the next request must observe that too.
        await ClearPermissionsAsync(member.Id, cancellationToken);
        (await ProbeAsync(session.Client, PermissionKeys.CalendarEdit, cancellationToken)).Should()
            .Be(HttpStatusCode.Forbidden);
        (await ProbeAsync(session.Client, PermissionKeys.CalendarView, cancellationToken)).Should()
            .Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Forced_change_gate_wins_over_permission_enforcement()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", Password, cancellationToken, UserRole.Admin, mustChangePassword: true);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var session = await SignInAsync(factory, "root", Password, cancellationToken);

        // Even an actor who holds every key is blocked while flagged.
        var response = await session.Client.GetAsync(
            PermissionMatrix.ProbePath(PermissionKeys.CalendarView),
            cancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadErrorCodeAsync(response, cancellationToken)).Should().Be(ErrorCodes.PasswordChangeRequired);

        var adminProbe = await session.Client.GetAsync(PermissionMatrix.AdminProbePath, cancellationToken);
        adminProbe.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadErrorCodeAsync(adminProbe, cancellationToken)).Should().Be(ErrorCodes.PasswordChangeRequired);
    }

    [Fact]
    public async Task Chained_requirements_deny_a_member_holding_only_the_permission_in_both_orders()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        var member = await SeedUserAsync("jane", Password, cancellationToken, UserRole.Member);
        await SeedPermissionAsync(member.Id, PermissionKeys.CalendarView, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var session = await SignInAsync(factory, "jane", Password, cancellationToken);

        // The member holds calendar.view but is not an Admin: both chained
        // orders must deny (AND semantics), regardless of metadata order.
        foreach (var path in new[]
        {
            PermissionMatrix.AdminThenPermissionProbePath,
            PermissionMatrix.PermissionThenAdminProbePath,
        })
        {
            var response = await session.Client.GetAsync(path, cancellationToken);
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, "the member is not an Admin ({0})", path);
            (await ReadErrorCodeAsync(response, cancellationToken)).Should().Be(ErrorCodes.Forbidden);
        }
    }

    [Fact]
    public async Task Chained_requirements_allow_admins_in_both_orders()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", Password, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var session = await SignInAsync(factory, "root", Password, cancellationToken);

        foreach (var path in new[]
        {
            PermissionMatrix.AdminThenPermissionProbePath,
            PermissionMatrix.PermissionThenAdminProbePath,
        })
        {
            var response = await session.Client.GetAsync(path, cancellationToken);
            response.StatusCode.Should().Be(HttpStatusCode.OK, "an Admin satisfies both requirements ({0})", path);
        }
    }

    [Fact]
    public async Task Filter_without_requirement_metadata_fails_fast()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();

        // Attaching the filter without a PermissionRequirement is a server
        // misconfiguration: it must fail closed, never fail open. M1-15 maps the
        // misconfiguration exception to the shared 500 envelope (and logs it)
        // instead of letting it escape to the client.
        var response = await client.GetAsync("/api/v1/permission-probe/misconfigured", cancellationToken);
        ((int)response.StatusCode).Should().Be(500);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        raw.Should().Contain("INTERNAL_ERROR");
        raw.Should().NotContain(nameof(PermissionRequirement));
    }

    [Fact]
    public async Task Probe_routes_are_gated_before_setup()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            PermissionMatrix.ProbePath(PermissionKeys.CalendarView),
            cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadErrorCodeAsync(response, cancellationToken)).Should().Be(ErrorCodes.SetupRequired);
    }

    [Fact]
    public async Task Probe_routes_are_absent_outside_the_testing_environment()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        var user = await SeedUserAsync("root", Password, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Production"));

        using var scope = factory.Services.CreateScope();
        var (_, rawToken) = await scope.ServiceProvider
            .GetRequiredService<SessionService>()
            .CreateAsync(user.Id, "127.0.0.1", "permission-matrix-test", cancellationToken);

        using var client = factory.CreateDefaultClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            PermissionMatrix.ProbePath(PermissionKeys.CalendarView));
        request.Headers.Add(
            "Cookie",
            $"{SessionAuthenticationDefaults.CookieName}={rawToken}");

        var response = await client.SendAsync(request, cancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static async Task<HttpStatusCode> ProbeAsync(
        HttpClient client,
        string permissionKey,
        CancellationToken cancellationToken)
    {
        var response = await client.GetAsync(PermissionMatrix.ProbePath(permissionKey), cancellationToken);
        return response.StatusCode;
    }

    private sealed record SignedInClient(HttpClient Client) : IDisposable
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

        var response = await client.SendAsync(request, cancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return new SignedInClient(client);
    }

    private async Task SeedHouseholdAsync(AuthMode authMode, CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        context.HouseholdSettings.Add(new HouseholdSettings
        {
            Id = 1,
            HouseholdName = "Permission Matrix Household",
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
        bool mustChangePassword = false)
    {
        await using var context = fixture.CreateContext();
        var user = new User
        {
            Username = username,
            NormalizedUsername = UsernameNormalizer.Normalize(username),
            PasswordHash = new Argon2idPasswordHasher().Hash(password),
            Role = role,
            IsActive = true,
            MustChangePassword = mustChangePassword,
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
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

    private async Task ClearPermissionsAsync(long userId, CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        await context.UserPermissions
            .Where(permission => permission.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);
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
}
