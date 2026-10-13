using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Common;

namespace VuStudio.Sources.Http;

/// <summary>
/// Calls a web API on an interval and publishes values picked out of its JSON answer. With a "fields"
/// table each row becomes a sensor; without one, every leaf of the answer becomes a sensor.
/// </summary>
internal sealed class HttpJsonProvider : PollingSensorProvider
{
    private static readonly ProductInfoHeaderValue UserAgent = new("VuStudio", "1");

    /// <summary>Headers that belong to the request body rather than to the request.</summary>
    private static readonly HashSet<string> ContentHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Allow", "Content-Disposition", "Content-Encoding", "Content-Language", "Content-Length",
        "Content-Location", "Content-MD5", "Content-Range", "Content-Type", "Expires", "Last-Modified",
    };

    private readonly HttpJsonOptions _options;
    private readonly HttpClient _client;
    private readonly string? _device;
    private readonly double _staleAfter;
    private readonly List<(FieldSpec Field, JsonPathLite Path)> _fields = [];
    private readonly HashSet<string> _discovered = new(StringComparer.Ordinal);
    private readonly IDisposable? _owner;
    private SensorRegistry? _sensors;

    /// <param name="owner">Disposed with the provider: a private client, <c>null</c> for the shared one.</param>
    public HttpJsonProvider(string id, HttpJsonOptions options, HttpClient client, string? deviceLabel, ILogger logger, IDisposable? owner = null)
        : base(id, options.Interval, logger)
    {
        _options = options;
        _client = client;
        _owner = owner;
        _device = deviceLabel ?? options.Url?.Host;
        _staleAfter = SensorHints.StaleAfterSeconds(options.Interval);
        foreach (var field in options.Fields)
        {
            _fields.Add((field, JsonPathLite.Parse(field.Path)));
        }
    }

    protected override Task InitializeAsync(ISensorSink sink, CancellationToken cancellationToken)
    {
        _sensors = new SensorRegistry(sink, _staleAfter);
        foreach (var (field, _) in _fields)
        {
            _sensors.Define(field.ToSensorInfo(_device, _staleAfter));
        }

        return Task.CompletedTask;
    }

    protected override async Task PollAsync(ISensorSink sink, CancellationToken cancellationToken)
    {
        var sensors = _sensors ?? throw new InvalidOperationException("The provider was not initialised.");
        if (_options.Url is null)
        {
            throw new InvalidOperationException(string.Join(" ", _options.Issues));
        }

        using var request = BuildRequest();
        var root = await HttpJson.SendAsync(_client, request, _options.Timeout, cancellationToken).ConfigureAwait(false);

        if (_fields.Count > 0)
        {
            PublishFields(sensors, root);
        }
        else
        {
            PublishEverything(sensors, root);
        }

        // Rows of the settings that could not be used were skipped above; make that visible.
        if (_options.Issues.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" ", _options.Issues));
        }
    }

    private void PublishFields(SensorRegistry sensors, JsonNode? root)
    {
        foreach (var (field, path) in _fields)
        {
            var value = field.Convert(path.Evaluate(root));
            if (value.Number is { } number)
            {
                sensors.Publish(field.Id, number);
            }
            else if (value.Text is not null)
            {
                sensors.PublishText(field.Id, value.Text);
            }
            else
            {
                sensors.Invalidate(field.Id);
            }
        }
    }

    /// <summary>The zero-configuration mode: show what the API returns so the user can pick from it.</summary>
    private void PublishEverything(SensorRegistry sensors, JsonNode? root)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var leaf in JsonFlattener.Flatten(root))
        {
            var id = LocalIds.SlugPath(leaf.Key);
            if (!seen.Add(id))
            {
                continue; // two keys that normalise to the same id: the first one wins
            }

            var isText = leaf.Number is null;
            sensors.Define(new SensorInfo
            {
                Id = id,
                Name = leaf.Key,
                Device = _device,
                Category = SensorCategory.Custom,
                Kind = isText ? SensorKinds.Text : null,
                ValueKind = isText ? SensorValueKind.Text : SensorValueKind.Number,
                Icon = "api",
            });

            if (leaf.Number is { } number)
            {
                sensors.Publish(id, number);
            }
            else
            {
                sensors.PublishText(id, leaf.Text);
            }
        }

        // Leaves that were there before and are gone now are offline, not deleted.
        foreach (var gone in _discovered.Except(seen))
        {
            sensors.Invalidate(gone);
        }

        _discovered.Clear();
        _discovered.UnionWith(seen);
    }

    private HttpRequestMessage BuildRequest()
    {
        var request = new HttpRequestMessage(_options.Method, _options.Url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.Add(UserAgent);

        StringContent? content = null;
        if (_options.Method == HttpMethod.Post && !string.IsNullOrEmpty(_options.Body))
        {
            content = new StringContent(_options.Body, Encoding.UTF8, "application/json");
            request.Content = content;
        }

        foreach (var header in _options.Headers)
        {
            if (ContentHeaders.Contains(header.Name))
            {
                // These describe the body. HttpRequestHeaders would accept them too (as "custom" headers) and
                // they would then be sent twice, so they must be set on the content, and only when there is one.
                if (content is not null && !header.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    content.Headers.Remove(header.Name);
                    content.Headers.TryAddWithoutValidation(header.Name, header.Value);
                }

                continue;
            }

            // The user's header replaces the default of the same name.
            request.Headers.Remove(header.Name);
            request.Headers.TryAddWithoutValidation(header.Name, header.Value);
        }

        // Also set here (not only by the client's handler) so that no user header can remove or fake it.
        OutboundMarker.Apply(request);
        return request;
    }

    public override ValueTask DisposeAsync()
    {
        _owner?.Dispose();
        return base.DisposeAsync();
    }
}
