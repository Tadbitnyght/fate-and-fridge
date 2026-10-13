using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.LiveSync;
using FateAndFridge.Api.Services;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// M9-01 hub foundation (docs/M9_PLAN.md §1.1–§1.4, §1.11–§1.13, §2.1, §2.2):
/// connection gating for multiuser and single-user modes, the same-origin check,
/// the IP allowlist on the upgrade, the documented guards, the bounded
/// per-session connection cap, and the household broadcast plus session
/// revocation. Requires a Docker daemon; run with
/// <c>dotnet test --filter "Category!=Integration"</c> to skip locally.
/// </summary>
[Trait("Category", "Integration")]
[Collection(M1SetupCollection.Name)]
public sealed class M9LiveSyncHubTests(M1SetupFixture fixture) : IAsyncLifetime
{
    private const string AllowedIp = "203.0.113.10";
    private const string BlockedIp = "198.51.100.5";

    /// <summary>Rounds in the register/unregister race regression (M9-01 review).</summary>
    private const int RaceRounds = 2000;

    private static readonly DateTimeOffset SeedTimestamp =
        new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public ValueTask InitializeAsync() =>
        new(fixture.ResetAsync(TestContext.Current.CancellationToken));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Multiuser_connection_without_a_session_cookie_is_refused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        await using var connection = CreateConnection(factory);

        (await TryStartAsync(connection, cancellationToken)).Should().BeFalse();
    }

    [Fact]
    public async Task Multiuser_connection_with_a_valid_session_cookie_connects()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        var user = await SeedUserAsync("alice", mustChangePassword: false, cancellationToken);
        var session = await CreateSessionAsync(factory, user, cancellationToken);

        await using var connection = CreateConnection(factory, session);
        await connection.StartAsync(cancellationToken);
        await WaitForConnectionsAsync(factory, session.SessionId, 1, cancellationToken);

        var registered = GetRegistry(factory).GetConnections(session.SessionId).Single();
        registered.ActorUserId.Should().Be(user.Id);
    }

    [Fact]
    public async Task Single_user_connection_without_a_cookie_connects_as_the_system_actor()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.SingleUser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        await using var connection = CreateConnection(factory);
        await connection.StartAsync(cancellationToken);
        await WaitForConnectionsAsync(
            factory,
            LiveSyncConnectionRegistry.SystemSessionId,
            1,
            cancellationToken);

        var registered = GetRegistry(factory)
            .GetConnections(LiveSyncConnectionRegistry.SystemSessionId)
            .Single();
        registered.ActorUserId.Should().BeNull();
    }

    [Fact]
    public async Task Connection_before_setup_is_refused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Single-user with no cookie: without the setup gate this socket would
        // connect as the system actor (see
        // Single_user_connection_without_a_cookie_connects_as_the_system_actor),
        // so this test only passes when SetupGating still covers the hub path.
        // It is the discriminating regression for the
        // LiveSyncPaths.IsHubPath clause in SetupGating.ShouldGate.
        await SeedHouseholdAsync(AuthMode.SingleUser, cancellationToken);

        using var factory = fixture.CreateFactory();
        await using var connection = CreateConnection(factory);

        (await TryStartAsync(connection, cancellationToken)).Should().BeFalse();
    }

    [Fact]
    public async Task Connection_for_a_must_change_password_session_is_refused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        var user = await SeedUserAsync("bob", mustChangePassword: true, cancellationToken);
        var session = await CreateSessionAsync(factory, user, cancellationToken);

        await using var connection = CreateConnection(factory, session);

        (await TryStartAsync(connection, cancellationToken)).Should().BeFalse();
    }

    [Fact]
    public async Task Foreign_origin_is_refused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        var user = await SeedUserAsync("carol", mustChangePassword: false, cancellationToken);
        var session = await CreateSessionAsync(factory, user, cancellationToken);

        await using var connection = CreateConnection(
            factory,
            session,
            origin: "http://evil.example");

        (await TryStartAsync(connection, cancellationToken)).Should().BeFalse();
    }

    [Fact]
    public async Task Matching_origin_connects()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        var user = await SeedUserAsync("dave", mustChangePassword: false, cancellationToken);
        var session = await CreateSessionAsync(factory, user, cancellationToken);

        await using var connection = CreateConnection(
            factory,
            session,
            origin: "http://localhost");
        await connection.StartAsync(cancellationToken);
        await WaitForConnectionsAsync(factory, session.SessionId, 1, cancellationToken);

        connection.State.Should().Be(HubConnectionState.Connected);
    }

    [Fact]
    public async Task Ip_allowlist_applies_to_the_hub_connect()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);
        await SeedSecuritySettingsAsync(true, ["203.0.113.0/24"], cancellationToken);

        using var factory = fixture.CreateFactory().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddSingleton<IStartupFilter>(new TestRemoteIpStartupFilter())));
        var user = await SeedUserAsync("erin", mustChangePassword: false, cancellationToken);
        var session = await CreateSessionAsync(factory, user, cancellationToken);

        await using var allowed = CreateConnection(factory, session, clientIp: AllowedIp);
        await allowed.StartAsync(cancellationToken);
        await WaitForConnectionsAsync(factory, session.SessionId, 1, cancellationToken);

        await using var blocked = CreateConnection(factory, session, clientIp: BlockedIp);
        (await TryStartAsync(blocked, cancellationToken)).Should().BeFalse();
    }

    [Fact]
    public void Hub_guards_are_configured()
    {
        using var factory = fixture.CreateFactory();
        var options = factory.Services.GetRequiredService<IOptions<HubOptions>>().Value;

        options.MaximumReceiveMessageSize.Should().Be(LiveSyncHub.MaximumInboundMessageBytes);
        options.EnableDetailedErrors.Should().BeFalse();
    }

    [Fact]
    public async Task Per_session_connection_cap_refuses_an_extra_connection()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        var user = await SeedUserAsync("frank", mustChangePassword: false, cancellationToken);
        var session = await CreateSessionAsync(factory, user, cancellationToken);

        var connections = new List<HubConnection>();
        try
        {
            for (var i = 0; i < LiveSyncConnectionRegistry.MaxConnectionsPerSession; i++)
            {
                var connection = CreateConnection(factory, session);
                await connection.StartAsync(cancellationToken);
                connections.Add(connection);
            }

            await WaitForConnectionsAsync(
                factory,
                session.SessionId,
                LiveSyncConnectionRegistry.MaxConnectionsPerSession,
                cancellationToken);

            await using var extra = CreateConnection(factory, session);
            (await TryStartAsync(extra, cancellationToken)).Should().BeFalse();
        }
        finally
        {
            foreach (var connection in connections)
            {
                await connection.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task Unregister_racing_with_register_keeps_the_session_connection_tracked()
    {
        // Regression for the M9-01 review finding: Unregister must serialize
        // with TryRegister, otherwise it can observe a session's map as empty
        // and drop it from _bySession after a concurrent TryRegister re-added a
        // connection to that same map, leaving a live connection untracked (so
        // it can neither be revoked nor counted against the cap). Each round
        // races one unregister against one register for one session; the racer
        // must remain tracked afterwards.
        const long sessionId = 7;
        var registry = new LiveSyncConnectionRegistry();

        for (var round = 0; round < RaceRounds; round++)
        {
            registry.TryRegister(
                    "existing",
                    sessionId,
                    actorUserId: 1,
                    new StubHubCallerContext("existing"))
                .Should().BeTrue();

            using var barrier = new Barrier(2);
            var unregister = new Thread(() =>
            {
                barrier.SignalAndWait();
                registry.Unregister("existing");
            });
            var register = new Thread(() =>
            {
                barrier.SignalAndWait();
                registry.TryRegister("racer", sessionId, actorUserId: 1, new StubHubCallerContext("racer"));
            });
            unregister.Start();
            register.Start();
            unregister.Join();
            register.Join();

            registry.CountForSession(sessionId).Should().Be(1);
            registry.GetConnections(sessionId).Single().ConnectionId.Should().Be("racer");
            registry.Count.Should().Be(1);

            registry.Unregister("racer");
        }
    }

    [Fact]
    public void Household_connection_cap_refuses_an_extra_connection()
    {
        var registry = new LiveSyncConnectionRegistry();
        var index = 0;
        string NextConnectionId() => $"conn-{index++}";

        // Fill the household to the cap, session by session, each at its
        // per-session limit.
        for (var sessionId = 1L;
             registry.Count < LiveSyncConnectionRegistry.MaxConnectionsPerHousehold;
             sessionId++)
        {
            for (var i = 0;
                 i < LiveSyncConnectionRegistry.MaxConnectionsPerSession
                 && registry.Count < LiveSyncConnectionRegistry.MaxConnectionsPerHousehold;
                 i++)
            {
                var connectionId = NextConnectionId();
                registry.TryRegister(
                        connectionId,
                        sessionId,
                        actorUserId: sessionId,
                        new StubHubCallerContext(connectionId))
                    .Should().BeTrue();
            }
        }

        registry.Count.Should().Be(LiveSyncConnectionRegistry.MaxConnectionsPerHousehold);

        var extraConnectionId = NextConnectionId();
        registry.TryRegister(
                extraConnectionId,
                sessionId: 999,
                actorUserId: null,
                new StubHubCallerContext(extraConnectionId))
            .Should().BeFalse();
        registry.Count.Should().Be(LiveSyncConnectionRegistry.MaxConnectionsPerHousehold);
    }

    [Fact]
    public async Task Connection_is_refused_when_the_request_context_is_missing()
    {
        var registry = new LiveSyncConnectionRegistry();
        var context = new StubHubCallerContext("no-http");
        var hub = new LiveSyncHub(
            new StubActorProvider(new CurrentActor(IsSystem: true, Role: UserRole.Admin)),
            registry,
            NullLogger<LiveSyncHub>.Instance)
        {
            Context = context,
        };

        await hub.OnConnectedAsync();

        context.Aborted.Should().BeTrue();
        registry.Count.Should().Be(0);
    }

    [Fact]
    public async Task Broadcast_reaches_both_clients_and_revocation_closes_only_that_session()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        var user = await SeedUserAsync("grace", mustChangePassword: false, cancellationToken);
        var sessionA = await CreateSessionAsync(factory, user, cancellationToken);
        var sessionB = await CreateSessionAsync(factory, user, cancellationToken);

        await using var connectionA = CreateConnection(factory, sessionA);
        await using var connectionB = CreateConnection(factory, sessionB);
        var receivedA = TrackEvents(connectionA);
        var receivedB = TrackEvents(connectionB);
        var revokedA = TrackRevocation(connectionA);
        var revokedB = TrackRevocation(connectionB);

        await connectionA.StartAsync(cancellationToken);
        await connectionB.StartAsync(cancellationToken);
        await WaitForConnectionsAsync(factory, sessionA.SessionId, 1, cancellationToken);
        await WaitForConnectionsAsync(factory, sessionB.SessionId, 1, cancellationToken);

        var publisher = factory.Services.GetRequiredService<ILiveSyncPublisher>();
        await publisher.PublishAsync(
            LiveSyncEvent.Create(
                LiveSyncEventType.GroceriesUpdated,
                entity: "groceries",
                operationId: "op-1",
                actorUserId: user.Id),
            cancellationToken);

        var eventA = await ReceiveAsync(receivedA, cancellationToken);
        var eventB = await ReceiveAsync(receivedB, cancellationToken);
        eventA.Type.Should().Be(LiveSyncEventType.GroceriesUpdated);
        eventA.Entity.Should().Be("groceries");
        eventA.OperationId.Should().Be("op-1");
        eventA.ActorUserId.Should().Be(user.Id);
        eventA.Timestamp.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        eventB.Type.Should().Be(LiveSyncEventType.GroceriesUpdated);

        await publisher.PublishSessionRevokedAsync(sessionA.SessionId, cancellationToken);

        await ReceiveAsync(revokedA.Received, cancellationToken);
        await revokedA.State.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        connectionA.State.Should().Be(HubConnectionState.Disconnected);
        connectionB.State.Should().Be(HubConnectionState.Connected);
        revokedB.State.Task.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task Recording_publisher_fake_is_injectable_via_the_test_host()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.SingleUser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        var fake = new RecordingLiveSyncPublisher();
        using var factory = fixture.CreateFactory(configureServices: services =>
            services.AddSingleton<ILiveSyncPublisher>(fake));

        var resolved = factory.Services.GetRequiredService<ILiveSyncPublisher>();
        resolved.Should().BeSameAs(fake);

        await resolved.PublishAsync(
            LiveSyncEvent.Create(LiveSyncEventType.SettingsUpdated, entity: "settings", actorUserId: 7),
            cancellationToken);

        fake.Events.Should().ContainSingle();
        fake.Events.Single().Type.Should().Be(LiveSyncEventType.SettingsUpdated);
    }

    private sealed record TestSession(long SessionId, string RawToken);

    private sealed class LiveSyncEventDto
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("entity")]
        public string? Entity { get; set; }

        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("operation_id")]
        public string? OperationId { get; set; }

        [JsonPropertyName("actor_user_id")]
        public long? ActorUserId { get; set; }

        [JsonPropertyName("timestamp")]
        public DateTimeOffset Timestamp { get; set; }
    }

    private sealed record RevocationState(
        TaskCompletionSource Received,
        TaskCompletionSource<object?> State);

    private static HubConnection CreateConnection(
        WebApplicationFactory<Program> factory,
        TestSession? session = null,
        string? origin = null,
        string? clientIp = null) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, LiveSyncPaths.HubPath), options =>
            {
                // Long polling keeps the tests on the TestServer's in-process
                // handler; the SPA uses skipNegotiation WebSockets in production.
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                if (session is not null)
                {
                    options.Headers["Cookie"] =
                        $"{SessionAuthenticationDefaults.CookieName}={session.RawToken}";
                }

                if (origin is not null)
                {
                    options.Headers["Origin"] = origin;
                }

                if (clientIp is not null)
                {
                    options.Headers[TestRemoteIpStartupFilter.HeaderName] = clientIp;
                }
            })
            .Build();

    private static TaskCompletionSource<LiveSyncEventDto> TrackEvents(HubConnection connection)
    {
        var source = new TaskCompletionSource<LiveSyncEventDto>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<LiveSyncEventDto>(SignalRLiveSyncPublisher.ClientMethod, evt => source.TrySetResult(evt));
        return source;
    }

    private static RevocationState TrackRevocation(HubConnection connection)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<LiveSyncEventDto>(SignalRLiveSyncPublisher.ClientMethod, evt =>
        {
            if (evt.Type == LiveSyncEventType.SessionRevoked)
            {
                received.TrySetResult();
            }
        });
        connection.Closed += _ =>
        {
            state.TrySetResult(null);
            return Task.CompletedTask;
        };
        return new RevocationState(received, state);
    }

    private static async Task<bool> TryStartAsync(HubConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await connection.StartAsync(cancellationToken);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested is false)
        {
            return false;
        }

        // A refused connection can complete the handshake before the server
        // aborts it, so wait briefly for the abort to land.
        for (var i = 0; i < 100 && connection.State == HubConnectionState.Connected; i++)
        {
            await Task.Delay(20, cancellationToken);
        }

        return connection.State == HubConnectionState.Connected;
    }

    private static Task<LiveSyncEventDto> ReceiveAsync(
        TaskCompletionSource<LiveSyncEventDto> source,
        CancellationToken cancellationToken) =>
        source.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);

    private static Task ReceiveAsync(TaskCompletionSource source, CancellationToken cancellationToken) =>
        source.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);

    private static LiveSyncConnectionRegistry GetRegistry(WebApplicationFactory<Program> factory) =>
        factory.Services.GetRequiredService<LiveSyncConnectionRegistry>();

    private static async Task WaitForConnectionsAsync(
        WebApplicationFactory<Program> factory,
        long sessionId,
        int expected,
        CancellationToken cancellationToken)
    {
        var registry = GetRegistry(factory);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (registry.CountForSession(sessionId) < expected)
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"Expected {expected} live-sync connection(s) for session {sessionId}.");
            }

            await Task.Delay(20, cancellationToken);
        }
    }

    private async Task<TestSession> CreateSessionAsync(
        WebApplicationFactory<Program> factory,
        User user,
        CancellationToken cancellationToken)
    {
        using var scope = factory.Services.CreateScope();
        var (session, rawToken) = await scope.ServiceProvider
            .GetRequiredService<SessionService>()
            .CreateAsync(user.Id, "127.0.0.1", "m9-hub-test", cancellationToken);
        return new TestSession(session.Id, rawToken);
    }

    private async Task<HouseholdSettings> SeedHouseholdAsync(
        AuthMode authMode,
        CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        var settings = new HouseholdSettings
        {
            Id = 1,
            HouseholdName = "M9 Household",
            Timezone = "America/Chicago",
            DefaultDinerCount = 4,
            WeekStartsOn = 0,
            StartupPage = StartupPage.Calendar,
            AuthMode = authMode,
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
        };
        context.HouseholdSettings.Add(settings);
        await context.SaveChangesAsync(cancellationToken);
        return settings;
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

    private async Task SeedSecuritySettingsAsync(
        bool enabled,
        string[] ranges,
        CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        await UpsertAsync(
            context,
            SetupCompletionService.IpAllowlistEnabledKey,
            JsonSerializer.Serialize(enabled),
            cancellationToken);
        await UpsertAsync(
            context,
            SetupCompletionService.IpAllowlistRangesKey,
            JsonSerializer.Serialize(ranges),
            cancellationToken);
        await UpsertAsync(
            context,
            SetupCompletionService.TrustedProxiesKey,
            JsonSerializer.Serialize(Array.Empty<string>()),
            cancellationToken);
    }

    private static async Task UpsertAsync(
        Data.AppDbContext context,
        string key,
        string valueJson,
        CancellationToken cancellationToken) =>
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO application_settings (key, value_json, updated_at)
            VALUES ({key}, {valueJson}::jsonb, {DateTimeOffset.UtcNow})
            ON CONFLICT (key) DO UPDATE
            SET value_json = EXCLUDED.value_json, updated_at = EXCLUDED.updated_at
            """,
            cancellationToken);

    private async Task<User> SeedUserAsync(
        string username,
        bool mustChangePassword,
        CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        var user = new User
        {
            Username = username,
            NormalizedUsername = username.ToLowerInvariant(),
            PasswordHash = "phc-placeholder",
            Role = UserRole.Admin,
            IsActive = true,
            MustChangePassword = mustChangePassword,
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
        };
        context.Users.Add(user);
        await context.SaveChangesAsync(cancellationToken);
        return user;
    }

    /// <summary>
    /// Minimal <see cref="HubCallerContext"/> for registry and hub unit tests:
    /// no HTTP features, so <c>GetHttpContext()</c> returns null, and it records
    /// whether the hub aborted the connection.
    /// </summary>
    private sealed class StubHubCallerContext(string connectionId) : HubCallerContext
    {
        public override string ConnectionId { get; } = connectionId;

        public override string? UserIdentifier => null;

        public override ClaimsPrincipal? User => null;

        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();

        public override IFeatureCollection Features { get; } = new FeatureCollection();

        public override CancellationToken ConnectionAborted => CancellationToken.None;

        public bool Aborted { get; private set; }

        public override void Abort() => Aborted = true;
    }

    private sealed class StubActorProvider(CurrentActor actor) : ICurrentActorProvider
    {
        public Task<CurrentActor?> GetCurrentActorAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<CurrentActor?>(actor);
    }
}
