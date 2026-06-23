using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

namespace AttendanceMonitoring.Services;

/// <summary>
/// Resolves the client's IP address and (best-effort) computer name from
/// the current HTTP request. Used during login so the dashboard can show
/// which workstation a user signed in from.
/// </summary>
public static class ClientInfo
{
    /// <summary>Reverse-DNS lookups are capped at this many milliseconds.</summary>
    private const int ReverseDnsTimeoutMs = 600;

    /// <summary>Free geo lookup is capped at this many milliseconds.</summary>
    private const int GeoLookupTimeoutMs = 2000;

    /// <summary>
    /// In-process cache for recent IP→location lookups so repeated logins
    /// from the same address don't hit the upstream service each time.
    /// Bounded; entries are evicted opportunistically when the cache
    /// crosses <see cref="GeoCacheMaxEntries"/>.
    /// </summary>
    private static readonly Dictionary<string, string?> GeoCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object GeoCacheLock = new();
    private const int GeoCacheMaxEntries = 512;

    /// <summary>
    /// Shared HTTP client for geo lookups. .NET recommends a single
    /// long-lived <see cref="HttpClient"/> instance per service endpoint
    /// to avoid socket exhaustion.
    /// </summary>
    private static readonly HttpClient GeoHttp = new()
    {
        Timeout = TimeSpan.FromMilliseconds(GeoLookupTimeoutMs),
    };

    /// <summary>
    /// Returns (ip, host) for the current request. <paramref name="host"/>
    /// may be null when reverse DNS isn't available (typical on the public
    /// internet) or the request came from a tunnel/proxy without XFF.
    /// </summary>
    public static async Task<(string? Ip, string? Host)> ResolveAsync(HttpContext ctx)
    {
        var ip = GetClientIp(ctx);
        if (string.IsNullOrEmpty(ip)) return (null, null);

        var host = await TryReverseDnsAsync(ip);
        return (ip, host);
    }

    /// <summary>
    /// Returns a compact browser/OS label for the current request, parsed
    /// from the User-Agent header. Used at sign-in so the admin dashboard
    /// can differentiate two users who share a public IP because they're
    /// behind the same office wifi gateway. Browsers do NOT expose the
    /// local machine name or Windows username to the server \u2014 that is
    /// a deliberate browser security boundary, not a bug in this code.
    /// Returns null when no User-Agent was supplied.
    /// </summary>
    public static string? GetAgentLabel(HttpContext ctx)
    {
        var ua = ctx.Request.Headers.UserAgent.ToString();
        if (string.IsNullOrWhiteSpace(ua)) return null;
        return ParseAgentLabel(ua);
    }

    /// <summary>
    /// Heuristic User-Agent parser. Builds a short label like
    /// <c>"Chrome on Windows 11"</c>, <c>"Edge on macOS"</c>, or
    /// <c>"Safari on iOS"</c>. Falls back to the raw UA truncated to
    /// 96 chars when no pattern matches.
    /// </summary>
    internal static string ParseAgentLabel(string ua)
    {
        // ----- OS ----------
        string os;
        if (ua.Contains("Windows NT 11", StringComparison.Ordinal)) os = "Windows 11";
        else if (ua.Contains("Windows NT 10", StringComparison.Ordinal)) os = "Windows 10";
        else if (ua.Contains("Windows NT 6.3", StringComparison.Ordinal)) os = "Windows 8.1";
        else if (ua.Contains("Windows NT 6.2", StringComparison.Ordinal)) os = "Windows 8";
        else if (ua.Contains("Windows NT 6.1", StringComparison.Ordinal)) os = "Windows 7";
        else if (ua.Contains("Windows", StringComparison.Ordinal)) os = "Windows";
        else if (ua.Contains("Android", StringComparison.Ordinal)) os = "Android";
        else if (ua.Contains("iPhone", StringComparison.Ordinal) || ua.Contains("iPad", StringComparison.Ordinal)) os = "iOS";
        else if (ua.Contains("Mac OS X", StringComparison.Ordinal) || ua.Contains("Macintosh", StringComparison.Ordinal)) os = "macOS";
        else if (ua.Contains("Linux", StringComparison.Ordinal)) os = "Linux";
        else os = "Unknown OS";

        // ----- Browser ----------
        string browser;
        if (ua.Contains("Edg/", StringComparison.Ordinal)) browser = "Edge";
        else if (ua.Contains("OPR/", StringComparison.Ordinal) || ua.Contains("Opera", StringComparison.Ordinal)) browser = "Opera";
        else if (ua.Contains("Firefox/", StringComparison.Ordinal)) browser = "Firefox";
        else if (ua.Contains("Chrome/", StringComparison.Ordinal)) browser = "Chrome";
        else if (ua.Contains("Safari/", StringComparison.Ordinal)) browser = "Safari";
        else
        {
            var trimmed = ua.Length > 96 ? ua[..96] : ua;
            return trimmed;
        }

        return $"{browser} on {os}";
    }

    /// <summary>
    /// Best-effort geo lookup for <paramref name="ip"/>. Returns a
    /// short, human-readable location string (e.g. <c>"Quezon City, Philippines"</c>)
    /// or <c>null</c> when the IP is private/loopback, the upstream
    /// service is unreachable, or it returns no data. Caches results
    /// in process memory so we don't hammer the free tier.
    /// </summary>
    public static async Task<string?> ResolveLocationAsync(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return null;
        if (!IPAddress.TryParse(ip, out var addr)) return null;
        if (IsPrivateOrLocal(addr)) return null;

        lock (GeoCacheLock)
        {
            if (GeoCache.TryGetValue(ip, out var cached)) return cached;
        }

        string? location = null;
        try
        {
            // ip-api.com — free, no auth, 45 req/min, HTTP only on the
            // free tier (paid plan offers HTTPS). The call is server-to-
            // server so the user's browser never sees the cleartext hop.
            var url = $"http://ip-api.com/json/{Uri.EscapeDataString(ip)}"
                      + "?fields=status,country,regionName,city,message";

            using var cts = new CancellationTokenSource(GeoLookupTimeoutMs);
            var response = await GeoHttp.GetFromJsonAsync<IpApiResponse>(url, cts.Token);

            if (response is not null
                && string.Equals(response.Status, "success", StringComparison.OrdinalIgnoreCase))
            {
                var city = (response.City ?? string.Empty).Trim();
                var region = (response.RegionName ?? string.Empty).Trim();
                var country = (response.Country ?? string.Empty).Trim();

                // Prefer "City, Country"; fall back to "Region, Country"
                // when the city is missing (some carriers hide it). If we
                // only have a country, that alone is still useful.
                var primary = !string.IsNullOrEmpty(city)
                    ? city
                    : !string.IsNullOrEmpty(region) ? region : null;

                if (!string.IsNullOrEmpty(primary) && !string.IsNullOrEmpty(country))
                    location = $"{primary}, {country}";
                else if (!string.IsNullOrEmpty(primary))
                    location = primary;
                else if (!string.IsNullOrEmpty(country))
                    location = country;
            }
        }
        catch
        {
            // Swallow all errors — geo is purely cosmetic and must never
            // block sign-in. Network failures, DNS errors, JSON parse
            // problems all fall through to a null location.
            location = null;
        }

        lock (GeoCacheLock)
        {
            if (GeoCache.Count >= GeoCacheMaxEntries) GeoCache.Clear();
            GeoCache[ip] = location;
        }

        return location;
    }

    private static bool IsPrivateOrLocal(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // Link-local fe80::/10 + unique-local fc00::/7. Anything else
            // gets sent to the upstream service.
            var s = ip.ToString();
            return s.StartsWith("fe80", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("fc", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("fd", StringComparison.OrdinalIgnoreCase);
        }

        var bytes = ip.GetAddressBytes();
        if (bytes.Length != 4) return false;
        // 10.0.0.0/8
        if (bytes[0] == 10) return true;
        // 172.16.0.0/12
        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
        // 192.168.0.0/16
        if (bytes[0] == 192 && bytes[1] == 168) return true;
        // 169.254.0.0/16 — link-local
        if (bytes[0] == 169 && bytes[1] == 254) return true;
        return false;
    }

    private sealed class IpApiResponse
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("country")] public string? Country { get; set; }
        [JsonPropertyName("regionName")] public string? RegionName { get; set; }
        [JsonPropertyName("city")] public string? City { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
    }

    private sealed class NominatimAddress
    {
        [JsonPropertyName("city")]          public string? City { get; set; }
        [JsonPropertyName("town")]          public string? Town { get; set; }
        [JsonPropertyName("village")]       public string? Village { get; set; }
        [JsonPropertyName("municipality")]  public string? Municipality { get; set; }
        [JsonPropertyName("suburb")]        public string? Suburb { get; set; }
        [JsonPropertyName("county")]        public string? County { get; set; }
        [JsonPropertyName("state")]         public string? State { get; set; }
        [JsonPropertyName("country")]       public string? Country { get; set; }
    }

    private sealed class NominatimResponse
    {
        [JsonPropertyName("address")] public NominatimAddress? Address { get; set; }
        [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
    }

    /// <summary>
    /// Reverse-geocodes browser-supplied WGS-84 coordinates to a short
    /// "City, Country" label using OpenStreetMap Nominatim. Returns
    /// <c>null</c> when the upstream call fails or the coordinates land
    /// in the ocean / on un-tagged terrain. The Nominatim usage policy
    /// requires a descriptive <c>User-Agent</c>; we set one on every
    /// request. Cached results are reused for the same rounded lat/lon
    /// to stay well under the 1 req/s rate ceiling.
    /// </summary>
    public static async Task<string?> ReverseGeocodeAsync(double latitude, double longitude)
    {
        if (double.IsNaN(latitude) || double.IsNaN(longitude)) return null;
        if (latitude is < -90 or > 90 || longitude is < -180 or > 180) return null;

        // Bucket to ~100 m so two clicks from the same room reuse the cache.
        var cacheKey = $"geo:{latitude:F3},{longitude:F3}";
        lock (GeoCacheLock)
        {
            if (GeoCache.TryGetValue(cacheKey, out var cached)) return cached;
        }

        string? location = null;
        try
        {
            var url = "https://nominatim.openstreetmap.org/reverse"
                      + $"?lat={latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                      + $"&lon={longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                      + "&format=json&zoom=10&addressdetails=1";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd(
                "CSIPH-Attendance/1.0 (+https://csiph-attendance.onrender.com)");
            req.Headers.AcceptLanguage.ParseAdd("en");

            using var cts = new CancellationTokenSource(GeoLookupTimeoutMs);
            using var resp = await GeoHttp.SendAsync(req, cts.Token);
            if (resp.IsSuccessStatusCode)
            {
                var data = await resp.Content.ReadFromJsonAsync<NominatimResponse>(cancellationToken: cts.Token);
                var addr = data?.Address;
                if (addr is not null)
                {
                    var city = (addr.City
                                ?? addr.Town
                                ?? addr.Municipality
                                ?? addr.Village
                                ?? addr.Suburb
                                ?? addr.County
                                ?? addr.State
                                ?? string.Empty).Trim();
                    var country = (addr.Country ?? string.Empty).Trim();

                    if (!string.IsNullOrEmpty(city) && !string.IsNullOrEmpty(country))
                        location = $"{city}, {country}";
                    else if (!string.IsNullOrEmpty(city))
                        location = city;
                    else if (!string.IsNullOrEmpty(country))
                        location = country;
                }
            }
        }
        catch
        {
            // Geo is cosmetic; never propagate the error.
            location = null;
        }

        lock (GeoCacheLock)
        {
            if (GeoCache.Count >= GeoCacheMaxEntries) GeoCache.Clear();
            GeoCache[cacheKey] = location;
        }

        return location;
    }

    private static string? GetClientIp(HttpContext ctx)
    {
        // Dev Tunnels and reverse proxies inject the original client address
        // here. Take the left-most entry; that's the original requester.
        var xff = ctx.Request.Headers["X-Forwarded-For"].ToString();
        if (!string.IsNullOrWhiteSpace(xff))
        {
            var first = xff.Split(',', StringSplitOptions.RemoveEmptyEntries
                                     | StringSplitOptions.TrimEntries)
                           .FirstOrDefault();
            if (!string.IsNullOrEmpty(first)) return Normalize(first);
        }

        var addr = ctx.Connection.RemoteIpAddress;
        return addr is null ? null : Normalize(addr);
    }

    private static string Normalize(string raw)
    {
        // Strip IPv6 zone id (e.g. "fe80::1%eth0") and brackets.
        raw = raw.Trim().TrimStart('[').TrimEnd(']');
        var pct = raw.IndexOf('%');
        if (pct > 0) raw = raw[..pct];

        if (IPAddress.TryParse(raw, out var ip)) return Normalize(ip);
        return raw;
    }

    private static string Normalize(IPAddress ip)
    {
        // Map IPv4-in-IPv6 (::ffff:1.2.3.4) and loopbacks to plain IPv4 form
        // so the dashboard shows "127.0.0.1" rather than "::1".
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == AddressFamily.InterNetworkV6 && IPAddress.IsLoopback(ip))
            return "127.0.0.1";
        return ip.ToString();
    }

    private static async Task<string?> TryReverseDnsAsync(string ip)
    {
        if (!IPAddress.TryParse(ip, out var addr)) return null;

        try
        {
            var lookup = Dns.GetHostEntryAsync(addr);
            var winner = await Task.WhenAny(lookup, Task.Delay(ReverseDnsTimeoutMs));
            if (winner != lookup) return null; // timed out

            var entry = await lookup;
            var host = entry.HostName;
            if (string.IsNullOrWhiteSpace(host)) return null;

            // For machines on a Windows domain GetHostEntry often returns
            // "WORKSTATION.contoso.local" — keep the short name for display.
            var dot = host.IndexOf('.');
            var shortName = dot > 0 ? host[..dot] : host;

            // Skip useless results that just echo the IP back.
            if (string.Equals(shortName, ip, StringComparison.OrdinalIgnoreCase))
                return null;

            return shortName;
        }
        catch
        {
            return null;
        }
    }
}
