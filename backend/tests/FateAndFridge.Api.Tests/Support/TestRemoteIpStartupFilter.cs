using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace FateAndFridge.Api.Tests.Support;

/// <summary>
/// Sets <c>Connection.RemoteIpAddress</c> from the <c>X-Test-Remote-IP</c>
/// request header so a single test host can exercise several client addresses
/// (allowlist and trusted-proxy scenarios). The header only exists in tests.
/// </summary>
public sealed class TestRemoteIpStartupFilter : IStartupFilter
{
    public const string HeaderName = "X-Test-Remote-IP";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
        application =>
        {
            application.Use((context, nextMiddleware) =>
            {
                var header = context.Request.Headers[HeaderName].ToString();
                if (!string.IsNullOrWhiteSpace(header) && IPAddress.TryParse(header, out var address))
                {
                    context.Connection.RemoteIpAddress = address;
                }

                return nextMiddleware(context);
            });

            next(application);
        };
}
