// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Jellyfin.Plugin.DataManager.Encoding;

/// <summary>
/// Checks a finished file before it's placed: it has its main video, every audio and subtitle track the plan kept,
/// and its film's sound.
/// </summary>
internal static class OutputCheck
{
    /// <summary>
    /// Finds what's wrong with a new file, if anything.
    /// </summary>
    /// <param name="source">The original's audio and subtitle tracks, from ffprobe.</param>
    /// <param name="outputJson">ffprobe's JSON for the new file, with codec_type and the attached_pic disposition.</param>
    /// <param name="plan">The tracks the encode kept, or null when it kept them all.</param>
    /// <param name="normalize">Turns a language tag into the code it's matched by.</param>
    /// <param name="sourceJson">ffprobe's JSON for the original, with stream durations, to check the new file's audio is in step; or null not to.</param>
    /// <returns>What's wrong, as a sentence, or null if the file is fine.</returns>
    public static string? Problem(IReadOnlyList<TrackInfo> source, string outputJson, TrackPlan? plan, Func<string?, string?> normalize, string? sourceJson = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(outputJson);
        int videos;
        IReadOnlyList<TrackInfo> output;
        try
        {
            videos = MainVideoStreams(outputJson);
            output = EncodeService.ParseTracks(outputJson, normalize);
        }
        catch (JsonException)
        {
            return "The new file couldn't be read.";
        }

        if (videos != 1)
        {
            return videos == 0 ? "The new file has no video." : "The new file has " + videos + " video streams instead of one.";
        }

        var expected = plan?.Kept ?? source;
        var problems = new List<string>();
        foreach (var (kind, word) in new[] { (TrackKind.Audio, "audio"), (TrackKind.Subtitle, "subtitle") })
        {
            var want = expected.Count(t => t.Kind == kind);
            var got = output.Count(t => t.Kind == kind);
            if (want != got)
            {
                problems.Add(got + " " + word + (got == 1 ? " track" : " tracks") + " instead of " + want);
            }
        }

        if (problems.Count > 0)
        {
            return "The new file has " + string.Join(" and ", problems) + ".";
        }

        if (!TrackSelection.KeepsMainAudio(source, output))
        {
            return "The new file has no main audio, only a commentary or none at all.";
        }

        // Its main audio should run as long as its video, as the original's did.
        if (sourceJson is not null && Drift(outputJson) is { } after)
        {
            var before = Drift(sourceJson, plan?.Kept.FirstOrDefault(t => t.Kind == TrackKind.Audio)?.Index) ?? 0;
            if (Math.Abs(after) > Math.Max(1.0, Math.Abs(before) + 0.5))
            {
                return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"The new file's audio runs {Math.Abs(after):F1}s {(after > 0 ? "longer" : "shorter")} than its video, so it would play out of step.");
            }
        }

        return null;
    }

    /// <summary>
    /// Gets how much longer a file's first audio track (or the one given) runs than its main video, from the lengths
    /// the file records.
    /// </summary>
    /// <param name="json">ffprobe's JSON with stream durations and tags.</param>
    /// <param name="audioIndex">The stream index of the audio track to measure, or null for the first.</param>
    /// <returns>The difference in seconds, or null when the lengths aren't recorded.</returns>
    public static double? Drift(string json, int? audioIndex = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("streams", out var streams))
            {
                return null;
            }

            double? video = null;
            double? audio = null;
            foreach (var stream in streams.EnumerateArray())
            {
                var type = stream.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                if (type == "video" && video is null && !IsPicture(stream))
                {
                    video = Seconds(stream);
                }
                else if (type == "audio" && audio is null
                    && (audioIndex is null || (stream.TryGetProperty("index", out var index) && index.GetInt32() == audioIndex)))
                {
                    audio = Seconds(stream);
                }
            }

            return video is { } v && audio is { } a ? a - v : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Gets whether a file's main video says what colours it's in: its primaries, transfer and matrix.
    /// </summary>
    /// <param name="json">ffprobe's JSON with color_primaries, color_transfer and color_space.</param>
    /// <returns>True when all three are set.</returns>
    public static bool HasColourTags(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("streams", out var streams))
            {
                return false;
            }

            foreach (var stream in streams.EnumerateArray())
            {
                if (stream.TryGetProperty("codec_type", out var t) && t.GetString() == "video" && !IsPicture(stream))
                {
                    static bool Set(JsonElement s, string name) => s.TryGetProperty(name, out var v) && v.GetString() is { Length: > 0 } text && text != "unknown" && text != "reserved";
                    return Set(stream, "color_primaries") && Set(stream, "color_transfer") && Set(stream, "color_space");
                }
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Gets whether a line ffmpeg printed while reading a file is about broken timestamps.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <returns>True for timestamps that go backwards, repeat or are missing.</returns>
    public static bool IsTimestampError(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return line.Contains("non monotonically increasing", StringComparison.OrdinalIgnoreCase)
            || line.Contains("non-monotonous", StringComparison.OrdinalIgnoreCase)
            || line.Contains("out of order", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Invalid timestamps", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Timestamps are unset", StringComparison.OrdinalIgnoreCase)
            || line.Contains("pts has no value", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPicture(JsonElement stream)
        => stream.TryGetProperty("disposition", out var disposition)
            && disposition.TryGetProperty("attached_pic", out var pic)
            && pic.ValueKind == JsonValueKind.Number && pic.GetInt32() == 1;

    // A stream's length: mp4 keeps it on the stream, matroska in a DURATION tag.
    private static double? Seconds(JsonElement stream)
    {
        if (stream.TryGetProperty("duration", out var duration)
            && double.TryParse(duration.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
        {
            return seconds;
        }

        if (stream.TryGetProperty("tags", out var tags))
        {
            foreach (var tag in tags.EnumerateObject())
            {
                var parts = tag.Name.StartsWith("DURATION", StringComparison.OrdinalIgnoreCase) ? tag.Value.GetString()?.Split(':') : null;
                if (parts is { Length: 3 }
                    && int.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var h)
                    && int.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var m)
                    && double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sec))
                {
                    return (h * 3600) + (m * 60) + sec;
                }
            }
        }

        return null;
    }

    // Video streams that aren't cover art.
    private static int MainVideoStreams(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("streams", out var streams))
        {
            return 0;
        }

        var count = 0;
        foreach (var stream in streams.EnumerateArray())
        {
            if (!stream.TryGetProperty("codec_type", out var type) || type.GetString() != "video")
            {
                continue;
            }

            if (!IsPicture(stream))
            {
                count++;
            }
        }

        return count;
    }
}
