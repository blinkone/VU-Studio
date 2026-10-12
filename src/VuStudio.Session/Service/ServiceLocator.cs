using System.Globalization;
using System.Text.Json;

namespace VuStudio.Session.Service;

/// <summary>Where the service listens and where its configuration lives.</summary>
public static class ServiceLocator
{
    public const int DefaultPort = 5341;

    /// <summary>The Windows service name (<c>AppInfo.ServiceName</c> in the server).</summary>
    public const string ServiceName = "VUStudio";

    /// <summary>
    /// <c>VUSTUDIO_DATA</c> (same override as the service), else <c>%ProgramData%\VU Studio</c>.
    /// </summary>
    public static string DataDirectory(Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        return environment("VUSTUDIO_DATA") is { Length: > 0 } data
            ? data
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VU Studio");
    }

    /// <summary>
    /// Port resolution: <c>--port N</c>, then <c>VUSTUDIO_PORT</c>, then <c>web.port</c> of <c>config.json</c>, else 5341.
    /// The configuration may be unreadable for a standard user (its ACL can be limited to Administrators and
    /// SYSTEM); that falls back to the default.
    /// </summary>
    public static int ResolvePort(int? commandLinePort, Func<string, string?>? environment = null, string? configPath = null)
    {
        if (commandLinePort is { } cli && IsValidPort(cli))
        {
            return cli;
        }

        environment ??= Environment.GetEnvironmentVariable;
        if (int.TryParse(environment("VUSTUDIO_PORT"), NumberStyles.None, CultureInfo.InvariantCulture, out var env) && IsValidPort(env))
        {
            return env;
        }

        configPath ??= Path.Combine(DataDirectory(environment), "config.json");
        try
        {
            if (File.Exists(configPath) && ReadPort(File.ReadAllText(configPath)) is { } port)
            {
                return port;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return DefaultPort;
    }

    /// <summary>Reads <c>web.port</c> from config.json text; <c>null</c> when missing or invalid.</summary>
    public static int? ReadPort(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                TryGetProperty(doc.RootElement, "web", out var web) && web.ValueKind == JsonValueKind.Object &&
                TryGetProperty(web, "port", out var port))
            {
                var value = port.ValueKind switch
                {
                    JsonValueKind.Number when port.TryGetInt32(out var n) => n,
                    JsonValueKind.String when int.TryParse(port.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var s) => s,
                    _ => 0,
                };
                return IsValidPort(value) ? value : null;
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    public static Uri AppUri(int port) => new($"http://127.0.0.1:{port}/");

    private static bool IsValidPort(int port) => port is > 0 and <= 65535;

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}

/// <summary>Which URLs the embedded UI may navigate to; everything else opens in the default browser.</summary>
public static class NavigationPolicy
{
    /// <summary>True for <c>http://127.0.0.1|localhost|[::1]:{port}/…</c> of the service.</summary>
    public static bool IsAppUri(Uri? uri, Uri appBase)
    {
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttp || uri.Port != appBase.Port)
        {
            return false;
        }

        return uri.IsLoopback && (uri.Host == "127.0.0.1" || uri.Host == "localhost" || uri.Host == "[::1]");
    }

    /// <summary>Internal navigations WebView2 performs on its own (blank pages).</summary>
    public static bool IsBenign(Uri? uri) => uri is not null && uri.IsAbsoluteUri && uri.Scheme == "about";

    /// <summary>Schemes handed to the shell; anything else (file:, javascript:, custom protocols) is dropped.</summary>
    public static bool CanOpenExternally(Uri? uri) =>
        uri is not null && uri.IsAbsoluteUri && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeMailto);
}
