using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using VuStudio.Core.Config;
using VuStudio.Core.Engine;
using VuStudio.Core.Faces;
using VuStudio.Core.History;
using VuStudio.Core.Lighting;
using VuStudio.Core.Mapping;
using VuStudio.Core.Profiles;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Server.Hosting;
using VuStudio.Server.Live;
using VuStudio.Server.Security;
using VuStudio.Sources.Push;

namespace VuStudio.Server.Api;

public sealed record FacePreviewRequest(int Slot, DialPage Page, int? Scale, string? Tint);

public sealed record ProfileRequest(string? ProfileId);

public sealed record PauseRequest(bool Paused);

public sealed record AdvanceRequest(int Delta);

public sealed record RescanRequest(bool Provision);

public sealed record RawRequest(int Raw);

public sealed record CalibrateRequest(CalibrationPoint Point, int Raw);

public sealed record PasswordRequest(string Password);

public sealed record PushItem(string Id, double? Value, string? Text, string? Name, string? Unit, SensorCategory? Category, double? Min, double? Max, string? Icon);

public sealed record PushRequest(List<PushItem>? Sensors);

public sealed record TrackRequest(string? Sensor);

/// <summary>Maps every <c>/api</c> endpoint (see docs/api.md).</summary>
public static partial class Endpoints
{
    public static void MapVuStudioApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/info", (HttpContext http, RuntimeOptions runtime, AppPaths paths) => new
        {
            name = AppInfo.Name,
            version = AppInfo.Version,
            os = Environment.OSVersion.VersionString,
            machine = Environment.MachineName,
            dataDirectory = SecurityMiddleware.IsLocal(http) ? paths.Root : null,
            simulate = runtime.Simulate,
            local = SecurityMiddleware.IsLocal(http),
        });

        api.MapGet("/state", (StateBuilder state) => state.Build());

        MapConfig(api);
        MapSensors(api);
        MapFaces(api);
        MapControl(api);
        MapHistory(api);
        MapAssets(api);
        MapAuth(api);
        MapPush(api);
        MapDiscovery(api);

        app.Map("/api/live", async (HttpContext http, LiveHub hub) =>
        {
            if (!http.WebSockets.IsWebSocketRequest)
            {
                return Results.BadRequest();
            }

            using var socket = await http.WebSockets.AcceptWebSocketAsync();
            await hub.HandleAsync(socket, http.RequestAborted);
            return Results.Empty;
        });

        app.Map("/api/session", async (HttpContext http, SessionChannel channel) =>
        {
            // Only the local desktop app (a non-browser client: no Origin header) may use this channel.
            if (!http.WebSockets.IsWebSocketRequest || !SecurityMiddleware.IsLocal(http) || http.Request.Headers.Origin.Count > 0)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            using var socket = await http.WebSockets.AcceptWebSocketAsync();
            await channel.HandleAsync(socket, http.RequestAborted);
            return Results.Empty;
        });
    }

    /// <summary>
    /// Unknown <c>/api/…</c> paths answer 404 instead of falling through to the SPA's <c>index.html</c> with 200.
    /// Call before <c>MapFallbackToFile</c>; the catch-all has a lower precedence than every real API route.
    /// </summary>
    public static void MapUnknownApi(this IEndpointRouteBuilder app) =>
        app.Map("/api/{**rest}", () => Results.NotFound(new { error = "Unknown API endpoint." }));

    private static void MapConfig(RouteGroupBuilder api)
    {
        api.MapGet("/config", (ConfigService service) => service.GetMasked());

        api.MapPut("/config", (HttpContext http, AppConfig config, ConfigService service) =>
        {
            long? baseRevision = long.TryParse(http.Request.Headers.IfMatch.ToString().Trim('"'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : null;
            try
            {
                return Results.Ok(service.Save(config, baseRevision, SecurityMiddleware.IsLocal(http)));
            }
            catch (ConfigConflictException ex)
            {
                return Results.Conflict(new { error = ex.Message, revision = ex.Actual });
            }
            catch (ConfigValidationException ex)
            {
                return Results.BadRequest(new { errors = ex.Errors });
            }
            catch (ConfigForbiddenException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
            }
        });
    }

    private static void MapSensors(RouteGroupBuilder api)
    {
        api.MapGet("/sensors", (SensorHub hub) => hub.GetAll().Select(s => new
        {
            id = s.Id,
            name = s.Info.Name,
            device = s.Info.Device,
            category = s.Info.Category,
            kind = s.Info.Kind,
            unit = s.Info.Unit,
            valueKind = s.Info.ValueKind,
            min = s.Info.Min,
            max = s.Info.Max,
            icon = s.Info.Icon,
            host = s.Info.Host,
            providerId = s.Info.ProviderId,
            value = s.Value,
            text = s.Text,
            updated = s.Updated,
            stale = s.IsStale,
        }));

        api.MapGet("/providers", (ProviderManager providers) => new
        {
            types = providers.Descriptors.Select(d => new { descriptor = d, supported = d.IsSupportedHere }),
            status = providers.GetStatus(),
        });

        api.MapGet("/units/{unit}", (string unit) => UnitConverter.CompatibleUnits(Uri.UnescapeDataString(unit)));
    }

    private static void MapFaces(RouteGroupBuilder api)
    {
        api.MapGet("/catalog/themes", (IFaceRenderer renderer) => renderer.Themes);
        api.MapGet("/catalog/fonts", (IFaceRenderer renderer) => renderer.Fonts);
        api.MapGet("/catalog/icons", (IFaceRenderer renderer) => renderer.Icons);

        api.MapGet("/dials/{slot:int}/face.png", (int slot, int? scale, bool? tint, DialEngine engine, IFaceRenderer renderer) =>
        {
            var face = engine.GetFace(slot);
            if (face is null)
            {
                return Results.NotFound();
            }

            RgbwColor? color = null;
            if (tint == true && engine.Snapshot.Dials.FirstOrDefault(d => d.Slot == slot) is { } dial)
            {
                color = dial.Light.IsOff ? new RgbwColor(60, 60, 60) : dial.Light.ToPreviewColor();
            }

            var png = renderer.EncodePng(face, Math.Clamp(scale ?? 1, 1, 8), color);
            return Results.File(png, "image/png");
        });

        api.MapPost("/faces/preview", (FacePreviewRequest request, DialEngine engine, IFaceRenderer renderer) =>
        {
            var context = engine.CreateContext(request.Slot, request.Page);
            var document = renderer.BuildDocument(request.Page.Face, context);
            var bitmap = renderer.Render(document, context);
            RgbwColor? tint = RgbwColor.TryParse(request.Tint, out var t) ? t : null;
            return Results.File(renderer.EncodePng(bitmap, Math.Clamp(request.Scale ?? 3, 1, 8), tint), "image/png");
        });

        api.MapPost("/faces/document", (FacePreviewRequest request, DialEngine engine, IFaceRenderer renderer) =>
        {
            var context = engine.CreateContext(request.Slot, request.Page);
            var face = Json.Clone(request.Page.Face);
            face.Custom = null;
            return Results.Json(renderer.BuildDocument(face, context), Json.Compact);
        });
    }

    private static void MapControl(RouteGroupBuilder api)
    {
        api.MapPost("/engine/profile", (ProfileRequest request, DialEngine engine) =>
        {
            engine.SetManualProfile(string.IsNullOrWhiteSpace(request.ProfileId) ? null : request.ProfileId);
            return Results.NoContent();
        });

        api.MapPost("/engine/pause", (PauseRequest request, DialEngine engine) =>
        {
            engine.SetPaused(request.Paused);
            return Results.NoContent();
        });

        api.MapPost("/engine/slots/{slot:int}/advance", (int slot, AdvanceRequest request, DialEngine engine) =>
        {
            engine.Advance(slot, Math.Sign(request.Delta) == 0 ? 1 : Math.Sign(request.Delta));
            return Results.NoContent();
        });

        api.MapPost("/engine/refresh", (DialEngine engine) =>
        {
            engine.Refresh();
            return Results.NoContent();
        });

        api.MapPost("/hardware/rescan", async (RescanRequest request, IDialOutput output, CancellationToken ct) =>
        {
            await output.RescanAsync(request.Provision, ct);
            return Results.NoContent();
        });

        api.MapPost("/hardware/dials/{uid}/identify", async (string uid, IDialOutput output, CancellationToken ct) =>
        {
            await output.IdentifyAsync(uid, ct);
            return Results.NoContent();
        });

        api.MapPost("/hardware/dials/{uid}/raw", async (string uid, RawRequest request, IDialOutput output, CancellationToken ct) =>
            await output.SetRawAsync(uid, request.Raw, ct) ? Results.NoContent() : Results.Problem("The dial did not accept the value."));

        api.MapPost("/hardware/dials/{uid}/calibrate", async (string uid, CalibrateRequest request, IDialOutput output, CancellationToken ct) =>
            await output.CalibrateAsync(uid, request.Point, request.Raw, ct) ? Results.NoContent() : Results.Problem("Calibration failed."));
    }

    private static void MapHistory(RouteGroupBuilder api)
    {
        api.MapGet("/history", (string sensor, DateTimeOffset? from, DateTimeOffset? to, HistoryRecorder history) =>
        {
            var end = to ?? DateTimeOffset.UtcNow;
            var start = from ?? end.AddHours(-24);
            return history.Query(sensor, start, end).Select(a => new { minute = a.Minute, min = a.Min, avg = a.Avg, max = a.Max, count = a.Count });
        });

        api.MapGet("/history/recent", (string sensor, int? seconds, HistoryRecorder history) =>
        {
            var window = TimeSpan.FromSeconds(Math.Clamp(seconds ?? 900, 10, 3600));
            return history.GetRecent(sensor, window).Select(p => new { time = p.Time, value = p.Value });
        });

        api.MapPost("/history/track", Track);
    }

    /// <summary>
    /// Starts recording a sensor that is not in the configured set (the UI calls it when it opens a chart). A POST,
    /// so it carries the CSRF header; unknown sensors are refused and the set is capped (<see cref="HistoryRecorder.MaxTracked"/>).
    /// </summary>
    internal static IResult Track(TrackRequest request, HistoryRecorder history, SensorHub hub)
    {
        if (string.IsNullOrWhiteSpace(request.Sensor) || !hub.TryGet(request.Sensor, out _))
        {
            return Results.NotFound(new { error = "Unknown sensor." });
        }

        return history.Track(request.Sensor)
            ? Results.NoContent()
            : Results.Json(new { error = $"Too many sensors are being recorded (limit {HistoryRecorder.MaxTracked})." }, statusCode: StatusCodes.Status429TooManyRequests);
    }

    private static void MapAssets(RouteGroupBuilder api)
    {
        api.MapGet("/assets", (IAssetStore assets) => assets.List());

        api.MapPost("/assets", async (HttpRequest request, IAssetStore assets) =>
        {
            if (!request.HasFormContentType)
            {
                return Results.BadRequest(new { error = "Send the image as multipart/form-data field 'file'." });
            }

            var form = await request.ReadFormAsync();
            if (form.Files.GetFile("file") is not { } file)
            {
                return Results.BadRequest(new { error = "Missing 'file'." });
            }

            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer);
            try
            {
                return Results.Ok(new { id = assets.Add(buffer.ToArray(), file.FileName) });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).DisableAntiforgery();

        api.MapGet("/assets/{id}", (string id, IAssetStore assets) =>
            assets.Get(id) is { } bytes ? Results.File(bytes, "application/octet-stream") : Results.NotFound());

        api.MapDelete("/assets/{id}", (string id, IAssetStore assets) =>
            assets.Delete(id) ? Results.NoContent() : Results.NotFound());
    }

    private static void MapAuth(RouteGroupBuilder api)
    {
        api.MapGet("/auth", (HttpContext http, ConfigStore config, AuthSessions sessions) => new
        {
            local = SecurityMiddleware.IsLocal(http),
            authenticated = SecurityMiddleware.IsLocal(http) || sessions.IsValid(http.Request.Cookies[AuthSessions.CookieName]),
            lanEnabled = config.Current.Web.AllowLan,
            passwordSet = config.Current.Web.PasswordHash is not null,
        });

        api.MapPost("/auth/login", (HttpContext http, PasswordRequest request, ConfigStore config, AuthSessions sessions) =>
        {
            var client = http.Connection.RemoteIpAddress?.ToString() ?? "?";
            if (sessions.IsLockedOut(client))
            {
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            }

            if (!PasswordHasher.Verify(request.Password, config.Current.Web.PasswordHash))
            {
                sessions.RecordFailure(client);
                return Results.Unauthorized();
            }

            sessions.RecordSuccess(client);
            http.Response.Cookies.Append(AuthSessions.CookieName, sessions.Create(), new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                IsEssential = true,
                MaxAge = TimeSpan.FromDays(30),
            });
            return Results.NoContent();
        });

        api.MapPost("/auth/logout", (HttpContext http, AuthSessions sessions) =>
        {
            sessions.Revoke(http.Request.Cookies[AuthSessions.CookieName]);
            http.Response.Cookies.Delete(AuthSessions.CookieName);
            return Results.NoContent();
        });

        api.MapPost("/auth/password", (HttpContext http, PasswordRequest request, ConfigStore config, AuthSessions sessions) =>
        {
            if (!SecurityMiddleware.IsLocal(http))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (string.IsNullOrEmpty(request.Password))
            {
                config.Update(c =>
                {
                    c.Web.PasswordHash = null;
                    c.Web.AllowLan = false;
                });
            }
            else if (request.Password.Length < 8)
            {
                return Results.BadRequest(new { error = "Use at least 8 characters." });
            }
            else
            {
                var hash = PasswordHasher.Hash(request.Password);
                config.Update(c => c.Web.PasswordHash = hash);
            }

            sessions.RevokeAll();
            return Results.NoContent();
        });
    }

    /// <summary>
    /// The push token is accepted only in <c>Authorization: Bearer</c>: a token in the query string ends up in logs,
    /// proxies and browser history. Compared in constant time.
    /// </summary>
    internal static bool IsPushAuthorized(HttpRequest request, string? expected)
    {
        var header = request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : "";
        return !string.IsNullOrEmpty(expected) && !string.IsNullOrEmpty(token) &&
               System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                   System.Text.Encoding.UTF8.GetBytes(token), System.Text.Encoding.UTF8.GetBytes(expected));
    }

    internal static void MapDiscovery(RouteGroupBuilder api)
    {
        api.MapGet("/discovery/agents", async (AgentDiscoveryService discovery, ConfigStore config, CancellationToken ct) =>
        {
            var result = await discovery.GetAsync(ct);
            var added = await AgentDiscoveryService.AddedEndpointsAsync(config.Current.Providers, ct);
            return new
            {
                agents = result.Agents.Select(a => new
                {
                    name = a.Name,
                    host = a.Addresses.Count > 0 ? a.Addresses[0] : "",
                    port = a.Port,
                    addresses = a.Addresses,
                    version = a.Version,
                    alreadyAdded = a.Addresses.Any(address => added.Contains((address, a.Port))),
                }),
                message = result.Message,
            };
        });
    }

    private static void MapPush(RouteGroupBuilder api)
    {
        api.MapPost("/push", (HttpContext http, PushRequest request, ConfigStore config, PushGateway gateway) =>
        {
            if (!IsPushAuthorized(http.Request, config.Current.Web.ApiToken))
            {
                return Results.Unauthorized();
            }

            var errors = new List<string>();
            foreach (var item in request.Sensors ?? [])
            {
                try
                {
                    var meta = item.Name is null && item.Unit is null && item.Category is null && item.Min is null && item.Max is null && item.Icon is null
                        ? null
                        : new PushSensorMeta(item.Name, item.Unit, item.Category, item.Min, item.Max, item.Icon);
                    gateway.Push(item.Id ?? "", item.Value, item.Text, meta);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                {
                    errors.Add($"{item.Id}: {ex.Message}");
                }
            }

            return errors.Count == 0 ? Results.NoContent() : Results.BadRequest(new { errors });
        });
    }
}
