using VuStudio.Core.Providers;
using VuStudio.Sources.Common;

namespace VuStudio.Sources.Pings;

/// <summary>A host to ping.</summary>
/// <param name="Id">Local id segment: <c>{Id}/rtt</c>, <c>{Id}/loss</c>, <c>{Id}/up</c>.</param>
/// <param name="Host">Host name or IP address.</param>
/// <param name="Name">Label shown in the catalog.</param>
internal sealed record PingTarget(string Id, string Host, string Name);

/// <summary>The <c>ping</c> settings, parsed. Problems are collected in <see cref="Issues"/>.</summary>
internal sealed class PingSettings
{
    public const double DefaultIntervalSeconds = 5;
    public const int DefaultTimeoutMs = 1000;
    public const int DefaultWindow = 20;

    /// <summary>Id segments reserved for the gateway and the internet summary.</summary>
    public const string GatewayId = "gateway";
    public const string InternetId = "internet";

    public IReadOnlyList<PingTarget> Targets { get; private init; } = [];

    public bool IncludeGateway { get; private init; }

    public TimeSpan Interval { get; private init; }

    public TimeSpan Timeout { get; private init; }

    public int Window { get; private init; }

    public IReadOnlyList<string> Issues { get; private init; } = [];

    public static PingSettings From(ProviderSettings settings)
    {
        var issues = new List<string>();
        var used = new HashSet<string>(StringComparer.Ordinal) { GatewayId, InternetId };
        var targets = new List<PingTarget>();
        var row = 0;
        foreach (var entry in settings.GetTable("targets"))
        {
            row++;
            var host = NormalizeHost(entry.GetString("host"));
            if (host is null)
            {
                if (entry.GetString("name") is not null)
                {
                    issues.Add($"Target {row}: the host is empty.");
                }

                continue;
            }

            var id = LocalIds.Unique(LocalIds.Slug(host, "host"), used);
            targets.Add(new PingTarget(id, host, entry.GetString("name") ?? host));
        }

        return new PingSettings
        {
            Targets = targets,
            IncludeGateway = settings.GetBool("includeGateway", true),
            Interval = settings.GetSeconds("interval", DefaultIntervalSeconds),
            Timeout = TimeSpan.FromMilliseconds(Math.Clamp(settings.GetInt("timeoutMs", DefaultTimeoutMs), 50, 60_000)),
            Window = Math.Clamp(settings.GetInt("window", DefaultWindow), 1, 1000),
            Issues = issues,
        };
    }

    /// <summary>
    /// Reduces what the user typed to a host name or address: a pasted URL gives its host, and a trailing
    /// <c>:port</c> is dropped. Returns <c>null</c> for blank input.
    /// </summary>
    public static string? NormalizeHost(string? text)
    {
        var host = text?.Trim();
        if (string.IsNullOrEmpty(host))
        {
            return null;
        }

        if (host.Contains("://", StringComparison.Ordinal))
        {
            return Uri.TryCreate(host, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.DnsSafeHost : null;
        }

        var colon = host.IndexOf(':');
        if (colon > 0 && colon == host.LastIndexOf(':') && host[(colon + 1)..].All(char.IsAsciiDigit))
        {
            host = host[..colon]; // "example.com:8080"; IPv6 addresses have several colons and are left alone
        }

        return host.Length == 0 ? null : host;
    }
}
