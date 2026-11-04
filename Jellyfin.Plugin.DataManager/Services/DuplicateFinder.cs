// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.DataManager.Models;

namespace Jellyfin.Plugin.DataManager.Services;

/// <summary>
/// Finds movies and episodes that are in the library more than once, and picks the copy with the best picture.
/// </summary>
/// <remarks>
/// Copies are matched on Jellyfin's own versions, on a movie's IMDb, TMDB or TVDB id, on its name and year, and on an
/// episode's series, season and episode number. Extras are never matched. Nothing is deleted here: the page lists
/// the copies, and the user picks which to delete and confirms.
/// </remarks>
public static class DuplicateFinder
{
    // Copies whose bitrates are this close count as the same picture, so audio, subtitles and size decide.
    private const double SimilarRate = 1.1;

    // Runtimes further apart than this are probably different cuts, which the page warns about.
    private const double MinimumRuntimeGapSeconds = 120;
    private const double MinimumRuntimeGapShare = 0.05;

    /// <summary>
    /// Gets the providers whose ids a movie is matched on.
    /// </summary>
    public static IReadOnlyList<string> ProviderNames { get; } = ["Imdb", "Tmdb", "Tvdb"];

    /// <summary>
    /// Finds the titles with more than one copy.
    /// </summary>
    /// <param name="report">The last scan, or null if none has run.</param>
    /// <param name="keptPaths">The re-encodes the user chose to keep. With this, an original and a re-encode still waiting to be accepted are left to Smaller files.</param>
    /// <returns>The titles, the most space to free first.</returns>
    public static DuplicateReport Find(StorageReport? report, IReadOnlySet<string>? keptPaths = null)
    {
        var result = new DuplicateReport();
        if (report is null)
        {
            return result;
        }

        result.GeneratedAt = report.GeneratedAt;
        result.NeedsScan = report.Files.Count > 0 && report.Files.All(f => string.IsNullOrEmpty(f.Kind));

        var reEncodes = SmallerFiles.Find(report).Select(f => f.OutputId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = report.Files.Where(f => !f.IsExtra).ToList();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < files.Count; i++)
        {
            index.TryAdd(files[i].ItemId, i);
        }

        var parents = Enumerable.Range(0, files.Count).ToArray();
        int Root(int i)
        {
            while (parents[i] != i)
            {
                parents[i] = parents[parents[i]];
                i = parents[i];
            }

            return i;
        }

        void Join(int a, int b) => parents[Root(a)] = Root(b);

        // Every file that shares a key with another is the same title. A file is often matched on several keys.
        var byKey = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < files.Count; i++)
        {
            foreach (var key in Keys(files[i]))
            {
                if (!byKey.TryGetValue(key, out var members))
                {
                    byKey[key] = members = [];
                }

                members.Add(i);
            }

            foreach (var other in files[i].OtherVersionIds)
            {
                if (index.TryGetValue(other, out var j))
                {
                    Join(i, j);
                }
            }
        }

        foreach (var members in byKey.Values.Where(m => m.Count > 1))
        {
            for (var k = 1; k < members.Count; k++)
            {
                Join(members[0], members[k]);
            }
        }

        var matchedOn = new Dictionary<int, SortedSet<string>>();
        foreach (var (key, members) in byKey.Where(p => p.Value.Count > 1))
        {
            var root = Root(members[0]);
            if (!matchedOn.TryGetValue(root, out var labels))
            {
                matchedOn[root] = labels = new SortedSet<string>(StringComparer.Ordinal);
            }

            labels.Add(KeyLabel(key));
        }

        foreach (var copies in Enumerable.Range(0, files.Count).GroupBy(Root).Where(g => g.Count() > 1))
        {
            var labels = matchedOn.TryGetValue(copies.Key, out var found) ? found.ToList() : [];
            var group = Build(copies.Select(i => files[i]).ToList(), labels, reEncodes);

            // Smaller files offers an original and its kept re-encode, so counting them here too would count them twice.
            var bestPath = group.Copies[0].Path;
            if (keptPaths is not null && group.Copies.Count == 2 && reEncodes.Contains(group.BestItemId) && !keptPaths.Contains(bestPath))
            {
                result.CoveredBySmallerFiles.Add(group.Title);
                continue;
            }

            result.Groups.Add(group);
        }

        var ordered = result.Groups.OrderByDescending(g => g.ReclaimableBytes).ThenBy(g => g.Title, StringComparer.CurrentCulture).ToList();
        result.Groups.Clear();
        foreach (var group in ordered)
        {
            result.Groups.Add(group);
        }

        result.ReclaimableBytes = result.Groups.Sum(g => g.ReclaimableBytes);
        return result;
    }

    /// <summary>
    /// Gets the height a video is sold as, such as 1080 for 1920x800, so letterboxed and full-frame copies compare fairly.
    /// </summary>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    /// <returns>2160, 1440, 1080, 720, 576 or 480, or 0 if unknown.</returns>
    public static int ResolutionClass(int width, int height)
    {
        var size = Math.Max(height, width * 9 / 16);
        return size switch
        {
            >= 2000 => 2160,
            >= 1300 => 1440,
            >= 1000 => 1080,
            >= 700 => 720,
            >= 560 => 576,
            > 0 => 480,
            _ => 0
        };
    }

    private static DuplicateGroup Build(List<MediaFileInfo> copies, List<string> labels, HashSet<string> reEncodes)
    {
        // Resolution first, then HDR, then the most picture data for the codec.
        var ranked = copies
            .OrderByDescending(f => ResolutionClass(f.Width, f.Height))
            .ThenByDescending(f => f.IsHdr)
            .ThenByDescending(EffectiveRate)
            .ThenBy(f => f.Path, StringComparer.Ordinal)
            .ToList();

        // Copies with the same resolution and a similar bitrate look the same, so the one with more tracks, then the smaller one, wins.
        var top = ranked[0];
        var topRate = EffectiveRate(top);
        var alike = ranked
            .Where(f => ResolutionClass(f.Width, f.Height) == ResolutionClass(top.Width, top.Height) && f.IsHdr == top.IsHdr && EffectiveRate(f) * SimilarRate >= topRate)
            .ToList();
        var best = alike
            .OrderByDescending(f => f.AudioTracks)
            .ThenByDescending(f => f.SubtitleTracks)
            .ThenBy(f => f.SizeBytes)
            .ThenBy(f => f.Path, StringComparer.Ordinal)
            .First();

        // A smaller re-encode has less picture data than its original by design, so going by the picture alone would
        // always keep the original. The user made it to save space, so it's the one to keep.
        var reEncode = ranked.FirstOrDefault(f => reEncodes.Contains(f.ItemId));
        var isReEncode = reEncode is not null;
        best = reEncode ?? best;
        ranked.Remove(best);
        ranked.Insert(0, best);

        if (copies.Any(f => f.OtherVersionIds.Any(id => copies.Any(o => string.Equals(o.ItemId, id, StringComparison.OrdinalIgnoreCase)))))
        {
            labels.Insert(0, "Jellyfin versions");
        }

        var group = new DuplicateGroup
        {
            Title = Title(best),
            MatchedOn = string.Join(", ", labels.Distinct(StringComparer.Ordinal)),
            BestItemId = best.ItemId,
            BestReason = isReEncode ? "Your smaller re-encode. Compare them before deleting the original" : Reason(best, ranked[1], alike.Count > 1),
            ReclaimableBytes = ranked.Skip(1).Sum(f => f.SizeBytes)
        };
        foreach (var copy in ranked)
        {
            group.Copies.Add(copy);
        }

        var runtimes = copies.Where(f => f.DurationSeconds > 0).Select(f => f.DurationSeconds).ToList();
        if (runtimes.Count > 1)
        {
            var gap = runtimes.Max() - runtimes.Min();
            if (gap > Math.Max(MinimumRuntimeGapSeconds, runtimes.Max() * MinimumRuntimeGapShare))
            {
                group.DifferentCuts = true;
                group.Notes.Add(string.Create(CultureInfo.InvariantCulture, $"The runtimes differ by {Math.Round(gap / 60)} minutes, so these may be different cuts or editions."));
            }
        }

        if (copies.Select(f => f.Library).Distinct(StringComparer.Ordinal).Count() > 1)
        {
            group.Notes.Add("The copies are in different libraries.");
        }

        return group;
    }

    private static string Reason(MediaFileInfo best, MediaFileInfo next, bool lookAlike)
    {
        var bestClass = ResolutionClass(best.Width, best.Height);
        if (!lookAlike)
        {
            if (bestClass != ResolutionClass(next.Width, next.Height))
            {
                return string.Create(CultureInfo.InvariantCulture, $"Highest resolution ({bestClass}p)");
            }

            if (best.IsHdr != next.IsHdr)
            {
                return "HDR";
            }

            return "Higher bitrate for its codec, so more detail";
        }

        if (best.AudioTracks != next.AudioTracks)
        {
            return "Same picture quality, with more audio tracks";
        }

        if (best.SubtitleTracks != next.SubtitleTracks)
        {
            return "Same picture quality, with more subtitles";
        }

        return best.SizeBytes < next.SizeBytes ? "Same picture quality, in a smaller file" : "Same picture quality";
    }

    // The bits per second the picture gets, scaled by how much more an efficient codec does with each bit.
    private static double EffectiveRate(MediaFileInfo file)
    {
        double rate = file.Bitrate > 0 ? file.Bitrate : file.DurationSeconds > 0 ? file.SizeBytes * 8 / file.DurationSeconds : 0;
        return rate * CodecEfficiency(file.VideoCodec);
    }

    private static double CodecEfficiency(string codec) => codec.ToUpperInvariant() switch
    {
        "AV1" => 2.0,
        "HEVC" or "H265" => 1.6,
        "VP9" => 1.5,
        "H264" or "AVC" => 1.0,
        "VC1" => 0.8,
        "MPEG4" or "XVID" or "DIVX" or "MSMPEG4V3" => 0.7,
        "MPEG2VIDEO" or "MPEG1VIDEO" => 0.5,
        _ => 1.0
    };

    private static IEnumerable<string> Keys(MediaFileInfo file)
    {
        if (file.Kind == "Movie")
        {
            foreach (var (provider, id) in file.ProviderIds)
            {
                yield return "movie-id:" + provider + ":" + id.ToUpperInvariant();
            }

            var name = Normalize(file.Name);
            if (file.Year is int year && name.Length > 0)
            {
                yield return string.Create(CultureInfo.InvariantCulture, $"movie-name:{name}:{year}");
            }
        }
        else if (file.Kind == "Episode" && file.SeasonNumber is int season && file.EpisodeNumber is int episode)
        {
            var number = string.Create(CultureInfo.InvariantCulture, $"{season}:{episode}:{file.EpisodeNumberEnd ?? episode}");
            if (!string.IsNullOrEmpty(file.SeriesId))
            {
                yield return "episode-series:" + file.SeriesId + ":" + number;
            }

            var series = Normalize(file.SeriesName);
            if (series.Length > 0)
            {
                yield return "episode-name:" + series + ":" + number;
            }
        }
    }

    private static string KeyLabel(string key)
    {
        if (key.StartsWith("movie-id:", StringComparison.Ordinal))
        {
            var provider = key.Split(':')[1];
            return provider switch
            {
                "Imdb" => "IMDb id",
                "Tmdb" => "TMDB id",
                "Tvdb" => "TVDB id",
                _ => provider + " id"
            };
        }

        return key.StartsWith("movie-name:", StringComparison.Ordinal) ? "name and year" : "series, season and episode";
    }

    private static string Title(MediaFileInfo file)
    {
        if (file.Kind == "Episode" && !string.IsNullOrEmpty(file.SeriesName) && file.SeasonNumber is int season && file.EpisodeNumber is int episode)
        {
            var end = file.EpisodeNumberEnd is int last && last > episode ? string.Create(CultureInfo.InvariantCulture, $"-E{last:00}") : string.Empty;
            return string.Create(CultureInfo.InvariantCulture, $"{file.SeriesName} S{season:00}E{episode:00}{end}: {file.Name}");
        }

        return file.Year is int year ? string.Create(CultureInfo.InvariantCulture, $"{file.Name} ({year})") : file.Name;
    }

    // Letters and digits only, in lower case, so "Example: Title" and "Example - Title" match.
    private static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(name.Length);
        var space = false;
        foreach (var c in name.Normalize(NormalizationForm.FormD))
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                if (space && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(char.ToLowerInvariant(c));
                space = false;
            }
            else
            {
                space = true;
            }
        }

        return builder.ToString();
    }
}
