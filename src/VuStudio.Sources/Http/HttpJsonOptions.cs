using System.Text.RegularExpressions;
using VuStudio.Core.Providers;
using VuStudio.Sources.Common;

namespace VuStudio.Sources.Http;

/// <summary>A request header from the settings.</summary>
internal sealed record HttpHeader(string Name, string Value);

/// <summary>The <c>http-json</c> settings, parsed and validated. Problems are collected in <see cref="Issues"/> rather than thrown.</summary>
internal sealed partial class HttpJsonOptions
{
    public const double DefaultIntervalSeconds = 30;
    public const double DefaultTimeoutSeconds = 10;
    public const double MaxTimeoutSeconds = 100;

    /// <summary>The address to call; <c>null</c> when the setting is missing or not an http(s) URL.</summary>
    public Uri? Url { get; private init; }

    public HttpMethod Method { get; private init; } = HttpMethod.Get;

    public IReadOnlyList<HttpHeader> Headers { get; private init; } = [];

    public string? Body { get; private init; }

    public TimeSpan Interval { get; private init; }

    public TimeSpan Timeout { get; private init; }

    /// <summary>The fields to extract; empty means "publish every leaf of the response".</summary>
    public IReadOnlyList<FieldSpec> Fields { get; private init; } = [];

    /// <summary>Human readable descriptions of settings that could not be used.</summary>
    public IReadOnlyList<string> Issues { get; private init; } = [];

    [GeneratedRegex("^[!#$%&'*+.^_`|~0-9A-Za-z-]+$")]
    private static partial Regex HeaderName();

    public static HttpJsonOptions From(ProviderSettings settings)
    {
        var issues = new List<string>();

        Uri? url = null;
        var urlText = settings.GetString("url");
        if (urlText is null)
        {
            issues.Add("The URL is not set.");
        }
        else if (!Uri.TryCreate(urlText, UriKind.Absolute, out url) || url.Scheme is not ("http" or "https"))
        {
            issues.Add($"'{urlText}' is not a valid http or https address.");
            url = null;
        }

        var method = string.Equals(settings.GetString("method"), "POST", StringComparison.OrdinalIgnoreCase)
            ? HttpMethod.Post
            : HttpMethod.Get;

        var headers = new List<HttpHeader>();
        var headerNumber = 0;
        foreach (var row in settings.GetTable("headers"))
        {
            headerNumber++;
            var name = row.GetString("name");
            if (name is null)
            {
                continue; // an empty row is just an unfilled line of the table
            }

            if (!HeaderName().IsMatch(name))
            {
                issues.Add($"Header {headerNumber}: '{name}' is not a valid header name.");
                continue;
            }

            var value = row.GetString("value") ?? "";
            if (value.AsSpan().IndexOfAny('\r', '\n') >= 0)
            {
                issues.Add($"Header '{name}': the value must be on a single line.");
                continue;
            }

            headers.Add(new HttpHeader(name, value));
        }

        return new HttpJsonOptions
        {
            Url = url,
            Method = method,
            Headers = headers,
            Body = settings.GetString("body"),
            Interval = settings.GetSeconds("interval", DefaultIntervalSeconds),
            Timeout = TimeSpan.FromSeconds(Math.Clamp(settings.GetDouble("timeout", DefaultTimeoutSeconds), 0.5, MaxTimeoutSeconds)),
            Fields = FieldSpecs.Parse(settings, issues),
            Issues = issues,
        };
    }
}
