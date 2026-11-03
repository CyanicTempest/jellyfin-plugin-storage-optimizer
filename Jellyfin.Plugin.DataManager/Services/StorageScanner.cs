// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
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
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DataManager.Services;

/// <summary>
/// Scans every library for video files and stores a storage report.
/// </summary>
public class StorageScanner
{
    private const string ReportFileName = "storage-report.json";

    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = false };

    // Deletes change the saved report between scans, one at a time.
    private static readonly SemaphoreSlim _reportLock = new(1, 1);

    private readonly ILibraryManager _libraryManager;
    private readonly EncodeService _encodeService;
    private readonly LanguageCodes _languages;
    private readonly ILogger<StorageScanner> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StorageScanner"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="encodeService">The re-encode queue, which finds files left by interrupted encodes.</param>
    /// <param name="languages">Names the languages of each file's tracks.</param>
    /// <param name="logger">The logger.</param>
    public StorageScanner(ILibraryManager libraryManager, EncodeService encodeService, LanguageCodes languages, ILogger<StorageScanner> logger)
    {
        _languages = languages;
        _libraryManager = libraryManager;
        _encodeService = encodeService;
        _logger = logger;
    }

    private static string ReportPath
    {
        get
        {
            var folder = Plugin.Instance?.DataFolderPath ?? throw new InvalidOperationException("Plugin is not loaded.");
            Directory.CreateDirectory(folder);
            return Path.Combine(folder, ReportFileName);
        }
    }

    /// <summary>
    /// Scans all libraries and saves the report.
    /// </summary>
    /// <param name="progress">Progress from 0 to 100.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The new report.</returns>
    public async Task<StorageReport> ScanAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);

        // Alternate versions and owned files (extras, parts) are hidden by default, but they take space too.
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            MediaTypes = [MediaType.Video],
            IsVirtualItem = false,
            Recursive = true,
            IncludeAlternateVersions = true,
            IncludeOwnedItems = true
        });

        var report = new StorageReport();
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = items[i];
            if (!string.IsNullOrEmpty(item.Path) && seenPaths.Add(item.Path))
            {
                var file = Describe(item);
                if (file is not null)
                {
                    report.Files.Add(file);
                }
            }

            progress.Report(100.0 * (i + 1) / items.Count);
        }

        // Working files left by interrupted encodes are cleaned up. The videos themselves are never touched here.
        _encodeService.RemoveUnfinishedFiles(report.Files.Select(f => f.Folder));

        report.GeneratedAt = DateTime.UtcNow;
        await _reportLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveAsync(report, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _reportLock.Release();
        }

        _logger.LogInformation("Storage Optimizer scanned {Count} files totalling {Bytes} bytes", report.FileCount, report.TotalBytes);
        return report;
    }

    /// <summary>
    /// Loads the last saved report.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The report, or null if no scan has run yet.</returns>
    public static async Task<StorageReport?> LoadAsync(CancellationToken cancellationToken)
    {
        var path = ReportPath;
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var stream = File.OpenRead(path);
            await using (stream.ConfigureAwait(false))
            {
                return await JsonSerializer.DeserializeAsync<StorageReport>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // An unreadable report is the same as none: the next scan writes a fresh one.
            return null;
        }
    }

    /// <summary>
    /// Takes deleted videos out of the saved report, so the page is right without another scan.
    /// </summary>
    /// <param name="itemIds">The deleted videos' ids.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public static Task RemoveFilesAsync(IReadOnlyCollection<string> itemIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        var gone = itemIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ChangeAsync(
            report =>
            {
                foreach (var file in report.Files.Where(f => gone.Contains(f.ItemId)).ToList())
                {
                    report.Files.Remove(file);
                }

                foreach (var file in report.Files)
                {
                    file.OtherVersionIds = file.OtherVersionIds.Where(id => !gone.Contains(id)).ToList();
                }
            },
            gone.Count > 0,
            cancellationToken);
    }

    private static async Task ChangeAsync(Action<StorageReport> change, bool needed, CancellationToken cancellationToken)
    {
        if (!needed)
        {
            return;
        }

        await _reportLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await LoadAsync(cancellationToken).ConfigureAwait(false) is { } report)
            {
                change(report);
                await SaveAsync(report, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _reportLock.Release();
        }
    }

    private static async Task SaveAsync(StorageReport report, CancellationToken cancellationToken)
    {
        report.TotalBytes = report.Files.Sum(f => f.SizeBytes);
        report.FileCount = report.Files.Count;

        // Written beside the report and moved over it, so a reader never sees a half-written file.
        var tempPath = ReportPath + ".tmp";
        var stream = File.Create(tempPath);
        await using (stream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(stream, report, _jsonOptions, cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, ReportPath, true);
    }

    /// <summary>
    /// Gets a name that says what an item is on its own. An episode's name is only its title, so the series,
    /// season and episode number go first.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>The name, such as "Example Show S01E01: The Weekend".</returns>
    public static string Title(BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item is not Episode episode)
        {
            return item.Name;
        }

        var series = SeriesName(episode);
        if (string.IsNullOrEmpty(series))
        {
            return item.Name;
        }

        var number = episode.ParentIndexNumber is int season && episode.IndexNumber is int index
            ? string.Create(CultureInfo.InvariantCulture, $" S{season:00}E{index:00}")
                + (episode.IndexNumberEnd is int end && end > index ? string.Create(CultureInfo.InvariantCulture, $"-E{end:00}") : string.Empty)
            : string.Empty;
        return series + number + ": " + item.Name;
    }

    private static string? SeriesName(Episode episode)
        => string.IsNullOrEmpty(episode.SeriesName) ? episode.FindSeriesName() : episode.SeriesName;

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

    private MediaFileInfo? Describe(BaseItem item)
    {
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
        if (item is Episode episode && SeriesName(episode) is { Length: > 0 } seriesName)
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
