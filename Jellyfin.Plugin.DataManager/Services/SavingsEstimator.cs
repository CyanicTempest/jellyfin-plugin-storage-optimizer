// SPDX-License-Identifier: GPL-3.0-only
using System;
using Jellyfin.Plugin.DataManager.Encoding;
using Jellyfin.Plugin.DataManager.Models;

namespace Jellyfin.Plugin.DataManager.Services;

/// <summary>
/// Estimates how big a file would be after re-encoding, and how much space that would save.
/// </summary>
/// <remarks>
/// These are rough rules of thumb, not measurements. HEVC usually needs about 60% of the
/// bitrate H.264 needs for the same quality, and older codecs compress far worse than both.
/// The caps stop high bitrate sources from being squeezed below a safe quality for their resolution.
/// </remarks>
public static class SavingsEstimator
{
    private const long DefaultAudioBitrate = 640_000;

    /// <summary>
    /// Estimates the bytes saved by re-encoding the video stream to HEVC at the same size and copying the audio.
    /// </summary>
    /// <param name="file">The file to estimate.</param>
    /// <param name="audioBitrate">The combined audio bitrate in bits per second, or 0 if unknown.</param>
    /// <returns>The estimated saving in bytes, never negative.</returns>
    public static long Estimate(MediaFileInfo file, long audioBitrate)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (file.DurationSeconds <= 0 || file.SizeBytes <= 0 || CodecFactor(file.VideoCodec) is null)
        {
            return 0;
        }

        return Math.Max(file.SizeBytes - EstimateSize(file, audioBitrate, TargetCodec.Hevc, file.Width, file.Height, videoScale: RoughScale(file.VideoCodec)), 0);
    }

    /// <summary>
    /// Estimates the size of a file after re-encoding its video and copying the audio.
    /// </summary>
    /// <param name="file">The file to estimate.</param>
    /// <param name="audioBitrate">The combined audio bitrate in bits per second, or 0 if unknown.</param>
    /// <param name="codec">The codec to encode to.</param>
    /// <param name="width">The width to encode at.</param>
    /// <param name="height">The height to encode at.</param>
    /// <param name="quality">The quality to encode at.</param>
    /// <param name="effort">How long to spend encoding.</param>
    /// <param name="videoScale">A correction to the video's size from test encodes, or 1 for none.</param>
    /// <param name="outputAudioBitrate">The audio kept, in bits per second, when some tracks are removed; null when all are copied.</param>
    /// <param name="subtitleBitrate">The subtitles kept, in bits per second, when tracks are chosen; 0 to leave them out of the size as before.</param>
    /// <returns>The estimated size in bytes.</returns>
    public static long EstimateSize(MediaFileInfo file, long audioBitrate, TargetCodec codec, int width, int height, QualityPreset quality = QualityPreset.Balanced, EncodeEffort effort = EncodeEffort.Standard, double videoScale = 1, long? outputAudioBitrate = null, long subtitleBitrate = 0)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (file.DurationSeconds <= 0 || file.SizeBytes <= 0)
        {
            return file.SizeBytes;
        }

        var audio = outputAudioBitrate ?? AudioBitrate(audioBitrate);
        var video = EstimateVideoBitrate(file, audioBitrate, codec, width, height, quality, effort) * videoScale;
        return (long)((video + audio + subtitleBitrate) * file.DurationSeconds / 8);
    }

    /// <summary>
    /// Estimates the video bitrate after re-encoding, by rule of thumb.
    /// </summary>
    /// <param name="file">The file to estimate.</param>
    /// <param name="audioBitrate">The combined audio bitrate in bits per second, or 0 if unknown.</param>
    /// <param name="codec">The codec to encode to.</param>
    /// <param name="width">The width to encode at.</param>
    /// <param name="height">The height to encode at.</param>
    /// <param name="quality">The quality to encode at.</param>
    /// <param name="effort">How long to spend encoding.</param>
    /// <returns>The video bitrate, in bits per second.</returns>
    public static double EstimateVideoBitrate(MediaFileInfo file, long audioBitrate, TargetCodec codec, int width, int height, QualityPreset quality, EncodeEffort effort)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (file.DurationSeconds <= 0 || file.SizeBytes <= 0)
        {
            return 0;
        }

        var overallBitrate = file.Bitrate > 0 ? file.Bitrate : (long)(file.SizeBytes * 8 / file.DurationSeconds);
        var sourceVideoBitrate = Math.Max(overallBitrate - AudioBitrate(audioBitrate), 0);

        // Fewer pixels need fewer bits, though not proportionally: a quarter of the pixels needs about 35% of the bitrate.
        var sourcePixels = (double)file.Width * file.Height;
        var pixelShare = sourcePixels > 0 && width > 0 && height > 0 ? Math.Min(width * (double)height / sourcePixels, 1) : 1;

        // AV1 and unknown codecs are already efficient, so HEVC needs a little more than they use.
        var hevcBitrate = sourceVideoBitrate * HevcBitrateFactor(file.VideoCodec) * Math.Pow(pixelShare, 0.75);
        return Math.Min(hevcBitrate, BitrateCap(width, height, file.IsHdr)) * TargetFactor(codec) * QualityFactor(quality) * EffortFactor(effort);
    }

    /// <summary>
    /// Gets the audio bitrate to count, guessing when it isn't known.
    /// </summary>
    /// <param name="audioBitrate">The combined audio bitrate in bits per second, or 0 if unknown.</param>
    /// <returns>The bitrate.</returns>
    public static long AudioBitrate(long audioBitrate) => audioBitrate > 0 ? audioBitrate : DefaultAudioBitrate;

    /// <summary>
    /// Gets the bitrate HEVC needs compared with a codec, for the same quality.
    /// </summary>
    /// <param name="codec">The codec, as Jellyfin names it.</param>
    /// <returns>The factor: 0.6 for H.264, for example.</returns>
    public static double HevcBitrateFactor(string codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        return CodecFactor(codec) ?? 1.25;
    }

    // The list's estimate before any test encode. H.264 sources came out smaller than the rule of thumb in real
    // encodes (62 to 66% smaller against 38 to 57% estimated), so the rough figure leans that way. Test-encode
    // corrections are measured against the plain rule, so this stays out of them.
    /// <summary>
    /// Gets the correction for a rough estimate of an H.264 or other source, before any test encode.
    /// </summary>
    /// <param name="codec">The source's codec, as Jellyfin names it.</param>
    /// <returns>The correction to the video's size.</returns>
    public static double RoughScale(string codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        return codec.ToUpperInvariant() is "H264" or "AVC" ? 0.8 : 1;
    }

    // Bitrate compared with Balanced. Each step of an encoder's quality scale changes the bitrate by about a
    // sixth, so a quality level two steps up needs about a third more; grainy films change more than clean ones,
    // and test encodes correct it.
    private static double QualityFactor(QualityPreset quality) => quality switch
    {
        QualityPreset.NearIdentical => 2.4,
        QualityPreset.VeryHigh => 1.8,
        QualityPreset.High => 1.35,
        QualityPreset.Smaller => 0.62,
        QualityPreset.Smallest => 0.4,
        _ => 1.0
    };

    // Slower presets keep more texture at the same quality setting, and texture takes bits.
    private static double EffortFactor(EncodeEffort effort) => effort switch
    {
        EncodeEffort.Faster => 0.9,
        EncodeEffort.Slower => 1.25,
        EncodeEffort.Slowest => 1.3,
        _ => 1.0
    };

    // Bitrate needed compared with HEVC for the same quality.
    private static double TargetFactor(TargetCodec codec) => codec switch
    {
        TargetCodec.Av1 => 0.8,
        TargetCodec.H264 => 1 / 0.6,
        _ => 1.0
    };

    private static double? CodecFactor(string codec) => codec.ToUpperInvariant() switch
    {
        "H264" or "AVC" => 0.6,
        "HEVC" or "H265" => 1.0,
        "MPEG2VIDEO" or "MPEG4" or "VC1" or "WMV3" or "MSMPEG4V3" => 0.35,
        "VP8" => 0.6,
        "VP9" => 0.9,
        _ => null // AV1 and anything unknown: no recommendation.
    };

    // Wide films are short for their width (1920x800 is still 1080p), so width counts too.
    private static long BitrateCap(int width, int height, bool isHdr) => (width, height) switch
    {
        _ when width <= 0 && height <= 0 => long.MaxValue,
        _ when width >= 3200 || height >= 2000 => isHdr ? 22_000_000 : 18_000_000,
        _ when width >= 2200 || height >= 1300 => isHdr ? 12_000_000 : 10_000_000,
        _ when width >= 1600 || height >= 1000 => isHdr ? 8_000_000 : 6_000_000,
        _ when width >= 1100 || height >= 700 => 3_000_000,
        _ => 1_500_000
    };
}
