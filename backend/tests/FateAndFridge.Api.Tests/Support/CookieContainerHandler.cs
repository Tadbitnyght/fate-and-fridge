using System.Net;

namespace FateAndFridge.Api.Tests.Support;

/// <summary>
/// Applies a shared <see cref="CookieContainer"/> to test-server requests so
/// session and antiforgery cookies survive across requests and hosts.
/// </summary>
public sealed class CookieContainerHandler(CookieContainer container) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var uri = request.RequestUri
            ?? throw new InvalidOperationException("The request URI is required.");

        var cookieHeader = container.GetCookieHeader(uri);
        if (!string.IsNullOrEmpty(cookieHeader))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        }

        var response = await base.SendAsync(request, cancellationToken);

        if (response.Headers.TryGetValues("Set-Cookie", out var setCookieHeaders))
        {
            foreach (var setCookieHeader in setCookieHeaders)
            {
                container.SetCookies(uri, setCookieHeader);
            }
        }

        return response;
    }
}
