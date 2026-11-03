// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.DataManager.Services;

/// <summary>
/// Reads where keyframes sit in a file from its index, without decoding anything, so it takes a moment even for a large file.
/// </summary>
public static class KeyframeIndex
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Reads the keyframe at or before each of a number of evenly spaced points through a file.
    /// </summary>
    /// <param name="probePath">The path to ffprobe.</param>
    /// <param name="path">The file.</param>
    /// <param name="durationSeconds">The file's length.</param>
    /// <param name="points">How many points to read. Each is one seek in the index.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The keyframes, in order, without repeats.</returns>
    /// <exception cref="TimeoutException">ffprobe took too long.</exception>
    /// <exception cref="InvalidOperationException">ffprobe failed.</exception>
    public static async Task<IReadOnlyList<Keyframe>> ReadAsync(string probePath, string path, double durationSeconds, int points, CancellationToken cancellationToken)
    {
        var intervals = string.Join(',', Enumerable.Range(0, points)
            .Select(i => (durationSeconds * i / points).ToString("0.###", CultureInfo.InvariantCulture) + "%+#1"));
        var (output, _) = await FfmpegRunner.RunAsync(
            probePath,
            ["-v", "error", "-select_streams", "v:0", "-read_intervals", intervals, "-show_entries", "packet=pts_time,pos", "-of", "csv=p=0", path],
            _timeout,
            "read the file's index",
            cancellationToken).ConfigureAwait(false);

        var keyframes = new SortedDictionary<double, long>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split(',');
            if (fields.Length >= 2
                && double.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                && long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var position))
            {
                keyframes.TryAdd(seconds, position);
            }
        }

        return keyframes.Select(k => new Keyframe(k.Key, k.Value)).ToList();
    }

    /// <summary>
    /// Gets the stretches between keyframes and their bitrates. Bytes between two keyframes over the time between
    /// them is how many bits the original encoder needed there, which tracks how busy the picture is.
    /// </summary>
    /// <param name="keyframes">The keyframes, in order.</param>
    /// <returns>The stretches, or none if there are too few to say anything.</returns>
    public static IReadOnlyList<Stretch> Stretches(IReadOnlyList<Keyframe> keyframes)
    {
        ArgumentNullException.ThrowIfNull(keyframes);

        var stretches = new List<Stretch>();
        for (var i = 1; i < keyframes.Count; i++)
        {
            var seconds = keyframes[i].Seconds - keyframes[i - 1].Seconds;
            var bytes = keyframes[i].Position - keyframes[i - 1].Position;
            if (seconds >= 1 && bytes > 0)
            {
                stretches.Add(new Stretch(keyframes[i - 1].Seconds, seconds, bytes * 8 / seconds));
            }
        }

        return stretches.Count >= 4 ? stretches : [];
    }
}

/// <summary>
/// A keyframe: where it plays and where it sits in the file.
/// </summary>
/// <param name="Seconds">When it plays.</param>
/// <param name="Position">Its byte position in the file.</param>
public readonly record struct Keyframe(double Seconds, long Position);

/// <summary>
/// A stretch of a file between two keyframes, and its bitrate.
/// </summary>
/// <param name="Start">When it starts, in seconds.</param>
/// <param name="Seconds">How long it lasts.</param>
/// <param name="Bitrate">Its bitrate, in bits per second, counting every track.</param>
public sealed record Stretch(double Start, double Seconds, double Bitrate);
