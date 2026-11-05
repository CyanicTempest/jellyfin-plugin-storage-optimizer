// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Globalization;
using System.Text.Json;

namespace Jellyfin.Plugin.DataManager.Encoding;

/// <summary>
/// How long a file's main video runs, if the file says, and how long the file as a whole runs.
/// </summary>
/// <param name="VideoSeconds">The main video's length, or null when the file doesn't record it.</param>
/// <param name="FileSeconds">The longest of all the file's tracks, or 0 if unknown.</param>
internal readonly record struct MediaLengths(double? VideoSeconds, double FileSeconds);

/// <summary>
/// Decides whether a new file runs as long as its original.
/// </summary>
/// <remarks>
/// A file's length is that of its longest track, which needn't be the video: a dub can run minutes past the film.
/// When such a track is removed the new file is rightly shorter, so the videos are compared, not the files.
/// </remarks>
internal static class DurationCheck
{
    /// <summary>
    /// Gets the arguments for ffprobe that <see cref="Parse"/> reads. Capital V leaves out cover art.
    /// </summary>
    public static string[] ProbeArguments(string path) =>
        ["-v", "error", "-select_streams", "V:0", "-show_entries", "stream=duration:stream_tags:format=duration", "-of", "json", path];

    /// <summary>
    /// Reads the lengths from ffprobe's output.
    /// </summary>
    /// <param name="ffprobeJson">What ffprobe printed for <see cref="ProbeArguments"/>.</param>
    /// <returns>The lengths found.</returns>
    public static MediaLengths Parse(string ffprobeJson)
    {
        try
        {
            using var document = JsonDocument.Parse(ffprobeJson);
            var root = document.RootElement;
            var file = root.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var total)
                ? Seconds(total.GetString())
                : null;
            double? video = null;
            if (root.TryGetProperty("streams", out var streams) && streams.GetArrayLength() > 0)
            {
                video = VideoSeconds(streams[0]);
            }

            return new MediaLengths(video, file ?? 0);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    /// <summary>
    /// Checks that the new file runs as long as the original, to within 1% or two seconds.
    /// </summary>
    /// <param name="source">The original's lengths.</param>
    /// <param name="output">The new file's lengths.</param>
    /// <returns>Whether they match.</returns>
    public static bool Matches(MediaLengths source, MediaLengths output)
    {
        if (output.FileSeconds <= 0)
        {
            return false;
        }

        var (expected, actual) = source.VideoSeconds is { } sourceVideo && output.VideoSeconds is { } outputVideo
            ? (sourceVideo, outputVideo)
            : (source.FileSeconds, output.FileSeconds);
        return Math.Abs(expected - actual) <= Math.Max(2, expected * 0.01);
    }

    // mp4 keeps the length on the stream; matroska in a tag, sometimes named DURATION-eng.
    private static double? VideoSeconds(JsonElement stream)
    {
        if (stream.TryGetProperty("duration", out var duration) && Seconds(duration.GetString()) is { } seconds)
        {
            return seconds;
        }

        if (stream.TryGetProperty("tags", out var tags))
        {
            foreach (var tag in tags.EnumerateObject())
            {
                if (tag.Name.StartsWith("DURATION", StringComparison.OrdinalIgnoreCase) && TagSeconds(tag.Value.GetString()) is { } tagged)
                {
                    return tagged;
                }
            }
        }

        return null;
    }

    private static double? Seconds(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0 ? seconds : null;

    // Written as 01:41:41.095000000, with more decimals than TimeSpan accepts.
    private static double? TagSeconds(string? text)
    {
        var parts = text?.Split(':');
        return parts is { Length: 3 }
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            && (hours * 3600) + (minutes * 60) + seconds is > 0 and var total
            ? total
            : null;
    }
}
