using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FateAndFridge.Api.Imports;

/// <summary>
/// SSRF-hardened fetch pipeline for recipe import (docs/M4_PLAN.md §1.13–§1.14,
/// docs/SECURITY.md §6–§7).
/// </summary>
/// <remarks>
/// <para>
/// The URL is classified by <see cref="ImportUrlPolicy"/> before connecting,
/// DNS is resolved by the injected <see cref="IDnsResolver"/> and every
/// resolved address is checked against the blocked-range table, and the
/// production transport pins the connection to a validated address through
/// <see cref="SocketsHttpHandler.ConnectCallback"/> so a second DNS answer
/// cannot redirect the socket (DNS rebinding).
/// </para>
/// <para>
/// The pre-connect resolution in <see cref="ValidateResolutionAsync"/> is a
/// fail-fast check only. <b>Connect-time revalidation is authoritative:</b>
/// <see cref="ConnectAsync"/> re-resolves and may only connect to an address
/// that passes <see cref="ImportUrlPolicy.IsBlockedAddress"/> at connect time,
/// so a resolver that turns hostile between the pre-check and the socket open
/// (DNS rebinding) fails closed.
/// </para>
/// <para>
/// Redirects are followed manually (<c>AllowAutoRedirect = false</c>) and each
/// hop is re-resolved and re-validated. No cookies or credentials are ever
/// sent; the User-Agent identifies the application and version.
/// </para>
/// </remarks>
public sealed class SafeUrlFetcher : IImportFetcher, IDisposable
{
    private readonly ILogger<SafeUrlFetcher> _logger;
    private readonly ImportFetchOptions _options;
    private readonly IDnsResolver _dnsResolver;
    private readonly HttpClient _client;

    public SafeUrlFetcher(
        ILogger<SafeUrlFetcher> logger,
        IOptions<ImportFetchOptions> options,
        IDnsResolver dnsResolver)
        : this(logger, options.Value, dnsResolver, handlerFactory: null)
    {
    }

    /// <summary>
    /// Test/DI constructor. <paramref name="handlerFactory"/> replaces the
    /// pinned production transport; tests supply a loopback-backed handler so
    /// no public network access occurs.
    /// </summary>
    public SafeUrlFetcher(
        ILogger<SafeUrlFetcher> logger,
        ImportFetchOptions options,
        IDnsResolver dnsResolver,
        Func<HttpMessageHandler>? handlerFactory)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(dnsResolver);

        _logger = logger;
        _options = options;
        _dnsResolver = dnsResolver;

        var handler = handlerFactory is null
            ? CreatePinnedHandler()
            : handlerFactory();
        _client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd($"FateAndFridge/{AppInfo.Version}");
        _client.DefaultRequestHeaders.AcceptEncoding.Clear();
    }

    public Task<ImportFetchResult> FetchPageAsync(string url, CancellationToken cancellationToken = default) =>
        FetchAsync(url, ImportFetchKind.Page, cancellationToken);

    public Task<ImportFetchResult> FetchImageAsync(string url, CancellationToken cancellationToken = default) =>
        FetchAsync(url, ImportFetchKind.Image, cancellationToken);

    /// <summary>Fetches <paramref name="url"/> under the limits for <paramref name="kind"/>.</summary>
    public async Task<ImportFetchResult> FetchAsync(
        string url,
        ImportFetchKind kind,
        CancellationToken cancellationToken = default)
    {
        var initial = ImportUrlPolicy.Evaluate(url);
        if (!initial.IsAllowed)
        {
            return ImportFetchResult.Failure(MapPolicyFailure(initial.Reason), initial.Reason.ToString());
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.ReadTimeout);
        var token = timeout.Token;

        try
        {
            return await FetchLoopAsync(initial.Uri!, kind, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ImportFetchResult.Failure(ImportFetchFailureReason.Timeout, "read timeout");
        }
        catch (HttpRequestException exception) when (ContainsInner<BlockedTargetException>(exception))
        {
            return ImportFetchResult.Failure(ImportFetchFailureReason.BlockedTarget, "connect target blocked");
        }
        catch (HttpRequestException exception) when (ContainsInner<TimeoutException>(exception))
        {
            return ImportFetchResult.Failure(ImportFetchFailureReason.Timeout, "connect timeout");
        }
        catch (HttpRequestException exception)
        {
            _logger.LogDebug(exception, "Import fetch transport failure for {Host}", initial.Uri!.Host);
            return ImportFetchResult.Failure(ImportFetchFailureReason.HttpError, "transport failure");
        }
        catch (IOException exception)
        {
            _logger.LogDebug(exception, "Import fetch I/O failure for {Host}", initial.Uri!.Host);
            return ImportFetchResult.Failure(ImportFetchFailureReason.HttpError, "transport failure");
        }
    }

    private async Task<ImportFetchResult> FetchLoopAsync(
        Uri start,
        ImportFetchKind kind,
        CancellationToken cancellationToken)
    {
        var current = start;
        var redirects = 0;

        while (true)
        {
            var resolutionFailure = await ValidateResolutionAsync(current, cancellationToken).ConfigureAwait(false);
            if (resolutionFailure is not null)
            {
                return resolutionFailure;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            foreach (var mediaType in AllowedContentTypes(kind))
            {
                request.Headers.Accept.ParseAdd(mediaType);
            }

            using var response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            var statusCode = (int)response.StatusCode;

            if (IsRedirect(response.StatusCode))
            {
                var location = response.Headers.Location?.ToString();
                if (string.IsNullOrWhiteSpace(location))
                {
                    return ImportFetchResult.Failure(
                        ImportFetchFailureReason.HttpError,
                        "redirect without Location",
                        statusCode);
                }

                redirects++;
                if (redirects > _options.MaxRedirects)
                {
                    return ImportFetchResult.Failure(ImportFetchFailureReason.TooManyRedirects, "redirect limit");
                }

                var next = ImportUrlPolicy.EvaluateLocation(current, location);
                if (!next.IsAllowed)
                {
                    return ImportFetchResult.Failure(MapPolicyFailure(next.Reason), next.Reason.ToString());
                }

                current = next.Uri!;
                continue;
            }

            if (statusCode is < 200 or >= 300)
            {
                return ImportFetchResult.Failure(
                    ImportFetchFailureReason.HttpError,
                    $"HTTP {statusCode}",
                    statusCode);
            }

            var mediaTypeResult = response.Content.Headers.ContentType?.MediaType;
            if (string.IsNullOrWhiteSpace(mediaTypeResult) || !IsAllowedContentType(mediaTypeResult, kind))
            {
                return ImportFetchResult.Failure(
                    ImportFetchFailureReason.UnsupportedContentType,
                    mediaTypeResult,
                    statusCode,
                    mediaTypeResult);
            }

            var maxBytes = kind == ImportFetchKind.Page ? _options.MaxHtmlBytes : _options.MaxImageBytes;
            if (response.Content.Headers.ContentLength is long declared && declared > maxBytes)
            {
                return ImportFetchResult.Failure(
                    ImportFetchFailureReason.TooLarge,
                    $"declared {declared} bytes",
                    statusCode);
            }

            var content = await ReadWithLimitAsync(response.Content, maxBytes, cancellationToken)
                .ConfigureAwait(false);
            if (content is null)
            {
                return ImportFetchResult.Failure(ImportFetchFailureReason.TooLarge, "body exceeds limit", statusCode);
            }

            return ImportFetchResult.Success(current, statusCode, mediaTypeResult, content);
        }
    }

    /// <summary>
    /// Resolves the host and rejects DNS failures or any blocked address
    /// (fail closed when the resolver returns a mix).
    /// </summary>
    private async Task<ImportFetchResult?> ValidateResolutionAsync(Uri uri, CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        try
        {
            addresses = await ResolveHostAsync(uri.DnsSafeHost, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return ImportFetchResult.Failure(ImportFetchFailureReason.DnsFailure, "dns failure");
        }

        if (addresses.Length == 0)
        {
            return ImportFetchResult.Failure(ImportFetchFailureReason.DnsFailure, "dns returned no addresses");
        }

        foreach (var address in addresses)
        {
            if (ImportUrlPolicy.IsBlockedAddress(address))
            {
                return ImportFetchResult.Failure(
                    ImportFetchFailureReason.BlockedTarget,
                    "resolved address blocked");
            }
        }

        return null;
    }

    private async Task<IPAddress[]> ResolveHostAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            return [literal];
        }

        return await _dnsResolver.ResolveAsync(host, cancellationToken).ConfigureAwait(false);
    }

    internal SocketsHttpHandler CreatePinnedHandler() => new()
    {
        AllowAutoRedirect = false,
        ConnectTimeout = _options.ConnectTimeout,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        // Never tunnel through an environment-configured proxy: the pinned
        // connect guarantee only covers sockets this process opens itself.
        UseProxy = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectCallback = ConnectAsync,
    };

    /// <summary>
    /// Authoritative connect-time validation for the pinned transport. The
    /// pre-check in <see cref="ValidateResolutionAsync"/> is fail-fast only;
    /// this re-resolves and connects only to an address that passes
    /// <see cref="ImportUrlPolicy.IsBlockedAddress"/> at connect time.
    /// </summary>
    internal async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var addresses = await ResolveHostAsync(context.DnsEndPoint.Host, cancellationToken)
            .ConfigureAwait(false);
        var target = Array.Find(addresses, address => !ImportUrlPolicy.IsBlockedAddress(address));
        if (target is null)
        {
            throw new BlockedTargetException(context.DnsEndPoint.Host);
        }

        var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
        };

        try
        {
            await socket
                .ConnectAsync(new IPEndPoint(target, context.DnsEndPoint.Port), cancellationToken)
                .ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task<byte[]?> ReadWithLimitAsync(
        HttpContent content,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private List<string> AllowedContentTypes(ImportFetchKind kind) =>
        kind == ImportFetchKind.Page ? _options.AllowedPageContentTypes : _options.AllowedImageContentTypes;

    private bool IsAllowedContentType(string mediaType, ImportFetchKind kind)
    {
        foreach (var allowed in AllowedContentTypes(kind))
        {
            if (string.Equals(mediaType, allowed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Found or
        HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    private static ImportFetchFailureReason MapPolicyFailure(ImportUrlBlockReason reason) => reason switch
    {
        ImportUrlBlockReason.HostBlocked or ImportUrlBlockReason.AddressBlocked =>
            ImportFetchFailureReason.BlockedTarget,
        _ => ImportFetchFailureReason.InvalidUrl,
    };

    private static bool ContainsInner<TException>(Exception exception)
        where TException : Exception
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is TException)
            {
                return true;
            }
        }

        return false;
    }

    public void Dispose() => _client.Dispose();

    /// <summary>Raised by the pinned connect callback when an address is blocked at connect time.</summary>
    internal sealed class BlockedTargetException(string host)
        : Exception($"Blocked target address for host '{host}'.");
}
