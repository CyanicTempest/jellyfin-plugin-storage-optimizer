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
    /// <returns>What's wrong, as a sentence, or null if the file is fine.</returns>
    public static string? Problem(IReadOnlyList<TrackInfo> source, string outputJson, TrackPlan? plan, Func<string?, string?> normalize)
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

        return TrackSelection.KeepsMainAudio(source, output) ? null : "The new file has no main audio, only a commentary or none at all.";
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

            var picture = stream.TryGetProperty("disposition", out var disposition)
                && disposition.TryGetProperty("attached_pic", out var pic)
                && pic.ValueKind == JsonValueKind.Number && pic.GetInt32() == 1;
            if (!picture)
            {
                count++;
            }
        }

        return count;
    }
}
