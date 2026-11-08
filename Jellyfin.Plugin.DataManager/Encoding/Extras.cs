// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.DataManager.Encoding;

/// <summary>
/// What big audio tracks are converted to.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AudioFormat
{
    /// <summary>Copy every audio track as it is.</summary>
    Keep,

    /// <summary>Dolby Digital Plus, which nearly every TV, soundbar and receiver plays. Up to 5.1.</summary>
    Eac3,

    /// <summary>AAC, which every device plays. Up to 7.1.</summary>
    Aac,

    /// <summary>Opus, the smallest for the same sound, but not every TV or receiver plays it. Up to 7.1.</summary>
    Opus
}

/// <summary>
/// What happens to HDR and Dolby Vision.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HdrChoice
{
    /// <summary>Keep HDR, and Dolby Vision where the encoder keeps it.</summary>
    Keep,

    /// <summary>Remove Dolby Vision's enhancement layer and metadata, keeping the HDR10 or HLG picture beneath it.</summary>
    RemoveDolbyVision,

    /// <summary>Turn HDR into SDR, for screens that can't show HDR. Needs a re-encode.</summary>
    ToneMapToSdr
}

/// <summary>
/// What a re-encode or remux does to one audio track that it converts.
/// </summary>
/// <param name="Index">The track's stream index in the source.</param>
/// <param name="Encoder">The ffmpeg encoder, such as eac3.</param>
/// <param name="BitRate">The new bitrate, in bits per second.</param>
/// <param name="Channels">The new number of channels.</param>
/// <param name="Label">What the new track is, such as "Dolby Digital Plus 5.1".</param>
public sealed record AudioConversion(int Index, string Encoder, long BitRate, int Channels, string Label);

/// <summary>
/// A font, picture or other file attached to an mkv.
/// </summary>
/// <param name="Index">Its stream index.</param>
/// <param name="MimeType">Its type, such as font/ttf or image/jpeg, or null.</param>
/// <param name="FileName">Its file name, or null.</param>
/// <param name="IsAttachedPicture">Whether ffmpeg reads it as a picture stream (cover art) rather than an attachment.</param>
public sealed record AttachmentInfo(int Index, string? MimeType, string? FileName, bool IsAttachedPicture = false)
{
    /// <summary>Gets a value indicating whether it's a picture, such as cover art.</summary>
    public bool IsImage => IsAttachedPicture
        || (MimeType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ?? false)
        || HasExtension(".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp");

    /// <summary>Gets a value indicating whether it's a font, which styled subtitles use.</summary>
    public bool IsFont => (MimeType is { } m && (m.StartsWith("font/", StringComparison.OrdinalIgnoreCase) || m.Contains("font", StringComparison.OrdinalIgnoreCase) || m.Contains("truetype", StringComparison.OrdinalIgnoreCase) || m.Contains("opentype", StringComparison.OrdinalIgnoreCase)))
        || HasExtension(".ttf", ".otf", ".ttc", ".woff", ".woff2");

    private bool HasExtension(params string[] extensions)
        => FileName is not null && extensions.Any(e => FileName.EndsWith(e, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The choices beyond the video and which tracks to keep: audio conversion, HDR, and attachments.
/// </summary>
/// <param name="Audio">The audio tracks to convert, by source index. Tracks not listed are copied.</param>
/// <param name="ToneMap">Whether to turn HDR into SDR.</param>
/// <param name="DolbyVisionStrip">The bitstream filter that removes Dolby Vision metadata, such as dovi_rpu=strip=1, to remove Dolby Vision and its enhancement layer from the video; or null to leave it.</param>
/// <param name="Attachments">The source attachments (fonts and other files) to keep, by stream index, or null to keep them as before (all of them when fonts are needed).</param>
/// <param name="StripCoverArt">Whether to leave out cover art, including Jellyfin's pictures when details are written into the file.</param>
/// <param name="SourceAttachmentCount">How many attachments (not counting cover art) the source has, to tell whether any are left out.</param>
public sealed record EncodeExtras(IReadOnlyList<AudioConversion> Audio, bool ToneMap, string? DolbyVisionStrip, IReadOnlyList<int>? Attachments, bool StripCoverArt, int SourceAttachmentCount = 0)
{
    /// <summary>Gets the changes to the kept tracks' titles, languages and flags. Converted tracks with none listed are retitled as before.</summary>
    public IReadOnlyList<TrackRewrite> Rewrites { get; init; } = [];

    /// <summary>Gets extras that change nothing.</summary>
    public static EncodeExtras None { get; } = new([], false, null, null, false);
}

/// <summary>
/// Works out which audio tracks to convert, and how.
/// </summary>
public static class AudioConverter
{
    private static readonly string[] FormatWords = ["truehd", "atmos", "dts", "flac", "pcm", "lossless", "dolby", "5.1", "7.1", "surround", "mlp"];

    /// <summary>
    /// Gets whether a track is one worth converting: lossless audio (TrueHD, DTS-HD MA, FLAC, PCM) or DTS, which
    /// take several times the space of Dolby Digital Plus, AAC or Opus. Already small lossy tracks are left alone,
    /// since converting them again loses sound for little space.
    /// </summary>
    /// <param name="track">The track.</param>
    /// <returns>True if converting it saves real space.</returns>
    public static bool IsBig(TrackInfo track)
    {
        ArgumentNullException.ThrowIfNull(track);
        var codec = track.Codec.ToUpperInvariant();
        return codec is "TRUEHD" or "MLP" or "DTS" or "FLAC" or "ALAC" or "WAVPACK" or "APE" || codec.StartsWith("PCM_", StringComparison.Ordinal);
    }

    /// <summary>
    /// Plans the conversion of one track, or returns null to copy it as it is. A big track is converted to the
    /// chosen format. With stereo chosen, any track with more than two channels is mixed down to stereo, in the
    /// chosen format or AAC. A track is never made bigger.
    /// </summary>
    /// <param name="track">The track.</param>
    /// <param name="format">The format to convert big tracks to.</param>
    /// <param name="stereo">Whether to mix surround tracks down to stereo.</param>
    /// <returns>The conversion, or null.</returns>
    public static AudioConversion? Plan(TrackInfo track, AudioFormat format, bool stereo)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (track.Kind != TrackKind.Audio)
        {
            return null;
        }

        var channels = track.Channels > 0 ? track.Channels : 2;
        var downmix = stereo && channels > 2;
        if (!downmix && (format == AudioFormat.Keep || !IsBig(track)))
        {
            return null;
        }

        var target = format == AudioFormat.Keep ? AudioFormat.Aac : format;
        var outChannels = downmix ? 2 : Math.Min(channels, target == AudioFormat.Eac3 ? 6 : 8);
        var bitRate = BitRate(target, outChannels);
        if (track.EffectiveBitRate <= bitRate)
        {
            return null;
        }

        return new AudioConversion(track.Index, Encoder(target), bitRate, outChannels, Name(target) + " " + ChannelName(outChannels));
    }

    /// <summary>
    /// Plans the conversions for a file's tracks.
    /// </summary>
    /// <param name="tracks">The tracks kept.</param>
    /// <param name="format">The format to convert big tracks to.</param>
    /// <param name="stereo">Whether to mix surround tracks down to stereo.</param>
    /// <returns>The conversions, in the file's order.</returns>
    public static IReadOnlyList<AudioConversion> PlanAll(IEnumerable<TrackInfo> tracks, AudioFormat format, bool stereo)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        return tracks.OrderBy(t => t.Index).Select(t => Plan(t, format, stereo)).OfType<AudioConversion>().ToList();
    }

    /// <summary>
    /// Gets the bitrate a format needs to sound the same as the original to nearly everyone.
    /// </summary>
    /// <param name="format">The format.</param>
    /// <param name="channels">The channels.</param>
    /// <returns>The bitrate in bits per second.</returns>
    public static long BitRate(AudioFormat format, int channels) => format switch
    {
        AudioFormat.Eac3 => channels <= 2 ? 224_000 : 640_000,
        AudioFormat.Opus => channels <= 1 ? 64_000 : channels <= 2 ? 128_000 : channels <= 6 ? 320_000 : 448_000,
        _ => channels <= 1 ? 96_000 : channels <= 2 ? 192_000 : channels <= 6 ? 448_000 : 576_000
    };

    /// <summary>
    /// Gets the ffmpeg arguments that convert the tracks, which go after the general -c:a copy.
    /// </summary>
    /// <param name="conversions">The tracks to convert.</param>
    /// <param name="audioOrder">The source index of each audio track in the new file, in order.</param>
    /// <param name="retitle">The source indexes of tracks whose title names the old format, which get a new title.</param>
    /// <returns>The arguments.</returns>
    public static IList<string> Arguments(IReadOnlyList<AudioConversion> conversions, IReadOnlyList<int> audioOrder, IReadOnlyCollection<int> retitle)
    {
        ArgumentNullException.ThrowIfNull(conversions);
        ArgumentNullException.ThrowIfNull(audioOrder);
        ArgumentNullException.ThrowIfNull(retitle);
        var args = new List<string>();
        foreach (var c in conversions)
        {
            var n = IndexOf(audioOrder, c.Index);
            if (n < 0)
            {
                continue;
            }

            var s = ":a:" + n.ToString(CultureInfo.InvariantCulture);
            args.AddRange(["-c" + s, c.Encoder, "-b" + s, c.BitRate.ToString(CultureInfo.InvariantCulture), "-ac" + s, c.Channels.ToString(CultureInfo.InvariantCulture), "-ar" + s, "48000"]);

            // Opus refuses some surround layouts, such as 5.1 with side speakers, so they're put in the plain ones.
            if (c.Encoder == "libopus")
            {
                args.AddRange(["-filter" + s, "aformat=channel_layouts=7.1|5.1|stereo|mono"]);
            }

            if (retitle.Contains(c.Index))
            {
                args.AddRange(["-metadata:s" + s, "title=" + c.Label]);
            }
        }

        return args;
    }

    /// <summary>
    /// Gets whether a track's title names its format, such as "TrueHD Atmos 7.1", so it would be wrong once converted.
    /// </summary>
    /// <param name="title">The title.</param>
    /// <returns>True if it names a format.</returns>
    public static bool TitleNamesFormat(string? title)
        => title is not null && FormatWords.Any(w => title.Contains(w, StringComparison.OrdinalIgnoreCase));

    private static int IndexOf(IReadOnlyList<int> list, int value)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] == value)
            {
                return i;
            }
        }

        return -1;
    }

    private static string Encoder(AudioFormat format) => format switch
    {
        AudioFormat.Eac3 => "eac3",
        AudioFormat.Opus => "libopus",
        _ => "aac"
    };

    private static string Name(AudioFormat format) => format switch
    {
        AudioFormat.Eac3 => "Dolby Digital Plus",
        AudioFormat.Opus => "Opus",
        _ => "AAC"
    };

    private static string ChannelName(int channels) => channels switch
    {
        1 => "Mono",
        2 => "Stereo",
        3 => "2.1",
        5 => "5.0",
        6 => "5.1",
        7 => "6.1",
        8 => "7.1",
        _ => channels.ToString(CultureInfo.InvariantCulture) + " ch"
    };
}

/// <summary>
/// The video filters and settings that turn HDR into SDR.
/// </summary>
public static class ToneMapping
{
    /// <summary>
    /// Gets the filter that maps HDR10 or HLG down to SDR BT.709, keeping highlights from clipping (Hable), and
    /// leaves it in a format the encoder takes.
    /// </summary>
    /// <param name="pixelFormat">The pixel format the encoder wants afterwards.</param>
    /// <returns>The filter.</returns>
    public static string Filter(string pixelFormat)
        => "zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv,format=" + pixelFormat;
}
