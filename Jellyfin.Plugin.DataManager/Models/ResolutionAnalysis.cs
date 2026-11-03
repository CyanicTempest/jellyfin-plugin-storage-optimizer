// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.DataManager.Models;

/// <summary>
/// How much real detail a video holds, found by shrinking sample frames and scaling them back up.
/// </summary>
public class ResolutionAnalysis
{
    /// <summary>Gets or sets the item id, in "N" format.</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>Gets or sets the file that was analysed.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the file size when analysed, to spot changes.</summary>
    public long SizeBytes { get; set; }

    /// <summary>Gets or sets the file's last write time when analysed, to spot changes.</summary>
    public DateTime ModifiedUtc { get; set; }

    /// <summary>Gets or sets the source width.</summary>
    public int Width { get; set; }

    /// <summary>Gets or sets the source height.</summary>
    public int Height { get; set; }

    /// <summary>Gets or sets the width of the smallest size that keeps the real detail.</summary>
    public int DetailWidth { get; set; }

    /// <summary>Gets or sets the height of the smallest size that keeps the real detail.</summary>
    public int DetailHeight { get; set; }

    /// <summary>Gets the result for each smaller size tried, largest first.</summary>
    public IList<ResolutionCheck> Checks { get; init; } = [];

    /// <summary>Gets or sets when the analysis ran.</summary>
    public DateTime AnalysedAt { get; set; }
}

/// <summary>
/// How alike the frames look after a round trip through a smaller size.
/// </summary>
/// <param name="Width">The smaller width.</param>
/// <param name="Height">The smaller height.</param>
/// <param name="Similarity">The average SSIM of the round-tripped frames against the originals, from 0 to 1.</param>
/// <param name="LowestSimilarity">The SSIM of the least alike frame.</param>
public record ResolutionCheck(int Width, int Height, double Similarity, double LowestSimilarity);
