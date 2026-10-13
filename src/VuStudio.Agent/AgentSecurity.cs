using System.Net;
using System.Security.Cryptography;

namespace VuStudio.Agent;

/// <summary>What <see cref="AgentSecurity.EnsureToken"/> did, for the startup log.</summary>
public enum TokenSource
{
    /// <summary>A token came from the settings.</summary>
    Configured,

    /// <summary>Listening on loopback only: no token needed.</summary>
    NotNeeded,

    /// <summary><c>VUSTUDIO_AGENT_INSECURE=1</c>: no token on purpose.</summary>
    Insecure,

    /// <summary>Read from the state file written on an earlier run.</summary>
    Stored,

    /// <summary>Generated now and saved in the state file.</summary>
    Generated,

    /// <summary>Generated now but the state file could not be written: it changes at every start.</summary>
    Ephemeral,
}

public sealed record TokenOutcome(AgentOptions Options, TokenSource Source, string? Path, string? Error = null);

/// <summary>Secure defaults of the agent: a token whenever it is reachable from the network, and a Host check.</summary>
public static class AgentSecurity
{
    private static readonly Lazy<HashSet<string>> MachineNames = new(BuildMachineNames);

    /// <summary>True when <paramref name="bind"/> only accepts connections from this machine.</summary>
    public static bool IsLoopbackBind(string bind)
    {
        var b = bind.Trim().Trim('[', ']');
        return string.Equals(b, "localhost", StringComparison.OrdinalIgnoreCase) ||
               (IPAddress.TryParse(b, out var ip) && IPAddress.IsLoopback(ip));
    }

    /// <summary>
    /// Where a generated token is kept: <c>$VUSTUDIO_AGENT_DATA/token</c> (Docker: <c>/data/token</c>), else
    /// <c>$STATE_DIRECTORY/token</c> (systemd <c>StateDirectory=</c>), else <c>$XDG_STATE_HOME/vustudio-agent/token</c>
    /// (default <c>~/.local/state</c>), on Windows <c>%LOCALAPPDATA%\vustudio-agent\token</c>.
    /// </summary>
    public static string TokenFile(AgentOptions options, Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        if (!string.IsNullOrWhiteSpace(options.DataDirectory))
        {
            return Path.Combine(options.DataDirectory, "token");
        }

        if (environment("STATE_DIRECTORY") is { Length: > 0 } systemd)
        {
            return Path.Combine(systemd.Split(':')[0], "token");
        }

        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "vustudio-agent", "token");
        }

        var state = environment("XDG_STATE_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
        return Path.Combine(state, "vustudio-agent", "token");
    }

    /// <summary>
    /// Makes sure an agent reachable from the network has a token: a configured one wins; otherwise the one saved on
    /// an earlier run; otherwise a new random one, saved (mode 600) so VU Studio keeps working across restarts.
    /// </summary>
    public static TokenOutcome EnsureToken(AgentOptions options, string tokenFile)
    {
        if (options.Token is not null)
        {
            return new TokenOutcome(options, TokenSource.Configured, null);
        }

        if (IsLoopbackBind(options.Bind))
        {
            return new TokenOutcome(options, TokenSource.NotNeeded, null);
        }

        if (options.Insecure)
        {
            return new TokenOutcome(options, TokenSource.Insecure, null);
        }

        try
        {
            if (File.Exists(tokenFile) && File.ReadAllText(tokenFile).Trim() is { Length: >= 16 } stored)
            {
                return new TokenOutcome(options with { Token = stored }, TokenSource.Stored, tokenFile);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Fall through: a new token is generated (and the write below reports the problem).
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(tokenFile)!);
            var fileOptions = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (var writer = new StreamWriter(tokenFile, fileOptions))
            {
                writer.Write(token);
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(tokenFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            return new TokenOutcome(options with { Token = token }, TokenSource.Generated, tokenFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new TokenOutcome(options with { Token = token }, TokenSource.Ephemeral, tokenFile, ex.Message);
        }
    }

    /// <summary>
    /// DNS-rebinding protection: the Host header (without port) must be an IP literal, <c>localhost</c>, this machine's
    /// name (also with <c>.local</c>, <c>.lan</c>, <c>.home</c>), any <c>*.local</c> mDNS name or a configured extra name.
    /// </summary>
    public static bool IsAllowedHost(string? host, IReadOnlyCollection<string> extra, IReadOnlySet<string>? machineNames = null)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var h = host.Trim().Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        return IPAddress.TryParse(h, out _) ||
               h == "localhost" ||
               (h.EndsWith(".local", StringComparison.Ordinal) && h.Length > ".local".Length) ||
               (machineNames ?? MachineNames.Value).Contains(h) ||
               extra.Any(e => string.Equals(e.Trim().TrimEnd('.'), h, StringComparison.OrdinalIgnoreCase));
    }

    internal static HashSet<string> BuildMachineNames(params string?[] names)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var n = name.Trim().ToLowerInvariant();
            set.Add(n);
            set.Add(n + ".lan");
            set.Add(n + ".home");
        }

        return set;
    }

    private static HashSet<string> BuildMachineNames()
    {
        string? dns = null;
        try
        {
            dns = Dns.GetHostName();
        }
        catch (System.Net.Sockets.SocketException)
        {
        }

        return BuildMachineNames(Environment.MachineName, dns);
    }
}
