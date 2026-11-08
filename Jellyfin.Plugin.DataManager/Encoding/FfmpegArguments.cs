// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.DataManager.Encoding;

/// <summary>
/// Builds the ffmpeg arguments for a re-encode.
/// </summary>
public static class FfmpegArguments
{
    /// <summary>
    /// The encoder name that copies the video as it is, for a remux that only removes tracks.
    /// </summary>
    public const string CopyVideo = "copy";

    /// <summary>
    /// Builds the argument list.
    /// </summary>
    /// <param name="inputPath">The source file.</param>
    /// <param name="outputPath">The file to write.</param>
    /// <param name="encoder">The ffmpeg encoder, such as libx265, or <see cref="CopyVideo"/> to copy the video as it is.</param>
    /// <param name="quality">The quality preset.</param>
    /// <param name="effort">How long to spend encoding.</param>
    /// <param name="source">What is known about the source video.</param>
    /// <param name="outputContainer">The output container: mkv or mp4.</param>
    /// <param name="scaleTo">The size to scale the video to, or null to keep its size.</param>
    /// <param name="tags">Jellyfin's details to write into the file instead of the original's tags, or null to keep the original's.</param>
    /// <param name="hardware">Arguments that set up the GPU, which go before the input, or null for none.</param>
    /// <param name="tracks">The audio and subtitle tracks to keep, or null to keep them all.</param>
    /// <param name="extras">Audio conversion, HDR and attachment choices, or null for none.</param>
    /// <param name="framesOnGpu">Whether the GPU decodes the video and its frames go to the encoder without leaving the GPU.</param>
    /// <returns>The arguments, one per entry.</returns>
    public static IList<string> Build(string inputPath, string outputPath, string encoder, QualityPreset quality, EncodeEffort effort, SourceVideo source, string outputContainer, (int Width, int Height)? scaleTo = null, EmbeddedTags? tags = null, IReadOnlyList<string>? hardware = null, TrackPlan? tracks = null, EncodeExtras? extras = null, bool framesOnGpu = false)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        ArgumentNullException.ThrowIfNull(source);
        extras ??= EncodeExtras.None;
        if (extras.Audio.Count > 0 && tracks is null)
        {
            throw new ArgumentException("Converting audio needs the list of tracks kept.", nameof(tracks));
        }

        var isMp4 = string.Equals(outputContainer, "mp4", StringComparison.OrdinalIgnoreCase);
        var args = new List<string> { "-hide_banner", "-nostdin", "-y" };
        args.AddRange(hardware ?? []);

        // Older containers often have timestamps missing or out of order, which ffmpeg can rebuild as it reads.
        if (extras.RebuildTimestamps)
        {
            args.AddRange(["-fflags", "+genpts"]);
        }

        args.AddRange(["-i", inputPath]);

        // The tags come from a second input; an mp4's cover art from a third, as a picture stream.
        var images = extras.StripCoverArt || tags is null ? [] : tags.Images;
        var cover = isMp4 && images.Count > 0 ? images[0] : null;
        if (tags is not null)
        {
            args.AddRange(["-f", "ffmetadata", "-i", tags.TagsPath]);
        }

        if (cover is not null)
        {
            args.AddRange(["-i", cover.Path]);
        }

        // Main video (not a picture attached to the file), the audio and subtitle tracks kept (every one unless
        // some were chosen), and fonts for styled subtitles in mkv, unless no styled subtitles are kept.
        args.AddRange(["-map", "0:V:0"]);
        if (tracks is null)
        {
            args.AddRange(["-map", "0:a?", "-map", "0:s?"]);
        }
        else
        {
            foreach (var track in tracks.Kept)
            {
                args.AddRange(["-map", "0:" + track.Index.ToString(CultureInfo.InvariantCulture)]);
            }
        }

        if (!isMp4 && source.IsMatroska && extras.Attachments is { } attachments)
        {
            foreach (var index in attachments)
            {
                args.AddRange(["-map", "0:" + index.ToString(CultureInfo.InvariantCulture)]);
            }
        }
        else if (!isMp4 && source.IsMatroska && (tracks is null || tracks.NeedsFonts))
        {
            args.AddRange(["-map", "0:t?"]);
        }

        if (cover is not null)
        {
            args.AddRange(["-map", "2:0"]);
        }

        var copy = encoder == CopyVideo;
        args.AddRange(copy ? ["-c:v:0", "copy"] : VideoArguments(encoder, quality, effort, source, scaleTo, extras.ToneMap, framesOnGpu, extras.Deinterlace, extras.HdrBrightness, extras.EightBit, extras.ConstantQp, extras.ToneMapStyle));

        // Dolby Vision's enhancement layer (NAL type 63) and metadata (62) ride inside the HEVC video; removing them,
        // and the record that says they're there, leaves the HDR10 or HLG picture beneath.
        if (extras.DolbyVisionStrip is { } strip)
        {
            var hevc = copy ? string.Equals(source.Codec, "hevc", StringComparison.OrdinalIgnoreCase) : encoder == "libx265" || encoder.StartsWith("hevc_", StringComparison.Ordinal);
            var av1 = copy ? string.Equals(source.Codec, "av1", StringComparison.OrdinalIgnoreCase) : encoder == "libsvtav1" || encoder.StartsWith("av1_", StringComparison.Ordinal);
            if (hevc)
            {
                args.AddRange(["-bsf:v:0", "filter_units=remove_types=62|63," + strip]);
            }
            else if (av1 && strip.StartsWith("dovi_rpu", StringComparison.Ordinal))
            {
                args.AddRange(["-bsf:v:0", strip]);
            }
        }

        if (isMp4 && (encoder == "libx265" || encoder.StartsWith("hevc_", StringComparison.Ordinal) || (copy && string.Equals(source.Codec, "hevc", StringComparison.OrdinalIgnoreCase))))
        {
            // Apple players only recognise HEVC in mp4 with this tag.
            args.AddRange(["-tag:v:0", "hvc1"]);
        }

        if (cover is not null)
        {
            args.AddRange(["-c:v:1", "copy", "-disposition:v:1", "attached_pic"]);
        }

        args.AddRange(["-c:a", "copy"]);
        var flags = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (extras.Audio.Count > 0 && tracks is not null)
        {
            var audioKept = tracks.Kept.Where(t => t.Kind == TrackKind.Audio).ToList();

            // Without planned titles, a converted track whose title names its old format gets a new one.
            var retitle = extras.Rewrites.Count > 0 ? [] : audioKept.Where(t => AudioConverter.TitleNamesFormat(t.Title)).Select(t => t.Index).ToHashSet();
            args.AddRange(AudioConverter.Arguments(extras.Audio, audioKept.Select(t => t.Index).ToList(), retitle));
        }

        // With the default audio track removed, the first one kept becomes the default, so players pick it.
        if (tracks is not null && tracks.Removed.Any(t => t.Kind == TrackKind.Audio && t.IsDefault)
            && tracks.Kept.Any(t => t.Kind == TrackKind.Audio) && !tracks.Kept.Any(t => t.Kind == TrackKind.Audio && t.IsDefault))
        {
            flags["a:0"] = ["default"];
        }

        // Titles, languages and flags, by each track's place among the new file's audio or subtitles.
        if (tracks is not null && extras.Rewrites.Count > 0)
        {
            var byIndex = extras.Rewrites.ToDictionary(r => r.Index);
            foreach (var kind in new[] { TrackKind.Audio, TrackKind.Subtitle })
            {
                var kept = tracks.Kept.Where(t => t.Kind == kind).ToList();
                for (var n = 0; n < kept.Count; n++)
                {
                    if (!byIndex.TryGetValue(kept[n].Index, out var rewrite))
                    {
                        continue;
                    }

                    var spec = (kind == TrackKind.Audio ? "a:" : "s:") + n.ToString(CultureInfo.InvariantCulture);
                    if (rewrite.Title is not null)
                    {
                        args.AddRange(["-metadata:s:" + spec, "title=" + rewrite.Title]);
                    }

                    if (rewrite.Language is not null)
                    {
                        args.AddRange(["-metadata:s:" + spec, "language=" + rewrite.Language]);
                    }

                    if (rewrite.Flags.Count > 0)
                    {
                        (flags.TryGetValue(spec, out var list) ? list : flags[spec] = []).AddRange(rewrite.Flags);
                    }
                }
            }
        }

        // Flags are added to what each track already has.
        foreach (var (spec, list) in flags)
        {
            args.AddRange(["-disposition:" + spec, string.Concat(list.Distinct(StringComparer.Ordinal).Select(f => "+" + f))]);
        }

        // mp4 text subtitles (mov_text) can't be copied into mkv, so they become srt. mp4 output only happens
        // when replacing an mp4, so its subtitles are already mov_text.
        args.AddRange(["-c:s", !isMp4 && source.IsMp4 ? "srt" : "copy"]);

        // Pictures go into an mkv as attachments named for what they are, after the original's own attachments.
        if (!isMp4 && tags is not null)
        {
            for (var i = 0; i < images.Count; i++)
            {
                var index = (tags.SourceAttachments + i).ToString(CultureInfo.InvariantCulture);
                args.AddRange(["-attach", images[i].Path, "-metadata:s:t:" + index, "mimetype=" + images[i].MimeType, "-metadata:s:t:" + index, "filename=" + images[i].FileName]);
            }
        }

        if (isMp4)
        {
            args.AddRange(["-movflags", "+faststart"]);
        }

        // Each track keeps its own language and title either way; the file's tags come from Jellyfin or the original.
        args.AddRange(["-map_metadata", tags is null ? "0" : "1", "-map_chapters", "0"]);
        args.AddRange(["-progress", "pipe:1", "-nostats", "-f", isMp4 ? "mp4" : "matroska", outputPath]);
        return args;
    }

    /// <summary>
    /// Builds the arguments for a short test encode that measures how fast this server encodes and how big the
    /// result is. The video is written bare, with no container, so its size is the video's alone.
    /// </summary>
    /// <param name="inputPath">The source file.</param>
    /// <param name="outputPath">The file to write, which the caller deletes.</param>
    /// <param name="startSeconds">Where in the source to start.</param>
    /// <param name="frames">How many frames to encode.</param>
    /// <param name="encoder">The ffmpeg encoder.</param>
    /// <param name="quality">The quality preset.</param>
    /// <param name="effort">How long to spend encoding.</param>
    /// <param name="source">What is known about the source video.</param>
    /// <param name="scaleTo">The size to scale the video to, or null to keep its size.</param>
    /// <param name="hardware">Arguments that set up the GPU, which go before the input, or null for none.</param>
    /// <param name="framesOnGpu">Whether the GPU decodes the video and its frames go to the encoder without leaving the GPU.</param>
    /// <returns>The arguments, one per entry.</returns>
    public static IList<string> BuildSpeedTest(string inputPath, string outputPath, double startSeconds, int frames, string encoder, QualityPreset quality, EncodeEffort effort, SourceVideo source, (int Width, int Height)? scaleTo, IReadOnlyList<string>? hardware = null, bool framesOnGpu = false)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        ArgumentNullException.ThrowIfNull(source);

        var args = new List<string> { "-hide_banner", "-nostdin", "-y" };
        args.AddRange(hardware ?? []);
        args.AddRange(
        [
            "-ss", startSeconds.ToString("0.###", CultureInfo.InvariantCulture), "-i", inputPath,
            "-map", "0:V:0", "-frames:v", frames.ToString(CultureInfo.InvariantCulture)
        ]);
        args.AddRange(VideoArguments(encoder, quality, effort, source, scaleTo, false, framesOnGpu));
        // Each frame is written as it comes out, so the bytes written so far match the frames encoded so far.
        args.AddRange(["-an", "-sn", "-progress", "pipe:1", "-stats_period", "0.5", "-nostats", "-flush_packets", "1", "-f", BareFormat(encoder), outputPath]);
        return args;
    }

    /// <summary>
    /// Builds the arguments for a speed test that encodes a generated picture instead of a file, so nothing is read
    /// from a drive or decoded and only the encoder is timed. A second of moving picture is made once and played
    /// round and round from memory, which costs the CPU next to nothing even at thousands of frames a second.
    /// </summary>
    /// <param name="outputPath">The file to write, which the caller deletes.</param>
    /// <param name="width">The picture's width.</param>
    /// <param name="height">The picture's height.</param>
    /// <param name="frameRate">The frames a second the picture is made at.</param>
    /// <param name="frames">How many frames to encode at most.</param>
    /// <param name="encoder">The ffmpeg encoder.</param>
    /// <param name="quality">The quality preset.</param>
    /// <param name="effort">How long to spend encoding.</param>
    /// <param name="source">What is known about the video being stood in for, which decides the pixel format.</param>
    /// <param name="hardware">Arguments that set up the GPU, which go before the input, or null for none.</param>
    /// <returns>The arguments, one per entry.</returns>
    public static IList<string> BuildGeneratedSpeedTest(string outputPath, int width, int height, double frameRate, int frames, string encoder, QualityPreset quality, EncodeEffort effort, SourceVideo source, IReadOnlyList<string>? hardware = null)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        ArgumentNullException.ThrowIfNull(source);

        var rate = Math.Clamp((int)Math.Round(frameRate), 1, 120);
        var args = new List<string> { "-hide_banner", "-nostdin", "-y" };
        args.AddRange(hardware ?? []);

        // Only a dozen frames are kept for the loop: at 4K each is about 12 MB, and a test runs up to four of these.
        args.AddRange(
        [
            "-f", "lavfi", "-i",
            string.Create(CultureInfo.InvariantCulture, $"testsrc2=size={width}x{height}:rate={rate}:duration=1,loop=loop=-1:size={Math.Min(rate, 12)}:start=0"),
            "-frames:v", frames.ToString(CultureInfo.InvariantCulture)
        ]);
        args.AddRange(VideoArguments(encoder, quality, effort, source, null));
        args.AddRange(["-an", "-sn", "-progress", "pipe:1", "-stats_period", "0.5", "-nostats", "-flush_packets", "1", "-f", BareFormat(encoder), outputPath]);
        return args;
    }

    /// <summary>
    /// Builds the arguments that copy a stretch of a video, unchanged and without its sound, for timing encodes on.
    /// </summary>
    /// <param name="inputPath">The source file.</param>
    /// <param name="outputPath">The file to write, an mkv.</param>
    /// <param name="startSeconds">Where in the source to start.</param>
    /// <param name="seconds">How much to copy.</param>
    /// <returns>The arguments, one per entry.</returns>
    public static IList<string> BuildSceneCopy(string inputPath, string outputPath, double startSeconds, double seconds)
        =>
        [
            "-hide_banner", "-nostdin", "-y",
            "-ss", startSeconds.ToString("0.###", CultureInfo.InvariantCulture), "-i", inputPath,
            "-map", "0:V:0", "-t", seconds.ToString("0.###", CultureInfo.InvariantCulture), "-c:v", "copy", "-an", "-sn", "-f", "matroska", outputPath
        ];

    /// <summary>
    /// Builds the arguments for a preview: a few frames encoded exactly as a real encode would, video only.
    /// </summary>
    /// <param name="inputPath">The source file.</param>
    /// <param name="outputPath">The file to write, an mkv.</param>
    /// <param name="startSeconds">Where in the source to start.</param>
    /// <param name="frames">How many frames to encode.</param>
    /// <param name="encoder">The ffmpeg encoder.</param>
    /// <param name="quality">The quality preset.</param>
    /// <param name="effort">How long to spend encoding.</param>
    /// <param name="source">What is known about the source video.</param>
    /// <param name="scaleTo">The size to scale the video to, or null to keep its size.</param>
    /// <param name="hardware">Arguments that set up the GPU, which go before the input, or null for none.</param>
    /// <param name="toneMap">Whether to turn HDR into SDR.</param>
    /// <param name="variant">How the video is encoded, when it isn't the usual way.</param>
    /// <param name="style">How HDR is fitted into SDR.</param>
    /// <returns>The arguments, one per entry.</returns>
    public static IList<string> BuildPreview(string inputPath, string outputPath, double startSeconds, int frames, string encoder, QualityPreset quality, EncodeEffort effort, SourceVideo source, (int Width, int Height)? scaleTo, IReadOnlyList<string>? hardware = null, bool toneMap = false, VideoVariant? variant = null, ToneMapStyle style = ToneMapStyle.Hable)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        ArgumentNullException.ThrowIfNull(source);

        var args = new List<string> { "-hide_banner", "-nostdin", "-y" };
        args.AddRange(hardware ?? []);
        args.AddRange(
        [
            "-ss", startSeconds.ToString("0.###", CultureInfo.InvariantCulture), "-i", inputPath,
            "-map", "0:V:0", "-frames:v", frames.ToString(CultureInfo.InvariantCulture)
        ]);
        args.AddRange(VideoArguments(variant?.Encoder ?? encoder, quality, effort, source, scaleTo, toneMap, eightBit: variant?.EightBit ?? false, constantQp: variant?.ConstantQp ?? false, style: style));
        args.AddRange(["-an", "-sn", "-f", "matroska", outputPath]);
        return args;
    }

    /// <summary>
    /// Builds a second-long encode of a generated picture, to find out whether a GPU encoder takes 10-bit video and a
    /// quality level. Nothing is written.
    /// </summary>
    /// <param name="encoder">The ffmpeg encoder.</param>
    /// <param name="quality">The quality level.</param>
    /// <param name="tenBit">Whether the picture is 10-bit.</param>
    /// <param name="hardware">Arguments that set up the GPU.</param>
    /// <param name="constantQp">Whether QSV uses a constant quantizer.</param>
    /// <returns>The arguments.</returns>
    public static IList<string> BuildCapabilityTest(string encoder, QualityPreset quality, bool tenBit, IReadOnlyList<string>? hardware, bool constantQp = false)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        var source = new SourceVideo(IsMatroska: true, IsMp4: false, IsTenBit: tenBit, IsHdr: false, ColorTransfer: null);
        var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-v", "error" };
        args.AddRange(hardware ?? []);
        args.AddRange(["-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=24,format=" + (tenBit ? "yuv420p10le" : "yuv420p"), "-frames:v", "24"]);
        args.AddRange(VideoArguments(encoder, quality, EncodeEffort.Faster, source, null, constantQp: constantQp));
        args.AddRange(["-f", "null", "-"]);
        return args;
    }

    /// <summary>
    /// Gets whether an encode of this source would make 10-bit video.
    /// </summary>
    /// <param name="encoder">The ffmpeg encoder.</param>
    /// <param name="source">The source.</param>
    /// <param name="toneMap">Whether HDR is turned into SDR.</param>
    /// <returns>True for 10-bit.</returns>
    public static bool WantsTenBit(string encoder, SourceVideo source, bool toneMap)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        ArgumentNullException.ThrowIfNull(source);
        toneMap = toneMap && source.IsHdr;
        var h264 = encoder == "libx264" || encoder.StartsWith("h264_", StringComparison.Ordinal);
        var vaapi = encoder.EndsWith("_vaapi", StringComparison.Ordinal);
        return (source.IsTenBit || source.IsHdr) && !(toneMap && h264) && !(vaapi && h264);
    }

    /// <summary>
    /// Gets the efforts an encoder can tell apart. Software encoders, NVENC and QSV have a range of presets; AMF has
    /// two; VideoToolbox has one speed.
    /// </summary>
    /// <param name="encoder">The ffmpeg encoder.</param>
    /// <returns>The efforts, quickest first.</returns>
    public static IReadOnlyList<EncodeEffort> Efforts(string encoder)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        return encoder switch
        {
            _ when encoder.EndsWith("_videotoolbox", StringComparison.Ordinal) => [EncodeEffort.Standard],
            _ when encoder.EndsWith("_amf", StringComparison.Ordinal) => [EncodeEffort.Faster, EncodeEffort.Standard],
            _ when encoder.EndsWith("_vaapi", StringComparison.Ordinal) => [EncodeEffort.Standard],
            _ => [EncodeEffort.Faster, EncodeEffort.Standard, EncodeEffort.Slower, EncodeEffort.Slowest]
        };
    }

    /// <summary>
    /// Gets the video encoder arguments for a quality and effort, as a re-encode uses them, without any scaling.
    /// </summary>
    /// <param name="encoder">The ffmpeg encoder.</param>
    /// <param name="quality">The quality preset.</param>
    /// <param name="effort">How long to spend encoding.</param>
    /// <param name="source">What is known about the video being encoded.</param>
    /// <returns>The arguments, one per entry.</returns>
    public static IList<string> EncoderArguments(string encoder, QualityPreset quality, EncodeEffort effort, SourceVideo source)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        ArgumentNullException.ThrowIfNull(source);
        return VideoArguments(encoder, quality, effort, source, null);
    }

    private static List<string> VideoArguments(string encoder, QualityPreset quality, EncodeEffort effort, SourceVideo source, (int Width, int Height)? scaleTo, bool toneMap = false, bool framesOnGpu = false, bool deinterlace = false, Services.HdrBrightness? brightness = null, bool eightBit = false, bool constantQp = false, ToneMapStyle style = ToneMapStyle.Hable)
    {
        var args = new List<string> { "-c:v", encoder };

        // Frames kept on the GPU go to the encoder as they are, in the format the decoder made them. Scaling, tone
        // mapping and deinterlacing here run on the CPU, which wants the frames in system memory, so they never go
        // together; and the pixel format is already right, so asking for one would make ffmpeg copy them back to change it.
        framesOnGpu = framesOnGpu && scaleTo is null && !toneMap && !deinterlace;
        args.AddRange(QualityArguments(encoder, quality, effort, constantQp));

        // Tone mapping only applies to HDR; afterwards the picture is SDR. H.264 is kept 8-bit, as players expect;
        // HEVC and AV1 stay 10-bit, which keeps skies and shadows from banding at no cost in size.
        toneMap = toneMap && source.IsHdr;
        var hdr = source.IsHdr && !toneMap;
        var h264 = encoder == "libx264" || encoder.StartsWith("h264_", StringComparison.Ordinal);
        var vaapi = encoder.EndsWith("_vaapi", StringComparison.Ordinal);

        // VAAPI's H.264 encoder only does 8-bit, and so do older GPUs, for which the video is made 8-bit on purpose.
        var tenBit = WantsTenBit(encoder, source, toneMap) && !eightBit;
        hdr = hdr && tenBit;

        // The GPU encoders that take frames from system memory want 10-bit as P010.
        var p010 = encoder.EndsWith("_videotoolbox", StringComparison.Ordinal) || encoder.EndsWith("_qsv", StringComparison.Ordinal) || encoder.EndsWith("_nvenc", StringComparison.Ordinal);
        var pixelFormat = tenBit ? (p010 ? "p010le" : "yuv420p10le") : "yuv420p";

        // Only the main video is filtered; an mp4's cover art is copied as it is.
        var filters = new List<string>();

        // Interlaced frames first, before anything changes their size; frames already progressive pass through.
        if (deinterlace)
        {
            filters.Add("bwdif=mode=send_frame:parity=auto:deint=interlaced");
        }

        if (toneMap)
        {
            filters.Add(ToneMapping.Filter(tenBit ? "yuv420p10le" : "yuv420p", style));
        }

        if (scaleTo is { } size)
        {
            filters.Add(string.Create(CultureInfo.InvariantCulture, $"scale={size.Width}:{size.Height}:flags=lanczos"));
        }

        // The frames go up to the GPU in the format its encoder wants, as the last step.
        if (vaapi)
        {
            filters.Add(tenBit ? "format=p010le" : "format=nv12");
            filters.Add("hwupload");
        }

        if (filters.Count > 0)
        {
            args.AddRange(["-filter:v:0", string.Join(',', filters)]);
        }

        if (tenBit && !vaapi && !framesOnGpu)
        {
            args.AddRange(["-pix_fmt", pixelFormat]);
        }

        if (hdr)
        {
            args.AddRange(["-color_primaries", "bt2020", "-color_trc", source.ColorTransfer ?? "smpte2084", "-colorspace", "bt2020nc"]);
        }
        else if (toneMap)
        {
            args.AddRange(["-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709"]);
        }

        // x265 takes all of its own settings in one argument. SAO smooths away fine texture such as grain and
        // background detail, which is what makes HEVC look soft, and turning it off costs little space.
        if (encoder == "libx265")
        {
            var x265 = new List<string> { "no-sao=1" };
            if (hdr)
            {
                x265.Add("hdr10=1:repeat-headers=1:hdr10-opt=1");

                // The original's brightness information, so players show the new file the same way.
                if (brightness?.MasterDisplay is { } display)
                {
                    x265.Add("master-display=" + display);
                }

                if (brightness?.MaxCll is { } light)
                {
                    x265.Add("max-cll=" + light);
                }
            }

            args.AddRange(["-x265-params", string.Join(':', x265)]);
        }

        return args;
    }

    // Each encoder's own scale for the six qualities, chosen so the same quality looks about the same whichever
    // encoder makes it, and its presets for the four efforts.
    private static List<string> QualityArguments(string encoder, QualityPreset quality, EncodeEffort effort, bool constantQp = false)
    {
        string Pick(ReadOnlySpan<int> levels) => levels[Math.Clamp((int)quality, 0, levels.Length - 1)].ToString(CultureInfo.InvariantCulture);
        string Speed(string faster, string standard, string slower, string slowest) => effort switch
        {
            EncodeEffort.Faster => faster,
            EncodeEffort.Slower => slower,
            EncodeEffort.Slowest => slowest,
            _ => standard
        };

        return encoder switch
        {
            "libx265" => ["-preset", Speed("fast", "medium", "slow", "slower"), "-crf", Pick([16, 18, 20, 22, 25, 28])],
            "libx264" => ["-preset", Speed("medium", "slow", "slower", "veryslow"), "-crf", Pick([14, 16, 18, 20, 23, 26])],
            "libsvtav1" => ["-preset", Speed("8", "6", "4", "3"), "-crf", Pick([18, 21, 24, 28, 32, 36])],
            _ when encoder.EndsWith("_nvenc", StringComparison.Ordinal) => ["-preset", Speed("p3", "p5", "p6", "p7"), "-rc", "vbr", "-cq", Pick([17, 19, 21, 24, 28, 31]), "-b:v", "0"],
            // QSV's ICQ mode: constant quality on a scale like x264's CRF. Some drivers, such as Linux's iHD on older
            // chips, refuse ICQ; a constant quantizer (-q:v) on the same scale is the fallback.
            _ when encoder.EndsWith("_qsv", StringComparison.Ordinal) => constantQp
                ? ["-preset", Speed("faster", "medium", "slow", "veryslow"), "-q:v", Pick([18, 20, 22, 25, 28, 31])]
                : ["-preset", Speed("faster", "medium", "slow", "veryslow"), "-global_quality", Pick([18, 20, 22, 25, 28, 31])],
            _ when encoder.EndsWith("_videotoolbox", StringComparison.Ordinal) => ["-q:v", Pick([80, 75, 70, 62, 52, 45])],
            _ when encoder.EndsWith("_amf", StringComparison.Ordinal) => ["-quality", effort == EncodeEffort.Faster ? "balanced" : "quality", "-rc", "cqp", "-qp_i", Pick([16, 18, 20, 23, 26, 29]), "-qp_p", Pick([18, 20, 22, 25, 28, 31])],
            // VAAPI has no presets, so every effort is the same. A constant quantizer on a scale like QSV's.
            _ when encoder.EndsWith("_vaapi", StringComparison.Ordinal) => ["-rc_mode", "CQP", "-qp", Pick([18, 20, 22, 25, 28, 31])],
            _ => []
        };
    }

    // The bare stream format for an encoder's codec, with no container around it.
    private static string BareFormat(string encoder) => encoder switch
    {
        "libx264" => "h264",
        "libsvtav1" => "obu",
        _ when encoder.StartsWith("h264_", StringComparison.Ordinal) => "h264",
        _ when encoder.StartsWith("av1_", StringComparison.Ordinal) => "obu",
        _ => "hevc"
    };
}

/// <summary>
/// What the encoder needs to know about the source video.
/// </summary>
/// <param name="IsMatroska">Whether the source is an mkv file.</param>
/// <param name="IsMp4">Whether the source is an mp4 or m4v file.</param>
/// <param name="IsTenBit">Whether the source has 10 or more bits per channel.</param>
/// <param name="IsHdr">Whether the source is HDR.</param>
/// <param name="ColorTransfer">The source transfer characteristic, such as smpte2084 or arib-std-b67.</param>
/// <param name="Codec">The source video codec as Jellyfin names it, such as hevc, which decides whether the GPU can decode it.</param>
/// <param name="DolbyVisionProfile">The Dolby Vision profile, such as 8, or null when the video has none.</param>
/// <param name="DolbyVisionOnly">Whether the video is only Dolby Vision (profile 5), with no HDR10, HLG or SDR picture beneath it.</param>
/// <param name="DolbyVisionLayer">Whether the video carries a Dolby Vision enhancement layer (profile 7).</param>
/// <param name="IsInterlaced">Whether Jellyfin found the video interlaced.</param>
/// <param name="Container">The container, such as mkv or avi.</param>
public record SourceVideo(bool IsMatroska, bool IsMp4, bool IsTenBit, bool IsHdr, string? ColorTransfer, string? Codec = null, int? DolbyVisionProfile = null, bool DolbyVisionOnly = false, bool DolbyVisionLayer = false, bool IsInterlaced = false, string? Container = null);

/// <summary>
/// Jellyfin's details to write into a new file.
/// </summary>
/// <param name="TagsPath">An ffmpeg metadata file with the tags.</param>
/// <param name="Images">The pictures to attach. An mp4 takes only the first, as its cover art.</param>
/// <param name="SourceAttachments">How many attachments, such as fonts, the original mkv has, which are copied first.</param>
public record EmbeddedTags(string TagsPath, IReadOnlyList<EmbeddedImage> Images, int SourceAttachments);
