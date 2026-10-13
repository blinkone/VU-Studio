using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using VuStudio.Core.Providers;
using VuStudio.Sources.Common;

namespace VuStudio.Sources.Remote;

/// <summary>A source that cannot work until the user fixes something (bad credentials, missing setting, unsupported device).</summary>
internal sealed class SourceUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The remote side asked us to slow down.</summary>
internal sealed class RateLimitedException(string message, TimeSpan retryAfter) : Exception(message)
{
    public TimeSpan RetryAfter { get; } = retryAfter;
}

/// <summary>A fully read HTTP response.</summary>
internal sealed record NetResponse(HttpStatusCode Status, byte[] Body, HttpResponseHeaders Headers, Uri RequestUri)
{
    public bool IsSuccess => (int)Status is >= 200 and < 300;

    public string Text => Encoding.UTF8.GetString(Body.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? Body.AsSpan(3) : Body);

    public JsonNode? Json() => HttpJson.Parse(Body);
}

/// <summary>HTTP helpers shared by the network providers: client creation, bounded reads, readable errors.</summary>
internal static class NetHttp
{
    public const int DefaultMaxBytes = 16 * 1024 * 1024;

    public static readonly string UserAgent = "VUStudio/0.1 (+https://github.com/blinkone/vudials)";

    /// <summary>
    /// The shared client, or a private one that accepts any TLS certificate (self-signed devices on the LAN)
    /// and/or does not follow redirects. The second value must be disposed with the provider (<c>null</c> for the shared client).
    /// </summary>
    public static (HttpClient Client, IDisposable? Owner) CreateClient(IServiceProvider services, bool allowInvalidCertificate, bool followRedirects = true)
    {
        if (!allowInvalidCertificate && followRedirects)
        {
            return (ProviderServices.Http(services), null);
        }

        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = followRedirects,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
        if (allowInvalidCertificate)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
        }

        var client = new HttpClient(OutboundMarker.Wrap(handler), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        return (client, client);
    }

    /// <summary>Parses a user-typed address: adds <c>http://</c> when no scheme is present. Returns <c>null</c> when empty or invalid.</summary>
    public static Uri? NormalizeBase(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var text = url.Trim();
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "http://" + text;
        }

        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri : null;
    }

    /// <summary>Combines a base address with a relative path (and optional query), keeping any path prefix of the base (reverse proxies).</summary>
    public static Uri Combine(Uri baseUri, string relative)
    {
        var prefix = baseUri.AbsolutePath.TrimEnd('/');
        var q = relative.IndexOf('?', StringComparison.Ordinal);
        var path = q >= 0 ? relative[..q] : relative;
        var builder = new UriBuilder(baseUri)
        {
            Path = prefix + "/" + path.TrimStart('/'),
            Query = q >= 0 ? relative[(q + 1)..] : "",
        };
        return builder.Uri;
    }

    public static AuthenticationHeaderValue? Basic(string? user, string? password) =>
        string.IsNullOrEmpty(user)
            ? null
            : new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));

    public static AuthenticationHeaderValue? Bearer(string? token) =>
        string.IsNullOrWhiteSpace(token) ? null : new AuthenticationHeaderValue("Bearer", token.Trim());

    /// <summary>Sends a request and reads the whole (size-capped) body. Network failures become readable exceptions; status codes are not interpreted.</summary>
    public static async Task<NetResponse> SendAsync(HttpClient client, HttpRequestMessage request, TimeSpan timeout, CancellationToken cancellationToken, int maxBytes = DefaultMaxBytes)
    {
        if (!request.Headers.UserAgent.Any())
        {
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is long length && length > maxBytes)
            {
                throw new InvalidDataException($"The response is too large ({length} bytes).");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeoutSource.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, timeoutSource.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                {
                    throw new InvalidDataException($"The response is too large (limit {maxBytes} bytes).");
                }

                buffer.Write(chunk, 0, read);
            }

            return new NetResponse(response.StatusCode, buffer.ToArray(), response.Headers, request.RequestUri!);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{request.RequestUri?.Authority}: no response within {timeout.TotalSeconds:0.#} s.");
        }
        catch (HttpRequestException ex)
        {
            var reason = ex.InnerException?.Message ?? ex.Message;
            throw new IOException($"Cannot reach {request.RequestUri?.Authority}: {reason}", ex);
        }
    }

    /// <summary>Throws a descriptive exception unless the response is a success. <paramref name="what"/> names the credential for 401/403 messages.</summary>
    public static NetResponse EnsureSuccess(NetResponse response, string what = "credentials")
    {
        if (response.IsSuccess)
        {
            return response;
        }

        var code = (int)response.Status;
        var host = response.RequestUri.Authority;
        throw response.Status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                new SourceUnavailableException($"{host} rejected the {what} (HTTP {code})."),
            HttpStatusCode.TooManyRequests =>
                new RateLimitedException($"{host} is rate limiting requests (HTTP 429).", RetryAfter(response.Headers) ?? TimeSpan.FromMinutes(1)),
            >= (HttpStatusCode)300 and < (HttpStatusCode)400 =>
                new SourceUnavailableException($"{host} redirected the request to {response.Headers.Location} (HTTP {code}). Use that address, for example https://, in the settings."),
            _ => new IOException($"{host} answered HTTP {code} {response.Status}."),
        };
    }

    public static TimeSpan? RetryAfter(HttpResponseHeaders headers)
    {
        var value = headers.RetryAfter;
        if (value?.Delta is { } delta)
        {
            return delta;
        }

        if (value?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : null;
        }

        return null;
    }

    public static async Task<JsonNode?> GetJsonAsync(HttpClient client, Uri uri, TimeSpan timeout, CancellationToken cancellationToken, Action<HttpRequestMessage>? configure = null, string what = "credentials")
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        configure?.Invoke(request);
        var response = EnsureSuccess(await SendAsync(client, request, timeout, cancellationToken).ConfigureAwait(false), what);
        return response.Json();
    }
}
