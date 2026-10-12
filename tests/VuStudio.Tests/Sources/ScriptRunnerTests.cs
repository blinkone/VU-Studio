using System.Diagnostics;
using System.Text;
using VuStudio.Sources.Common;
using VuStudio.Sources.Script;

namespace VuStudio.Tests.Sources;

public class ScriptRunnerTests
{
    private static void RequireUnixShell(string name = "sh") =>
        Assert.SkipUnless(!OperatingSystem.IsWindows() && ExecutableLocator.Find(name) is not null, $"'{name}' is not available on this machine.");

    private static Task<ProcessRunResult> Run(ScriptRequest request) =>
        ScriptRunner.RunAsync(request, TestContext.Current.CancellationToken);

    private static ScriptRequest Sh(string command, double timeoutSeconds = 30) =>
        new(command) { Shell = ScriptShell.Sh, Timeout = TimeSpan.FromSeconds(timeoutSeconds) };

    /// <summary>Whether a process exists and is not a zombie (a killed child that nobody reaped yet is dead).</summary>
    private static bool IsAlive(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            var state = stat[(stat.LastIndexOf(')') + 2)..][0];
            return state is not ('Z' or 'X');
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static async Task<bool> BecomesDeadAsync(int pid)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (!IsAlive(pid))
            {
                return true;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        return false;
    }

    // ----- real processes -----

    [Fact]
    public async Task Captures_stdout_stderr_and_the_exit_code()
    {
        RequireUnixShell();
        var result = await Run(Sh("echo hello; echo oops 1>&2; exit 3"));

        Assert.Equal("hello\n", result.StdOut);
        Assert.Equal("oops\n", result.StdErr);
        Assert.Equal(3, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.False(result.Succeeded);
        Assert.True(result.Duration < TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task A_command_that_exits_zero_succeeds()
    {
        RequireUnixShell();
        var result = await Run(Sh("echo 42"));
        Assert.True(result.Succeeded);
        Assert.Equal("42", result.StdOut.Trim());
    }

    [Fact]
    public async Task Runs_multi_line_scripts()
    {
        RequireUnixShell();
        var result = await Run(Sh("A=5\nB=$((A * 2))\necho \"a=$A\"\necho \"b=$B\""));
        Assert.Equal("a=5\nb=10\n", result.StdOut);
    }

    [Fact]
    public async Task Bash_runs_bash_scripts()
    {
        RequireUnixShell("bash");
        var result = await Run(new ScriptRequest("[[ 'x' == 'x' ]] && echo bash-works; echo ${BASH_VERSION:+versioned}")
        {
            Shell = ScriptShell.Bash,
            Timeout = TimeSpan.FromSeconds(30),
        });
        Assert.Equal("bash-works\nversioned\n", result.StdOut);
    }

    [Fact]
    public async Task Auto_finds_a_shell_on_this_machine()
    {
        RequireUnixShell();
        var result = await Run(new ScriptRequest("echo auto") { Timeout = TimeSpan.FromSeconds(30) });
        Assert.Equal("auto", result.StdOut.Trim());
    }

    [Fact]
    public async Task Stdin_is_closed_so_programs_that_read_input_do_not_hang()
    {
        RequireUnixShell();
        var result = await Run(Sh("cat; echo done", timeoutSeconds: 20));
        Assert.False(result.TimedOut);
        Assert.Equal("done", result.StdOut.Trim());
    }

    [Fact]
    public async Task The_working_directory_is_used()
    {
        RequireUnixShell();
        using var directory = new TempDirectory("cwd");
        var request = Sh("pwd") with { WorkingDirectory = directory.Path };
        var result = await Run(request);
        Assert.EndsWith(System.IO.Path.GetFileName(directory.Path), result.StdOut.Trim());
    }

    [Fact]
    public async Task Output_is_decoded_as_utf8()
    {
        RequireUnixShell();
        var result = await Run(Sh("printf 'caf\\303\\251 \\342\\202\\254'"));
        Assert.Equal("café €", result.StdOut);
    }

    [Fact]
    public async Task Output_beyond_the_limit_is_discarded_without_blocking_the_program()
    {
        RequireUnixShell();
        var request = Sh("yes | head -c 3000000") with { MaxOutputChars = 1000 };
        var result = await Run(request);
        Assert.Equal(1000, result.StdOut.Length);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task A_command_that_runs_too_long_is_stopped()
    {
        RequireUnixShell();
        var result = await Run(Sh("echo started; sleep 30", timeoutSeconds: 0.5));

        Assert.True(result.TimedOut);
        Assert.False(result.Succeeded);
        Assert.True(result.Duration < TimeSpan.FromSeconds(15), $"took {result.Duration}");
        Assert.Equal("started", result.StdOut.Trim()); // what was printed before the kill is kept
    }

    [Fact]
    public async Task A_timeout_kills_the_whole_process_tree()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && ExecutableLocator.Find("sh") is not null, "needs Linux /proc and sh");

        // The shell starts a background sleep and prints its pid; killing only the shell would leave it running.
        var result = await Run(Sh("sleep 120 & echo $!; wait", timeoutSeconds: 1));

        Assert.True(result.TimedOut);
        var pid = int.Parse(result.StdOut.Trim());
        Assert.True(await BecomesDeadAsync(pid), $"child process {pid} survived the timeout");
    }

    [Fact]
    public async Task Cancelling_kills_the_tree_and_throws()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && ExecutableLocator.Find("sh") is not null, "needs Linux /proc and sh");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var clock = Stopwatch.StartNew();
        var run = ScriptRunner.RunAsync(Sh("sleep 120 & echo $!; wait"), cts.Token);
        await Task.Delay(700, TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task A_background_program_holding_the_output_does_not_block_the_result()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && ExecutableLocator.Find("sh") is not null, "needs Linux and sh");

        // The shell exits at once, but its background child keeps stdout open for a few more seconds.
        var result = await Run(Sh("sleep 6 & echo started"));

        Assert.True(result.Succeeded);
        Assert.Equal("started", result.StdOut.Trim());
        Assert.True(result.Duration < TimeSpan.FromSeconds(5), $"waited {result.Duration} for the background process");
    }

    [Fact]
    public async Task Without_a_shell_the_program_is_started_directly()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows() && ExecutableLocator.Find("echo") is not null, "needs an echo program");
        var result = await Run(new ScriptRequest("echo a \"b  c\" 'd e'") { Shell = ScriptShell.None });
        Assert.Equal("a b  c d e\n", result.StdOut);
    }

    [Fact]
    public async Task A_program_that_does_not_exist_cannot_be_started()
    {
        var request = new ScriptRequest("definitely-not-a-real-program-xyz --flag") { Shell = ScriptShell.None };
        await Assert.ThrowsAnyAsync<System.ComponentModel.Win32Exception>(() => Run(request));
    }

    // ----- command construction (no processes) -----

    [Fact]
    public void An_empty_command_is_rejected()
    {
        foreach (var shell in Enum.GetValues<ScriptShell>())
        {
            Assert.Throws<ArgumentException>(() => ScriptRunner.BuildInvocation(new ScriptRequest("  \n ") { Shell = shell }));
        }
    }

    [Theory]
    [InlineData("auto", ScriptShell.Auto)]
    [InlineData("powershell", ScriptShell.PowerShell)]
    [InlineData("PWSH", ScriptShell.Pwsh)]
    [InlineData("cmd", ScriptShell.Cmd)]
    [InlineData("bash", ScriptShell.Bash)]
    [InlineData(" sh ", ScriptShell.Sh)]
    [InlineData("none", ScriptShell.None)]
    [InlineData("zsh", ScriptShell.Auto)]
    [InlineData("", ScriptShell.Auto)]
    [InlineData(null, ScriptShell.Auto)]
    public void ParseShell_understands_the_setting_values(string? text, ScriptShell expected) =>
        Assert.Equal(expected, ScriptRunner.ParseShell(text));

    [Fact]
    public void Auto_resolves_to_a_concrete_shell()
    {
        var resolved = ScriptRunner.ResolveShell(ScriptShell.Auto);
        Assert.NotEqual(ScriptShell.Auto, resolved);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(ScriptShell.PowerShell, resolved);
        }
        else
        {
            Assert.True(resolved is ScriptShell.Bash or ScriptShell.Sh);
        }

        Assert.Equal(ScriptShell.Cmd, ScriptRunner.ResolveShell(ScriptShell.Cmd));
    }

    [Fact]
    public void Bash_and_sh_receive_the_command_as_one_argument()
    {
        const string command = "echo \"it's\"\necho $HOME | tr a b";
        var bash = ScriptRunner.BuildInvocation(new ScriptRequest(command) { Shell = ScriptShell.Bash });
        Assert.Equal("bash", bash.FileName);
        Assert.Equal(["-c", command], bash.Arguments);

        var sh = ScriptRunner.BuildInvocation(new ScriptRequest(command) { Shell = ScriptShell.Sh });
        Assert.Equal("sh", sh.FileName);
        Assert.Equal(["-c", command], sh.Arguments);
        Assert.Null(sh.RawArguments);
    }

    [Fact]
    public void PowerShell_gets_the_script_encoded_and_with_utf8_output()
    {
        const string command = "Write-Output \"it's $(1 + 1)\"\r\nGet-Date";
        var invocation = ScriptRunner.BuildInvocation(new ScriptRequest(command) { Shell = ScriptShell.Pwsh });

        Assert.Equal("pwsh", invocation.FileName);
        Assert.Contains("-NoProfile", invocation.Arguments);
        Assert.Contains("-NonInteractive", invocation.Arguments);
        var encodedIndex = invocation.Arguments.ToList().IndexOf("-EncodedCommand");
        Assert.True(encodedIndex >= 0);
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(invocation.Arguments[encodedIndex + 1]));
        Assert.EndsWith(command, script);
        Assert.Contains("[Console]::OutputEncoding=[System.Text.Encoding]::UTF8", script);
        Assert.Equal(OperatingSystem.IsWindows(), invocation.Arguments.Contains("Bypass"));
    }

    [Fact]
    public void Windows_powershell_is_powershell_exe_on_windows_and_pwsh_elsewhere()
    {
        var invocation = ScriptRunner.BuildInvocation(new ScriptRequest("Get-Date") { Shell = ScriptShell.PowerShell });
        Assert.Equal(OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh", invocation.FileName);
    }

    [Fact]
    public void An_oversized_powershell_command_is_refused_with_a_hint()
    {
        var huge = new ScriptRequest(new string('x', 20_000)) { Shell = ScriptShell.Pwsh };
        var error = Assert.Throws<InvalidOperationException>(() => ScriptRunner.BuildInvocation(huge));
        Assert.Contains("too long", error.Message);
    }

    [Fact]
    public void Cmd_runs_from_a_temporary_batch_file_that_is_cleaned_up()
    {
        var invocation = ScriptRunner.BuildInvocation(new ScriptRequest("echo one\necho two") { Shell = ScriptShell.Cmd });
        try
        {
            Assert.Equal("cmd.exe", invocation.FileName);
            Assert.NotNull(invocation.TempFile);
            Assert.EndsWith(".cmd", invocation.TempFile);
            Assert.Equal($"/d /s /c \"\"{invocation.TempFile}\"\"", invocation.RawArguments);

            var text = File.ReadAllText(invocation.TempFile);
            Assert.StartsWith("@echo off\r\n", text);
            Assert.Contains("echo one\r\necho two\r\n", text);
        }
        finally
        {
            invocation.Cleanup();
        }

        Assert.False(File.Exists(invocation.TempFile));
    }

    [Fact]
    public void Without_a_shell_the_first_word_is_the_program()
    {
        var invocation = ScriptRunner.BuildInvocation(new ScriptRequest("\"C:\\Tools\\my app.exe\" --level 5 \"two words\"") { Shell = ScriptShell.None });
        Assert.Equal("C:\\Tools\\my app.exe", invocation.FileName);
        Assert.Equal(["--level", "5", "two words"], invocation.Arguments);
    }
}

public class ExecutableLocatorTests
{
    [Fact]
    public void Finds_programs_on_the_given_path_and_rejects_missing_ones()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "uses Unix permission bits");
        using var directory = new TempDirectory("path");
        var tool = directory.Write("mytool", "#!/bin/sh\n");
        File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var plain = directory.Write("plainfile", "data");
        File.SetUnixFileMode(plain, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var searchPath = directory.Path + System.IO.Path.PathSeparator + "/definitely/not/here";
        Assert.Equal(tool, ExecutableLocator.Find("mytool", searchPath));
        Assert.Null(ExecutableLocator.Find("plainfile", searchPath)); // present but not executable
        Assert.Null(ExecutableLocator.Find("nothing-here", searchPath));
        Assert.Null(ExecutableLocator.Find("", searchPath));
        Assert.Equal(tool, ExecutableLocator.Find(tool)); // a path is checked as it is
        Assert.Equal(tool, ExecutableLocator.Find("mytool", "", [directory.Path])); // extra directories
    }
}
