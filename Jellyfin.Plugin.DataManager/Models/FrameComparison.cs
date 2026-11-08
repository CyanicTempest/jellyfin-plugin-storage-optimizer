// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Generic;

namespace Jellyfin.Plugin.DataManager.Models;

/// <summary>
/// The same frame taken from two versions of a video, with a map of where detail was lost and similarity scores.
/// </summary>
public class FrameComparison
{
    /// <summary>Gets or sets what the preview had to do differently from the usual encode, such as using the CPU for a GPU that can't encode 10-bit, or null.</summary>
    public string? Note { get; set; }

    /// <summary>Gets or sets the position of the frame, in seconds.</summary>
    public double PositionSeconds { get; set; }

    /// <summary>Gets or sets the length of the video, in seconds.</summary>
    public double DurationSeconds { get; set; }

    /// <summary>Gets or sets the structural similarity (SSIM) of the two frames, from 0 to 1.</summary>
    public double? Ssim { get; set; }

    /// <summary>Gets or sets the peak signal-to-noise ratio of the two frames, in dB.</summary>
    public double? Psnr { get; set; }

    /// <summary>Gets or sets a value indicating whether HDR frames were tone-mapped for display.</summary>
    public bool ToneMapped { get; set; }

    /// <summary>Gets or sets the frame from the first video, as a JPEG data URL.</summary>
    public string FirstImage { get; set; } = string.Empty;

    /// <summary>Gets or sets the frame from the second video, as a JPEG data URL.</summary>
    public string SecondImage { get; set; } = string.Empty;

    /// <summary>Gets or sets the share of the first frame's fine detail the second kept, from 0 to 1, or null if the frame is too flat to say.</summary>
    public double? DetailKept { get; set; }

    /// <summary>Gets or sets the share of fine detail kept in the softest tenth of the picture.</summary>
    public double? SoftestKept { get; set; }

    /// <summary>Gets or sets where the softest area is across the frame, from 0 to 1.</summary>
    public double SoftestX { get; set; }

    /// <summary>Gets or sets where the softest area is down the frame, from 0 to 1.</summary>
    public double SoftestY { get; set; }

    /// <summary>Gets or sets the detail map, one byte per area row by row, as base64: the share of detail lost from 0 (none) to 250 (all), or 255 where the original had no fine detail to lose.</summary>
    public string? DetailMap { get; set; }

    /// <summary>Gets or sets the detail map's width, in areas.</summary>
    public int DetailMapWidth { get; set; }

    /// <summary>Gets or sets the detail map's height, in areas.</summary>
    public int DetailMapHeight { get; set; }
}

/// <summary>
/// Frames spread through a video, as small pictures to pick from.
/// </summary>
public class Filmstrip
{
    /// <summary>Gets or sets the length of the video, in seconds.</summary>
    public double DurationSeconds { get; set; }

    /// <summary>Gets the frames, in order.</summary>
    public IList<FilmstripFrame> Frames { get; init; } = [];
}

/// <summary>
/// One frame of a filmstrip.
/// </summary>
public class FilmstripFrame
{
    /// <summary>Gets or sets where the frame is, in seconds. It's a keyframe, so seeking to it is exact.</summary>
    public double PositionSeconds { get; set; }

    /// <summary>Gets or sets the frame, as a small JPEG data URL.</summary>
    public string Image { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether this is the busiest stretch of the video, where the original needed the most bits.</summary>
    public bool IsBusiest { get; set; }
}
