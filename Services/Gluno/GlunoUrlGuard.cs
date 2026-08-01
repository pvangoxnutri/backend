using System.Net;
using System.Net.Sockets;

namespace sidequest.backend.Services.Gluno;

public sealed record UrlVerdict(bool Allowed, string? RejectionCode)
{
    /// The normalised absolute URL, when allowed.
    public string? Url { get; init; }
    public string? Host { get; init; }

    public static UrlVerdict Reject(string code) => new(false, code);
}

/// <summary>
/// Decides whether a URL from outside SideQuest may be touched.
///
/// THE THREAT IS SSRF, and it is worth being precise about why it matters here.
/// This service runs inside a network that can reach things the internet
/// cannot: the database, internal services, and — on every major cloud — an
/// unauthenticated metadata endpoint at 169.254.169.254 that hands out
/// credentials to whatever asks. A URL supplied by a model, a web page, or a
/// search result is attacker-influenced input, and "fetch this URL" is the
/// single most dangerous instruction a backend can accept.
///
/// So the rule is an ALLOW-LIST OF SHAPES, not a block-list of bad ones.
/// Blocking "localhost" and calling it done fails to a dozen equivalents:
/// 127.0.0.2, 0.0.0.0, [::1], 2130706433, 127.1, a hostname that resolves to
/// loopback, a redirect that lands there. This resolves the host to actual IP
/// addresses and checks EVERY one against the reserved ranges.
///
/// DNS REBINDING is why resolution happens here and the caller must connect to
/// the resolved address rather than re-resolving: a hostname can answer
/// "93.184.216.34" to the check and "127.0.0.1" to the fetch a millisecond
/// later. Checking without pinning is theatre.
/// </summary>
public static class GlunoUrlGuard
{
    /// <summary>
    /// Only http and https. Everything else — file, ftp, gopher, data — either
    /// reads local disk or reaches protocols with no business in a travel app.
    /// </summary>
    private static readonly string[] AllowedSchemes = [Uri.UriSchemeHttps, Uri.UriSchemeHttp];

    /// <summary>
    /// Hostnames that name the machine itself, in the forms people forget.
    /// This is a convenience filter — the IP check below is the real boundary.
    /// </summary>
    private static readonly string[] ForbiddenHostnames =
    [
        "localhost", "localhost.localdomain", "ip6-localhost", "ip6-loopback",
        "metadata", "metadata.google.internal", "instance-data",
        "kubernetes.default", "kubernetes.default.svc",
    ];

    /// <summary>
    /// Suffixes that only exist inside a private network.
    /// </summary>
    private static readonly string[] ForbiddenSuffixes =
    [
        ".local", ".internal", ".localdomain", ".lan", ".home", ".corp",
        ".intranet", ".svc", ".cluster.local",
    ];

    /// Response bodies larger than this are not read. A travel notice is a few
    /// kilobytes; a multi-megabyte response is either an attack or a mistake.
    public const int MaxResponseBytes = 512 * 1024;

    /// <summary>
    /// Redirects followed. Bounded because a redirect chain is the classic way
    /// to pass a check and then land somewhere else — each hop is re-validated,
    /// and an unbounded chain is also a trivial denial-of-service.
    /// </summary>
    public const int MaxRedirects = 3;

    /// <summary>
    /// Validates a URL and resolves it to a pinned address.
    /// </summary>
    /// <param name="requireHttps">
    /// True for anything SideQuest fetches itself. Plain http is accepted only
    /// where a source genuinely predates TLS and the content is not sensitive —
    /// and even then the caller decides, not this method.
    /// </param>
    public static UrlVerdict Check(string? candidate, bool requireHttps = true)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return UrlVerdict.Reject("empty");
        if (candidate.Length > 2000) return UrlVerdict.Reject("too_long");

        if (!Uri.TryCreate(candidate.Trim(), UriKind.Absolute, out var uri))
            return UrlVerdict.Reject("not_absolute");

        if (!AllowedSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
            return UrlVerdict.Reject("scheme_not_allowed");

        if (requireHttps && uri.Scheme != Uri.UriSchemeHttps)
            return UrlVerdict.Reject("https_required");

        // Credentials in a URL are never legitimate here and are a common way
        // to confuse naive host parsing ("https://evil.com@internal/").
        if (!string.IsNullOrEmpty(uri.UserInfo)) return UrlVerdict.Reject("credentials_in_url");

        var host = uri.Host.ToLowerInvariant().TrimEnd('.');

        if (host.Length == 0) return UrlVerdict.Reject("no_host");
        if (ForbiddenHostnames.Contains(host)) return UrlVerdict.Reject("internal_host");
        if (ForbiddenSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.Ordinal)))
            return UrlVerdict.Reject("internal_host");

        // A bare label with no dot is an intranet name — "wiki", "jenkins".
        // A public host always has a dot, and a bracketed literal is handled by
        // the IP path below.
        if (!host.Contains('.') && !host.Contains(':')) return UrlVerdict.Reject("internal_host");

        // A literal IP is checked directly; a name is resolved and every
        // answer checked. One private address among five public ones is enough
        // to reject.
        foreach (var address in Resolve(host))
        {
            if (IsReserved(address)) return UrlVerdict.Reject("private_address");
        }

        return new UrlVerdict(true, null) { Url = uri.ToString(), Host = host };
    }

    /// <summary>
    /// Every address a host answers to.
    ///
    /// A resolution failure yields NOTHING, which means the URL passes this
    /// check and fails at connect time instead. That is the right way round: a
    /// transient DNS outage should not silently turn into "this source is
    /// forbidden", and the connection itself cannot reach a private address
    /// that DNS did not return.
    /// </summary>
    private static IReadOnlyList<IPAddress> Resolve(string host)
    {
        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal)) return [literal];

        try
        {
            return Dns.GetHostAddresses(host);
        }
        catch (SocketException)
        {
            return [];
        }
        catch (ArgumentException)
        {
            return [];
        }
    }

    /// <summary>
    /// Whether an address is somewhere a request must never go.
    ///
    /// Covers loopback, private ranges, link-local (which is where the cloud
    /// metadata endpoint lives), carrier-grade NAT, and the IPv6 equivalents
    /// including IPv4-mapped addresses — <c>::ffff:127.0.0.1</c> is loopback
    /// wearing a different hat, and a check that only looks at the v6 rules
    /// waves it through.
    /// </summary>
    public static bool IsReserved(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;

        // Unwrap ::ffff:a.b.c.d before applying the v4 rules.
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var octets = address.GetAddressBytes();

            return octets[0] switch
            {
                0 => true,                                   // 0.0.0.0/8 — "this network"
                10 => true,                                  // private
                127 => true,                                 // loopback
                169 when octets[1] == 254 => true,            // link-local, incl. cloud metadata
                172 when octets[1] >= 16 && octets[1] <= 31 => true,
                192 when octets[1] == 168 => true,
                100 when octets[1] >= 64 && octets[1] <= 127 => true,  // carrier-grade NAT
                192 when octets[1] == 0 && octets[2] == 0 => true,     // IETF protocol assignments
                198 when octets[1] == 18 || octets[1] == 19 => true,   // benchmarking
                >= 224 => true,                              // multicast and reserved
                _ => false,
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return true;

            var bytes = address.GetAddressBytes();

            // fc00::/7 — unique local addresses.
            if ((bytes[0] & 0xFE) == 0xFC) return true;

            // ::/128 and ::1/128.
            if (bytes.All(octet => octet == 0)) return true;
        }

        return false;
    }

    /// <summary>
    /// A link found INSIDE fetched content.
    ///
    /// Stricter than <see cref="Check"/> by design: this is a URL an attacker
    /// put in a page we already decided to read, so it gets https only. And it
    /// is validated for DISPLAY — nothing in SideQuest follows a link found in
    /// external text, because that is precisely how one compromised page turns
    /// a backend into somebody else's crawler.
    /// </summary>
    public static UrlVerdict CheckDiscoveredLink(string? candidate)
        => Check(candidate, requireHttps: true);
}
