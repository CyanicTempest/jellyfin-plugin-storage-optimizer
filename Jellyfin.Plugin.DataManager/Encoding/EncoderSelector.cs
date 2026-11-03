// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.DataManager.Encoding;

/// <summary>
/// Picks the ffmpeg encoder for a codec, using the GPU when Jellyfin is set up for it.
/// </summary>
public static class EncoderSelector
{
    // ffmpeg's names for the device aliases this plugin sets up, kept apart from anything Jellyfin's own ffmpeg runs use.
    private const string VaapiAlias = "dmva";
    private const string D3d11Alias = "dmdx";
    private const string QsvAlias = "dmqs";

    // Hardware types whose encoders this plugin knows how to drive. Each takes plain frames in system memory, so a
    // file decoded on the CPU, or on the GPU and copied back, encodes the same way.
    private static readonly Dictionary<HardwareAccelerationType, string> _hardwareSuffix = new()
    {
        [HardwareAccelerationType.nvenc] = "nvenc",
        [HardwareAccelerationType.qsv] = "qsv",
        [HardwareAccelerationType.videotoolbox] = "videotoolbox",
        [HardwareAccelerationType.amf] = "amf",
        [HardwareAccelerationType.vaapi] = "vaapi"
    };

    /// <summary>
    /// Gets the software encoder for a codec.
    /// </summary>
    /// <param name="codec">The codec.</param>
    /// <returns>The encoder name.</returns>
    public static string SoftwareEncoder(TargetCodec codec) => codec switch
    {
        TargetCodec.H264 => "libx264",
        TargetCodec.Av1 => "libsvtav1",
        _ => "libx265"
    };

    /// <summary>
    /// Gets the hardware encoder name for a codec, or null if the hardware type isn't supported yet.
    /// </summary>
    /// <param name="codec">The codec.</param>
    /// <param name="hardware">Jellyfin's hardware acceleration setting.</param>
    /// <returns>The encoder name, or null.</returns>
    public static string? HardwareEncoder(TargetCodec codec, HardwareAccelerationType hardware)
    {
        if (!_hardwareSuffix.TryGetValue(hardware, out var suffix))
        {
            return null;
        }

        var prefix = codec switch
        {
            TargetCodec.H264 => "h264",
            TargetCodec.Av1 => "av1",
            _ => "hevc"
        };
        return prefix + "_" + suffix;
    }

    /// <summary>
    /// Gets the ffmpeg hwaccel that decodes a source on the GPU, following Jellyfin's hardware decoding settings,
    /// or null when the CPU should decode it.
    /// </summary>
    /// <param name="hardware">Jellyfin's hardware acceleration setting.</param>
    /// <param name="decodingCodecs">The codecs Jellyfin is set to decode on the GPU, such as h264 and hevc.</param>
    /// <param name="sourceCodec">The source's video codec, as Jellyfin names it.</param>
    /// <param name="isTenBit">Whether the source has 10 or more bits per channel.</param>
    /// <param name="allowTenBitHevc">Jellyfin's setting for decoding 10-bit HEVC on the GPU.</param>
    /// <param name="allowTenBitVp9">Jellyfin's setting for decoding 10-bit VP9 on the GPU.</param>
    /// <param name="isWindows">Whether the server runs on Windows.</param>
    /// <returns>The hwaccel name, or null.</returns>
    public static string? DecodeHwaccel(HardwareAccelerationType hardware, IReadOnlyCollection<string> decodingCodecs, string? sourceCodec, bool isTenBit, bool allowTenBitHevc, bool allowTenBitVp9, bool isWindows)
    {
        ArgumentNullException.ThrowIfNull(decodingCodecs);

        var hwaccel = hardware switch
        {
            HardwareAccelerationType.nvenc => "cuda",
            HardwareAccelerationType.qsv => isWindows ? "d3d11va" : "vaapi",
            _ => null
        };
        if (hwaccel is null || string.IsNullOrEmpty(sourceCodec) || !decodingCodecs.Contains(sourceCodec, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        if (isTenBit
            && ((string.Equals(sourceCodec, "hevc", StringComparison.OrdinalIgnoreCase) && !allowTenBitHevc)
                || (string.Equals(sourceCodec, "vp9", StringComparison.OrdinalIgnoreCase) && !allowTenBitVp9)))
        {
            return null;
        }

        return hwaccel;
    }

    /// <summary>
    /// Builds the ffmpeg arguments that set up the GPU, which go before the input. Decoded frames are copied back to
    /// system memory, so scaling and pixel formats work as they do for a CPU decode, and a CPU encoder still works.
    /// </summary>
    /// <param name="hardware">Jellyfin's hardware acceleration setting.</param>
    /// <param name="encoder">The encoder in use.</param>
    /// <param name="hwaccel">The hwaccel that decodes the source, or null when the CPU decodes it.</param>
    /// <param name="devicePath">The Intel GPU's render node on Linux, such as /dev/dri/renderD128.</param>
    /// <param name="isWindows">Whether the server runs on Windows.</param>
    /// <returns>The arguments, one per entry.</returns>
    public static IReadOnlyList<string> HardwareArguments(HardwareAccelerationType hardware, string encoder, string? hwaccel, string devicePath, bool isWindows)
    {
        ArgumentNullException.ThrowIfNull(encoder);

        var args = new List<string>();
        if (hardware == HardwareAccelerationType.nvenc)
        {
            if (hwaccel is not null)
            {
                args.AddRange(["-hwaccel", hwaccel]);
            }

            return args;
        }

        // VAAPI encoders take frames as surfaces on the GPU, which the filter chain uploads to the device set up here.
        // Decoding stays on the CPU.
        if (hardware == HardwareAccelerationType.vaapi)
        {
            if (encoder.EndsWith("_vaapi", StringComparison.Ordinal))
            {
                args.AddRange(["-init_hw_device", "vaapi=" + VaapiAlias + ":" + devicePath, "-filter_hw_device", VaapiAlias]);
            }

            return args;
        }

        if (hardware != HardwareAccelerationType.qsv)
        {
            return args;
        }

        // QSV runs on top of the system's own GPU interface: VA-API on Linux, Direct3D 11 on Windows. That device
        // decodes, and a QSV device made from it encodes, which ffmpeg hands to the QSV encoder by itself.
        var qsvEncoder = encoder.EndsWith("_qsv", StringComparison.Ordinal);
        if (hwaccel is null && !qsvEncoder)
        {
            return args;
        }

        var parent = isWindows ? D3d11Alias : VaapiAlias;
        args.AddRange(["-init_hw_device", isWindows ? "d3d11va=" + D3d11Alias : "vaapi=" + VaapiAlias + ":" + devicePath + ",driver=iHD"]);
        if (qsvEncoder)
        {
            args.AddRange(["-init_hw_device", "qsv=" + QsvAlias + "@" + parent]);
        }

        if (hwaccel is not null)
        {
            args.AddRange(["-hwaccel", hwaccel, "-hwaccel_device", parent]);
        }

        return args;
    }
}
