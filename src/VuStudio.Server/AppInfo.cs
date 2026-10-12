using System.Reflection;

namespace VuStudio.Server;

public static class AppInfo
{
    public const string Name = "VU Studio";

    public const string ServiceName = "VUStudio";

    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(AppInfo).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";
}
