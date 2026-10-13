using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace VuStudio.Agent;

/// <summary>
/// Agent settings. Sources, later ones winning: <c>appsettings.json</c>, environment variables prefixed
/// <c>VUSTUDIO_AGENT_</c> (<c>VUSTUDIO_AGENT_PORT</c>, <c>_TOKEN</c>, <c>_NAME</c>…) and command-line
/// switches (<c>--port 5342 --token abc --name Tower</c>).
/// </summary>
public sealed record AgentOptions
{
    public const int DefaultPort = 5342;
    public const string DefaultUnraidPath = "/var/local/emhttp";

    /// <summary>Name shown in VU Studio as the host of the sensors.</summary>
    public string Name { get; init; } = Environment.MachineName;

    public int Port { get; init; } = DefaultPort;

    /// <summary>Address to listen on. <c>0.0.0.0</c> by default; <c>127.0.0.1</c> to keep the agent local.</summary>
    public string Bind { get; init; } = "0.0.0.0";

    /// <summary>When set, every endpoint requires <c>Authorization: Bearer &lt;token&gt;</c>.</summary>
    public string? Token { get; init; }

    /// <summary>Advertise the agent over mDNS.</summary>
    public bool Mdns { get; init; } = true;

    /// <summary>Seconds between readings of the system sensors.</summary>
    public double IntervalSeconds { get; init; } = 1;

    public bool PerCore { get; init; } = true;

    public bool NvidiaSmi { get; init; } = true;

    /// <summary>Directory with Unraid's <c>var.ini</c> and <c>disks.ini</c>. Ignored when the files do not exist.</summary>
    public string UnraidPath { get; init; } = DefaultUnraidPath;

    public static AgentOptions From(IConfiguration configuration)
    {
        var defaults = new AgentOptions();
        var token = Text(configuration, "token");
        if (token is null && Text(configuration, "token-file") is { } file)
        {
            try
            {
                token = File.ReadAllText(file).Trim();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A secret that is not mounted: run without a token; the startup log says so.
            }
        }

        return new AgentOptions
        {
            Name = Text(configuration, "name") ?? defaults.Name,
            Port = Int(configuration, "port", defaults.Port, 1, 65535),
            Bind = Text(configuration, "bind") ?? defaults.Bind,
            Token = string.IsNullOrEmpty(token) ? null : token,
            Mdns = Bool(configuration, "mdns", defaults.Mdns),
            IntervalSeconds = Math.Clamp(Double(configuration, "interval", defaults.IntervalSeconds), 0.25, 60),
            PerCore = Bool(configuration, "per-core", defaults.PerCore),
            NvidiaSmi = Bool(configuration, "nvidia-smi", defaults.NvidiaSmi),
            UnraidPath = Text(configuration, "unraid-path") ?? defaults.UnraidPath,
        };
    }

    // Environment variables cannot contain dashes: VUSTUDIO_AGENT_PER_CORE is read as "per-core" too.
    private static string? Text(IConfiguration c, string key)
    {
        var value = c[key] ?? c[key.Replace('-', '_')];
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static int Int(IConfiguration c, string key, int fallback, int min, int max) =>
        int.TryParse(Text(c, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, min, max) : fallback;

    private static double Double(IConfiguration c, string key, double fallback) =>
        double.TryParse(Text(c, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : fallback;

    private static bool Bool(IConfiguration c, string key, bool fallback) => Text(c, key)?.ToLowerInvariant() switch
    {
        "1" or "true" or "yes" or "on" => true,
        "0" or "false" or "no" or "off" => false,
        _ => fallback,
    };
}
