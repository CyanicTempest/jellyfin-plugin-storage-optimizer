// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.DataManager.Models;

namespace Jellyfin.Plugin.DataManager.Services;

/// <summary>
/// Decides which files are worth suggesting for a re-encode.
/// </summary>
public static class SuggestionRules
{
    /// <summary>Gets the least a suggestion must save, in bytes, from the settings.</summary>
    public static long MinimumSavingsBytes => Math.Max(0, Plugin.Instance?.Configuration.SuggestMinimumMegabytes ?? 300) * 1024L * 1024;

    /// <summary>Gets the least share of its size a suggestion must save, from the settings.</summary>
    public static double MinimumSavingsShare => Math.Clamp(Plugin.Instance?.Configuration.SuggestMinimumPercent ?? 25, 0, 100) / 100.0;

    // Such as "300 MB" or "1 GB".
    private static string Amount(long bytes) => bytes >= 1024L * 1024 * 1024 && bytes % (1024L * 1024 * 1024) == 0
        ? (bytes / (1024L * 1024 * 1024)).ToString(System.Globalization.CultureInfo.InvariantCulture) + " GB"
        : (bytes / (1024 * 1024)).ToString(System.Globalization.CultureInfo.InvariantCulture) + " MB";

    /// <summary>
    /// Marks the files worth suggesting. A file qualifies on its own, and an episode also qualifies when its whole
    /// show does, as no single episode of a long show saves much. A title that already has another version, such as a
    /// kept re-encode, isn't suggested again.
    /// </summary>
    /// <param name="files">The files to mark.</param>
    public static void Apply(IEnumerable<MediaFileInfo> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var all = files as IReadOnlyCollection<MediaFileInfo> ?? files.ToList();

        foreach (var file in all)
        {
            file.IsRecommended = false;
            file.SuggestedReason = null;
            if (file.OtherVersionIds.Count == 0 && WorthIt(file.EstimatedSavingsBytes, file.SizeBytes))
            {
                file.IsRecommended = true;
                file.SuggestedReason = "Saves over " + Amount(MinimumSavingsBytes);
            }
        }

        var shows = all
            .Where(f => f.OtherVersionIds.Count == 0 && (f.SeriesId ?? f.SeriesName) is not null)
            .GroupBy(f => f.SeriesId ?? f.SeriesName!, StringComparer.Ordinal);
        foreach (var show in shows)
        {
            if (!WorthIt(show.Sum(f => f.EstimatedSavingsBytes), show.Sum(f => f.SizeBytes)))
            {
                continue;
            }

            // The show earns the suggestion, but an episode that would barely shrink isn't worth re-encoding.
            foreach (var episode in show.Where(e => e.EstimatedSavingsBytes >= e.SizeBytes * MinimumSavingsShare))
            {
                episode.IsRecommended = true;
                episode.SuggestedReason = "Saves over " + Amount(MinimumSavingsBytes) + " across the show";
            }
        }
    }

    private static bool WorthIt(long saving, long size) => saving >= MinimumSavingsBytes && saving >= size * MinimumSavingsShare;
}
