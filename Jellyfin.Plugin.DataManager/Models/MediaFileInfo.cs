// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.DataManager.Models;

/// <summary>
/// Storage details for one media file.
/// </summary>
public class MediaFileInfo
{
    /// <summary>Gets or sets the Jellyfin item id.</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>Gets or sets the item name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the full file path.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the folder that contains the file.</summary>
    public string Folder { get; set; } = string.Empty;

    /// <summary>Gets or sets the library the file belongs to.</summary>
    public string Library { get; set; } = string.Empty;

    /// <summary>Gets or sets the file size in bytes.</summary>
    public long SizeBytes { get; set; }

    /// <summary>Gets or sets the container, such as mkv or mp4.</summary>
    public string Container { get; set; } = string.Empty;

    /// <summary>Gets or sets the video codec, such as h264 or hevc.</summary>
    public string VideoCodec { get; set; } = string.Empty;

    /// <summary>Gets or sets the video width in pixels.</summary>
    public int Width { get; set; }

    /// <summary>Gets or sets the video height in pixels.</summary>
    public int Height { get; set; }

    /// <summary>Gets or sets the overall bitrate in bits per second.</summary>
    public long Bitrate { get; set; }

    /// <summary>Gets or sets the duration in seconds.</summary>
    public double DurationSeconds { get; set; }

    /// <summary>Gets or sets a value indicating whether the video is HDR or Dolby Vision.</summary>
    public bool IsHdr { get; set; }

    /// <summary>Gets or sets the number of audio tracks in the file.</summary>
    public int AudioTracks { get; set; }

    /// <summary>Gets or sets the languages of the audio tracks, by name, such as English. Tracks with no language set aren't listed.</summary>
    public IReadOnlyList<string> AudioLanguages { get; set; } = [];

    /// <summary>Gets or sets the number of subtitle tracks inside the file. Subtitle files beside it aren't counted.</summary>
    public int SubtitleTracks { get; set; }

    /// <summary>Gets or sets the languages of the subtitle tracks inside the file, by name.</summary>
    public IReadOnlyList<string> SubtitleLanguages { get; set; } = [];

    /// <summary>Gets or sets the estimated bytes saved by re-encoding to HEVC.</summary>
    public long EstimatedSavingsBytes { get; set; }

    /// <summary>Gets or sets a value indicating whether the file is worth re-encoding, under the current settings.</summary>
    public bool IsRecommended { get; set; }

    /// <summary>
    /// Gets or sets why the file is suggested, or null if it isn't.
    /// </summary>
    public string? SuggestedReason { get; set; }

    /// <summary>Gets or sets the height of the smallest size that keeps the file's real detail, or 0 if it hasn't been analysed.</summary>
    public int DetailHeight { get; set; }

    /// <summary>Gets or sets the width of the smallest size that keeps the file's real detail, or 0 if it hasn't been analysed.</summary>
    public int DetailWidth { get; set; }

    /// <summary>Gets or sets the item ids ("N" format) of the other versions of the same title, such as a kept re-encode.</summary>
    public IReadOnlyList<string> OtherVersionIds { get; set; } = [];

    /// <summary>Gets or sets the series id ("N" format) of an episode, or null for anything that isn't one.</summary>
    public string? SeriesId { get; set; }

    /// <summary>Gets or sets the series name of an episode.</summary>
    public string? SeriesName { get; set; }

    /// <summary>Gets or sets the season number of an episode, with 0 for specials, or null if it isn't known.</summary>
    public int? SeasonNumber { get; set; }

    /// <summary>Gets or sets the season name of an episode, such as "Season 1" or "Specials".</summary>
    public string? SeasonName { get; set; }

    /// <summary>Gets or sets the episode number, or null if it isn't known.</summary>
    public int? EpisodeNumber { get; set; }

    /// <summary>Gets or sets the last episode number of a file that holds several episodes, or null.</summary>
    public int? EpisodeNumberEnd { get; set; }

    /// <summary>Gets or sets the kind of extra, such as "Trailer" or "Sample", or null for the main feature or an episode.</summary>
    public string? ExtraType { get; set; }

    /// <summary>Gets or sets what the item is: Movie, Episode or Video. Empty in reports from before duplicates were looked for.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets the year a movie came out, or null if it isn't known.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets the movie's ids on IMDb, TMDB and TVDB, by provider name. Empty for anything else.</summary>
    public IReadOnlyDictionary<string, string> ProviderIds { get; set; } = new Dictionary<string, string>();

    /// <summary>Gets or sets a value indicating whether the file is an extra, such as a trailer or behind the scenes.</summary>
    public bool IsExtra { get; set; }

    /// <summary>Gets or sets when the file was added to the library, or null if it isn't known.</summary>
    public DateTime? DateAdded { get; set; }

    /// <summary>Gets or sets when anyone on the server last watched it, or any other version of it, or null if nobody has.</summary>
    public DateTime? LastPlayed { get; set; }

    /// <summary>Gets or sets how many times it, and the other versions of it, have been played, across every user.</summary>
    public int PlayCount { get; set; }

    /// <summary>Gets or sets a value indicating whether the watch history could be read. If not, <see cref="LastPlayed"/> means nothing.</summary>
    public bool HistoryKnown { get; set; }
}
