// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.DataManager.Encoding;
using Jellyfin.Plugin.DataManager.Models;
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

    /// <summary>
    /// Initializes a new instance of the <see cref="SummaryBuilder"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="resolutionAnalyzer">The saved detail checks.</param>
    /// <param name="encodeService">The encode service, for what test encodes have measured.</param>
    public SummaryBuilder(ILibraryManager libraryManager, ResolutionAnalyzer resolutionAnalyzer, EncodeService encodeService)
    {
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
        var roots = _libraryManager.GetVirtualFolders()
            .SelectMany(f => f.Locations)
            .Select(l => Path.TrimEndingDirectorySeparator(l))
            .ToList();

        summary.GeneratedAt = report.GeneratedAt;
        summary.TotalBytes = report.TotalBytes;
        summary.FileCount = report.FileCount;
        (summary.DriveTotalBytes, summary.FreeBytes) = DriveSpace(roots);

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

    // Libraries can share a drive, so each drive is counted once. On Linux a drive's root is the path it was
    // asked about, so two folders on one disk are told apart by their sizes instead.
    private static (long Total, long Free) DriveSpace(List<string> roots)
    {
        var drives = new Dictionary<(long Total, long Free), (long Total, long Free)>();
        foreach (var root in roots)
        {
            try
            {
                var drive = new DriveInfo(root);
                var size = (drive.TotalSize, drive.AvailableFreeSpace);
                drives.TryAdd(size, size);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Skip locations whose drive can't be read.
            }
        }

        return drives.Count == 0 ? (-1, -1) : (drives.Values.Sum(d => d.Total), drives.Values.Sum(d => d.Free));
    }

    private static void AddRange<T>(IList<T> list, IEnumerable<T> items)
    {
        foreach (var item in items)
        {
            list.Add(item);
        }
    }
}
