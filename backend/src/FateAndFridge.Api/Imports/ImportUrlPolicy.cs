using System.Net;
using System.Net.Sockets;
using FateAndFridge.Api.Services;

namespace FateAndFridge.Api.Imports;

/// <summary>
/// Why a candidate import URL was rejected (docs/M4_PLAN.md §1.14,
/// docs/SECURITY.md §6).
/// </summary>
public enum ImportUrlBlockReason
{
    None = 0,

    /// <summary>Not an absolute, well-formed URL.</summary>
    Malformed,

    /// <summary>Scheme other than HTTP/HTTPS (file, ftp, gopher, data, …).</summary>
    SchemeNotAllowed,

    /// <summary>URL carried a userinfo component (credentials).</summary>
    CredentialsNotAllowed,

    /// <summary>Host name is a blocked local/internal/container name.</summary>
    HostBlocked,

    /// <summary>Host resolved to (or literally named) a blocked address range.</summary>
    AddressBlocked,
}

/// <summary>Outcome of <see cref="ImportUrlPolicy.Evaluate(string)"/>.</summary>
public readonly record struct ImportUrlPolicyResult(
    bool IsAllowed,
    ImportUrlBlockReason Reason,
    Uri? Uri)
{
    /// <summary>Allow the URL and carry the parsed absolute URI.</summary>
    public static ImportUrlPolicyResult Allow(Uri uri) => new(true, ImportUrlBlockReason.None, uri);

    /// <summary>Reject the URL for the supplied reason.</summary>
    public static ImportUrlPolicyResult Block(ImportUrlBlockReason reason) => new(false, reason, null);
}

/// <summary>
/// Pure, table-driven classification of import URLs and resolved addresses
/// (docs/M4_PLAN.md §1.14). Shared by <see cref="SafeUrlFetcher"/> and the
/// image-from-URL endpoint so both apply the same SSRF rules.
/// </summary>
/// <remarks>
/// <para>
/// This type deliberately performs no I/O and uses no culture-sensitive APIs
/// (<c>String.Normalize</c> is never used; this workstation runs invariant
/// globalization). Comparison is ordinal / ordinal-ignore-case.
/// </para>
/// <para>
/// Host names are matched as strings; resolved addresses are matched against
/// explicit CIDR tables. Legacy IPv4 shorthand (decimal, octal, hex) is
/// detected from the raw authority and rejected before <see cref="Uri"/> can
/// canonicalize it into a routable-looking address.
/// </para>
/// </remarks>
public static class ImportUrlPolicy
{
    // IPv4 ranges that must never be targeted: "this network", RFC1918,
    // CGNAT, loopback, link-local, IETF protocol assignments, documentation,
    // benchmarking, 6to4 relay anycast, multicast, and reserved space.
    private static readonly IPNetwork[] BlockedV4 =
    [
        IPNetwork.Parse("0.0.0.0/8"),
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("100.64.0.0/10"),
        IPNetwork.Parse("127.0.0.0/8"),
        IPNetwork.Parse("169.254.0.0/16"),
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.0.0.0/24"),
        IPNetwork.Parse("192.0.2.0/24"),
        IPNetwork.Parse("192.88.99.0/24"),
        IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("198.18.0.0/15"),
        IPNetwork.Parse("198.51.100.0/24"),
        IPNetwork.Parse("203.0.113.0/24"),
        IPNetwork.Parse("224.0.0.0/4"),
        IPNetwork.Parse("240.0.0.0/4"),
    ];

    // IPv6 ranges: unspecified/loopback/IPv4-compatible, NAT64, discard-only,
    // Teredo/6to4 tunneling, benchmarking, documentation, ORCHID, ULA,
    // link-local, multicast. IPv4-mapped addresses are normalized to IPv4 first.
    private static readonly IPNetwork[] BlockedV6 =
    [
        IPNetwork.Parse("::/96"),
        IPNetwork.Parse("64:ff9b::/96"),
        IPNetwork.Parse("64:ff9b:1::/48"),
        IPNetwork.Parse("100::/64"),
        IPNetwork.Parse("2001::/32"),
        IPNetwork.Parse("2001:2::/48"),
        IPNetwork.Parse("2001:10::/28"),
        IPNetwork.Parse("2001:20::/28"),
        IPNetwork.Parse("2001:db8::/32"),
        IPNetwork.Parse("2002::/16"),
        IPNetwork.Parse("3fff::/20"),
        IPNetwork.Parse("5f00::/16"),
        IPNetwork.Parse("fc00::/7"),
        IPNetwork.Parse("fe80::/10"),
        IPNetwork.Parse("fec0::/10"), // deprecated site-local
        IPNetwork.Parse("ff00::/8"),
    ];

    // Bare container/orchestrator host names that are not covered by the
    // .local/.internal suffixes. All are matched after lowercasing and
    // trailing-dot trimming.
    private static readonly HashSet<string> BlockedContainerHosts =
    [
        "host.docker.internal",
        "gateway.docker.internal",
        "metadata",
        "kubernetes.default",
        "kubernetes.default.svc",
    ];

    /// <summary>
    /// Validates a raw URL string: scheme, credentials, host name, and any
    /// literal address. DNS is not resolved here.
    /// </summary>
    public static ImportUrlPolicyResult Evaluate(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return ImportUrlPolicyResult.Block(ImportUrlBlockReason.Malformed);
        }

        var trimmed = url.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return ImportUrlPolicyResult.Block(ImportUrlBlockReason.Malformed);
        }

        if (!IsHttpScheme(uri.Scheme))
        {
            return ImportUrlPolicyResult.Block(ImportUrlBlockReason.SchemeNotAllowed);
        }

        // Classify the raw authority before Uri canonicalization. Non-canonical
        // IPv4 obfuscation and trailing-dot numeric hosts must not slip through
        // as DNS names, and blocked numeric literals are rejected here even
        // when Uri reports them as host names.
        if (TryExtractRawHost(trimmed, out var rawHost))
        {
            var numericHost = rawHost.TrimEnd('.');
            if (IsObfuscatedIpv4Literal(numericHost))
            {
                return ImportUrlPolicyResult.Block(ImportUrlBlockReason.HostBlocked);
            }

            if (IPAddress.TryParse(numericHost, out var rawLiteral) && IsBlockedAddress(rawLiteral))
            {
                return ImportUrlPolicyResult.Block(ImportUrlBlockReason.AddressBlocked);
            }
        }

        if (uri.UserInfo.Length > 0)
        {
            return ImportUrlPolicyResult.Block(ImportUrlBlockReason.CredentialsNotAllowed);
        }

        var host = uri.DnsSafeHost;
        if (string.IsNullOrWhiteSpace(host))
        {
            return ImportUrlPolicyResult.Block(ImportUrlBlockReason.Malformed);
        }

        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
        {
            if (!IPAddress.TryParse(host, out var literal) || IsBlockedAddress(literal))
            {
                return ImportUrlPolicyResult.Block(ImportUrlBlockReason.AddressBlocked);
            }
        }
        else if (IsBlockedHostName(host))
        {
            return ImportUrlPolicyResult.Block(ImportUrlBlockReason.HostBlocked);
        }

        // The transport resolves Uri.IdnHost (SocketsHttpHandler keys its
        // connection on it), while DnsSafeHost keeps the Unicode spelling. A
        // host such as "127。0。0。1" (U+3002 dots) is a DNS name to DnsSafeHost
        // but IDN-maps to the blocked literal 127.0.0.1, so the IDN spelling
        // must pass the same literal/name checks.
        var idnHost = uri.IdnHost;
        if (!string.Equals(idnHost, host, StringComparison.Ordinal))
        {
            var trimmedIdnHost = idnHost.TrimEnd('.');
            if (IsObfuscatedIpv4Literal(trimmedIdnHost))
            {
                return ImportUrlPolicyResult.Block(ImportUrlBlockReason.HostBlocked);
            }

            if (IPAddress.TryParse(trimmedIdnHost, out var idnLiteral) && IsBlockedAddress(idnLiteral))
            {
                return ImportUrlPolicyResult.Block(ImportUrlBlockReason.AddressBlocked);
            }

            if (IsBlockedHostName(idnHost))
            {
                return ImportUrlPolicyResult.Block(ImportUrlBlockReason.HostBlocked);
            }
        }

        return ImportUrlPolicyResult.Allow(uri);
    }

    /// <summary>Validates an already-parsed absolute URI.</summary>
    public static ImportUrlPolicyResult Evaluate(Uri? uri) =>
        uri is null
            ? ImportUrlPolicyResult.Block(ImportUrlBlockReason.Malformed)
            : Evaluate(uri.OriginalString);

    /// <summary>
    /// Resolves a <c>Location</c> header value against the current URL and
    /// validates the result. Raw absolute and protocol-relative locations are
    /// validated before resolution so obfuscated authorities cannot be hidden
    /// behind relative-form parsing.
    /// </summary>
    public static ImportUrlPolicyResult EvaluateLocation(Uri baseUri, string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return ImportUrlPolicyResult.Block(ImportUrlBlockReason.Malformed);
        }

        var raw = location.Trim();

        // Protocol-relative first: on Unix, "//host/path" parses as an absolute
        // UNC-style file URI, so it must be prefixed before absolute detection.
        if (raw.StartsWith("//", StringComparison.Ordinal))
        {
            return Evaluate($"{baseUri.Scheme}:{raw}");
        }

        // Only HTTP(S) is treated as a valid absolute target. Other schemes
        // (including the file URI that Unix path syntax produces) fall through
        // to relative resolution, where Evaluate still rejects the scheme.
        if (Uri.TryCreate(raw, UriKind.Absolute, out var absolute) && IsHttpScheme(absolute.Scheme))
        {
            return Evaluate(raw);
        }

        if (!Uri.TryCreate(baseUri, raw, out var resolved))
        {
            return ImportUrlPolicyResult.Block(ImportUrlBlockReason.Malformed);
        }

        return Evaluate(resolved.OriginalString);
    }

    /// <summary>
    /// True when the address is loopback, private, link-local, ULA, CGNAT,
    /// multicast, reserved, documentation, or cloud metadata. Null is blocked
    /// (fail closed).
    /// </summary>
    public static bool IsBlockedAddress(IPAddress? address)
    {
        var normalized = ClientAddress.Normalize(address);
        if (normalized is null)
        {
            return true;
        }

        var ranges = normalized.AddressFamily == AddressFamily.InterNetworkV6
            ? BlockedV6
            : BlockedV4;

        foreach (var network in ranges)
        {
            if (network.Contains(normalized))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True for <c>localhost</c>, bare or subdomain <c>*.localhost</c>,
    /// <c>*.local</c>, <c>*.internal</c>, and known container host names.
    /// </summary>
    public static bool IsBlockedHostName(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return true;
        }

        var normalized = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (normalized.Length == 0)
        {
            return true;
        }

        if (normalized == "localhost"
            || normalized.EndsWith(".localhost", StringComparison.Ordinal)
            || normalized.EndsWith(".local", StringComparison.Ordinal)
            || normalized.EndsWith(".internal", StringComparison.Ordinal))
        {
            return true;
        }

        return BlockedContainerHosts.Contains(normalized);
    }

    private static bool IsHttpScheme(string scheme) =>
        string.Equals(scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
        || string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Extracts the authority host from a raw URL without invoking
    /// <see cref="Uri"/> host canonicalization.
    /// </summary>
    private static bool TryExtractRawHost(string url, out string host)
    {
        host = string.Empty;
        var schemeSeparator = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparator < 0)
        {
            return false;
        }

        var start = schemeSeparator + 3;
        var end = url.Length;
        for (var index = start; index < url.Length; index++)
        {
            var character = url[index];
            if (character is '/' or '?' or '#')
            {
                end = index;
                break;
            }
        }

        var authority = url[start..end];
        var at = authority.LastIndexOf('@');
        if (at >= 0)
        {
            authority = authority[(at + 1)..];
        }

        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']');
            host = close < 0 ? authority : authority[1..close];
            return host.Length > 0;
        }

        var colon = authority.LastIndexOf(':');
        if (colon >= 0)
        {
            var tail = authority[(colon + 1)..];
            if (tail.Length > 0 && AllAsciiDigits(tail))
            {
                authority = authority[..colon];
            }
        }

        // A trailing dot marks a fully-qualified name; it is not an empty label.
        host = authority.TrimEnd('.');
        return host.Length > 0;
    }

    /// <summary>
    /// True when a raw authority looks like a non-canonical IPv4 literal:
    /// a single decimal/hex integer, fewer or more than four labels, a hex or
    /// leading-zero (octal) label, or an unparsable dotted form. Such hosts
    /// are rejected rather than treated as routable addresses.
    /// </summary>
    private static bool IsObfuscatedIpv4Literal(string host)
    {
        if (host.Length == 0 || host.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        var parts = host.Split('.');
        var allDigits = true;
        foreach (var part in parts)
        {
            if (part.Length == 0)
            {
                return true;
            }

            if (part.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!AllAsciiDigits(part))
            {
                allDigits = false;
            }
        }

        if (!allDigits)
        {
            return false;
        }

        if (parts.Length != 4)
        {
            return true;
        }

        foreach (var part in parts)
        {
            if (part.Length > 1 && part[0] == '0')
            {
                return true;
            }
        }

        return false;
    }

    private static bool AllAsciiDigits(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }
}
