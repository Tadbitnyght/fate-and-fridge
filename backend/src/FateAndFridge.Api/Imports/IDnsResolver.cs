using System.Net;
using System.Net.Sockets;

namespace FateAndFridge.Api.Imports;

/// <summary>
/// DNS seam for <see cref="SafeUrlFetcher"/>. The production implementation
/// uses the system resolver; tests substitute a deterministic fake.
/// </summary>
public interface IDnsResolver
{
    /// <summary>Resolves a host name to its addresses.</summary>
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}

/// <summary>System DNS resolver.</summary>
internal sealed class SystemDnsResolver : IDnsResolver
{
    public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
        await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
}
