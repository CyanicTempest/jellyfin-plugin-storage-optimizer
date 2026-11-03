// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.DataManager.Services;

/// <summary>
/// Measures how much fine detail an encode kept, area by area. Encoders save space by smoothing low-contrast
/// texture, such as a background wall, foliage or grain. That looks soft on a big screen but barely moves a plain
/// difference, because each pixel changes by only a few levels. Here the detail in each area of the new frame is
/// measured against the detail the original had there, so an area that lost half its texture shows as half lost
/// however faint the texture was.
/// </summary>
internal static class DetailMap
{
    // Areas with less fine detail than this (RMS, in 8-bit levels) are flat in the original, so there's nothing to lose.
    private const double MinDetail = 1.0;

    // HDR is measured on its PQ signal, where the same texture spans fewer levels.
    private const double MinDetailHdr = 0.4;

    // The map holds the share of detail lost from 0 to this, and FlatArea where there was none to lose.
    private const double AllLost = 250;

    /// <summary>The map value for an area the original had no fine detail in, so nothing could be lost.</summary>
    public const byte FlatArea = 255;

    /// <summary>
    /// Measures one frame.
    /// </summary>
    /// <param name="original">The original frame's luma, 16 bits per pixel, little-endian.</param>
    /// <param name="encoded">The new frame's luma, the same size.</param>
    /// <param name="width">The frame width.</param>
    /// <param name="height">The frame height.</param>
    /// <param name="isHdr">Whether the frames are HDR.</param>
    /// <returns>The map and scores, or null if the frames are too small or too flat to say.</returns>
    public static DetailResult? Measure(byte[] original, byte[] encoded, int width, int height, bool isHdr)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(encoded);
        if (width < 64 || height < 64 || original.Length < width * height * 2 || encoded.Length < width * height * 2)
        {
            return null;
        }

        // Detail is what's left after a small blur, measured over overlapping windows. The sizes grow with the
        // frame so a 4K frame is judged at the same viewing scale as 1080p.
        var radius = Math.Max(2, (int)Math.Round(width / 960.0));
        var stride = Math.Max(4, (int)Math.Round(width / 480.0));
        var window = stride * 3;
        var energyX = WindowEnergy(HighPass(Luma(original, width, height), width, height, radius), width, height, stride, window, out var mapWidth, out var mapHeight);
        var energyY = WindowEnergy(HighPass(Luma(encoded, width, height), width, height, radius), width, height, stride, window, out _, out _);

        var minDetail = isHdr ? MinDetailHdr : MinDetail;
        var loss = new double[energyX.Length];
        var hasDetail = new double[energyX.Length];
        var keptRatios = new List<double>();
        double detail = 0, kept = 0;
        for (var i = 0; i < loss.Length; i++)
        {
            var x = energyX[i];
            if (x < minDetail)
            {
                continue;
            }

            var y = energyY[i];
            loss[i] = Math.Clamp((x - y) / x, 0, 1);
            hasDetail[i] = 1;
            detail += x;
            kept += Math.Min(x, y);
            keptRatios.Add(Math.Min(y / x, 1));
        }

        if (keptRatios.Count < 16)
        {
            return null;
        }

        // A light blur so areas read as areas rather than speckle, averaging only the neighbours that had detail,
        // so a flat sky beside a soft wall doesn't make the wall look better than it is.
        var smoothLoss = Smooth(loss, mapWidth, mapHeight, 1);
        var smoothDetail = Smooth(hasDetail, mapWidth, mapHeight, 1);
        var map = new byte[loss.Length];
        for (var i = 0; i < map.Length; i++)
        {
            map[i] = hasDetail[i] > 0
                ? (byte)Math.Round(Math.Clamp(smoothLoss[i] / smoothDetail[i], 0, 1) * AllLost)
                : FlatArea;
        }

        // The softest area is the one with the most lost over about a sixteenth of the frame's width, so a single
        // sharp edge doesn't count as an area.
        var wide = Smooth(loss, mapWidth, mapHeight, Math.Max(2, mapWidth / 32));
        var worst = 0;
        for (var i = 1; i < wide.Length; i++)
        {
            if (wide[i] > wide[worst])
            {
                worst = i;
            }
        }

        keptRatios.Sort();
        return new DetailResult(
            map,
            mapWidth,
            mapHeight,
            kept / detail,
            keptRatios[keptRatios.Count / 10],
            ((worst % mapWidth * stride) + (window / 2.0)) / width,
            ((worst / mapWidth * stride) + (window / 2.0)) / height);
    }

    private static float[] Luma(byte[] data, int width, int height)
    {
        // 16-bit samples, so 8-bit and 10-bit sources both come out in 8-bit levels.
        var luma = new float[width * height];
        for (var i = 0; i < luma.Length; i++)
        {
            luma[i] = (data[2 * i] | (data[(2 * i) + 1] << 8)) / 257f;
        }

        return luma;
    }

    // The frame minus a box blur of it, squared: the energy of the detail at each pixel.
    private static float[] HighPass(float[] luma, int width, int height, int radius)
    {
        var across = new float[luma.Length];
        for (var y = 0; y < height; y++)
        {
            BoxLine(luma, across, y * width, 1, width, radius);
        }

        var blurred = new float[luma.Length];
        for (var x = 0; x < width; x++)
        {
            BoxLine(across, blurred, x, width, height, radius);
        }

        for (var i = 0; i < luma.Length; i++)
        {
            var d = luma[i] - blurred[i];
            blurred[i] = d * d;
        }

        return blurred;
    }

    // A running box mean along one row or column, with the edge pixels repeated.
    private static void BoxLine(float[] source, float[] target, int start, int step, int count, int radius)
    {
        var size = (2 * radius) + 1;
        double sum = 0;
        for (var k = -radius; k <= radius; k++)
        {
            sum += source[start + (Math.Clamp(k, 0, count - 1) * step)];
        }

        for (var i = 0; i < count; i++)
        {
            target[start + (i * step)] = (float)(sum / size);
            sum += source[start + (Math.Min(i + radius + 1, count - 1) * step)] - source[start + (Math.Max(i - radius, 0) * step)];
        }
    }

    // RMS detail over each window, windows overlapping by stride.
    private static double[] WindowEnergy(float[] energy, int width, int height, int stride, int window, out int mapWidth, out int mapHeight)
    {
        mapWidth = ((width - window) / stride) + 1;
        mapHeight = ((height - window) / stride) + 1;
        var result = new double[mapWidth * mapHeight];
        var columns = new double[width];
        for (var j = 0; j < mapHeight; j++)
        {
            Array.Clear(columns);
            for (var y = j * stride; y < (j * stride) + window; y++)
            {
                var row = y * width;
                for (var x = 0; x < width; x++)
                {
                    columns[x] += energy[row + x];
                }
            }

            for (var i = 0; i < mapWidth; i++)
            {
                double sum = 0;
                for (var x = i * stride; x < (i * stride) + window; x++)
                {
                    sum += columns[x];
                }

                result[(j * mapWidth) + i] = Math.Sqrt(sum / (window * window));
            }
        }

        return result;
    }

    // The mean of each point's neighbours within a radius, from a summed-area table so a wide radius costs the same.
    private static double[] Smooth(double[] map, int width, int height, int radius)
    {
        var sums = new double[(width + 1) * (height + 1)];
        for (var y = 0; y < height; y++)
        {
            double row = 0;
            for (var x = 0; x < width; x++)
            {
                row += map[(y * width) + x];
                sums[((y + 1) * (width + 1)) + x + 1] = sums[(y * (width + 1)) + x + 1] + row;
            }
        }

        var result = new double[map.Length];
        for (var y = 0; y < height; y++)
        {
            var top = Math.Max(y - radius, 0);
            var bottom = Math.Min(y + radius + 1, height);
            for (var x = 0; x < width; x++)
            {
                var left = Math.Max(x - radius, 0);
                var right = Math.Min(x + radius + 1, width);
                var sum = sums[(bottom * (width + 1)) + right] - sums[(top * (width + 1)) + right] - sums[(bottom * (width + 1)) + left] + sums[(top * (width + 1)) + left];
                result[(y * width) + x] = sum / ((bottom - top) * (right - left));
            }
        }

        return result;
    }
}

/// <summary>
/// How much fine detail an encode kept in one frame.
/// </summary>
/// <param name="Map">The share of detail lost in each area, row by row, from 0 (all kept) to 250 (all lost), or <see cref="DetailMap.FlatArea"/> where the original had none to lose.</param>
/// <param name="MapWidth">The map's width.</param>
/// <param name="MapHeight">The map's height.</param>
/// <param name="Kept">The share of the original's fine detail the new frame kept, from 0 to 1.</param>
/// <param name="SoftestKept">The share kept in the softest tenth of the picture.</param>
/// <param name="SoftestX">Where the softest area is across the frame, from 0 to 1.</param>
/// <param name="SoftestY">Where the softest area is down the frame, from 0 to 1.</param>
internal sealed record DetailResult(byte[] Map, int MapWidth, int MapHeight, double Kept, double SoftestKept, double SoftestX, double SoftestY);
