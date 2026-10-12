using System.Text;
using VuStudio.Sources.Common;

namespace VuStudio.Sources.Script;

/// <summary>The program a script is handed to.</summary>
public enum ScriptShell
{
    /// <summary>PowerShell on Windows; <c>bash</c> (or <c>sh</c> when bash is missing) elsewhere.</summary>
    Auto,

    /// <summary>Windows PowerShell 5.1 (<c>powershell.exe</c>); <c>pwsh</c> on other systems.</summary>
    PowerShell,

    /// <summary>PowerShell 7+ (<c>pwsh</c>).</summary>
    Pwsh,

    /// <summary>The Windows command interpreter.</summary>
    Cmd,

    Bash,

    Sh,

    /// <summary>No shell: the first word of the command is the program, the rest are its arguments.</summary>
    None,
}

/// <summary>What <see cref="ScriptRunner"/> should run.</summary>
/// <param name="Command">The script text. Several lines are fine for every shell except <see cref="ScriptShell.None"/>.</param>
public sealed record ScriptRequest(string Command)
{
    public ScriptShell Shell { get; init; } = ScriptShell.Auto;

    public string? WorkingDirectory { get; init; }

    /// <summary>The process tree is killed after this long.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Output beyond this many characters per stream is discarded.</summary>
    public int MaxOutputChars { get; init; } = 1_048_576;
}

/// <summary>
/// Runs user scripts: picks the shell, hands over the command without any quoting accidents, enforces a
/// timeout by killing the whole process tree, captures stdout and stderr, and shows no window. Used by the
/// <c>script</c> provider and by the tray app, which runs user scripts in the interactive session.
/// </summary>
public static class ScriptRunner
{
    /// <summary>Largest encoded PowerShell command that still fits a Windows command line.</summary>
    private const int MaxEncodedCommandLength = 30_000;

    /// <summary>Parses the value of the "shell" setting; unknown text means <see cref="ScriptShell.Auto"/>.</summary>
    public static ScriptShell ParseShell(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "powershell" => ScriptShell.PowerShell,
        "pwsh" => ScriptShell.Pwsh,
        "cmd" => ScriptShell.Cmd,
        "bash" => ScriptShell.Bash,
        "sh" => ScriptShell.Sh,
        "none" => ScriptShell.None,
        _ => ScriptShell.Auto,
    };

    /// <summary>Turns <see cref="ScriptShell.Auto"/> into the concrete shell for this machine.</summary>
    public static ScriptShell ResolveShell(ScriptShell shell)
    {
        if (shell != ScriptShell.Auto)
        {
            return shell;
        }

        if (OperatingSystem.IsWindows())
        {
            return ScriptShell.PowerShell;
        }

        return ExecutableLocator.Find("bash") is not null ? ScriptShell.Bash : ScriptShell.Sh;
    }

    /// <summary>
    /// Runs the script. A timeout is reported through <see cref="ProcessRunResult.TimedOut"/>; cancelling the
    /// token kills the tree and throws. A shell or program that cannot be started throws.
    /// </summary>
    /// <exception cref="ArgumentException">The command is empty.</exception>
    public static async Task<ProcessRunResult> RunAsync(ScriptRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var invocation = BuildInvocation(request);
        try
        {
            return await ProcessRunner.RunAsync(
                new ProcessRunRequest(invocation.FileName)
                {
                    Arguments = invocation.Arguments,
                    RawArguments = invocation.RawArguments,
                    WorkingDirectory = request.WorkingDirectory,
                    Timeout = request.Timeout,
                    MaxOutputChars = request.MaxOutputChars,
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            invocation.Cleanup();
        }
    }

    /// <summary>Works out which program to start and with which arguments. Exposed to tests.</summary>
    internal static ScriptInvocation BuildInvocation(ScriptRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Command))
        {
            throw new ArgumentException("The command is empty.", nameof(request));
        }

        var shell = ResolveShell(request.Shell);
        var command = request.Command;
        switch (shell)
        {
            case ScriptShell.PowerShell:
                return PowerShell(OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh", command, bypassPolicy: OperatingSystem.IsWindows());
            case ScriptShell.Pwsh:
                return PowerShell("pwsh", command, bypassPolicy: OperatingSystem.IsWindows());
            case ScriptShell.Cmd:
                return Cmd(command);
            case ScriptShell.Bash:
                return new ScriptInvocation("bash", ["-c", command]);
            case ScriptShell.Sh:
                return new ScriptInvocation("sh", ["-c", command]);
            default:
                var words = CommandLineTokenizer.Split(command);
                return words.Count == 0
                    ? throw new ArgumentException("The command is empty.", nameof(request))
                    : new ScriptInvocation(words[0], words.Skip(1).ToList());
        }
    }

    private static ScriptInvocation PowerShell(string program, string command, bool bypassPolicy)
    {
        // -EncodedCommand carries any script, multi-line or full of quotes, without shell quoting rules.
        // Output is forced to UTF-8 because the default is the OEM code page, which mangles accents.
        var script = "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; $ProgressPreference='SilentlyContinue'; " + command;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        if (encoded.Length > MaxEncodedCommandLength)
        {
            throw new InvalidOperationException("The command is too long for a PowerShell command line; keep it in a script file and call that instead.");
        }

        var arguments = new List<string> { "-NoLogo", "-NoProfile", "-NonInteractive" };
        if (bypassPolicy)
        {
            arguments.AddRange(["-ExecutionPolicy", "Bypass"]);
        }

        arguments.AddRange(["-EncodedCommand", encoded]);
        return new ScriptInvocation(program, arguments);
    }

    private static ScriptInvocation Cmd(string command)
    {
        // cmd has no safe way to take a multi-line script on its command line, so it runs from a batch file.
        var path = Path.Combine(Path.GetTempPath(), "vustudio-" + Guid.NewGuid().ToString("N") + ".cmd");
        var text = "@echo off\r\nchcp 65001 >nul\r\n" + command.ReplaceLineEndings("\r\n") + "\r\n";
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return new ScriptInvocation("cmd.exe", [])
        {
            RawArguments = $"/d /s /c \"\"{path}\"\"",
            TempFile = path,
        };
    }
}

/// <summary>A resolved command: program, arguments, and a temporary file to delete afterwards.</summary>
internal sealed record ScriptInvocation(string FileName, IReadOnlyList<string> Arguments)
{
    public string? RawArguments { get; init; }

    public string? TempFile { get; init; }

    public void Cleanup()
    {
        if (TempFile is null)
        {
            return;
        }

        try
        {
            File.Delete(TempFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp file is harmless.
        }
    }
}
