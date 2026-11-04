// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.DataManager.Encoding;
using Jellyfin.Plugin.DataManager.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.DataManager.Services;

/// <summary>
/// Describes library videos for the storage report, and keeps the saved report right when one file changes, so a
/// finished encode shows on the page without scanning every library again.
/// </summary>
public sealed class ReportUpdater
{
    private readonly ILibraryManager _libraryManager;
    private readonly LanguageCodes _languages;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReportUpdater"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="languages">Names the languages of each file's tracks.</param>
    public ReportUpdater(ILibraryManager libraryManager, LanguageCodes languages)
    {
        _libraryManager = libraryManager;
        _languages = languages;
    }

    /// <summary>
    /// Puts a finished encode into the saved report: the new file, the original if it's still there, and any other
    /// versions of the title, whose links to each other have changed.
    /// </summary>
    /// <param name="originalId">The video that was encoded.</param>
    /// <param name="sourcePath">The original's path when the encode started.</param>
    /// <param name="outputPath">The new file's path.</param>
    /// <param name="outputId">The new file's library item, if it's already known.</param>
    /// <returns>A task.</returns>
    public async Task EncodeFinishedAsync(Guid originalId, string sourcePath, string outputPath, Guid? outputId)
    {
        var items = new Dictionary<Guid, BaseItem>();
        void Add(BaseItem? item)
        {
            if (item is null)
            {
                return;
            }

            items[item.Id] = item;
            if (item is Video video)
            {
                foreach (var version in video.GetAllVersions())
                {
                    items[version.Id] = version;
                }
            }
        }

        Add(_libraryManager.GetItemById(originalId));
        Add(outputId is Guid id ? _libraryManager.GetItemById(id) : _libraryManager.FindByPath(outputPath, false));

        var files = items.Values.Where(i => !string.IsNullOrEmpty(i.Path)).Select(Describe).OfType<MediaFileInfo>().ToList();
        var paths = files.Select(f => f.Path).Append(sourcePath).Append(outputPath).ToHashSet(StringComparer.Ordinal);
        var ids = files.Select(f => f.ItemId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        await StorageScanner.ChangeAsync(
            report =>
            {
                foreach (var old in report.Files.Where(f => paths.Contains(f.Path) || ids.Contains(f.ItemId)).ToList())
                {
                    report.Files.Remove(old);
                }

                foreach (var file in files)
                {
                    report.Files.Add(file);
                }
            },
            true,
            CancellationToken.None).ConfigureAwait(false);
    }

    private static List<string> OtherVersions(BaseItem item)
    {
        if (item is not Video video
            || !(video.PrimaryVersionId.HasValue || video.LinkedAlternateVersions.Length > 0 || video.HasLocalAlternateVersions || !video.OwnerId.Equals(Guid.Empty)))
        {
            return [];
        }

        return video.GetAllVersions()
            .Where(v => !v.Id.Equals(item.Id))
            .Select(v => v.Id.ToString("N", CultureInfo.InvariantCulture))
            .ToList();
    }

    /// <summary>
    /// Describes one video for the report.
    /// </summary>
    /// <param name="item">The video.</param>
    /// <returns>What the report keeps about it, or null if its file is gone.</returns>
    public MediaFileInfo? Describe(BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var fileInfo = new FileInfo(item.Path);
        if (!fileInfo.Exists)
        {
            return null;
        }

        var sources = item.GetMediaSources(false);
        var source = sources.Count > 0 ? sources[0] : null;
        var streams = source?.MediaStreams ?? item.GetMediaStreams();
        var video = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
        var audioBitrate = streams.Where(s => s.Type == MediaStreamType.Audio).Sum(s => (long)(s.BitRate ?? 0));

        var file = new MediaFileInfo
        {
            ItemId = item.Id.ToString("N", CultureInfo.InvariantCulture),
            Name = item.Name ?? fileInfo.Name,
            Path = fileInfo.FullName,
            Folder = fileInfo.DirectoryName ?? string.Empty,
            Library = _libraryManager.GetCollectionFolders(item).FirstOrDefault()?.Name ?? "Unknown",
            SizeBytes = fileInfo.Length,
            Container = source?.Container ?? fileInfo.Extension.TrimStart('.'),
            VideoCodec = video?.Codec ?? string.Empty,
            Width = video?.Width ?? 0,
            Height = video?.Height ?? 0,
            Bitrate = source?.Bitrate ?? 0,
            DurationSeconds = TimeSpan.FromTicks(item.RunTimeTicks ?? 0).TotalSeconds,
            IsHdr = video is not null && video.VideoRange == VideoRange.HDR
        };

        // The tracks inside the file, so the page can flag files with several languages or subtitles to remove.
        var tracks = _languages.Tracks(streams);
        List<string> Names(TrackKind kind) => tracks
            .Where(t => t.Kind == kind && t.Language is not null)
            .Select(t => t.Language!)
            .Distinct(StringComparer.Ordinal)
            .Select(_languages.DisplayName)
            .ToList();
        file.AudioTracks = tracks.Count(t => t.Kind == TrackKind.Audio);
        file.AudioLanguages = Names(TrackKind.Audio);
        file.SubtitleTracks = tracks.Count(t => t.Kind == TrackKind.Subtitle);
        file.SubtitleLanguages = Names(TrackKind.Subtitle);

        file.AudioBitrate = audioBitrate;
        file.EstimatedSavingsBytes = SavingsEstimator.Estimate(file, audioBitrate);
        file.OtherVersionIds = OtherVersions(item);

        // Trailers, featurettes and other extras, which can be deleted on their own without touching the main video.
        file.ExtraType = item.ExtraType?.ToString();

        // What the duplicate finder matches copies of the same title on.
        file.Kind = item switch
        {
            Movie => "Movie",
            Episode => "Episode",
            _ => "Video"
        };
        file.IsExtra = item.ExtraType is not null;
        if (item is Movie)
        {
            file.Year = item.ProductionYear;
            var ids = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var name in DuplicateFinder.ProviderNames)
            {
                if (item.GetProviderId(name) is { } id && !string.IsNullOrWhiteSpace(id))
                {
                    ids[name] = id.Trim();
                }
            }

            file.ProviderIds = ids;
        }

        // Episodes are listed by series and season, so the page needs to know where each one sits.
        if (item is Episode episode && StorageScanner.SeriesName(episode) is { Length: > 0 } seriesName)
        {
            var seriesId = episode.SeriesId.Equals(Guid.Empty) ? episode.FindSeriesId() : episode.SeriesId;
            file.SeriesId = seriesId.Equals(Guid.Empty) ? null : seriesId.ToString("N", CultureInfo.InvariantCulture);
            file.SeriesName = seriesName;
            file.SeasonNumber = episode.ParentIndexNumber;
            file.SeasonName = string.IsNullOrEmpty(episode.SeasonName) ? episode.FindSeasonName() : episode.SeasonName;
            file.EpisodeNumber = episode.IndexNumber;
            file.EpisodeNumberEnd = episode.IndexNumberEnd;
        }

        return file;
    }
}
