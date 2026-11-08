// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.DataManager.Encoding;
using Jellyfin.Plugin.DataManager.Models;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.DataManager.Services;

/// <summary>
/// Turns a storage report into the summary the overview page shows.
/// </summary>
public class SummaryBuilder
{
    private readonly ILibraryManager _libraryManager;
    private readonly ResolutionAnalyzer _resolutionAnalyzer;
    private readonly EncodeService _encodeService;
    private readonly IServerApplicationPaths _paths;
    private readonly DriveList _drives = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="SummaryBuilder"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="resolutionAnalyzer">The saved detail checks.</param>
    /// <param name="encodeService">The encode service, for what test encodes have measured.</param>
    /// <param name="paths">Jellyfin's folders, whose Collections and playlists aren't library drives.</param>
    public SummaryBuilder(ILibraryManager libraryManager, ResolutionAnalyzer resolutionAnalyzer, EncodeService encodeService, IServerApplicationPaths paths)
    {
        _paths = paths;
        _libraryManager = libraryManager;
        _resolutionAnalyzer = resolutionAnalyzer;
        _encodeService = encodeService;
    }

    /// <summary>
    /// Builds the summary.
    /// </summary>
    /// <param name="report">The last scan, or null if none has run.</param>
    /// <returns>The summary.</returns>
    public StorageSummary Build(StorageReport? report)
    {
        var summary = new StorageSummary();
        if (report is null)
        {
            return summary;
        }

        var files = report.Files;
        summary.GeneratedAt = report.GeneratedAt;
        summary.TotalBytes = report.TotalBytes;
        summary.FileCount = report.FileCount;
        AddDrives(summary, files);

        AddRange(summary.Libraries, Group(files, f => f.Library));
        AddRange(summary.Codecs, Group(files, f => string.IsNullOrEmpty(f.VideoCodec) ? "unknown" : f.VideoCodec));

        // Detail checks still apply while the file is unchanged.
        var analyses = _resolutionAnalyzer.GetAllByPath();
        foreach (var file in files)
        {
            if (analyses.TryGetValue(file.Path, out var analysis) && analysis.SizeBytes == file.SizeBytes)
            {
                file.DetailWidth = analysis.DetailWidth;
                file.DetailHeight = analysis.DetailHeight;
            }
        }

        _encodeService.ApplyTestedSavings(files);
        SuggestionRules.Apply(files);
        AddRange(summary.Files, files.OrderByDescending(f => f.EstimatedSavingsBytes).ThenByDescending(f => f.SizeBytes));
        return summary;
    }

    private static IEnumerable<StorageGroup> Group(IEnumerable<MediaFileInfo> files, Func<MediaFileInfo, string> key)
        => files.GroupBy(key)
            .Select(g => new StorageGroup(g.Key, g.Sum(f => f.SizeBytes), g.Count()))
            .OrderByDescending(g => g.SizeBytes);

    // Jellyfin's Collections and playlists are kept in its own data folder, not on a library drive.
    internal static bool IsLibraryFolder(string? collectionType, string location, string dataPath)
    {
        if (string.Equals(collectionType, "boxsets", StringComparison.OrdinalIgnoreCase) || string.Equals(collectionType, "playlists", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var data = Path.TrimEndingDirectorySeparator(dataPath);
        return data.Length == 0 || !(location.Equals(data, StringComparison.Ordinal) || location.StartsWith(data + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    // One drive per disk, found from the folders the files are in, so a disk shared by several libraries is counted
    // once and each file's free-space check is for its own disk.
    private void AddDrives(StorageSummary summary, IList<MediaFileInfo> files)
    {
        var roots = _libraryManager.GetVirtualFolders()
            .SelectMany(f => f.Locations.Select(l => (Type: f.CollectionType?.ToString(), Location: Path.TrimEndingDirectorySeparator(l))))
            .Where(l => IsLibraryFolder(l.Type, l.Location, _paths.DataPath))
            .Select(l => l.Location);
        var map = _drives.Map(roots.Concat(files.Select(f => f.Folder)));
        foreach (var file in files)
        {
            var drive = map.DriveOf(file.Folder);
            file.DriveId = drive?.Id;
            if (drive is not null)
            {
                drive.VideoBytes += file.SizeBytes;
                drive.FileCount++;
            }
        }

        var hidden = (Plugin.Instance?.Configuration.HiddenDrives ?? []).ToHashSet(StringComparer.Ordinal);
        foreach (var drive in map.Drives.OrderByDescending(d => d.VideoBytes).ThenBy(d => d.Name, StringComparer.Ordinal))
        {
            drive.Hidden = hidden.Contains(drive.Id);
            summary.Drives.Add(drive);
        }

        var shown = summary.Drives.Where(d => !d.Hidden && d.TotalBytes > 0).ToList();
        (summary.DriveTotalBytes, summary.FreeBytes) = shown.Count == 0 ? (-1, -1) : (shown.Sum(d => d.TotalBytes), shown.Sum(d => d.FreeBytes));
    }

    private static void AddRange<T>(IList<T> list, IEnumerable<T> items)
    {
        foreach (var item in items)
        {
            list.Add(item);
        }
    }
}
