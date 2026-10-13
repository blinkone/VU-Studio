using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace VuStudio.Server.Hosting;

/// <summary>Resolves where configuration, assets, history and logs live.</summary>
public sealed class AppPaths
{
    private readonly List<string> _problems = [];

    /// <param name="root">The data directory.</param>
    /// <param name="secure">
    /// Running as the Windows service (LocalSystem): the data directory holds secrets and the configuration that decides
    /// what runs, so it must not be writable, nor redirectable, by standard users. When set, the data directory and its
    /// <c>logs</c> and <c>assets</c> subdirectories are refused (removed and recreated) when they are reparse points
    /// (symbolic links, junctions), and on Windows the data directory gets a protected DACL: SYSTEM and Administrators
    /// full control, nothing inherited from <c>%ProgramData%</c> (where Users may create and write).
    /// </param>
    public AppPaths(string root, bool secure = false)
    {
        Root = Path.GetFullPath(root);
        if (secure)
        {
            RefuseReparsePoint(Root);
        }

        Directory.CreateDirectory(Root);
        if (secure && OperatingSystem.IsWindows())
        {
            try
            {
                RestrictToAdministrators(Root);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or PrivilegeNotHeldException)
            {
                _problems.Add($"Could not restrict the permissions of {Root}: {ex.Message}");
            }
        }

        foreach (var sub in new[] { Assets, Logs })
        {
            if (secure)
            {
                RefuseReparsePoint(sub);
            }

            Directory.CreateDirectory(sub);
        }
    }

    public string Root { get; }

    public string Config => Path.Combine(Root, "config.json");

    public string History => Path.Combine(Root, "history.db");

    public string Assets => Path.Combine(Root, "assets");

    public string Logs => Path.Combine(Root, "logs");

    /// <summary>Errors found while preparing the directories, to be logged once logging is up.</summary>
    public IReadOnlyList<string> Problems => _problems;

    /// <summary>
    /// <c>--data-dir</c> or <c>VUSTUDIO_DATA</c> wins; otherwise <c>%ProgramData%\VU Studio</c> on Windows,
    /// <c>/var/lib/vustudio</c> for root on Linux, and <c>$XDG_CONFIG_HOME/vustudio</c> (or <c>~/.config/vustudio</c>) for users.
    /// See the constructor for <paramref name="secure"/>.
    /// </summary>
    public static AppPaths Resolve(string? overridePath, bool secure = false)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return new AppPaths(overridePath, secure);
        }

        if (Environment.GetEnvironmentVariable("VUSTUDIO_DATA") is { Length: > 0 } env)
        {
            return new AppPaths(env, secure);
        }

        if (OperatingSystem.IsWindows())
        {
            return new AppPaths(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VU Studio"), secure);
        }

        if (OperatingSystem.IsLinux() && Environment.UserName == "root")
        {
            return new AppPaths("/var/lib/vustudio", secure);
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var baseDir = string.IsNullOrWhiteSpace(xdg)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
            : xdg;
        return new AppPaths(Path.Combine(baseDir, "vustudio"), secure);
    }

    /// <summary>
    /// A directory that is a symbolic link or junction could point the service's writes anywhere: remove the link
    /// (never its target) so the directory is recreated as a real one.
    /// </summary>
    private void RefuseReparsePoint(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                _problems.Add($"{path} is a symbolic link or junction (target: {info.LinkTarget ?? "?"}); it was removed and recreated as a plain directory.");
                info.Delete(); // deletes the link itself, not what it points to
            }
            else if (!info.Exists && File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            {
                _problems.Add($"{path} is a link to a file; it was removed and recreated as a plain directory.");
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"{path} is a symbolic link or junction and could not be removed: {ex.Message}", ex);
        }
    }

    /// <summary>Owner Administrators; SYSTEM and Administrators full control (inherited by everything inside); no inherited ACEs.</summary>
    [SupportedOSPlatform("windows")]
    private static void RestrictToAdministrators(string path)
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { system, administrators })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        // A standard user may have created the folder first; its owner could otherwise rewrite the DACL.
        security.SetOwner(administrators);
        new DirectoryInfo(path).SetAccessControl(security);
    }
}
