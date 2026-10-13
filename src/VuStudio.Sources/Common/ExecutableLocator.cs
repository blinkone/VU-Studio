namespace VuStudio.Sources.Common;

/// <summary>
/// Finds executables on the <c>PATH</c> the way a shell would (honouring <c>PATHEXT</c> on Windows unless told not to).
/// Code that runs in the service must not search the <c>PATH</c> on Windows (it is writable by installers and, through
/// user-writable entries, by standard users); use <see cref="FirstExisting"/> with absolute well-known paths there.
/// Relative <c>PATH</c> entries (such as <c>.</c>) are always skipped.
/// </summary>
public static class ExecutableLocator
{
    /// <summary>
    /// Returns the full path of <paramref name="name"/>, or <c>null</c> when it cannot be found.
    /// A name that contains a directory separator is checked as-is instead of searched.
    /// </summary>
    /// <param name="name">Program name such as <c>nvidia-smi</c> or <c>bash</c>.</param>
    /// <param name="searchPath">PATH-style list of directories; the process <c>PATH</c> when <c>null</c>.</param>
    /// <param name="extraDirectories">Directories to try after the PATH ones.</param>
    /// <param name="usePathExt">On Windows, also try <paramref name="name"/> with each <c>PATHEXT</c> extension. When false only the exact name is tried.</param>
    public static string? Find(string name, string? searchPath = null, IEnumerable<string>? extraDirectories = null, bool usePathExt = true)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar))
        {
            return IsExecutableFile(name) ? Path.GetFullPath(name) : null;
        }

        var directories = (searchPath ?? Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => d.Trim('"'))
            .Concat(extraDirectories ?? [])
            .Where(Path.IsPathFullyQualified);

        var candidates = CandidateNames(name, OperatingSystem.IsWindows() && usePathExt, Environment.GetEnvironmentVariable("PATHEXT")).ToList();
        foreach (var directory in directories)
        {
            foreach (var candidate in candidates)
            {
                string path;
                try
                {
                    path = Path.Combine(directory, candidate);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (IsExecutableFile(path))
                {
                    return path;
                }
            }
        }

        return null;
    }

    /// <summary>The first of <paramref name="paths"/> that is an existing executable file; relative paths are ignored.</summary>
    public static string? FirstExisting(IEnumerable<string> paths) =>
        paths.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && Path.IsPathFullyQualified(p) && IsExecutableFile(p));

    internal static IEnumerable<string> CandidateNames(string name, bool withPathExt, string? pathExt)
    {
        yield return name;
        if (!withPathExt)
        {
            yield break;
        }

        var extensions = (pathExt ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (extensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
        {
            yield break;
        }

        foreach (var extension in extensions)
        {
            yield return name + extension;
        }
    }

    private static bool IsExecutableFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            return OperatingSystem.IsWindows() ||
                   (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
