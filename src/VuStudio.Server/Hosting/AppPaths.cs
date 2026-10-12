namespace VuStudio.Server.Hosting;

/// <summary>Resolves where configuration, assets, history and logs live.</summary>
public sealed class AppPaths
{
    public AppPaths(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Assets);
        Directory.CreateDirectory(Logs);
    }

    public string Root { get; }

    public string Config => Path.Combine(Root, "config.json");

    public string History => Path.Combine(Root, "history.db");

    public string Assets => Path.Combine(Root, "assets");

    public string Logs => Path.Combine(Root, "logs");

    /// <summary>
    /// <c>--data-dir</c> or <c>VUSTUDIO_DATA</c> wins; otherwise <c>%ProgramData%\VU Studio</c> on Windows,
    /// <c>/var/lib/vustudio</c> for root on Linux, and <c>$XDG_CONFIG_HOME/vustudio</c> (or <c>~/.config/vustudio</c>) for users.
    /// </summary>
    public static AppPaths Resolve(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return new AppPaths(overridePath);
        }

        if (Environment.GetEnvironmentVariable("VUSTUDIO_DATA") is { Length: > 0 } env)
        {
            return new AppPaths(env);
        }

        if (OperatingSystem.IsWindows())
        {
            return new AppPaths(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VU Studio"));
        }

        if (OperatingSystem.IsLinux() && Environment.UserName == "root")
        {
            return new AppPaths("/var/lib/vustudio");
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var baseDir = string.IsNullOrWhiteSpace(xdg)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
            : xdg;
        return new AppPaths(Path.Combine(baseDir, "vustudio"));
    }
}
