using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Data;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.LiveSync;
using FateAndFridge.Api.Services;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// M14-07 adversarial live-sync authentication battery
/// (docs/M14_PLAN.md §3/§4, issue #180). It extends the M9-09 suites rather than
/// re-testing them: the pre-auth negotiation/connect semantics with exact
/// statuses, revoked/expired/disabled session teardown, connection-scoped
/// delivery and the coarse envelope, and the auth-mode-switch boundary found by
/// this battery (BUG-013). Requires a Docker daemon; the M9 suites describe how
/// to skip locally.
/// </summary>
public sealed partial class M9LiveSyncHubTests
{
    /// <summary>The exact six snake_case envelope fields (docs/API.md §19).</summary>
    private static readonly string[] DocumentedEnvelopeKeys =
        ["type", "entity", "id", "operation_id", "actor_user_id", "timestamp"];

    // ---- BUG-013: the auth-mode-switch boundary --------------------------

    /// <summary>
    /// Regression for BUG-013 (M14-07): the one-way single-user → multiuser
    /// switch must not leave a pre-switch cookie-less socket receiving household
    /// events. The switch aborts every registered connection (registry first, so
    /// no later broadcast can target it) without sending <c>session.revoked</c>;
    /// afterwards a cookie-less connection is refused and a connection carrying
    /// the new Admin session works.
    /// </summary>
    [Fact]
    public async Task Auth_mode_switch_disconnects_the_pre_switch_system_socket_and_requires_a_session()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.SingleUser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = Fixture.CreateFactory();
        await using var systemSocket = CreateConnection(factory);
        var collector = Track(systemSocket);
        var closed = TrackClose(systemSocket);
        await systemSocket.StartAsync(cancellationToken);
        await WaitForConnectionsAsync(
            factory,
            LiveSyncConnectionRegistry.SystemSessionId,
            1,
            cancellationToken);

        var cookies = new CookieContainer();
        using var switchingClient = factory.CreateDefaultClient(new CookieContainerHandler(cookies));
        using var switchResponse = await SendJsonAsync(
            switchingClient,
            HttpMethod.Post,
            "/api/v1/settings/auth-mode",
            """{"auth_mode":"multiuser","admin":{"username":"m14admin","password":"correct horse battery staple"}}""",
            csrfToken: null,
            cancellationToken);

        switchResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var switched = await ReadJsonAsync(switchResponse, cancellationToken);
        var admin = switched.GetProperty("user");
        var adminId = admin.GetProperty("id").GetInt64();
        switched.GetProperty("auth_mode").GetString().Should().Be("multiuser");

        // The pre-switch socket is unregistered before the switch publishes its
        // own settings.updated, so it observes no event at all and closes.
        var closeException = await closed.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        closeException.Should().BeNull("the switch aborts the transport without an error frame");
        systemSocket.State.Should().Be(HubConnectionState.Disconnected);
        collector.Events.Should().BeEmpty(
            "an unregistered connection is not a broadcast target, so even the switch's "
            + "settings.updated is never delivered to a socket authorized only by the old mode");
        await WaitForRegistryCountAsync(factory, 0, cancellationToken);

        // A broadcast after the switch does not reach the torn-down socket.
        var publisher = factory.Services.GetRequiredService<ILiveSyncPublisher>();
        await publisher.PublishAsync(
            LiveSyncEvent.Create(LiveSyncEventType.GroceriesUpdated, entity: "groceries"),
            cancellationToken);
        await Task.Delay(300, cancellationToken);
        collector.Events.Should().BeEmpty();
        systemSocket.State.Should().Be(HubConnectionState.Disconnected);

        // Cookie-less connections are now refused: multiuser mode requires a session.
        await using var refused = CreateConnection(factory);
        (await TryStartAsync(refused, cancellationToken)).Should().BeFalse();
        GetRegistry(factory).Count.Should().Be(0, "a refused connection is never registered");

        // The switch response's Set-Cookie signs the new Admin in, so a
        // reconnecting client authenticates under the new mode.
        var adminCookie = cookies.GetCookieHeader(factory.Server.BaseAddress);
        adminCookie.Should().Contain(SessionAuthenticationDefaults.CookieName);
        var adminSessionId = await ActiveSessionIdAsync(adminId, cancellationToken);

        await using var adminSocket = CreateConnection(factory, cookieHeader: adminCookie);
        await adminSocket.StartAsync(cancellationToken);
        await WaitForConnectionsAsync(factory, adminSessionId, 1, cancellationToken);
        GetRegistry(factory).GetConnections(adminSessionId).Single().ActorUserId
            .Should().Be(adminId);
        adminSocket.State.Should().Be(HubConnectionState.Connected);
    }

    // ---- M14 review: late registration after the switch snapshot ---------

    /// <summary>
    /// Regression for the M14 review finding (Low): the switch commits the new
    /// mode and only then snapshots the registry in
    /// <c>DisconnectAllAsync</c>. A cookie-less connect that resolved
    /// single-user mode just before the commit can register after that snapshot.
    /// The per-broadcast re-validation used to treat every system socket as
    /// always valid, so it kept receiving household events for good.
    /// <para>
    /// The interleaving is forced, not raced: the connect is paused inside its
    /// actor read (after it has observed single-user mode), the switch completes
    /// end to end, and only then is the registration allowed to happen. The
    /// server must refuse that registration, close the transport, and never
    /// deliver an event to it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_connect_registering_after_the_switch_snapshot_is_refused_and_gets_no_events()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.SingleUser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        var gate = new PausingActorReadGate();
        using var factory = Fixture.CreateFactory(configureServices: services =>
        {
            services.RemoveAll<ICurrentActorProvider>();
            services.AddScoped<ICurrentActorProvider>(provider => new PausingActorProvider(
                new SessionActorProvider(
                    provider.GetRequiredService<AppDbContext>(),
                    provider.GetRequiredService<IHttpContextAccessor>()),
                gate));
        });

        await using var lateSocket = CreateConnection(factory);
        var collector = Track(lateSocket);

        // The connect resolves the system actor (single-user mode) and parks
        // before it can register.
        var start = lateSocket.StartAsync(cancellationToken);
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        GetRegistry(factory).Count.Should().Be(0, "the connect is paused before it registers");

        var cookies = new CookieContainer();
        using var switchingClient = factory.CreateDefaultClient(new CookieContainerHandler(cookies));
        using var switchResponse = await SendJsonAsync(
            switchingClient,
            HttpMethod.Post,
            "/api/v1/settings/auth-mode",
            """{"auth_mode":"multiuser","admin":{"username":"m14late","password":"correct horse battery staple"}}""",
            csrfToken: null,
            cancellationToken);
        switchResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // The switch has committed, disconnected everything it could see, and
        // published settings.updated; the paused connect is still unregistered.
        GetRegistry(factory).Count.Should().Be(0);
        collector.Events.Should().BeEmpty();

        // Let the late connect finish. It must re-read the now-multiuser mode
        // and be refused rather than register as a trusted system socket.
        gate.Release();
        try
        {
            await start.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested is false)
        {
            // A refused handshake surfaces as an exception on the client; the
            // state assertion below is the actual contract.
        }

        // Wait for the server to settle: either the refusal closed the socket, or
        // the late registration became visible in the registry. Required because
        // long polling returns the connect response before OnConnectedAsync
        // finishes, so acting on the client's result alone would race the
        // server-side registration.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (!collector.Closed.Task.IsCompleted
            && GetRegistry(factory).CountForSession(LiveSyncConnectionRegistry.SystemSessionId) == 0)
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException(
                    "The late connect neither registered nor closed within 15 seconds.");
            }

            await Task.Delay(20, cancellationToken);
        }

        GetRegistry(factory).CountForSession(LiveSyncConnectionRegistry.SystemSessionId)
            .Should().Be(0, "the late system registration is rolled back");
        GetRegistry(factory).Count.Should().Be(0);
        collector.Events.Should().BeEmpty("no event may reach the late socket");
        (await collector.Closed.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken))
            .Should().BeNull("the hub aborts without an error frame");
        lateSocket.State.Should().Be(HubConnectionState.Disconnected);

        // And it never observes a household event afterwards either.
        var publisher = factory.Services.GetRequiredService<ILiveSyncPublisher>();
        await publisher.PublishAsync(
            LiveSyncEvent.Create(LiveSyncEventType.GroceriesUpdated, entity: "groceries"),
            cancellationToken);
        await Task.Delay(300, cancellationToken);
        collector.Events.Should().BeEmpty(
            "a socket that registered after the switch snapshot must never receive an event");
    }

    /// <summary>
    /// The broadcast-time half of the same review finding: even a system socket
    /// that was registered legitimately (single-user mode) must not stay trusted
    /// after an out-of-band mode change. This flips the mode in the database
    /// without going through the endpoint, so no <c>DisconnectAllAsync</c> runs,
    /// and proves the per-broadcast re-validation revokes it instead of
    /// delivering household data.
    /// </summary>
    [Fact]
    public async Task A_system_socket_outliving_an_out_of_band_mode_change_is_revoked_without_events()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.SingleUser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = Fixture.CreateFactory();
        await using var socket = CreateConnection(factory);
        var collector = Track(socket);
        await socket.StartAsync(cancellationToken);
        await WaitForConnectionsAsync(
            factory,
            LiveSyncConnectionRegistry.SystemSessionId,
            1,
            cancellationToken);

        await using (var context = Fixture.CreateContext())
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE household_settings SET auth_mode = 'multiuser'",
                cancellationToken);
        }

        var publisher = factory.Services.GetRequiredService<ILiveSyncPublisher>();
        await publisher.PublishAsync(
            LiveSyncEvent.Create(LiveSyncEventType.GroceriesUpdated, entity: "groceries"),
            cancellationToken);

        var first = await WaitForEventAsync(collector, _ => true, cancellationToken);
        first.Type.Should().Be(
            LiveSyncEventType.SessionRevoked,
            "the outlived system socket must be revoked, not handed household data");
        (await collector.Closed.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken))
            .Should().BeNull();
        socket.State.Should().Be(HubConnectionState.Disconnected);
        collector.Events.Should().OnlyContain(
            evt => evt.Type == LiveSyncEventType.SessionRevoked,
            "the outlived system socket gets its revocation and no household data");
    }

    // ---- 1. Unauthenticated / pre-auth ----------------------------------

    /// <summary>
    /// Multiuser mode: negotiate is anonymous (the connect is the authenticated
    /// step, because single-user mode must be able to connect cookie-less), but
    /// a missing, forged, or malformed session cookie yields a connection that is
    /// aborted by <c>OnConnectedAsync</c> and never enters the registry, so it can
    /// never receive an event. The abort surfaces on the client as a close with a
    /// <c>null</c> exception: over long polling no HTTP status or RFC 6455 close
    /// code is exposed (the WebSocket transport's abort is the 1006-equivalent
    /// "no close frame"; M14-09 re-checks that on a disposable stack).
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("forged-token-value")]
    [InlineData("%%%not-a-cookie%%%")]
    public async Task A_connection_without_a_valid_session_is_aborted_and_never_registered(
        string? cookieValue)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = Fixture.CreateFactory();
        var cookieHeader = cookieValue is null
            ? null
            : $"{SessionAuthenticationDefaults.CookieName}={cookieValue}";

        var negotiate = await NegotiateAsync(factory, cookieHeader, cancellationToken);
        negotiate.Status.Should().Be(HttpStatusCode.OK,
            "the hub route is anonymous by design; the connect gate is OnConnectedAsync, "
            + "so single-user mode can negotiate without a cookie");

        await using var connection = CreateConnection(factory, cookieHeader: cookieHeader);
        var collector = Track(connection);
        var closed = TrackClose(connection);
        await TryStartAsync(connection, cancellationToken);

        var closeException = await closed.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        closeException.Should().BeNull("the hub aborts without an error frame");
        connection.State.Should().Be(HubConnectionState.Disconnected);
        collector.Events.Should().BeEmpty();
        GetRegistry(factory).Count.Should().Be(0, "a refused connection is never registered");
        GetRegistry(factory).CountForSession(LiveSyncConnectionRegistry.SystemSessionId)
            .Should().Be(0, "a cookie-less connection in multiuser mode is not the system actor");

        // A socket that failed auth never observes a later broadcast.
        var publisher = factory.Services.GetRequiredService<ILiveSyncPublisher>();
        await publisher.PublishAsync(
            LiveSyncEvent.Create(LiveSyncEventType.GroceriesUpdated, entity: "groceries"),
            cancellationToken);
        await Task.Delay(300, cancellationToken);
        collector.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task Negotiation_before_setup_is_rejected_with_409_setup_required()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.SingleUser, cancellationToken);

        using var factory = Fixture.CreateFactory();
        var negotiate = await NegotiateAsync(factory, cookieHeader: null, cancellationToken);

        // SetupGatingMiddleware covers the hub path before the protocol starts.
        negotiate.Status.Should().Be(HttpStatusCode.Conflict);
        ErrorCode(negotiate.Body).Should().Be("SETUP_REQUIRED");
    }

    [Fact]
    public async Task Negotiation_for_a_must_change_password_session_is_rejected_with_403()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = Fixture.CreateFactory();
        var user = await SeedUserWithPasswordAsync("m14mustchange", UserPassword, cancellationToken);
        var session = await CreateSessionAsync(factory, user, cancellationToken);
        await using (var context = Fixture.CreateContext())
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE users SET must_change_password = true WHERE id = {user.Id}",
                cancellationToken);
        }

        var negotiate = await NegotiateAsync(
            factory,
            $"{SessionAuthenticationDefaults.CookieName}={session.RawToken}",
            cancellationToken);

        // MustChangePasswordMiddleware covers the hub path, so the connect request
        // never reaches the hub and no socket is created.
        negotiate.Status.Should().Be(HttpStatusCode.Forbidden);
        ErrorCode(negotiate.Body).Should().Be("PASSWORD_CHANGE_REQUIRED");
        GetRegistry(factory).Count.Should().Be(0);
    }

    [Fact]
    public void Hub_exposes_only_the_documented_lifecycle_surface()
    {
        // The M9-09 guard asserts the declared public methods; this pins the whole
        // client-callable surface: SignalR dispatches public instance methods
        // declared on the hub type, so anything beyond the two lifecycle overrides
        // (or any static method, which could hint at a helper exposed by mistake)
        // would be an RPC surface (docs/SECURITY.md §16).
        typeof(LiveSyncHub)
            .GetMethods(System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .Should().BeEquivalentTo(["OnConnectedAsync", "OnDisconnectedAsync"]);
        typeof(LiveSyncHub)
            .GetMethods(System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.DeclaredOnly)
            .Should().BeEmpty();

        // No other public instance member (property, field, or event) can be part
        // of the client-callable surface.
        typeof(LiveSyncHub)
            .GetProperties(System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.DeclaredOnly)
            .Should().BeEmpty();
        typeof(LiveSyncHub)
            .GetFields(System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.DeclaredOnly)
            .Should().BeEmpty();
        typeof(LiveSyncHub)
            .GetEvents(System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.DeclaredOnly)
            .Should().BeEmpty();

        // The only client method ever used is the documented "event" channel.
        SignalRLiveSyncPublisher.ClientMethod.Should().Be("event");
    }

    // ---- 2. Revoked / expired / disabled --------------------------------

    [Fact]
    public async Task An_idle_expired_session_socket_is_revoked_and_closed_on_the_next_broadcast()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new TestTimeProvider(SessionTestNow);
        using var factory = CreateClockFactory(clock);
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        var user = await SeedUserWithPasswordAsync("m14idle", UserPassword, cancellationToken);
        var expired = await CreateSessionAsync(factory, user, cancellationToken);
        await using var expiredSocket = CreateConnection(factory, expired);
        var expiredCollector = TrackRaw(expiredSocket);
        await expiredSocket.StartAsync(cancellationToken);
        await WaitForConnectionsAsync(factory, expired.SessionId, 1, cancellationToken);

        // 31 days with no request: past the 30-day idle window, inside the 90-day cap.
        clock.Advance(TimeSpan.FromDays(31));

        // A second, fresh session (same household) proves the sweep is scoped.
        var otherUser = await SeedUserWithPasswordAsync("m14fresh", UserPassword, cancellationToken);
        var fresh = await CreateSessionAsync(factory, otherUser, cancellationToken);
        await using var freshSocket = CreateConnection(factory, fresh);
        var freshCollector = TrackRaw(freshSocket);
        await freshSocket.StartAsync(cancellationToken);
        await WaitForConnectionsAsync(factory, fresh.SessionId, 1, cancellationToken);

        var publisher = factory.Services.GetRequiredService<ILiveSyncPublisher>();
        await publisher.PublishAsync(
            LiveSyncEvent.Create(LiveSyncEventType.GroceriesUpdated, entity: "groceries"),
            cancellationToken);

        var revoked = await WaitForRawEventAsync(
            expiredCollector,
            evt => evt.Type == LiveSyncEventType.SessionRevoked,
            cancellationToken);
        revoked.Entity.Should().BeNull();
        revoked.Id.Should().BeNull();
        revoked.OperationId.Should().BeNull();
        revoked.ActorUserId.Should().BeNull();
        (await expiredCollector.Closed.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken))
            .Should().BeNull();
        expiredSocket.State.Should().Be(HubConnectionState.Disconnected);

        // The fresh session gets the household event, never the revocation.
        var delivered = await WaitForRawEventAsync(
            freshCollector,
            evt => evt.Type == LiveSyncEventType.GroceriesUpdated,
            cancellationToken);
        delivered.Entity.Should().Be("groceries");
        freshSocket.State.Should().Be(HubConnectionState.Connected);
        freshCollector.Events.Should().NotContain(evt => evt.Type == LiveSyncEventType.SessionRevoked);

        // The expired session receives nothing more.
        await publisher.PublishAsync(
            LiveSyncEvent.Create(LiveSyncEventType.SettingsUpdated, entity: "settings"),
            cancellationToken);
        await WaitForRawEventAsync(
            freshCollector,
            evt => evt.Type == LiveSyncEventType.SettingsUpdated,
            cancellationToken);
        expiredCollector.Events.Should().OnlyContain(evt => evt.Type == LiveSyncEventType.SessionRevoked);
        await WaitForRegistryCountAsync(factory, 1, cancellationToken);

        // The row really was idle-expired while the absolute cap was still open.
        await using var context = Fixture.CreateContext();
        var row = await context.UserSessions.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == expired.SessionId, cancellationToken);
        row.IdleExpiresAt.Should().BeOnOrBefore(clock.UtcNow);
        row.AbsoluteExpiresAt.Should().BeAfter(clock.UtcNow);
    }

    [Fact]
    public async Task An_absolute_expired_session_socket_is_revoked_and_closed_even_with_a_fresh_idle_window()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new TestTimeProvider(SessionTestNow);
        using var factory = CreateClockFactory(clock);
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        var user = await SeedUserWithPasswordAsync("m14absolute", UserPassword, cancellationToken);
        var expired = await CreateSessionAsync(factory, user, cancellationToken);
        await using var expiredSocket = CreateConnection(factory, expired);
        var expiredCollector = TrackRaw(expiredSocket);
        await expiredSocket.StartAsync(cancellationToken);
        await WaitForConnectionsAsync(factory, expired.SessionId, 1, cancellationToken);

        // An absolute cap that bites before the idle window (the same adversarial
        // row shape SessionServiceTests uses for ResolveAsync): the socket must
        // fail closed on the absolute predicate alone.
        await using (var context = Fixture.CreateContext())
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE user_sessions
                SET idle_expires_at = {clock.UtcNow.Add(SessionService.IdleTimeout)},
                    absolute_expires_at = {clock.UtcNow.AddSeconds(-1)}
                WHERE id = {expired.SessionId}
                """,
                cancellationToken);
        }

        var freshUser = await SeedUserWithPasswordAsync("m14absolutefresh", UserPassword, cancellationToken);
        var fresh = await CreateSessionAsync(factory, freshUser, cancellationToken);
        await using var freshSocket = CreateConnection(factory, fresh);
        var freshCollector = TrackRaw(freshSocket);
        await freshSocket.StartAsync(cancellationToken);
        await WaitForConnectionsAsync(factory, fresh.SessionId, 1, cancellationToken);

        var publisher = factory.Services.GetRequiredService<ILiveSyncPublisher>();
        await publisher.PublishAsync(
            LiveSyncEvent.Create(LiveSyncEventType.RecipeUpdated, entity: "recipes", id: "9"),
            cancellationToken);

        await WaitForRawEventAsync(
            expiredCollector,
            evt => evt.Type == LiveSyncEventType.SessionRevoked,
            cancellationToken);
        (await expiredCollector.Closed.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken))
            .Should().BeNull();
        expiredSocket.State.Should().Be(HubConnectionState.Disconnected);

        await WaitForRawEventAsync(
            freshCollector,
            evt => evt.Type == LiveSyncEventType.RecipeUpdated,
            cancellationToken);
        freshSocket.State.Should().Be(HubConnectionState.Connected);

        await using var verify = Fixture.CreateContext();
        var row = await verify.UserSessions.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == expired.SessionId, cancellationToken);
        row.IdleExpiresAt.Should().BeAfter(clock.UtcNow);
        row.AbsoluteExpiresAt.Should().BeOnOrBefore(clock.UtcNow);
    }

    /// <summary>
    /// Pins the documented staleness limitation (BUG-014): expiry is re-validated
    /// per broadcast, so with zero household traffic an expired socket stays open
    /// (it can receive nothing, because the next broadcast evicts it). A future
    /// periodic sweep must update this test.
    /// </summary>
    [Fact]
    public async Task An_expired_socket_with_no_household_traffic_is_only_revalidated_on_the_next_broadcast()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new TestTimeProvider(SessionTestNow);
        using var factory = CreateClockFactory(clock);
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        var user = await SeedUserWithPasswordAsync("m14stale", UserPassword, cancellationToken);
        var session = await CreateSessionAsync(factory, user, cancellationToken);
        await using var connection = CreateConnection(factory, session);
        var collector = TrackRaw(connection);
        await connection.StartAsync(cancellationToken);
        await WaitForConnectionsAsync(factory, session.SessionId, 1, cancellationToken);

        clock.Advance(TimeSpan.FromDays(31));
        await Task.Delay(300, cancellationToken);

        connection.State.Should().Be(HubConnectionState.Connected,
            "no traffic means no re-validation yet (bounded staleness, BUG-014)");
        GetRegistry(factory).CountForSession(session.SessionId).Should().Be(1);
        collector.Events.Should().BeEmpty();

        // The next broadcast is the trigger that evicts it.
        var publisher = factory.Services.GetRequiredService<ILiveSyncPublisher>();
        await publisher.PublishAsync(
            LiveSyncEvent.Create(LiveSyncEventType.GroceriesUpdated, entity: "groceries"),
            cancellationToken);
        await WaitForRawEventAsync(
            collector,
            evt => evt.Type == LiveSyncEventType.SessionRevoked,
            cancellationToken);
        collector.Events.Should().OnlyContain(evt => evt.Type == LiveSyncEventType.SessionRevoked);
        await WaitForRegistryCountAsync(factory, 0, cancellationToken);
    }

    // ---- 4. Teardown ----------------------------------------------------

    [Fact]
    public async Task Logout_tears_the_socket_down_and_a_reconnect_requires_a_fresh_login()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = Fixture.CreateFactory();
        var user = await SeedUserWithPasswordAsync("m14logout", UserPassword, cancellationToken);
        using var signedIn = await SignInAsync(factory, "m14logout", UserPassword, cancellationToken);
        var sessionId = await ActiveSessionIdAsync(user.Id, cancellationToken);
        var oldCookie = signedIn.Cookies.GetCookieHeader(factory.Server.BaseAddress);

        await using var connection = CreateConnection(factory, cookieHeader: oldCookie);
        var collector = Track(connection);
        await connection.StartAsync(cancellationToken);
        await WaitForConnectionsAsync(factory, sessionId, 1, cancellationToken);

        var logout = await SendJsonAsync(
            signedIn.Client,
            HttpMethod.Post,
            "/api/v1/auth/logout",
            json: null,
            signedIn.CsrfToken,
            cancellationToken);
        logout.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await AssertRevokedAndClosedAsync(collector, connection, cancellationToken);
        await WaitForRegistryCountAsync(factory, 0, cancellationToken);

        // The revoked cookie cannot open a new socket...
        await using var replay = CreateConnection(factory, cookieHeader: oldCookie);
        (await TryStartAsync(replay, cancellationToken)).Should().BeFalse();
        GetRegistry(factory).Count.Should().Be(0);

        // ...only a fresh login (a new session row) can.
        using var fresh = await SignInAsync(factory, "m14logout", UserPassword, cancellationToken);
        var freshSessionId = await ActiveSessionIdAsync(user.Id, cancellationToken);
        freshSessionId.Should().NotBe(sessionId);
        await using var reconnected = CreateConnection(
            factory,
            cookieHeader: fresh.Cookies.GetCookieHeader(factory.Server.BaseAddress));
        await reconnected.StartAsync(cancellationToken);
        await WaitForConnectionsAsync(factory, freshSessionId, 1, cancellationToken);
        reconnected.State.Should().Be(HubConnectionState.Connected);
    }

    [Fact]
    public async Task User_disable_tears_the_socket_down_and_a_reenabled_user_needs_a_fresh_login()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = Fixture.CreateFactory();
        await SeedUserWithPasswordAsync("m14root", UserPassword, cancellationToken, UserRole.Admin);
        var target = await SeedUserWithPasswordAsync("m14disabled", UserPassword, cancellationToken, UserRole.Member);
        using var admin = await SignInAsync(factory, "m14root", UserPassword, cancellationToken);

        var sessionA = await CreateSessionAsync(factory, target, cancellationToken);
        var sessionB = await CreateSessionAsync(factory, target, cancellationToken);
        await using var socketA = CreateConnection(factory, sessionA);
        await using var socketB = CreateConnection(factory, sessionB);
        var collectorA = Track(socketA);
        var collectorB = Track(socketB);
        await socketA.StartAsync(cancellationToken);
        await socketB.StartAsync(cancellationToken);
        await WaitForConnectionsAsync(factory, sessionA.SessionId, 1, cancellationToken);
        await WaitForConnectionsAsync(factory, sessionB.SessionId, 1, cancellationToken);

        var disable = await SendJsonAsync(
            admin.Client,
            HttpMethod.Post,
            $"/api/v1/users/{target.Id}/disable",
            json: null,
            admin.CsrfToken,
            cancellationToken);
        disable.StatusCode.Should().Be(HttpStatusCode.OK);

        await AssertRevokedAndClosedAsync(collectorA, socketA, cancellationToken);
        await AssertRevokedAndClosedAsync(collectorB, socketB, cancellationToken);
        await WaitForRegistryCountAsync(factory, 0, cancellationToken);

        // A disabled user's session cannot reconnect...
        await using var replay = CreateConnection(factory, sessionA);
        (await TryStartAsync(replay, cancellationToken)).Should().BeFalse();
        GetRegistry(factory).Count.Should().Be(0);

        // ...and neither can a login while the account is disabled.
        using var anonymous = factory.CreateClient();
        var disabledLogin = await SendJsonAsync(
            anonymous,
            HttpMethod.Post,
            "/api/v1/auth/login",
            JsonSerializer.Serialize(new { username = "m14disabled", password = UserPassword }),
            csrfToken: null,
            cancellationToken);
        disabledLogin.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Re-enabling still requires a fresh login: the old session rows stay revoked.
        var enable = await SendJsonAsync(
            admin.Client,
            HttpMethod.Post,
            $"/api/v1/users/{target.Id}/enable",
            json: null,
            admin.CsrfToken,
            cancellationToken);
        enable.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var reenabledReplay = CreateConnection(factory, sessionA);
        (await TryStartAsync(reenabledReplay, cancellationToken)).Should().BeFalse();

        using var fresh = await SignInAsync(factory, "m14disabled", UserPassword, cancellationToken);
        var freshSessionId = await ActiveSessionIdAsync(target.Id, cancellationToken);
        await using var reconnected = CreateConnection(
            factory,
            cookieHeader: fresh.Cookies.GetCookieHeader(factory.Server.BaseAddress));
        await reconnected.StartAsync(cancellationToken);
        await WaitForConnectionsAsync(factory, freshSessionId, 1, cancellationToken);
        GetRegistry(factory).GetConnections(freshSessionId).Single().ActorUserId
            .Should().Be(target.Id);
    }

    // ---- 3. No leakage --------------------------------------------------

    [Fact]
    public async Task Revocation_is_connection_scoped_across_users_and_events_carry_only_the_documented_envelope()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = Fixture.CreateFactory();
        var alice = await SeedUserWithPasswordAsync("m14alice", UserPassword, cancellationToken);
        var bob = await SeedUserWithPasswordAsync("m14bob", UserPassword, cancellationToken, UserRole.Member);

        // Two sessions for one user plus a session for another (lower-privilege)
        // household member: the connection-scoped revocation must isolate session
        // B without touching either of the other two.
        var aliceA = await CreateSessionAsync(factory, alice, cancellationToken);
        var aliceB = await CreateSessionAsync(factory, alice, cancellationToken);
        var bobA = await CreateSessionAsync(factory, bob, cancellationToken);

        await using var socketAliceA = CreateConnection(factory, aliceA);
        await using var socketAliceB = CreateConnection(factory, aliceB);
        await using var socketBobA = CreateConnection(factory, bobA);
        var collectorAliceA = TrackRaw(socketAliceA);
        var collectorAliceB = TrackRaw(socketAliceB);
        var collectorBobA = TrackRaw(socketBobA);
        await socketAliceA.StartAsync(cancellationToken);
        await socketAliceB.StartAsync(cancellationToken);
        await socketBobA.StartAsync(cancellationToken);
        await WaitForConnectionsAsync(factory, aliceA.SessionId, 1, cancellationToken);
        await WaitForConnectionsAsync(factory, aliceB.SessionId, 1, cancellationToken);
        await WaitForConnectionsAsync(factory, bobA.SessionId, 1, cancellationToken);

        var publisher = factory.Services.GetRequiredService<ILiveSyncPublisher>();
        await publisher.PublishSessionRevokedAsync(aliceB.SessionId, cancellationToken);

        await WaitForRawEventAsync(
            collectorAliceB,
            evt => evt.Type == LiveSyncEventType.SessionRevoked,
            cancellationToken);
        (await collectorAliceB.Closed.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken))
            .Should().BeNull();
        socketAliceB.State.Should().Be(HubConnectionState.Disconnected);
        AssertCoarseEnvelope(collectorAliceB.Raw.Single());
        collectorAliceB.Raw.Single().GetProperty("type").GetString()
            .Should().Be(LiveSyncEventType.SessionRevoked);

        await Task.Delay(300, cancellationToken);
        socketAliceA.State.Should().Be(HubConnectionState.Connected);
        socketBobA.State.Should().Be(HubConnectionState.Connected);
        collectorAliceA.Events.Should().NotContain(evt => evt.Type == LiveSyncEventType.SessionRevoked);
        collectorBobA.Events.Should().NotContain(evt => evt.Type == LiveSyncEventType.SessionRevoked);

        var writes = new[]
        {
            LiveSyncEvent.Create(LiveSyncEventType.CalendarUpdated, entity: "calendar", id: "12", operationId: "op-m14", actorUserId: alice.Id),
            LiveSyncEvent.Create(LiveSyncEventType.CalendarLockChanged, entity: "calendar", id: "12", actorUserId: alice.Id),
            LiveSyncEvent.Create(LiveSyncEventType.GroceriesUpdated, entity: "groceries", id: "7", actorUserId: alice.Id),
            LiveSyncEvent.Create(LiveSyncEventType.RecipeUpdated, entity: "recipes", id: "3", actorUserId: alice.Id),
            LiveSyncEvent.Create(LiveSyncEventType.SettingsUpdated, entity: "settings", actorUserId: bob.Id),
        };
        foreach (var write in writes)
        {
            await publisher.PublishAsync(write, cancellationToken);
        }

        await WaitForRawEventAsync(
            collectorAliceA,
            evt => evt.Type == LiveSyncEventType.SettingsUpdated,
            cancellationToken);
        await WaitForRawEventAsync(
            collectorBobA,
            evt => evt.Type == LiveSyncEventType.SettingsUpdated,
            cancellationToken);
        await publisher.PublishAsync(
            LiveSyncEvent.Create(LiveSyncEventType.GroceriesUpdated, entity: "groceries", id: "8"),
            cancellationToken);
        await WaitForRawEventAsync(
            collectorBobA,
            evt => evt.Id == "8",
            cancellationToken);
        await WaitForRawEventAsync(
            collectorAliceA,
            evt => evt.Id == "8",
            cancellationToken);

        var expectedTypes = new[]
        {
            LiveSyncEventType.CalendarUpdated,
            LiveSyncEventType.CalendarLockChanged,
            LiveSyncEventType.GroceriesUpdated,
            LiveSyncEventType.RecipeUpdated,
            LiveSyncEventType.SettingsUpdated,
            LiveSyncEventType.GroceriesUpdated,
        };
        collectorAliceA.Events.Select(evt => evt.Type).Should().Equal(expectedTypes);
        collectorBobA.Events.Select(evt => evt.Type).Should().Equal(expectedTypes);
        collectorAliceA.Events.Select(evt => evt.Entity)
            .Should().Equal("calendar", "calendar", "groceries", "recipes", "settings", "groceries");
        collectorAliceA.Raw.Should().OnlyContain(raw => NoPayloadMember(raw));
        collectorBobA.Events.Should().NotContain(evt => evt.Type == LiveSyncEventType.SessionRevoked);

        // The revoked connection's only frame was its own session.revoked.
        collectorAliceB.Raw.Should().ContainSingle();
    }

    [Fact]
    public async Task The_install_is_single_household_so_cross_household_socket_scope_cannot_exist()
    {
        // V1 has exactly one household per instance and no per-household column on
        // users or live-sync connections, so "a session from a second household"
        // is structurally impossible; the equivalent adversarial axis is a second
        // user (covered by Revocation_is_connection_scoped_across_users_...). A
        // future multi-household change must revisit this test and the registry's
        // broadcast target list (docs/DATABASE.md §household_settings).
        await SeedHouseholdAsync(AuthMode.Multiuser, TestContext.Current.CancellationToken);

        await using var context = Fixture.CreateContext();
        (await context.HouseholdSettings.AsNoTracking().CountAsync(TestContext.Current.CancellationToken))
            .Should().Be(1);

        typeof(User).GetProperties().Should().NotContain(
            property => property.Name.Contains("Household", StringComparison.Ordinal));
        typeof(LiveSyncConnection).GetProperties().Select(property => property.Name)
            .Should().BeEquivalentTo(["ConnectionId", "SessionId", "ActorUserId", "Context"]);
    }

    // ---- Helpers for the M14-07 battery ----------------------------------

    private static readonly DateTimeOffset SessionTestNow =
        new(2026, 11, 15, 18, 0, 0, TimeSpan.Zero);

    private ApiFactory CreateClockFactory(TestTimeProvider clock) =>
        Fixture.CreateFactory(configureServices: services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        });

    /// <summary>
    /// A one-shot gate for the live-sync connect's first actor read: the read has
    /// already resolved the (single-user) actor when the gate is entered, and the
    /// test releases it only after the auth-mode switch has committed and
    /// disconnected, forcing the register-after-snapshot interleaving.
    /// </summary>
    private sealed class PausingActorReadGate
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _remaining = 1;

        public Task Entered => _entered.Task;

        public void Release() => _release.TrySetResult();

        /// <summary>True for the one read that must pause.</summary>
        public bool TryEnter()
        {
            if (Interlocked.Decrement(ref _remaining) < 0)
            {
                return false;
            }

            _entered.TrySetResult();
            return true;
        }

        public Task WaitAsync(CancellationToken cancellationToken) =>
            _release.Task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Delegates actor resolution to the real provider and pauses the first read
    /// that resolves the single-user system actor — the live-sync connect, which
    /// the test deliberately starts before any other request that would resolve
    /// an actor — so the mode switch can commit and disconnect inside that
    /// window. The hub dispatch for long polling runs outside the request's
    /// <c>IHttpContextAccessor</c> scope, so the pause cannot key off the path;
    /// a wrong first match fails the test loudly (the switch never starts)
    /// rather than letting the race pass unnoticed.
    /// </summary>
    private sealed class PausingActorProvider(
        ICurrentActorProvider inner,
        PausingActorReadGate gate) : ICurrentActorProvider
    {
        public async Task<CurrentActor?> GetCurrentActorAsync(CancellationToken cancellationToken = default)
        {
            var actor = await inner.GetCurrentActorAsync(cancellationToken);
            if (actor is { IsSystem: true } && gate.TryEnter())
            {
                await gate.WaitAsync(cancellationToken);
            }

            return actor;
        }
    }

    private static TaskCompletionSource<Exception?> TrackClose(HubConnection connection)
    {
        var source = new TaskCompletionSource<Exception?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += exception =>
        {
            source.TrySetResult(exception);
            return Task.CompletedTask;
        };
        return source;
    }

    /// <summary>
    /// Captures the raw <c>event</c> frames (for the exact key set) plus the
    /// typed DTO and the close exception. SignalR invokes only the first typed
    /// handler registered for a method name, so this is the only handler a test
    /// may register for <c>event</c>.
    /// </summary>
    private sealed class RawCollector
    {
        public ConcurrentQueue<JsonElement> Raw { get; } = new();

        public ConcurrentQueue<LiveSyncEventDto> Events { get; } = new();

        public TaskCompletionSource<Exception?> Closed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static RawCollector TrackRaw(HubConnection connection)
    {
        var collector = new RawCollector();
        connection.On<JsonElement>(SignalRLiveSyncPublisher.ClientMethod, raw =>
        {
            var payload = raw.Clone();
            collector.Raw.Enqueue(payload);
            var evt = JsonSerializer.Deserialize<LiveSyncEventDto>(payload);
            if (evt is not null)
            {
                collector.Events.Enqueue(evt);
            }
        });
        connection.Closed += exception =>
        {
            collector.Closed.TrySetResult(exception);
            return Task.CompletedTask;
        };
        return collector;
    }

    private static async Task<LiveSyncEventDto> WaitForRawEventAsync(
        RawCollector collector,
        Func<LiveSyncEventDto, bool> predicate,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (true)
        {
            if (collector.Events.FirstOrDefault(predicate) is { } match)
            {
                return match;
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException("The expected live-sync frame was not received.");
            }

            await Task.Delay(20, cancellationToken);
        }
    }

    private static void AssertCoarseEnvelope(JsonElement raw)
    {
        raw.EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo(DocumentedEnvelopeKeys);
        raw.EnumerateObject().Count().Should().Be(DocumentedEnvelopeKeys.Length);
        NoPayloadMember(raw).Should().BeTrue(
            "the envelope is coarse: no entity bodies, diffs, or nested payload on any field");
    }

    private static bool NoPayloadMember(JsonElement raw) => raw.EnumerateObject().All(property =>
        property.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)
        && DocumentedEnvelopeKeys.Contains(property.Name, StringComparer.Ordinal));

    private static async Task<(HttpStatusCode Status, string Body)> NegotiateAsync(
        WebApplicationFactory<Program> factory,
        string? cookieHeader,
        CancellationToken cancellationToken)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{LiveSyncPaths.HubPath}/negotiate?negotiateVersion=1");
        if (cookieHeader is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        }

        using var response = await client.SendAsync(request, cancellationToken);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private static string? ErrorCode(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("error").GetProperty("code").GetString();
    }

    private static async Task<JsonElement> ReadJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.Clone();
    }
}
