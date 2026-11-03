// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
    public static async Task<(string Output, string Errors)> RunAsync(string tool, IEnumerable<string> args, IDictionary<string, string>? environment, string? workingDirectory, TimeSpan timeout, string what, CancellationToken cancellationToken)
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

        // Both streams are read at once so ffmpeg never blocks on a full pipe.
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        var result = (await output.ConfigureAwait(false), await errors.ConfigureAwait(false));

        cancellationToken.ThrowIfCancellationRequested();
        if (limit.IsCancellationRequested)
        {
            throw new TimeoutException("ffmpeg took too long to " + what + ".");
        }

        if (process.ExitCode != 0)
        {
            var tail = result.Item2.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(3);
            throw new InvalidOperationException("ffmpeg could not " + what + ": " + string.Join(" ", tail));
        }

        return result;
    }

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
