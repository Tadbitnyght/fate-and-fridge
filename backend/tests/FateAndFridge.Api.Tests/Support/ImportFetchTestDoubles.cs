using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using FateAndFridge.Api.Imports;

namespace FateAndFridge.Api.Tests.Support;

/// <summary>Captured request facts for assertions after the request is disposed.</summary>
internal sealed record CapturedRequest(
    string Method,
    Uri Uri,
    string? UserAgent,
    string? Authorization,
    string? Cookie,
    string? AcceptEncoding);

/// <summary>
/// Deterministic <see cref="HttpMessageHandler"/> for fetcher tests. The
/// delegate receives the request and returns a canned response, so no network
/// access ever occurs.
/// </summary>
internal sealed class StubHttpMessageHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
{
    public List<CapturedRequest> Requests { get; } = [];

    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        : this((request, _) => Task.FromResult(handler(request)))
    {
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests.Add(new CapturedRequest(
            request.Method.Method,
            request.RequestUri!,
            request.Headers.UserAgent.ToString(),
            request.Headers.Authorization?.ToString(),
            string.Join(",", request.Headers.TryGetValues("Cookie", out var cookies) ? cookies : []),
            string.Join(",", request.Headers.TryGetValues("Accept-Encoding", out var encodings) ? encodings : [])));
        return handler(request, cancellationToken);
    }
}

/// <summary>Test response builders.</summary>
internal static class ImportTestResponses
{
    public static HttpResponseMessage Html(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        Content(status, "text/html; charset=utf-8", System.Text.Encoding.UTF8.GetBytes(body));

    public static HttpResponseMessage Content(
        HttpStatusCode status,
        string? contentType,
        byte[] body,
        string? location = null,
        long? declaredLength = null,
        string? contentEncoding = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new ByteArrayContent(body),
        };

        if (contentType is null)
        {
            response.Content.Headers.ContentType = null;
        }
        else
        {
            response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }

        if (contentEncoding is not null)
        {
            response.Content.Headers.ContentEncoding.Add(contentEncoding);
        }

        if (declaredLength is not null)
        {
            response.Content.Headers.ContentLength = declaredLength;
        }

        if (location is not null)
        {
            response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        }

        return response;
    }

    public static HttpResponseMessage Redirect(HttpStatusCode status, string location) =>
        new(status)
        {
            Headers = { Location = new Uri(location, UriKind.RelativeOrAbsolute) },
            Content = new ByteArrayContent([]),
        };
}

/// <summary>Content with no computable length (simulates chunked responses).</summary>
internal sealed class UnknownLengthContent : HttpContent
{
    private readonly byte[] _bytes;

    public UnknownLengthContent(string contentType, byte[] bytes)
    {
        _bytes = bytes;
        Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        stream.WriteAsync(_bytes).AsTask();

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}

/// <summary>DNS seam returning fixed addresses, or throwing, per test.</summary>
internal sealed class FakeDnsResolver : IDnsResolver
{
    private readonly Func<string, IPAddress[]> _resolve;

    public FakeDnsResolver(params IPAddress[] addresses)
        : this(_ => addresses)
    {
    }

    public FakeDnsResolver(Func<string, IPAddress[]> resolve) => _resolve = resolve;

    public List<string> QueriedHosts { get; } = [];

    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        QueriedHosts.Add(host);
        return Task.FromResult(_resolve(host));
    }
}

/// <summary>DNS seam that always fails.</summary>
internal sealed class FailingDnsResolver : IDnsResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
        throw new SocketException((int)SocketError.HostNotFound);
}

/// <summary>
/// DNS seam that returns successive address sets and repeats the last one.
/// Drives the DNS-rebinding test through the production pinned transport.
/// </summary>
internal sealed class StatefulDnsResolver : IDnsResolver
{
    private readonly Queue<IPAddress[]> _answers;
    private readonly Lock _gate = new();
    private IPAddress[] _last = [];

    public StatefulDnsResolver(params IPAddress[][] answers) => _answers = new Queue<IPAddress[]>(answers);

    public int Calls { get; private set; }

    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Calls++;
            if (_answers.Count > 0)
            {
                _last = _answers.Dequeue();
            }

            return Task.FromResult(_last);
        }
    }
}

/// <summary>
/// Minimal loopback HTTP server used for the happy-path integration test. It
/// binds to 127.0.0.1 only and never touches the public internet.
/// </summary>
internal sealed class LoopbackHttpServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly ConcurrentDictionary<string, Func<HttpListenerContext, Task>> _routes = new();
    private readonly Task _acceptLoop;

    public LoopbackHttpServer()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        BaseUri = new Uri($"http://127.0.0.1:{port}/");
        _listener.Prefixes.Add(BaseUri.ToString());
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public Uri BaseUri { get; }

    public void Map(string path, Func<HttpListenerContext, Task> handler) => _routes[path] = handler;

    public void MapHtml(string path, string body, HttpStatusCode status = HttpStatusCode.OK) =>
        Map(path, async context =>
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(body);
            context.Response.StatusCode = (int)status;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        });

    private async Task AcceptLoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (HttpListenerException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            if (_routes.TryGetValue(context.Request.Url?.AbsolutePath ?? string.Empty, out var handler))
            {
                await handler(context);
            }
            else
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                context.Response.Close();
            }
        }
        catch (Exception)
        {
            try
            {
                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                context.Response.Close();
            }
            catch (Exception)
            {
                // The client disconnected; nothing left to do.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Stop();
        _listener.Close();
        try
        {
            await _acceptLoop;
        }
        catch (Exception)
        {
            // Shutdown races are expected.
        }
    }
}

/// <summary>
/// Forwards requests for any host to a <see cref="LoopbackHttpServer"/>, so the
/// fetcher's URL policy still sees a normal public host name.
/// </summary>
internal sealed class LoopbackForwardingHandler : HttpMessageHandler
{
    private readonly Uri _baseAddress;
    private readonly HttpClient _inner = new(new SocketsHttpHandler { AllowAutoRedirect = false });

    public LoopbackForwardingHandler(Uri baseAddress) => _baseAddress = baseAddress;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var relative = request.RequestUri!.PathAndQuery;
        var target = new Uri(_baseAddress, relative);
        using var forwarded = new HttpRequestMessage(request.Method, target);
        return await _inner.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
