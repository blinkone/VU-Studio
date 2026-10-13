using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VuStudio.Session.Service;

public sealed record ServiceInfo(string Name, string Version, string? Machine);

public sealed record EngineStatus(long Revision, string? ActiveProfileId, string? ActiveProfileName, string? ManualProfileId, bool Paused);

public sealed record ProfileSummary(string Id, string Name, bool Enabled);

/// <summary>
/// The few REST calls the tray menu needs (docs/api.md). State-changing calls carry <c>X-VuStudio: 1</c>
/// (CSRF guard) and never an <c>Origin</c>. Loopback only, no proxy.
/// </summary>
public sealed class VuStudioApiClient : IDisposable
{
    public const string CsrfHeader = "X-VuStudio";

    private readonly HttpClient _http;
    private volatile Uri _baseUri;

    public VuStudioApiClient(Uri baseUri, HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        _baseUri = baseUri;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { UseProxy = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(3),
        };
    }

    public Uri BaseUri
    {
        get => _baseUri;
        set => _baseUri = value;
    }

    /// <summary><c>GET /api/info</c>; <c>null</c> when the service is unreachable or answers with an error.</summary>
    public async Task<ServiceInfo?> TryGetInfoAsync(CancellationToken ct = default)
    {
        try
        {
            var node = await GetJsonAsync("api/info", ct).ConfigureAwait(false);
            return node is JsonObject o
                ? new ServiceInfo(Str(o["name"]) ?? "VU Studio", Str(o["version"]) ?? "?", Str(o["machine"]))
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary><c>GET /api/state</c>, engine part only.</summary>
    public async Task<EngineStatus> GetEngineStatusAsync(CancellationToken ct = default)
    {
        var node = await GetJsonAsync("api/state", ct).ConfigureAwait(false) as JsonObject ?? throw new JsonException("Unexpected /api/state response.");
        var engine = node["engine"] as JsonObject;
        return new EngineStatus(
            node["revision"] is JsonValue r && r.TryGetValue<long>(out var revision) ? revision : 0,
            Str(engine?["activeProfileId"]),
            Str(engine?["activeProfileName"]),
            Str(engine?["manualProfileId"]),
            engine?["paused"] is JsonValue p && p.TryGetValue<bool>(out var paused) && paused);
    }

    /// <summary>Profiles from <c>GET /api/config</c>.</summary>
    public async Task<IReadOnlyList<ProfileSummary>> GetProfilesAsync(CancellationToken ct = default)
    {
        var node = await GetJsonAsync("api/config", ct).ConfigureAwait(false);
        if (node?["profiles"] is not JsonArray profiles)
        {
            return [];
        }

        return profiles
            .OfType<JsonObject>()
            .Select(p => new ProfileSummary(
                Str(p["id"]) ?? "",
                Str(p["name"]) ?? Str(p["id"]) ?? "",
                p["enabled"] is not JsonValue e || !e.TryGetValue<bool>(out var enabled) || enabled))
            .Where(p => p.Id.Length > 0)
            .ToList();
    }

    /// <summary><c>POST /api/engine/profile</c>; <c>null</c> = automatic.</summary>
    public Task SetProfileAsync(string? profileId, CancellationToken ct = default) =>
        PostAsync("api/engine/profile", new JsonObject { ["profileId"] = profileId }, ct);

    public Task SetPausedAsync(bool paused, CancellationToken ct = default) =>
        PostAsync("api/engine/pause", new JsonObject { ["paused"] = paused }, ct);

    public Task RefreshDialsAsync(CancellationToken ct = default) => PostAsync("api/engine/refresh", null, ct);

    public void Dispose() => _http.Dispose();

    private async Task<JsonNode?> GetJsonAsync(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(new Uri(_baseUri, path), ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonNode.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    private async Task PostAsync(string path, JsonObject? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, path));
        request.Headers.Add(CsrfHeader, "1");
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
