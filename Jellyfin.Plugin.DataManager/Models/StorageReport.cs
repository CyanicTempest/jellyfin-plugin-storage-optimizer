// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.DataManager.Models;

/// <summary>
/// The result of a library storage scan.
/// </summary>
public class StorageReport
{
    /// <summary>Gets or sets when the scan finished.</summary>
    public DateTime GeneratedAt { get; set; }

    /// <summary>Gets or sets the total size of all scanned files in bytes.</summary>
    public long TotalBytes { get; set; }

    /// <summary>Gets or sets the number of scanned files.</summary>
    public int FileCount { get; set; }

    /// <summary>Gets the per-file details.</summary>
    public IList<MediaFileInfo> Files { get; init; } = [];
}

/// <summary>
/// A named group of files and their combined size.
/// </summary>
/// <param name="Name">The group name.</param>
/// <param name="SizeBytes">The combined size in bytes.</param>
/// <param name="FileCount">The number of files.</param>
public record StorageGroup(string Name, long SizeBytes, int FileCount);

/// <summary>
/// The summary the overview page shows.
/// </summary>
public class StorageSummary
{
    /// <summary>Gets or sets when the underlying scan finished, or null if no scan has run.</summary>
    public DateTime? GeneratedAt { get; set; }

    /// <summary>Gets or sets the total size in bytes.</summary>
    public long TotalBytes { get; set; }

    /// <summary>Gets or sets the number of files.</summary>
    public int FileCount { get; set; }

    /// <summary>Gets the drives holding the libraries, one per disk, with the hidden ones marked.</summary>
    public IList<Services.StorageDrive> Drives { get; init; } = [];

    /// <summary>Gets or sets the free space in bytes across the drives the overview shows, or -1 if unknown.</summary>
    public long FreeBytes { get; set; } = -1;

    /// <summary>Gets or sets the total size in bytes of the drives the overview shows, or -1 if unknown.</summary>
    public long DriveTotalBytes { get; set; } = -1;

    /// <summary>Gets the size per library.</summary>
    public IList<StorageGroup> Libraries { get; init; } = [];

    /// <summary>Gets the size per video codec.</summary>
    public IList<StorageGroup> Codecs { get; init; } = [];

    /// <summary>Gets every file, largest estimated saving first.</summary>
    public IList<MediaFileInfo> Files { get; init; } = [];

    /// <summary>Gets the re-encodes kept beside their originals that came out smaller and are waiting for the user.</summary>
    public IList<Services.SmallerFile> SmallerFiles { get; init; } = [];
}
