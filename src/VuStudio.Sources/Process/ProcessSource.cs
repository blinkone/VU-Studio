using System.ComponentModel;
using System.Diagnostics;

namespace VuStudio.Sources.Processes;

/// <summary>One running process of interest.</summary>
/// <param name="Pid">Process id.</param>
/// <param name="Name">The normalised name (see <see cref="ProcessNames.Normalize"/>).</param>
/// <param name="CpuTime">Total processor time used so far; <c>null</c> when the system does not let us read it.</param>
/// <param name="WorkingSetBytes">Physical memory currently used by the process.</param>
internal readonly record struct ProcessSample(int Pid, string Name, TimeSpan? CpuTime, long WorkingSetBytes);

/// <summary>Lists running processes. Abstracted so that the provider's arithmetic can be tested.</summary>
internal interface IProcessSource
{
    /// <summary>The running processes whose normalised name is in <paramref name="names"/>.</summary>
    IReadOnlyList<ProcessSample> Snapshot(IReadOnlySet<string> names);
}

/// <summary>Normalisation of process names, so that <c>Chrome.EXE</c>, <c>chrome.exe</c> and <c>chrome</c> are one program.</summary>
internal static class ProcessNames
{
    /// <summary>
    /// Lowercases, drops surrounding quotes and any directory, and removes a trailing <c>.exe</c>.
    /// <c>"C:\Games\Elden Ring\eldenring.exe"</c> becomes <c>eldenring</c>. Returns <c>null</c> for blank input.
    /// </summary>
    public static string? Normalize(string? name)
    {
        var text = name?.Trim().Trim('"', '\'').Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var slash = text.LastIndexOfAny(['/', '\\']);
        if (slash >= 0)
        {
            text = text[(slash + 1)..];
        }

        if (text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^4];
        }

        text = text.Trim().ToLowerInvariant();
        return text.Length == 0 ? null : text;
    }
}

/// <summary>Reads the process list from the operating system.</summary>
internal sealed class SystemProcessSource : IProcessSource
{
    public IReadOnlyList<ProcessSample> Snapshot(IReadOnlySet<string> names)
    {
        var matches = new List<ProcessSample>();

        // One enumeration for all names (not one per name). Every Process object holds OS handles and must be disposed.
        var processes = Process.GetProcesses();
        try
        {
            foreach (var process in processes)
            {
                var name = TryRead(() => ProcessNames.Normalize(process.ProcessName));
                if (name is null || !names.Contains(name))
                {
                    continue;
                }

                var cpu = TryRead<TimeSpan?>(() => process.TotalProcessorTime);
                var memory = TryRead<long?>(() => process.WorkingSet64) ?? 0;
                matches.Add(new ProcessSample(process.Id, name, cpu, memory));
            }
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }

        return matches;
    }

    /// <summary>Reads a property that throws for processes that exited meanwhile or that we may not inspect.</summary>
    private static T? TryRead<T>(Func<T?> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
        {
            return default;
        }
    }
}
