using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FateAndFridge.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace FateAndFridge.Api.Services;

/// <summary>
/// The release metadata this application actually needs from GitHub Releases
/// </summary>
/// <param name="TagName">The normalized release version, e.g. <c>0.2.0</c>.</param>
/// <param name="ReleaseUrl">The public release page, when GitHub supplied one.</param>
/// <param name="PublishedAt">The published timestamp, when GitHub supplied one.</param>
public sealed record UpdateRelease(string TagName, string? ReleaseUrl, DateTimeOffset? PublishedAt);

/// <summary>Outcome of reading a GitHub "latest release" payload.</summary>
public enum ReleaseParseOutcome
{
    /// <summary>A published release that this application may compare against.</summary>
    Release,

    /// <summary>
    /// No usable release: a draft, a prerelease, a missing tag, or a tag that
    /// is not SemVer. Treated as "no update", never as an error.
    /// </summary>
    NoRelease,

    /// <summary>The payload was not a GitHub release object at all.</summary>
    Invalid,
}

/// <summary>
/// Strict SemVer comparison for the release tag.
/// </summary>
/// <remarks>
/// Only <c>MAJOR.MINOR.PATCH</c> releases are compared: a leading <c>v</c> is
/// stripped, build metadata and prerelease suffixes are rejected (the feed's
/// drafts/prereleases are ignored anyway), and anything unparseable yields
/// "no update" instead of an exception. Numeric components are compared as
/// integers, so <c>0.9.0 &lt; 0.10.0</c>.
/// </remarks>
public static class UpdateVersion
{
    /// <summary>Largest accepted component length; keeps the parse inside <see cref="int"/>.</summary>
    private const int MaxComponentDigits = 9;

    /// <summary>
    /// Parses a release tag (optionally <c>v</c>-prefixed) into its three
    /// numeric components. Returns <see langword="false"/> for prereleases
    /// build metadata, and anything else that is not a release SemVer.
    /// </summary>
    public static bool TryParse(string? value, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        if (text[0] is 'v' or 'V')
        {
            text = text[1..];
        }

        // A prerelease (`1.2.3-rc.1`) or build metadata (`1.2.3+abc`) is
        // never the version an operator should update to.
        if (text.Length == 0 || text.IndexOfAny(['-', '+']) >= 0)
        {
            return false;
        }

        var parts = text.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        Span<int> components = stackalloc int[3];
        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            if (part.Length == 0 || part.Length > MaxComponentDigits)
            {
                return false;
            }

            foreach (var character in part)
            {
                if (character is < '0' or > '9')
                {
                    return false;
                }
            }

            components[index] = int.Parse(part, NumberStyles.None, CultureInfo.InvariantCulture);
        }

        version = new Version(components[0], components[1], components[2]);
        return true;
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is a strictly newer release than
    /// <paramref name="current"/>. Unparseable values never count as an update.
    /// </summary>
    public static bool IsNewerThan(string? candidate, string current)
    {
        return TryParse(candidate, out var candidateVersion)
            && TryParse(current, out var currentVersion)
            && candidateVersion > currentVersion;
    }

    /// <summary>SemVer text without a leading tag prefix.</summary>
    public static string Normalize(string tag)
    {
        var text = tag.Trim();
        return text.Length > 0 && text[0] is 'v' or 'V' ? text[1..] : text;
    }
}

/// <summary>
/// Reads the few fields the update check needs from a GitHub
/// "releases/latest" payload. Drafts and prereleases are "no release", and a
/// tag that is not SemVer is "no update" — the check never crashes on a
/// surprising payload and never invents a version.
/// </summary>
public static class ReleaseMetadataParser
{
    public static ReleaseParseOutcome TryParse(string json, out UpdateRelease? release)
    {
        release = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return ReleaseParseOutcome.Invalid;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return ReleaseParseOutcome.Invalid;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return ReleaseParseOutcome.Invalid;
            }

            if (ReadBoolean(root, "draft") == true || ReadBoolean(root, "prerelease") == true)
            {
                return ReleaseParseOutcome.NoRelease;
            }

            var tag = ReadString(root, "tag_name");
            if (tag is null || !UpdateVersion.TryParse(tag, out _))
            {
                // An unparseable tag is never an update.
                return ReleaseParseOutcome.NoRelease;
            }

            release = new UpdateRelease(
                UpdateVersion.Normalize(tag),
                ReadReleaseUrl(root),
                ReadTimestamp(root, "published_at"));
            return ReleaseParseOutcome.Release;
        }
    }

    private static string? ReadString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var element)
            && element.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(element.GetString())
                ? element.GetString()
                : null;

    private static bool? ReadBoolean(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var element)
            && element.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? element.GetBoolean()
                : null;

    private static DateTimeOffset? ReadTimestamp(JsonElement root, string propertyName)
    {
        var text = ReadString(root, propertyName);
        return text is not null
            && DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var value)
                ? value
                : null;
    }

    /// <summary>
    /// Only an absolute <c>https</c> URL is surfaced, so the banner link can
    /// never become a <c>javascript:</c> or plain-http target.
    /// </summary>
    private static string? ReadReleaseUrl(JsonElement root)
    {
        var text = ReadString(root, "html_url");
        return text is not null
            && Uri.TryCreate(text, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
                ? uri.ToString()
                : null;
    }
}

/// <summary>
/// The one outbound release-metadata request.
/// </summary>
/// <remarks>
/// The request is a <c>GET</c> with no body and exactly two headers: a neutral
/// <c>User-Agent: FateAndFridge/&lt;version&gt;</c> and
/// <c>Accept: application/vnd.github+json</c>. No household data, no account or
/// database identifier, no token, and no cookies are sent. The endpoint is the
/// public GitHub Releases API for this repository.
/// </remarks>
public sealed class GitHubReleaseClient(HttpClient httpClient)
{
    /// <summary>Named-client registration used by <c>Program</c>.</summary>
    public const string ClientName = "github-releases";

    /// <summary>The only outbound URL of the update check.</summary>
    public const string LatestReleaseUrl =
        "https://api.github.com/repos/Tadbitnyght/fate-and-fridge/releases/latest";

    /// <summary>Bound on the response body; a release payload is a few KiB.</summary>
    public const int MaxResponseBytes = 256 * 1024;

    /// <summary>Size of one read from the release response stream.</summary>
    private const int ReadChunkBytes = 8 * 1024;

    /// <summary>The whole exchange is bounded well below a page render.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Applies the fixed request identity and timeout. Shared by the DI
    /// registration and the unit tests so both assert the same contract.
    /// </summary>
    public static void Configure(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        client.Timeout = RequestTimeout;

        // Defence in depth: a buffered read of this client is bounded too. The
        // explicit stream cap in GetLatestReleaseAsync is what enforces the limit.
        client.MaxResponseContentBufferSize = MaxResponseBytes;

        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"FateAndFridge/{AppInfo.Version}");
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    /// <summary>
    /// Fetches the latest-release payload. A non-success status is returned to
    /// the caller rather than thrown; transport failures propagate as
    /// <see cref="HttpRequestException"/>/<see cref="OperationCanceledException"/>
    /// for the service to record.
    /// </summary>
    public async Task<ReleaseFeedResult> GetLatestReleaseAsync(CancellationToken cancellationToken)
    {
        // ResponseHeadersRead: nothing is buffered until this method chooses to
        // read, so the byte cap below is the only thing that can grow memory.
        using var response = await httpClient
            .GetAsync(LatestReleaseUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return ReleaseFeedResult.Failure((int)response.StatusCode);
        }

        // A declared length above the cap is refused before a single body byte
        // is read; a chunked answer is caught by the capped read loop instead.
        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
        {
            return ReleaseFeedResult.Failure(0);
        }

        using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        using var buffer = new MemoryStream();
        var chunk = new byte[ReadChunkBytes];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
            {
                // Over the cap: the body is discarded, never buffered, and the
                // caller records the quiet "unexpected response" failure.
                return ReleaseFeedResult.Failure(0);
            }

            buffer.Write(chunk, 0, read);
        }

        return ReleaseFeedResult.Success(
            Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
    }
}

/// <summary>Outcome of the outbound release request.</summary>
public readonly record struct ReleaseFeedResult(int StatusCode, string? Body)
{
    public bool IsSuccess => StatusCode is >= 200 and < 300 && Body is not null;

    public static ReleaseFeedResult Success(string body) => new(200, body);

    public static ReleaseFeedResult Failure(int statusCode) => new(statusCode, null);
}

/// <summary>
/// What the About section and the dashboard banner render
/// Serialized snake_case.
/// </summary>
public sealed record UpdateStatus(
    string CurrentVersion,
    string? LatestVersion,
    bool UpdateAvailable,
    DateTimeOffset? LastCheckedAt,
    string? LastError,
    bool Enabled,
    string? ReleaseUrl);

/// <summary>
/// Privacy-preserving update check
/// The release metadata is cached in <c>application_settings</c> for
/// 24 hours, the <c>update_check_enabled</c> setup preference is honoured, and
/// every failure is recorded as a short, non-identifying <c>last_error</c>
/// instead of surfacing as an application error.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here updates the application: the check only describes what is
/// available. There is no telemetry and no second outbound call.
/// </para>
/// <para>
/// A failed check keeps the last known release (so a transient outage does not
/// erase a known update) and still stamps <c>last_checked_at</c>, so the 24-hour
/// cache also prevents hammering GitHub after an outage.
/// </para>
/// </remarks>
public sealed class UpdateCheckService(
    AppDbContext dbContext,
    GitHubReleaseClient releaseClient,
    IAuditService auditService,
    TimeProvider timeProvider,
    ILogger<UpdateCheckService> logger)
{
    /// <summary>The setup preference, absent meaning enabled.</summary>
    public const string EnabledKey = SetupCompletionService.UpdateCheckEnabledKey;

    /// <summary>Reserved <c>application_settings</c> key: last known release.</summary>
    public const string LatestVersionKey = "update_check_latest_version";

    /// <summary>Reserved <c>application_settings</c> key: last attempt (UTC).</summary>
    public const string CheckedAtKey = "update_check_checked_at";

    /// <summary>Reserved <c>application_settings</c> key: last failure (short).</summary>
    public const string LastErrorKey = "update_check_last_error";

    /// <summary>Reserved <c>application_settings</c> key: the release page.</summary>
    public const string ReleaseUrlKey = "update_check_release_url";

    /// <summary>Stable audit entity id for the update preference.</summary>
    public const string SettingsEntityId = "updates";

    /// <summary>How long a release result is reused before another check may run.</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    /// <summary>Upper bound for the startup check, which must never delay startup.</summary>
    public static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);

    private const string TimedOutMessage = "The release check timed out.";
    private const string UnreachableMessage = "The release feed could not be reached.";
    private const string UnexpectedMessage = "The release feed returned an unexpected response.";

    private static readonly string[] ReadKeys =
        [EnabledKey, LatestVersionKey, CheckedAtKey, LastErrorKey, ReleaseUrlKey];

    /// <summary>The cached status; never performs an outbound request.</summary>
    public async Task<UpdateStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        BuildStatus(await ReadStateAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Runs a check unless the 24-hour cache is still fresh and
    /// <paramref name="force"/> is false, or checks are disabled. Never throws
    /// for a feed failure; the failure is recorded in <c>last_error</c>.
    /// </summary>
    public async Task<UpdateStatus> CheckAsync(
        bool force,
        CancellationToken cancellationToken = default)
    {
        var state = await ReadStateAsync(cancellationToken).ConfigureAwait(false);
        if (!state.Enabled)
        {
            // Disabled means no outbound call at all, not a silent check.
            return BuildStatus(state);
        }

        var now = timeProvider.GetUtcNow();
        if (!force && !IsDue(state, now))
        {
            return BuildStatus(state);
        }

        var latestVersion = state.LatestVersion;
        var releaseUrl = state.ReleaseUrl;
        string? lastError = null;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(GitHubReleaseClient.RequestTimeout);

            var feed = await releaseClient
                .GetLatestReleaseAsync(timeout.Token)
                .ConfigureAwait(false);

            if (!feed.IsSuccess)
            {
                lastError = feed.StatusCode > 0
                    ? $"The release feed returned HTTP {feed.StatusCode}."
                    : UnexpectedMessage;
            }
            else
            {
                switch (ReleaseMetadataParser.TryParse(feed.Body!, out var release))
                {
                    case ReleaseParseOutcome.Release when release is not null:
                        latestVersion = release.TagName;
                        releaseUrl = release.ReleaseUrl;
                        logger.LogInformation(
                            "Update check found release {Version} (published {PublishedAt})",
                            release.TagName,
                            release.PublishedAt);
                        break;

                    case ReleaseParseOutcome.NoRelease:
                        // A draft, a prerelease, or an unparseable tag is
                        // "nothing newer to offer", not a failure.
                        latestVersion = null;
                        releaseUrl = null;
                        break;

                    default:
                        lastError = UnexpectedMessage;
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            lastError = TimedOutMessage;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            logger.LogDebug(exception, "The update check could not reach the release feed.");
            lastError = UnreachableMessage;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "The update check failed unexpectedly.");
            lastError = UnexpectedMessage;
        }

        // A caller cancellation is not a result: do not stamp a checked-at that
        // a shutdown happened to produce mid-request.
        if (cancellationToken.IsCancellationRequested)
        {
            return BuildStatus(new UpdateCheckState(
                state.Enabled,
                latestVersion,
                state.LastCheckedAt,
                lastError,
                releaseUrl));
        }

        await PersistAsync(latestVersion, now, lastError, releaseUrl, cancellationToken)
            .ConfigureAwait(false);

        return BuildStatus(new UpdateCheckState(
            state.Enabled,
            latestVersion,
            now,
            lastError,
            releaseUrl));
    }

    /// <summary>
    /// Persists the <c>update_check_enabled</c> preference (Admin-only route)
    /// and audits the change as <c>settings.update_check_changed</c>. The
    /// setting and its audit row commit together.
    /// </summary>
    public async Task<UpdateStatus> SetEnabledAsync(
        bool enabled,
        long? actorUserId,
        string? ipAddress,
        CancellationToken cancellationToken = default)
    {
        var state = await ReadStateAsync(cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();

        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await UpsertAsync(EnabledKey, JsonSerializer.Serialize(enabled), now, cancellationToken)
            .ConfigureAwait(false);
        await auditService.WriteAsync(
            new AuditEventEntry(
                AuditEventTypes.SettingsUpdateCheckChanged,
                AuditEntityTypes.Settings,
                EntityId: SettingsEntityId,
                BeforeJson: SnapshotJson(state.Enabled),
                AfterJson: SnapshotJson(enabled),
                IpAddress: ipAddress,
                ActorUserId: actorUserId),
            cancellationToken).ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return BuildStatus(new UpdateCheckState(
            enabled,
            state.LatestVersion,
            state.LastCheckedAt,
            state.LastError,
            state.ReleaseUrl));
    }

    /// <summary>
    /// Fire-and-forget startup check. The returned
    /// task is ignored by <c>Program</c>; it never throws and never delays
    /// startup because every await happens on the thread pool.
    /// </summary>
    public static Task StartStartupCheckAsync(
        IServiceProvider services,
        ILogger logger,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logger);

        return Task.Run(async () =>
        {
            try
            {
                using var cancellation = new CancellationTokenSource(timeout ?? StartupTimeout);
                using var scope = services.CreateScope();
                var updateCheck = scope.ServiceProvider.GetRequiredService<UpdateCheckService>();
                await updateCheck.CheckAsync(force: false, cancellation.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // A startup check must never fail or crash the application.
                logger.LogDebug(exception, "The startup update check did not complete.");
            }
        });
    }

    private static string SnapshotJson(bool enabled) =>
        JsonSerializer.Serialize(new Dictionary<string, object?> { ["enabled"] = enabled });

    private static bool IsDue(UpdateCheckState state, DateTimeOffset now)
    {
        if (state.LastCheckedAt is not { } lastChecked)
        {
            return true;
        }

        var elapsed = now - lastChecked;
        return elapsed < TimeSpan.Zero || elapsed >= CacheTtl;
    }

    private async Task<UpdateCheckState> ReadStateAsync(CancellationToken cancellationToken)
    {
        var stored = await dbContext.ApplicationSettings
            .AsNoTracking()
            .Where(setting => ReadKeys.Contains(setting.Key))
            .ToDictionaryAsync(setting => setting.Key, setting => setting.ValueJson, cancellationToken)
            .ConfigureAwait(false);

        return new UpdateCheckState(
            // The setup wizard writes the preference; before setup it is absent
            // and the setup default is "enabled".
            ReadBoolean(stored.GetValueOrDefault(EnabledKey), fallback: true),
            ReadString(stored.GetValueOrDefault(LatestVersionKey)),
            ReadTimestamp(stored.GetValueOrDefault(CheckedAtKey)),
            ReadString(stored.GetValueOrDefault(LastErrorKey)),
            ReadString(stored.GetValueOrDefault(ReleaseUrlKey)));
    }

    private async Task PersistAsync(
        string? latestVersion,
        DateTimeOffset checkedAt,
        string? lastError,
        string? releaseUrl,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await UpsertAsync(
            LatestVersionKey,
            JsonSerializer.Serialize(latestVersion),
            checkedAt,
            cancellationToken).ConfigureAwait(false);
        await UpsertAsync(
            CheckedAtKey,
            JsonSerializer.Serialize(checkedAt),
            checkedAt,
            cancellationToken).ConfigureAwait(false);
        await UpsertAsync(
            LastErrorKey,
            JsonSerializer.Serialize(lastError),
            checkedAt,
            cancellationToken).ConfigureAwait(false);
        await UpsertAsync(
            ReleaseUrlKey,
            JsonSerializer.Serialize(releaseUrl),
            checkedAt,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task UpsertAsync(
        string key,
        string valueJson,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO application_settings (key, value_json, updated_at)
            VALUES ({key}, {valueJson}::jsonb, {now})
            ON CONFLICT (key) DO UPDATE
            SET value_json = EXCLUDED.value_json,
                updated_at = EXCLUDED.updated_at
            """,
            cancellationToken).ConfigureAwait(false);

    private UpdateStatus BuildStatus(UpdateCheckState state) =>
        new(
            AppInfo.Version,
            state.LatestVersion,
            state.Enabled && UpdateVersion.IsNewerThan(state.LatestVersion, AppInfo.Version),
            state.LastCheckedAt,
            state.LastError,
            state.Enabled,
            state.LatestVersion is null ? null : state.ReleaseUrl);

    private static string? ReadString(string? valueJson)
    {
        if (string.IsNullOrWhiteSpace(valueJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(valueJson);
            return document.RootElement.ValueKind == JsonValueKind.String
                ? document.RootElement.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool ReadBoolean(string? valueJson, bool fallback)
    {
        if (string.IsNullOrWhiteSpace(valueJson))
        {
            return fallback;
        }

        try
        {
            using var document = JsonDocument.Parse(valueJson);
            return document.RootElement.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => fallback,
            };
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    private static DateTimeOffset? ReadTimestamp(string? valueJson)
    {
        var text = ReadString(valueJson);
        return text is not null
            && DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var value)
                ? value
                : null;
    }

    /// <summary>The cached settings as one value.</summary>
    private sealed record UpdateCheckState(
        bool Enabled,
        string? LatestVersion,
        DateTimeOffset? LastCheckedAt,
        string? LastError,
        string? ReleaseUrl);
}
