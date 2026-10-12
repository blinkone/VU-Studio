namespace VuStudio.Sources.Common;

/// <summary>Finds executables on the <c>PATH</c> the way a shell would (honouring <c>PATHEXT</c> on Windows).</summary>
public static class ExecutableLocator
{
    /// <summary>
    /// Returns the full path of <paramref name="name"/>, or <c>null</c> when it cannot be found.
    /// A name that contains a directory separator is checked as-is instead of searched.
    /// </summary>
    /// <param name="name">Program name such as <c>nvidia-smi</c> or <c>bash</c>.</param>
    /// <param name="searchPath">PATH-style list of directories; the process <c>PATH</c> when <c>null</c>.</param>
    /// <param name="extraDirectories">Directories to try after the PATH ones.</param>
    public static string? Find(string name, string? searchPath = null, IEnumerable<string>? extraDirectories = null)
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
            .Concat(extraDirectories ?? []);

        var candidates = CandidateNames(name).ToList();
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

    private static IEnumerable<string> CandidateNames(string name)
    {
        yield return name;
        if (!OperatingSystem.IsWindows())
        {
            yield break;
        }

        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
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
