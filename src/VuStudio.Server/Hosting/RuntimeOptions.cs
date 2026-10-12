using System.Globalization;

namespace VuStudio.Server.Hosting;

/// <summary>
/// Command-line options:
/// <c>--service</c> (run as a Windows service),
/// <c>--simulate N</c> (use a simulated hub with N dials, not saved to the config),
/// <c>--data-dir PATH</c>, <c>--port N</c>.
/// </summary>
public sealed record RuntimeOptions(bool IsService, int Simulate, string? DataDirectory, int? Port)
{
    public static RuntimeOptions Parse(string[] args)
    {
        var service = false;
        var simulate = 0;
        string? dataDir = null;
        int? port = null;

        for (var i = 0; i < args.Length; i++)
        {
            var (name, inline) = Split(args[i]);
            string? Next() => inline ?? (i + 1 < args.Length ? args[++i] : null);

            switch (name)
            {
                case "--service":
                    service = true;
                    break;
                case "--simulate":
                    var value = inline ?? (i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out _) ? args[++i] : "4");
                    simulate = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? Math.Clamp(n, 1, 16) : 4;
                    break;
                case "--data-dir":
                    dataDir = Next();
                    break;
                case "--port":
                    port = int.TryParse(Next(), NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? p : null;
                    break;
            }
        }

        return new RuntimeOptions(service, simulate, dataDir, port);
    }

    private static (string Name, string? Inline) Split(string arg)
    {
        var eq = arg.IndexOf('=');
        return eq > 0 ? (arg[..eq], arg[(eq + 1)..]) : (arg, null);
    }
}
