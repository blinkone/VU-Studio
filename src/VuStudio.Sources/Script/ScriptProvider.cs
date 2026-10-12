using Microsoft.Extensions.Logging;
using VuStudio.Core.Providers;
using VuStudio.Core.Sensors;
using VuStudio.Sources.Common;

namespace VuStudio.Sources.Script;

/// <summary>The <c>script</c> settings, parsed. Problems are collected in <see cref="Issues"/>.</summary>
internal sealed class ScriptOptions
{
    public const double DefaultIntervalSeconds = 10;
    public const double DefaultTimeoutSeconds = 10;

    public string? Command { get; private init; }

    public ScriptShell Shell { get; private init; }

    public ScriptParseMode Parse { get; private init; }

    public string? WorkingDirectory { get; private init; }

    public TimeSpan Interval { get; private init; }

    public TimeSpan Timeout { get; private init; }

    public IReadOnlyList<FieldSpec> Fields { get; private init; } = [];

    public IReadOnlyList<string> Issues { get; private init; } = [];

    public static ScriptOptions From(ProviderSettings settings)
    {
        var issues = new List<string>();
        var command = settings.GetString("command");
        if (command is null)
        {
            issues.Add("No command is set.");
        }

        return new ScriptOptions
        {
            Command = command,
            Shell = ScriptRunner.ParseShell(settings.GetString("shell")),
            Parse = ScriptOutputParser.ParseMode(settings.GetString("parse")),
            WorkingDirectory = settings.GetString("workingDirectory"),
            Interval = settings.GetSeconds("interval", DefaultIntervalSeconds),
            Timeout = TimeSpan.FromSeconds(Math.Clamp(settings.GetDouble("timeout", DefaultTimeoutSeconds), 0.5, 3600)),
            Fields = FieldSpecs.Parse(settings, issues),
            Issues = issues,
        };
    }
}

/// <summary>
/// Runs a command on an interval and turns its output into sensors. Without a "fields" table every value
/// found in the output becomes a sensor; with one, only the listed ones do.
/// </summary>
internal sealed class ScriptProvider : PollingSensorProvider
{
    private readonly ScriptOptions _options;
    private readonly string _device;
    private readonly double _staleAfter;
    private readonly Func<ScriptRequest, CancellationToken, Task<ProcessRunResult>> _run;
    private readonly HashSet<string> _discovered = new(StringComparer.Ordinal);
    private SensorRegistry? _sensors;

    public ScriptProvider(
        string id,
        ScriptOptions options,
        string? deviceLabel,
        ILogger logger,
        Func<ScriptRequest, CancellationToken, Task<ProcessRunResult>>? run = null)
        : base(id, options.Interval, logger)
    {
        _options = options;
        _device = deviceLabel ?? "Script";
        _staleAfter = SensorHints.StaleAfterSeconds(options.Interval);
        _run = run ?? ScriptRunner.RunAsync;
    }

    protected override Task InitializeAsync(ISensorSink sink, CancellationToken cancellationToken)
    {
        _sensors = new SensorRegistry(sink, _staleAfter);
        foreach (var field in _options.Fields)
        {
            _sensors.Define(field.ToSensorInfo(_device, _staleAfter));
        }

        return Task.CompletedTask;
    }

    protected override async Task PollAsync(ISensorSink sink, CancellationToken cancellationToken)
    {
        var sensors = _sensors ?? throw new InvalidOperationException("The provider was not initialised.");
        if (_options.Command is null)
        {
            throw new InvalidOperationException(string.Join(" ", _options.Issues));
        }

        var result = await _run(
            new ScriptRequest(_options.Command)
            {
                Shell = _options.Shell,
                WorkingDirectory = _options.WorkingDirectory,
                Timeout = _options.Timeout,
            },
            cancellationToken).ConfigureAwait(false);

        if (result.TimedOut)
        {
            throw new TimeoutException($"The script did not finish within {_options.Timeout.TotalSeconds:0.#} s and was stopped.");
        }

        if (result.ExitCode != 0)
        {
            var detail = FirstLine(result.StdErr) ?? FirstLine(result.StdOut);
            throw new InvalidOperationException($"The script exited with code {result.ExitCode}" + (detail is null ? "." : ": " + detail));
        }

        if (!ScriptOutputParser.TryParse(result.StdOut, _options.Parse, out var output, out var error))
        {
            throw new InvalidOperationException(error);
        }

        if (output.IsEmpty)
        {
            throw new InvalidOperationException("The script printed nothing.");
        }

        if (_options.Fields.Count > 0)
        {
            PublishFields(sensors, output);
        }
        else
        {
            PublishEverything(sensors, output);
        }

        if (_options.Issues.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" ", _options.Issues));
        }
    }

    private void PublishFields(SensorRegistry sensors, ScriptOutput output)
    {
        foreach (var field in _options.Fields)
        {
            var value = field.Convert(output.Select(field.Path));
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

    private void PublishEverything(SensorRegistry sensors, ScriptOutput output)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var leaf in output.Values)
        {
            var id = LocalIds.SlugPath(leaf.Key);
            if (!seen.Add(id))
            {
                continue;
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
                Icon = "console",
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

        foreach (var gone in _discovered.Except(seen))
        {
            sensors.Invalidate(gone);
        }

        _discovered.Clear();
        _discovered.UnionWith(seen);
    }

    private static string? FirstLine(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return line is { Length: > 200 } ? line[..200] : line;
    }
}
