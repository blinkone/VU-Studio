using System.Text.Json;
using System.Text.Json.Nodes;

namespace VuStudio.Sources.Common;

/// <summary>
/// Fetches a JSON document over HTTP with the safeguards a long-running provider needs: a per-request
/// timeout independent of <see cref="HttpClient.Timeout"/> (the client may be shared), a cap on the
/// response size, and readable error messages.
/// </summary>
internal static class HttpJson
{
    /// <summary>Default cap on the size of a response body.</summary>
    public const int DefaultMaxBytes = 8 * 1024 * 1024;

    private static readonly JsonDocumentOptions Lenient = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Sends <paramref name="request"/> and parses the body as JSON.
    /// Throws <see cref="HttpRequestException"/> for non-success status codes, <see cref="TimeoutException"/>
    /// when <paramref name="timeout"/> elapses, <see cref="InvalidDataException"/> for a body that is too large
    /// or not JSON, and <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public static async Task<JsonNode?> SendAsync(
        HttpClient client,
        HttpRequestMessage request,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        int maxBytes = DefaultMaxBytes)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd(),
                    inner: null,
                    response.StatusCode);
            }

            if (response.Content.Headers.ContentLength is long length && length > maxBytes)
            {
                throw new InvalidDataException($"The response is too large ({length} bytes, limit {maxBytes}).");
            }

            await using var body = await response.Content.ReadAsStreamAsync(timeoutSource.Token).ConfigureAwait(false);
            var bytes = await ReadBoundedAsync(body, maxBytes, timeoutSource.Token).ConfigureAwait(false);
            return Parse(bytes);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"No response within {timeout.TotalSeconds:0.#} s.");
        }
    }

    /// <summary>Parses UTF-8 JSON (a leading byte-order mark, comments and trailing commas are tolerated).</summary>
    public static JsonNode? Parse(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length >= 3 && utf8[0] == 0xEF && utf8[1] == 0xBB && utf8[2] == 0xBF)
        {
            utf8 = utf8[3..];
        }

        try
        {
            return JsonNode.Parse(utf8, nodeOptions: null, Lenient);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The response is not valid JSON: " + ex.Message, ex);
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw new InvalidDataException($"The response is too large (limit {maxBytes} bytes).");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
