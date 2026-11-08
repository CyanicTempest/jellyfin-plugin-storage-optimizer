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
using MediaBrowser.Controller.MediaEncoding;
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
    private readonly ReportUpdater _reports;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<StorageScanner> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StorageScanner"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="encodeService">The re-encode queue, which finds files left by interrupted encodes.</param>
    /// <param name="reports">Describes each file.</param>
    /// <param name="mediaEncoder">Jellyfin's ffmpeg, whose ffprobe reads HDR brightness information.</param>
    /// <param name="logger">The logger.</param>
    public StorageScanner(ILibraryManager libraryManager, EncodeService encodeService, ReportUpdater reports, IMediaEncoder mediaEncoder, ILogger<StorageScanner> logger)
    {
        _mediaEncoder = mediaEncoder;
        _reports = reports;
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
        var checkHdr = Plugin.Instance?.Configuration.CheckHdrBrightness == true;
        var hdrToCheck = new List<(BaseItem Item, MediaFileInfo File)>();

        for (var i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = items[i];
            if (!string.IsNullOrEmpty(item.Path) && seenPaths.Add(item.Path))
            {
                var file = _reports.Describe(item);
                if (file is not null)
                {
                    report.Files.Add(file);
                    if (checkHdr && IsHdr10(item) && FileChecks.Get(file.Path, file.SizeBytes)?.HasHdrBrightness is null)
                    {
                        hdrToCheck.Add((item, file));
                    }
                }
            }

            progress.Report((checkHdr ? 90.0 : 100.0) * (i + 1) / items.Count);
        }

        // HDR10 files not checked before have their first frame read for brightness information, about a second each.
        for (var i = 0; i < hdrToCheck.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (_, file) = hdrToCheck[i];
            var has = await HasBrightnessAsync(file.Path, cancellationToken).ConfigureAwait(false);
            if (has is { } found)
            {
                FileChecks.Update(file.Path, file.SizeBytes, c => c.HasHdrBrightness = found);
                if (!found && !file.Issues.Contains(FileIssues.HdrNoBrightness))
                {
                    file.Issues = file.Issues.Append(FileIssues.HdrNoBrightness).ToList();
                }
            }

            progress.Report(90.0 + (10.0 * (i + 1) / hdrToCheck.Count));
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

    // HDR10 (PQ) carries brightness information; HLG doesn't use it.
    private static bool IsHdr10(BaseItem item)
    {
        var video = item.GetMediaStreams().FirstOrDefault(s => s.Type == MediaStreamType.Video);
        return video?.VideoRange == VideoRange.HDR && string.Equals(video.ColorTransfer, "smpte2084", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool?> HasBrightnessAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var json = await FfprobeAsync(_mediaEncoder.ProbePath, ["-v", "error", "-select_streams", "V:0", "-read_intervals", "%+#1", "-show_frames", "-show_entries", "frame=side_data_list", "-of", "json", path], TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            return json is null ? null : FileIssues.ParseBrightness(json) is { MasterDisplay: not null };
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogDebug(ex, "Storage Optimizer couldn't read the HDR brightness of {Path}", path);
            return null;
        }
    }

    /// <summary>
    /// Runs ffprobe and returns what it printed, or null if it failed or took too long.
    /// </summary>
    /// <param name="probePath">ffprobe.</param>
    /// <param name="arguments">Its arguments.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The output, or null.</returns>
    internal static async Task<string?> FfprobeAsync(string probePath, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo(probePath)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = System.Diagnostics.Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start ffprobe.");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            var output = await process.StandardOutput.ReadToEndAsync(limit.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            return process.ExitCode == 0 ? output : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(true);
            return null;
        }
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

    internal static async Task ChangeAsync(Action<StorageReport> change, bool needed, CancellationToken cancellationToken)
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

    internal static string? SeriesName(Episode episode)
        => string.IsNullOrEmpty(episode.SeriesName) ? episode.FindSeriesName() : episode.SeriesName;
}
