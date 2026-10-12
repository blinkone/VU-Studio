using System.Diagnostics;
using System.Text;

namespace VuStudio.Sources.Common;

/// <summary>What <see cref="ProcessRunner"/> should execute.</summary>
/// <param name="FileName">Executable to start (no shell is involved).</param>
public sealed record ProcessRunRequest(string FileName)
{
    /// <summary>Arguments passed verbatim, one by one, without any quoting concerns.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>A raw command line, for the rare Windows program that parses its own quoting. Overrides <see cref="Arguments"/>.</summary>
    public string? RawArguments { get; init; }

    public string? WorkingDirectory { get; init; }

    /// <summary>The process tree is killed after this long.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Environment overrides; a <c>null</c> value removes the variable.</summary>
    public IReadOnlyDictionary<string, string?>? Environment { get; init; }

    /// <summary>Output beyond this many characters per stream is read and discarded.</summary>
    public int MaxOutputChars { get; init; } = 1_048_576;

    /// <summary>Encoding of stdout and stderr; UTF-8 when <c>null</c>.</summary>
    public Encoding? OutputEncoding { get; init; }
}

/// <summary>Outcome of a finished (or killed) process.</summary>
public sealed record ProcessRunResult(int ExitCode, string StdOut, string StdErr, bool TimedOut, TimeSpan Duration)
{
    /// <summary>Exited by itself with code 0.</summary>
    public bool Succeeded => !TimedOut && ExitCode == 0;
}

/// <summary>
/// Runs a child process without a window, captures stdout and stderr, enforces a timeout by killing the
/// whole process tree, and never blocks a thread while waiting. stdin is closed immediately so programs
/// that read input do not hang.
/// </summary>
public static class ProcessRunner
{
    private static readonly TimeSpan OutputGrace = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReapGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Starts the process and waits for it. A timeout is reported through <see cref="ProcessRunResult.TimedOut"/>;
    /// cancelling <paramref name="cancellationToken"/> kills the tree and throws <see cref="OperationCanceledException"/>.
    /// Failing to start the program (not found, not executable) throws.
    /// </summary>
    public static async Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var encoding = request.OutputEncoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var info = new ProcessStartInfo(request.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = encoding,
            StandardErrorEncoding = encoding,
        };

        if (request.RawArguments is not null)
        {
            info.Arguments = request.RawArguments;
        }
        else
        {
            foreach (var argument in request.Arguments)
            {
                info.ArgumentList.Add(argument);
            }
        }

        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            info.WorkingDirectory = request.WorkingDirectory;
        }

        if (request.Environment is not null)
        {
            foreach (var (key, value) in request.Environment)
            {
                if (value is null)
                {
                    info.Environment.Remove(key);
                }
                else
                {
                    info.Environment[key] = value;
                }
            }
        }

        var clock = Stopwatch.StartNew();
        using var process = new Process { StartInfo = info };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start '{request.FileName}'.");
        }

        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The program already exited or closed its stdin; nothing to send anyway.
        }

        var stdout = new OutputCapture(process.StandardOutput, request.MaxOutputChars);
        var stderr = new OutputCapture(process.StandardError, request.MaxOutputChars);

        var timedOut = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            await ReapAsync(process).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            timedOut = true;
        }

        // The pipes normally close with the process. A grandchild that inherited them can keep them open,
        // so wait only briefly and then return what was captured; disposing the process closes the rest.
        try
        {
            await Task.WhenAll(stdout.Completion, stderr.Completion).WaitAsync(OutputGrace, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

        clock.Stop();
        var exitCode = process.HasExited ? process.ExitCode : -1;
        return new ProcessRunResult(exitCode, stdout.Text, stderr.Text, timedOut, clock.Elapsed);
    }

    private static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Already gone, or not permitted to kill it; either way there is nothing more to do.
        }
    }

    private static async Task ReapAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync().WaitAsync(ReapGrace).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
        }
    }

    /// <summary>Reads a stream to the end on the thread pool's async I/O, keeping at most a bounded amount of text.</summary>
    private sealed class OutputCapture
    {
        private readonly StreamReader _reader;
        private readonly int _max;
        private readonly StringBuilder _text = new();
        private readonly object _gate = new();

        public OutputCapture(StreamReader reader, int max)
        {
            _reader = reader;
            _max = max;
            Completion = PumpAsync();
        }

        public Task Completion { get; }

        public string Text
        {
            get
            {
                lock (_gate)
                {
                    return _text.ToString().TrimStart('﻿');
                }
            }
        }

        private async Task PumpAsync()
        {
            var buffer = new char[4096];
            try
            {
                int read;
                while ((read = await _reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
                {
                    lock (_gate)
                    {
                        var room = _max - _text.Length;
                        if (room > 0)
                        {
                            _text.Append(buffer, 0, Math.Min(read, room));
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // The process was killed or disposed while we were reading; keep what we have.
            }
        }
    }
}
