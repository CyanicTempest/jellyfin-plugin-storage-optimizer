// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.DataManager.Encoding;

/// <summary>
/// Standard video sizes, and fitting a video inside one.
/// </summary>
public static class Resolutions
{
    // A source up to 7% bigger than a size, such as 4096-wide cinema 4K, counts as that size.
    private const double Tolerance = 1.07;

    /// <summary>
    /// Gets the standard sizes, largest first: 4K, 1440p, 1080p, 720p and 480p.
    /// </summary>
    public static IReadOnlyList<(int Width, int Height)> Sizes { get; } = [(3840, 2160), (2560, 1440), (1920, 1080), (1280, 720), (854, 480)];

    /// <summary>
    /// Gets the size a choice scales down to.
    /// </summary>
    /// <param name="choice">The resolution choice.</param>
    /// <returns>The size, or null for Original and Auto.</returns>
    public static (int Width, int Height)? Box(ResolutionChoice choice) => choice switch
    {
        ResolutionChoice.P2160 => Sizes[0],
        ResolutionChoice.P1440 => Sizes[1],
        ResolutionChoice.P1080 => Sizes[2],
        ResolutionChoice.P720 => Sizes[3],
        ResolutionChoice.P480 => Sizes[4],
        _ => null
    };

    /// <summary>
    /// Gets whether a video is bigger than a size, so scaling it down would change it.
    /// </summary>
    /// <param name="width">The video width.</param>
    /// <param name="height">The video height.</param>
    /// <param name="box">The size.</param>
    /// <returns>True if the video is bigger.</returns>
    public static bool IsLargerThan(int width, int height, (int Width, int Height) box)
        => width > box.Width * Tolerance || height > box.Height * Tolerance;

    /// <summary>
    /// Gets the standard sizes smaller than a video, largest first.
    /// </summary>
    /// <param name="width">The video width.</param>
    /// <param name="height">The video height.</param>
    /// <returns>The smaller sizes.</returns>
    public static IEnumerable<(int Width, int Height)> SmallerThan(int width, int height)
        => Sizes.Where(box => IsLargerThan(width, height, box));

    /// <summary>
    /// Fits a video inside a size, keeping its shape, so 1920x800 fitted to 720p becomes 1280x534.
    /// </summary>
    /// <param name="width">The video width.</param>
    /// <param name="height">The video height.</param>
    /// <param name="box">The size to fit inside.</param>
    /// <returns>The new size, or the video's own size if it already fits.</returns>
    public static (int Width, int Height) Fit(int width, int height, (int Width, int Height) box)
    {
        if (width <= 0 || height <= 0 || !IsLargerThan(width, height, box))
        {
            return (width, height);
        }

        var scale = Math.Min((double)box.Width / width, (double)box.Height / height);
        return (Even(width * scale), Even(height * scale));
    }

    /// <summary>
    /// Gets the everyday name for a video size, such as 4K or 1080p.
    /// </summary>
    /// <param name="width">The video width.</param>
    /// <param name="height">The video height.</param>
    /// <returns>The name.</returns>
    public static string Label(int width, int height) => (width, height) switch
    {
        _ when width >= 3200 || height >= 2000 => "4K",
        _ when width >= 2200 || height >= 1300 => "1440p",
        _ when width >= 1600 || height >= 1000 => "1080p",
        _ when width >= 1100 || height >= 700 => "720p",
        _ => height + "p"
    };

    // Most encoders need even dimensions.
    private static int Even(double value) => Math.Max((int)Math.Round(value / 2) * 2, 2);
}
