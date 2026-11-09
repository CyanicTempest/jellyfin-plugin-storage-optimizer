// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DataManager.Services;
using Xunit;

namespace Jellyfin.Plugin.DataManager.Tests;

public sealed class FfmpegRunnerTests
{
    [Fact]
    public void LastFrame_reads_the_newest_progress_line()
    {
        const string Errors = "Stream mapping:\nframe=    3 fps=0.5 q=28.0 size=       1KiB\rframe=   17 fps=1.2 q=28.0 size=      12KiB\r";

        Assert.Equal(17, FfmpegRunner.LastFrame(Errors));
    }

    [Fact]
    public void LastFrame_is_minus_one_before_any_progress()
    {
        Assert.Equal(-1, FfmpegRunner.LastFrame("Input #0, matroska,webm, from 'a.mkv':\n"));
    }

    [Fact]
    public async Task RunWatchedAsync_stops_a_process_doing_nothing()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var ex = await Assert.ThrowsAsync<FfmpegTimeoutException>(
            () => FfmpegRunner.RunWatchedAsync("sleep", ["60"], TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30), "encode the preview", CancellationToken.None));

        Assert.True(ex.Stalled);
        Assert.StartsWith("ffmpeg stopped making progress while trying to encode the preview", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunWatchedAsync_leaves_a_busy_process_to_the_limit()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Busy on the CPU but never printing a frame, like an encoder filling its look-ahead.
        var ex = await Assert.ThrowsAsync<FfmpegTimeoutException>(
            () => FfmpegRunner.RunWatchedAsync("sh", ["-c", "while :; do :; done"], TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(6), "encode the preview", CancellationToken.None));

        Assert.False(ex.Stalled);
    }

    [Fact]
    public async Task RunWatchedAsync_returns_what_a_finished_run_printed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var (output, errors) = await FfmpegRunner.RunWatchedAsync("sh", ["-c", "echo out; echo frame=1 >&2"], TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), "encode the preview", CancellationToken.None);

        Assert.Equal("out\n", output);
        Assert.Equal("frame=1\n", errors);
    }
}
