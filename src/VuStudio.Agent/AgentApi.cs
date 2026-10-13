using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using VuStudio.Core.Config;
using VuStudio.Core.Sensors;

namespace VuStudio.Agent;

/// <summary>Answer of <c>GET /api/v1/info</c>.</summary>
public sealed record AgentInfo(string Name, string Version, string Os, string Hostname, double UptimeSeconds);

/// <summary>
/// Answer of <c>GET /api/v1/sensors</c>. <see cref="Values"/> is keyed by sensor id: a number for numeric
/// sensors, a string for text and color sensors, <c>null</c> when there is no fresh value.
/// </summary>
public sealed record AgentSensorsResponse(
    string Name,
    string Version,
    DateTimeOffset Time,
    string CatalogVersion,
    IReadOnlyList<SensorInfo> Sensors,
    IReadOnlyDictionary<string, object?> Values);

/// <summary>The agent's HTTP surface (docs/api.md, "Agent API").</summary>
public static class AgentApi
{
    public static string Version { get; } =
        typeof(AgentApi).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public static AgentSensorsResponse BuildSensors(SensorHub hub, string name, DateTimeOffset now)
    {
        var snapshots = hub.GetAll();
        var values = new Dictionary<string, object?>(snapshots.Count, StringComparer.Ordinal);
        var sensors = new List<SensorInfo>(snapshots.Count);
        foreach (var snapshot in snapshots)
        {
            // The client sets Host itself; the provider instance id is meaningless on another machine.
            sensors.Add(snapshot.Info with { ProviderId = "", Host = null });
            values[snapshot.Id] = Value(snapshot);
        }

        return new AgentSensorsResponse(name, Version, now, hub.CatalogVersion.ToString(CultureInfo.InvariantCulture), sensors, values);
    }

    private static object? Value(SensorSnapshot snapshot)
    {
        if (snapshot.IsStale)
        {
            return null;
        }

        return snapshot.Info.ValueKind == SensorValueKind.Number
            ? snapshot.Value is { } v && double.IsFinite(v) ? v : null
            : snapshot.Text;
    }

    public static AgentInfo BuildInfo(string name) => new(
        name,
        Version,
        RuntimeInformation.OSDescription,
        Environment.MachineName,
        Math.Round(Environment.TickCount64 / 1000.0, 1));

    /// <summary>True when the header carries the token (or no token is required). Compared in constant time.</summary>
    public static bool IsAuthorized(string? token, string? authorizationHeader)
    {
        if (string.IsNullOrEmpty(token))
        {
            return true;
        }

        const string prefix = "Bearer ";
        if (authorizationHeader is null || !authorizationHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var given = Encoding.UTF8.GetBytes(authorizationHeader[prefix.Length..].Trim());
        var expected = Encoding.UTF8.GetBytes(token);
        return given.Length == expected.Length && CryptographicOperations.FixedTimeEquals(given, expected);
    }

    public static void Map(WebApplication app, AgentOptions options, SensorHub hub, TimeProvider time)
    {
        app.Use(async (context, next) =>
        {
            if (!AgentSecurity.IsAllowedHost(context.Request.Host.Host, options.AllowedHosts))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("Invalid Host header. Add the name to VUSTUDIO_AGENT_ALLOWED_HOSTS if it is yours.");
                return;
            }

            if (!IsAuthorized(options.Token, context.Request.Headers.Authorization.ToString()))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Bearer";
                return;
            }

            await next();
        });

        app.MapGet("/api/v1/info", () => Results.Json(BuildInfo(options.Name), Json.Compact));
        app.MapGet("/api/v1/sensors", () => Results.Json(BuildSensors(hub, options.Name, time.GetUtcNow()), Json.Compact));
    }
}
