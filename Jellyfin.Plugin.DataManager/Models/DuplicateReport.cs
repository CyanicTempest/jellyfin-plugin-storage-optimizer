// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.DataManager.Models;

/// <summary>
/// Titles that are in the library more than once.
/// </summary>
public class DuplicateReport
{
    /// <summary>Gets or sets when the scan the report comes from finished, or null if no scan has run.</summary>
    public DateTime? GeneratedAt { get; set; }

    /// <summary>Gets or sets a value indicating whether the last scan is from an older version that didn't save what duplicates are matched on, so a new scan is needed.</summary>
    public bool NeedsScan { get; set; }

    /// <summary>Gets or sets the space freed by keeping only the best copy of every title, in bytes.</summary>
    public long ReclaimableBytes { get; set; }

    /// <summary>Gets the titles with more than one copy, the most space to free first.</summary>
    public IList<DuplicateGroup> Groups { get; init; } = [];

    /// <summary>
    /// Gets the titles left out because they're a kept re-encode and its original, which Smaller files deals with.
    /// </summary>
    public IList<string> CoveredBySmallerFiles { get; init; } = [];
}

/// <summary>
/// One title with more than one copy.
/// </summary>
public class DuplicateGroup
{
    /// <summary>Gets or sets the title, such as "Film (2018)" or "Example Show S01E01".</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets what the copies were matched on, such as "IMDb id, name and year".</summary>
    public string MatchedOn { get; set; } = string.Empty;

    /// <summary>Gets or sets the item id ("N" format) of the copy with the best picture.</summary>
    public string BestItemId { get; set; } = string.Empty;

    /// <summary>Gets or sets why that copy is best, such as "Highest resolution".</summary>
    public string BestReason { get; set; } = string.Empty;

    /// <summary>Gets or sets the space freed by keeping only the best copy, in bytes.</summary>
    public long ReclaimableBytes { get; set; }

    /// <summary>Gets or sets a value indicating whether the runtimes differ enough that the copies may be different cuts, so none is picked for the user.</summary>
    public bool DifferentCuts { get; set; }

    /// <summary>Gets the things to check before deleting, such as runtimes that differ.</summary>
    public IList<string> Notes { get; init; } = [];

    /// <summary>Gets the copies, best first.</summary>
    public IList<MediaFileInfo> Copies { get; init; } = [];
}
