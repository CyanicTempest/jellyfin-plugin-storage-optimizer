// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.DataManager.Services;

/// <summary>
/// Runs ffmpeg or ffprobe to the end and collects what it printed.
/// </summary>
internal static class FfmpegRunner
{
    /// <summary>
    /// Runs a tool and waits for it to finish.
    /// </summary>
    /// <param name="tool">The path to ffmpeg or ffprobe.</param>
    /// <param name="args">The arguments, one per entry.</param>
    /// <param name="timeout">How long to wait before stopping it.</param>
    /// <param name="what">What the run does, to finish "ffmpeg could not …", such as "take the frames".</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What the tool wrote to standard output and to standard error.</returns>
    /// <exception cref="TimeoutException">The run took longer than the timeout.</exception>
    /// <exception cref="InvalidOperationException">The tool failed. The message ends with its last lines of output.</exception>
    public static Task<(string Output, string Errors)> RunAsync(string tool, IEnumerable<string> args, TimeSpan timeout, string what, CancellationToken cancellationToken)
        => RunAsync(tool, args, null, null, timeout, what, cancellationToken);

    /// <summary>
    /// Runs a tool with extra environment variables, in a given folder, and waits for it to finish.
    /// </summary>
    /// <param name="tool">The program.</param>
    /// <param name="args">The arguments, one per entry.</param>
    /// <param name="environment">Environment variables to add, or null.</param>
    /// <param name="workingDirectory">The folder to run it in, or null for the current one.</param>
    /// <param name="timeout">How long to wait before stopping it.</param>
    /// <param name="what">What the run does, to finish "ffmpeg could not …".</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What the tool wrote to standard output and to standard error.</returns>
    public static Task<(string Output, string Errors)> RunAsync(string tool, IEnumerable<string> args, IDictionary<string, string>? environment, string? workingDirectory, TimeSpan timeout, string what, CancellationToken cancellationToken)
        => RunCoreAsync(tool, args, environment, workingDirectory, timeout, null, what, cancellationToken);

    /// <summary>
    /// Runs ffmpeg for as long as it keeps working, and stops it once it stops making progress. A slow encode, such as
    /// 4K at the slowest effort on an old CPU, is left to finish; one stuck waiting on a GPU or a share is stopped soon.
    /// Progress is a new frame done or more CPU time used, as ffmpeg can spend a long while filling an encoder's
    /// look-ahead before the first frame comes out.
    /// </summary>
    /// <param name="tool">The path to ffmpeg.</param>
    /// <param name="args">The arguments, one per entry.</param>
    /// <param name="stallAfter">How long it may go without progress before it's stopped.</param>
    /// <param name="limit">The longest it may run at all, however it's getting on.</param>
    /// <param name="what">What the run does, to finish "ffmpeg could not …", such as "encode the preview".</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What the tool wrote to standard output and to standard error.</returns>
    /// <exception cref="FfmpegTimeoutException">It stopped making progress, or ran past the limit.</exception>
    /// <exception cref="InvalidOperationException">The tool failed. The message ends with its last lines of output.</exception>
    public static Task<(string Output, string Errors)> RunWatchedAsync(string tool, IEnumerable<string> args, TimeSpan stallAfter, TimeSpan limit, string what, CancellationToken cancellationToken)
        => RunCoreAsync(tool, args, null, null, limit, stallAfter, what, cancellationToken);

    /// <summary>
    /// Gets the number of the last frame ffmpeg reported done in its progress lines, or -1 if there is none yet.
    /// </summary>
    /// <param name="errors">What ffmpeg has written to standard error so far, or its end.</param>
    /// <returns>The frame count.</returns>
    internal static long LastFrame(string errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        var at = errors.LastIndexOf("frame=", StringComparison.Ordinal);
        if (at < 0)
        {
            return -1;
        }

        var i = at + 6;
        while (i < errors.Length && errors[i] == ' ')
        {
            i++;
        }

        var start = i;
        while (i < errors.Length && char.IsAsciiDigit(errors[i]))
        {
            i++;
        }

        return i > start && long.TryParse(errors.AsSpan(start, i - start), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var frame) ? frame : -1;
    }

    private static async Task<(string Output, string Errors)> RunCoreAsync(string tool, IEnumerable<string> args, IDictionary<string, string>? environment, string? workingDirectory, TimeSpan timeout, TimeSpan? stallAfter, string what, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(tool)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        foreach (var (key, value) in environment ?? new Dictionary<string, string>())
        {
            startInfo.Environment[key] = value;
        }

        if (!string.IsNullOrEmpty(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        using var process = new Process { StartInfo = startInfo };
        process.Start();
        using var registration = limit.Token.Register(() => Kill(process));

        // Both streams are read at once so ffmpeg never blocks on a full pipe. Standard error is read as it comes, so
        // its progress lines can be watched.
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var errorText = new StringBuilder();
        var errors = ReadAllAsync(process.StandardError, errorText);
        var stalled = false;
        using var watching = new CancellationTokenSource();
#pragma warning disable CA2025 // The watch is awaited below, before the process and its token source are disposed.
        var watch = stallAfter is { } stall
            ? WatchAsync(process, errorText, stall, () =>
            {
                stalled = true;
                Kill(process);
            }, watching.Token)
            : Task.CompletedTask;
#pragma warning restore CA2025

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        await watching.CancelAsync().ConfigureAwait(false);
        await watch.ConfigureAwait(false);
        await errors.ConfigureAwait(false);
        string errorsRead;
        lock (errorText)
        {
            errorsRead = errorText.ToString();
        }

        var result = (await output.ConfigureAwait(false), errorsRead);

        cancellationToken.ThrowIfCancellationRequested();
        if (stalled)
        {
            throw new FfmpegTimeoutException("ffmpeg stopped making progress while trying to " + what + ", so it was stopped after " + Seconds(stallAfter!.Value) + " with nothing done.", true);
        }

        if (limit.IsCancellationRequested)
        {
            throw stallAfter is null
                ? new TimeoutException("ffmpeg took too long to " + what + ".")
                : new FfmpegTimeoutException("ffmpeg was still trying to " + what + " after " + Seconds(timeout) + ", so it was stopped.", false);
        }

        if (process.ExitCode != 0)
        {
            var tail = result.Item2.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(3);
            throw new InvalidOperationException("ffmpeg could not " + what + ": " + string.Join(" ", tail));
        }

        return result;
    }

    private static async Task ReadAllAsync(System.IO.StreamReader reader, StringBuilder text)
    {
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false)) > 0)
        {
            lock (text)
            {
                text.Append(buffer, 0, read);
            }
        }
    }

    // Checks every few seconds whether ffmpeg has done another frame or used more CPU, and gives up on it once it
    // has done neither for the stall time.
    private static async Task WatchAsync(Process process, StringBuilder errors, TimeSpan stallAfter, Action onStall, CancellationToken cancellationToken)
    {
        var lastProgress = Stopwatch.StartNew();
        long lastFrame = -1;
        var lastCpu = CpuTime(process);
        var every = TimeSpan.FromSeconds(Math.Clamp(stallAfter.TotalSeconds / 6, 1, 5));
        try
        {
            while (true)
            {
                await Task.Delay(every, cancellationToken).ConfigureAwait(false);
                string tail;
                lock (errors)
                {
                    var from = Math.Max(errors.Length - 600, 0);
                    tail = errors.ToString(from, errors.Length - from);
                }

                var frame = LastFrame(tail);
                var cpu = CpuTime(process);

                // A tenth of a second of CPU a check is a process at work; one stuck on a GPU or a read uses none.
                if (frame > lastFrame || cpu is null || lastCpu is null || cpu.Value - lastCpu.Value >= TimeSpan.FromMilliseconds(100))
                {
                    lastProgress.Restart();
                }

                lastFrame = Math.Max(frame, lastFrame);
                lastCpu = cpu;
                if (lastProgress.Elapsed >= stallAfter)
                {
                    onStall();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // It finished.
        }
    }

    // How much CPU the process has used, or null where that can't be read, which then never counts as a stall.
    private static TimeSpan? CpuTime(Process process)
    {
        try
        {
            return process.HasExited ? null : process.TotalProcessorTime;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            return null;
        }
    }

    private static string Seconds(TimeSpan span)
        => span.TotalSeconds < 120
            ? ((int)span.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture) + " seconds"
            : ((int)Math.Round(span.TotalMinutes)).ToString(System.Globalization.CultureInfo.InvariantCulture) + " minutes";

    /// <summary>
    /// Stops a process and anything it started, if it's still running.
    /// </summary>
    /// <param name="process">The process.</param>
    public static void Kill(Process process)
    {
        try
        {
            process.Kill(true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
    }
}

/// <summary>
/// ffmpeg was stopped because it stopped making progress or ran past its limit.
/// </summary>
internal sealed class FfmpegTimeoutException : TimeoutException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FfmpegTimeoutException"/> class.
    /// </summary>
    public FfmpegTimeoutException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FfmpegTimeoutException"/> class.
    /// </summary>
    /// <param name="message">What happened.</param>
    public FfmpegTimeoutException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FfmpegTimeoutException"/> class.
    /// </summary>
    /// <param name="message">What happened.</param>
    /// <param name="innerException">The cause.</param>
    public FfmpegTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FfmpegTimeoutException"/> class.
    /// </summary>
    /// <param name="message">What happened.</param>
    /// <param name="stalled">Whether ffmpeg stopped making progress, rather than running past its limit while working.</param>
    public FfmpegTimeoutException(string message, bool stalled)
        : base(message)
    {
        Stalled = stalled;
    }

    /// <summary>
    /// Gets a value indicating whether ffmpeg stopped making progress, rather than running past its limit while working.
    /// </summary>
    public bool Stalled { get; }
}
