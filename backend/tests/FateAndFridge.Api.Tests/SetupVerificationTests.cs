using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FateAndFridge.Api.Data;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.Services;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Verification sweep for the setup-state repository: runtime
/// gating flips, recovery-key presence, HTTP validation envelope, concurrent
/// flag writes, and raw-path routing (issue #17).
/// </summary>
[Collection(SetupCollection.Name)]
public sealed class SetupVerificationTests(SetupFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Gating_flag_flips_at_runtime_without_restart()
    {
        await fixture.ResetAsync(Ct);
        using var factory = fixture.CreateFactory(CreateTempDataDir());
        using var client = factory.CreateClient();

        (await client.GetAsync("/api/v1/settings/household", Ct)).StatusCode
            .Should().Be(HttpStatusCode.Conflict);

        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<SetupStateService>()
                .SetSetupCompletedAsync(true, Ct);
        }

        // Setup gating opens immediately: the route now reaches authentication.
        (await client.GetAsync("/api/v1/settings/household", Ct)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);

        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<SetupStateService>()
                .SetSetupCompletedAsync(false, Ct);
        }

        (await client.GetAsync("/api/v1/settings/household", Ct)).StatusCode
            .Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Recovery_key_presence_follows_the_file_without_restart()
    {
        await fixture.ResetAsync(Ct);
        var dataDir = CreateTempDataDir();
        using var factory = fixture.CreateFactory(dataDir);
        using var client = factory.CreateClient();

        (await RecoveryKeyPresentAsync(client)).Should().BeFalse();

        string keyPath;
        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<RecoveryKeyStore>();
            await store.GetOrCreateAsync(Ct);
            keyPath = store.FilePath;
        }

        (await RecoveryKeyPresentAsync(client)).Should().BeTrue();

        File.Delete(keyPath);
        (await RecoveryKeyPresentAsync(client)).Should().BeFalse();
    }

    [Fact]
    public async Task Validation_envelope_matches_the_documented_shape_over_http()
    {
        await fixture.ResetAsync(Ct);
        using var factory = fixture.CreateFactory(CreateTempDataDir());
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/setup/complete", new { }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var error = body.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be(ErrorCodes.ValidationFailed);
        error.GetProperty("message").GetString().Should().Be("The request failed validation.");
        error.GetProperty("details").GetProperty("field_errors").TryGetProperty("household_name", out _)
            .Should().BeTrue();
        // No stray camelCase properties leaked from the serializer.
        error.TryGetProperty("Code", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Concurrent_setup_flag_writes_leave_exactly_one_row()
    {
        await fixture.ResetAsync(Ct);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();

        var tasks = Enumerable.Range(0, 8).Select(index => Task.Run(async () =>
        {
            await using var context = fixture.CreateContext();
            await new SetupStateService(context, configuration)
                .SetSetupCompletedAsync(index % 2 == 0, Ct);
        }));

        await Task.WhenAll(tasks);

        await using var verification = fixture.CreateContext();
        var rows = await verification.ApplicationSettings
            .Where(setting => setting.Key == SetupStateService.SetupCompletedKey)
            .ToListAsync(Ct);
        rows.Should().HaveCount(1);
    }

    // Raw-path routing normalization (dot segments, %2F) is verified against a
    // live Kestrel stack: the curl matrix recorded on issue #11 shows Kestrel
    // normalizing "../" before routing (the request reaches /setup/status with
    // no gating bypass) and "%2F" behaving like routing does in production.
    // TestServer cannot send raw request targets, so no unit-level probe is kept.

    private static async Task<bool> RecoveryKeyPresentAsync(HttpClient client)
    {
        var status = await client.GetFromJsonAsync<JsonElement>("/api/v1/setup/status", Ct);
        return status.GetProperty("recovery_key_present").GetBoolean();
    }

    private static string CreateTempDataDir()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "fate-and-fridge-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

}
