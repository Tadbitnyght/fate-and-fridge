using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Primitives;

namespace FateAndFridge.Api.Services;

/// <summary>
/// Single address rule for stored allowlist/trusted-proxy ranges and resolved
/// client addresses (docs/API.md §21, docs/SECURITY.md §5).
/// </summary>
/// <remarks>
/// <para>
/// Stored input accepts strict IPv4 dotted-quad or IPv6 addresses, optionally
/// with a CIDR prefix; bare addresses are normalized to single-host ranges
/// (<c>/32</c> or <c>/128</c>). Legacy IPv4 shorthand and hex forms
/// (<c>10</c>, <c>10.1</c>, <c>0x7f000001</c>), scope IDs, and malformed
/// values are rejected.
/// </para>
/// <para>
/// Runtime addresses are normalized (IPv4-mapped IPv6 → IPv4, scope IDs
/// stripped) so stored ranges and resolved clients compare consistently.
/// </para>
/// </remarks>
public static class ClientAddress
{
    /// <summary>
    /// Validates one range entry and returns its canonical CIDR form.
    /// </summary>
    public static bool TryNormalizeRange(string? input, out string cidr)
    {
        cidr = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var value = input.Trim();

        // Scope IDs (fe80::1%eth0) are link-local only and never remotely valid.
        if (value.Contains('%'))
        {
            return false;
        }

        string addressPart;
        var prefixLength = -1;
        var slash = value.IndexOf('/');
        if (slash >= 0)
        {
            if (value.IndexOf('/', slash + 1) >= 0)
            {
                return false;
            }

            addressPart = value[..slash];
            var prefixPart = value[(slash + 1)..];
            if (!int.TryParse(prefixPart, NumberStyles.None, CultureInfo.InvariantCulture, out prefixLength)
                || prefixLength < 0)
            {
                return false;
            }
        }
        else
        {
            addressPart = value;
        }

        if (!TryParseStrictAddress(addressPart, out var address))
        {
            return false;
        }

        var maxPrefix = address.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
        if (prefixLength < 0)
        {
            prefixLength = maxPrefix;
        }

        if (prefixLength > maxPrefix)
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            // Store IPv4-mapped ranges as IPv4 with the equivalent prefix. A
            // prefix shorter than /96 covers non-IPv4 space as well and cannot
            // be represented as an IPv4 range, so reject it instead of silently
            // widening the range (a /95 would otherwise become /0 = all IPv4).
            if (prefixLength < 96)
            {
                return false;
            }

            address = address.MapToIPv4();
            prefixLength -= 96;
        }

        cidr = $"{address}/{prefixLength}";
        return true;
    }

    /// <summary>
    /// Normalizes a runtime address: IPv4-mapped IPv6 becomes IPv4 and scope
    /// IDs are stripped. Null stays null.
    /// </summary>
    public static IPAddress? Normalize(IPAddress? address)
    {
        if (address is null)
        {
            return null;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0)
        {
            return new IPAddress(address.GetAddressBytes());
        }

        return address;
    }

    /// <summary>True when the (normalized) address is IPv4/IPv6 loopback.</summary>
    public static bool IsLoopback(IPAddress? address)
    {
        var normalized = Normalize(address);
        return normalized is not null && IPAddress.IsLoopback(normalized);
    }

    /// <summary>True when <paramref name="address"/> falls inside <paramref name="cidr"/>.</summary>
    public static bool IsInRange(IPAddress? address, string? cidr)
    {
        var normalized = Normalize(address);
        if (normalized is null
            || string.IsNullOrWhiteSpace(cidr)
            || !IPNetwork.TryParse(cidr, out var network))
        {
            return false;
        }

        return network.BaseAddress.AddressFamily == normalized.AddressFamily
            && network.Contains(normalized);
    }

    /// <summary>True when <paramref name="address"/> falls inside any range.</summary>
    public static bool IsInAnyRange(IPAddress? address, IReadOnlyList<string> ranges)
    {
        var normalized = Normalize(address);
        if (normalized is null)
        {
            return false;
        }

        foreach (var range in ranges)
        {
            if (IsInRange(normalized, range))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when the immediate socket peer is a configured trusted proxy.</summary>
    public static bool IsTrustedProxy(IPAddress? peer, IReadOnlyList<string> trustedProxies) =>
        IsInAnyRange(peer, trustedProxies);

    /// <summary>
    /// Resolves the client address for a request. When the immediate peer is
    /// trusted, <c>X-Forwarded-For</c> is walked right-to-left skipping trusted
    /// proxies and the first untrusted address wins; otherwise the peer is
    /// returned unchanged (forwarded headers are ignored for spoof protection).
    /// </summary>
    public static IPAddress? ResolveClientAddress(
        IPAddress? peer,
        IReadOnlyList<string> trustedProxies,
        StringValues forwardedFor)
    {
        var normalizedPeer = Normalize(peer);
        if (normalizedPeer is null || !IsTrustedProxy(normalizedPeer, trustedProxies))
        {
            return normalizedPeer;
        }

        var candidates = ReadForwardedAddresses(forwardedFor);
        for (var index = candidates.Count - 1; index >= 0; index--)
        {
            if (!IsTrustedProxy(candidates[index], trustedProxies))
            {
                return candidates[index];
            }
        }

        return normalizedPeer;
    }

    /// <summary>
    /// True when a trusted proxy forwarded an HTTPS connection (first
    /// <c>X-Forwarded-Proto</c> value); false for untrusted peers so spoofed
    /// headers cannot influence cookie flags.
    /// </summary>
    public static bool IsForwardedHttps(
        IPAddress? peer,
        IReadOnlyList<string> trustedProxies,
        StringValues forwardedProto)
    {
        if (!IsTrustedProxy(peer, trustedProxies) || forwardedProto.Count == 0)
        {
            return false;
        }

        var first = forwardedProto[0];
        if (string.IsNullOrWhiteSpace(first))
        {
            return false;
        }

        var value = first.Split(',')[0].Trim();
        return string.Equals(value, "https", StringComparison.OrdinalIgnoreCase);
    }

    private static List<IPAddress> ReadForwardedAddresses(StringValues forwardedFor)
    {
        var candidates = new List<IPAddress>();
        foreach (var header in forwardedFor)
        {
            if (string.IsNullOrWhiteSpace(header))
            {
                continue;
            }

            foreach (var part in header.Split(','))
            {
                var trimmed = part.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                // Strict parsing: legacy shorthand/hex entries in the header
                // are ignored rather than trusted.
                if (TryParseStrictAddress(trimmed, out var address))
                {
                    candidates.Add(Normalize(address)!);
                }
            }
        }

        return candidates;
    }

    /// <summary>
    /// Strict address parsing: IPv4 requires a four-octet dotted quad with no
    /// leading zeros; anything else must be a valid IPv6 address. Legacy
    /// shorthand and hex forms are rejected.
    /// </summary>
    private static bool TryParseStrictAddress(string value, out IPAddress address)
    {
        address = IPAddress.None;
        if (value.Length == 0)
        {
            return false;
        }

        if (value.Contains(':'))
        {
            if (!IPAddress.TryParse(value, out var ipv6) || ipv6.AddressFamily != AddressFamily.InterNetworkV6)
            {
                return false;
            }

            address = ipv6;
            return true;
        }

        var parts = value.Split('.');
        if (parts.Length != 4)
        {
            return false;
        }

        var octets = new byte[4];
        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            if (part.Length == 0
                || part.Length > 3
                || (part.Length > 1 && part[0] == '0')
                || !byte.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out octets[index]))
            {
                return false;
            }
        }

        address = new IPAddress(octets);
        return true;
    }
}
