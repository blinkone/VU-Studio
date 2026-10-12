using System.Net;
using VuStudio.Core.Config;

namespace VuStudio.Server.Security;

/// <summary>
/// Request gatekeeper:
/// <list type="bullet">
/// <item>DNS-rebinding protection: the Host header must be loopback, an IP literal, a machine name or an allowed host.</item>
/// <item>LAN clients need LAN access enabled and a login session (except for the login itself, the static UI and the push API).</item>
/// <item>CSRF protection: state-changing requests need <c>X-VuStudio: 1</c> and, when present, a same-origin <c>Origin</c>.</item>
/// <item>WebSocket upgrades from browsers must be same-origin.</item>
/// </list>
/// </summary>
public sealed class SecurityMiddleware(RequestDelegate next, ConfigStore config, AuthSessions sessions, ILogger<SecurityMiddleware> logger)
{
    public const string LocalItem = "vustudio.local";
    public const string CsrfHeader = "X-VuStudio";

    private static readonly HashSet<string> MachineNames = BuildMachineNames();

    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        var remote = context.Connection.RemoteIpAddress;
        var local = remote is null || IPAddress.IsLoopback(remote);
        context.Items[LocalItem] = local;

        var web = config.Current.Web;
        if (!IsAllowedHost(request.Host.Host, web))
        {
            logger.LogWarning("Rejected request with Host '{Host}' from {Remote}", request.Host.Host, remote);
            await Reject(context, StatusCodes.Status400BadRequest, "Invalid Host header.");
            return;
        }

        var path = request.Path.Value ?? "/";
        var isApi = path.StartsWith("/api", StringComparison.OrdinalIgnoreCase);
        var isPush = path.StartsWith("/api/push", StringComparison.OrdinalIgnoreCase);

        if (!local)
        {
            if (!web.AllowLan)
            {
                await Reject(context, StatusCodes.Status403Forbidden, "LAN access is disabled.");
                return;
            }

            var publicApi = path.StartsWith("/api/auth", StringComparison.OrdinalIgnoreCase) ||
                            path.Equals("/api/info", StringComparison.OrdinalIgnoreCase) || isPush;
            if (isApi && !publicApi && !sessions.IsValid(request.Cookies[AuthSessions.CookieName]))
            {
                await Reject(context, StatusCodes.Status401Unauthorized, "Login required.");
                return;
            }
        }

        var origin = request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin) && !IsSameOrigin(origin, request))
        {
            await Reject(context, StatusCodes.Status403Forbidden, "Cross-origin request rejected.");
            return;
        }

        var unsafeMethod = HttpMethods.IsPost(request.Method) || HttpMethods.IsPut(request.Method) ||
                           HttpMethods.IsDelete(request.Method) || HttpMethods.IsPatch(request.Method);
        if (isApi && unsafeMethod && !isPush && request.Headers[CsrfHeader] != "1")
        {
            await Reject(context, StatusCodes.Status403Forbidden, $"Missing {CsrfHeader} header.");
            return;
        }

        await next(context);
    }

    public static bool IsLocal(HttpContext context) => context.Items.TryGetValue(LocalItem, out var v) && v is true;

    private static bool IsAllowedHost(string host, WebSettings web)
    {
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        var h = host.Trim('[', ']').ToLowerInvariant();
        return h is "localhost" || IPAddress.TryParse(h, out _) || MachineNames.Contains(h) ||
               h.EndsWith(".localhost", StringComparison.Ordinal) ||
               web.AllowedHosts.Any(a => string.Equals(a.Trim(), h, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSameOrigin(string origin, HttpRequest request) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
        string.Equals(uri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);

    private static HashSet<string> BuildMachineNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? n)
        {
            if (!string.IsNullOrWhiteSpace(n))
            {
                names.Add(n.ToLowerInvariant());
                names.Add(n.ToLowerInvariant() + ".local");
                names.Add(n.ToLowerInvariant() + ".lan");
                names.Add(n.ToLowerInvariant() + ".home");
            }
        }

        Add(Environment.MachineName);
        try
        {
            Add(Dns.GetHostName());
        }
        catch (System.Net.Sockets.SocketException)
        {
        }

        return names;
    }

    private static Task Reject(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { error = message });
    }
}
