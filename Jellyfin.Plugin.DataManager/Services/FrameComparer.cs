// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.DataManager.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DataManager.Services;

/// <summary>
/// Takes the same frame from two versions of a video and measures how much it changed.
/// </summary>
public sealed partial class FrameComparer : IDisposable
{
    // Frames are sent at full size up to 4K, so they can be looked at closely.
    private const int MaxImageWidth = 3840;
    private const int ThumbnailWidth = 384;
    private const int FilmstripFrames = 8;

    // Points read from the index to find keyframes and the busiest stretch. Each is one seek.
    private const int FilmstripIndexPoints = 64;

    // Tone-maps HDR to SDR so frames look right on a normal screen. Needs ffmpeg with zscale, which Jellyfin's ffmpeg has.
    private const string ToneMap = "zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p,";

    // Clips are shown at up to 1080p: the size most screens show them at, and light enough to send.
    private const int MaxClipWidth = 1920;
    private const int MaxClipHeight = 1080;

    // Frames either side of the expected one that are checked for the closest match. Files can disagree on where
    // they start (audio priming, an encoder's B-frame delay, rounded timestamps), so the same time can land a frame or
    // two apart in each.
    private const int MatchRadius = 2;
    private const int MatchWidth = 128;
    private const int MatchHeight = 72;

    // ffmpeg is stopped once it has done nothing for this long, or at the limit, however it's getting on.
    private static readonly TimeSpan _stall = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan _timeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan _clipTimeout = TimeSpan.FromMinutes(20);

    private readonly ILibraryManager _libraryManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<FrameComparer> _logger;

    // One comparison at a time, so dragging through the timeline can't start a pile of ffmpeg processes.
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="FrameComparer"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="mediaEncoder">Jellyfin's media encoder, which knows where ffmpeg is.</param>
    /// <param name="logger">The logger.</param>
    public FrameComparer(ILibraryManager libraryManager, IMediaEncoder mediaEncoder, ILogger<FrameComparer> logger)
    {
        _libraryManager = libraryManager;
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    /// <summary>
    /// Compares one frame of two videos.
    /// </summary>
    /// <param name="firstId">The first video, usually the original.</param>
    /// <param name="secondId">The second video, usually the re-encode.</param>
    /// <param name="positionSeconds">Where in the video to take the frame.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Both frames, the detail map and the similarity scores.</returns>
    public async Task<FrameComparison> CompareAsync(Guid firstId, Guid secondId, double positionSeconds, CancellationToken cancellationToken)
    {
        var first = GetVideo(firstId);
        var second = GetVideo(secondId);
        var duration = TimeSpan.FromTicks(first.RunTimeTicks ?? 0).TotalSeconds;
        var position = duration > 1 ? Math.Clamp(positionSeconds, 0, duration - 1) : 0;
        var secondStream = VideoStream(second);

        // The second video is opened a little earlier, so the frames just before the expected one can be checked too.
        var fps = FramesPerSecond(secondStream);
        var secondSeek = Math.Max(0, position - ((MatchRadius + 0.5) / fps));
        var expected = (int)Math.Round((position - secondSeek) * fps);
        var result = await CompareFramesAsync(
            new Input(first.Path, position, 0),
            new Input(second.Path, secondSeek, expected),
            VideoStream(first),
            secondStream,
            null,
            cancellationToken).ConfigureAwait(false);
        result.PositionSeconds = position;
        result.DurationSeconds = duration;
        return result;
    }

    /// <summary>
    /// Compares a frame of a video with the same frame of a short preview encode of it.
    /// </summary>
    /// <param name="original">The original video.</param>
    /// <param name="positionSeconds">Where in the original the preview starts.</param>
    /// <param name="previewPath">The preview encode, which starts at that position.</param>
    /// <param name="frameIndex">Which frame of the preview to compare, counting from 0.</param>
    /// <param name="framesPerSecond">The frame rate, to say where the frame is.</param>
    /// <param name="scaleTo">The size the preview was scaled to, or null if it kept the original's size. The original is
    /// compared at that size, so the scores measure what the encoder lost rather than what the smaller size did.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Both frames, the detail map and the similarity scores.</returns>
    public async Task<FrameComparison> ComparePreviewAsync(Video original, double positionSeconds, string previewPath, int frameIndex, double framesPerSecond, (int Width, int Height)? scaleTo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(original);
        var stream = VideoStream(original);
        var result = await CompareFramesAsync(new Input(original.Path, positionSeconds, frameIndex), new Input(previewPath, null, frameIndex), stream, stream, scaleTo, cancellationToken).ConfigureAwait(false);
        result.PositionSeconds = positionSeconds + (frameIndex / framesPerSecond);
        result.DurationSeconds = TimeSpan.FromTicks(original.RunTimeTicks ?? 0).TotalSeconds;
        return result;
    }

    /// <summary>
    /// Takes small pictures of frames spread through a video, between the opening and the credits, with its busiest
    /// stretch among them. Each is a keyframe, so a preview can start exactly there.
    /// </summary>
    /// <param name="itemId">The video.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The frames.</returns>
    public async Task<Filmstrip> FilmstripAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var video = GetVideo(itemId);
        var stream = VideoStream(video);
        var duration = TimeSpan.FromTicks(video.RunTimeTicks ?? 0).TotalSeconds;
        var positions = await PickFramesAsync(video.Path, duration, cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var folder = Path.Combine(Path.GetTempPath(), "datamanager-filmstrip-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            var toneMap = stream?.VideoRange == VideoRange.HDR;
            try
            {
                await RunFfmpegAsync(ThumbnailArguments(video.Path, positions, toneMap, folder), cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex) when (toneMap)
            {
                _logger.LogWarning(ex, "Storage Optimizer could not tone-map HDR frames, so taking them without it");
                await RunFfmpegAsync(ThumbnailArguments(video.Path, positions, false, folder), cancellationToken).ConfigureAwait(false);
            }

            var strip = new Filmstrip { DurationSeconds = duration };
            for (var i = 0; i < positions.Count; i++)
            {
                // A frame past the end of a short or damaged file comes out empty; leave it out.
                var image = Path.Combine(folder, "t" + i.ToString(CultureInfo.InvariantCulture) + ".jpg");
                if (File.Exists(image) && new FileInfo(image).Length > 0)
                {
                    strip.Frames.Add(new FilmstripFrame
                    {
                        PositionSeconds = positions[i].Seconds,
                        Image = await DataUrlAsync(image, cancellationToken).ConfigureAwait(false),
                        IsBusiest = positions[i].IsBusiest
                    });
                }
            }

            return strip;
        }
        finally
        {
            _gate.Release();
            RemoveFolder(folder);
        }
    }

    /// <summary>
    /// Stacks a short encode under the same frames of the original, as an H.264 mp4 any browser can play. Both are
    /// shown at the encode's size (at most 1080p) and tone-mapped if HDR, and stored at very high quality so the
    /// clip itself adds nothing visible.
    /// </summary>
    /// <param name="original">The original video.</param>
    /// <param name="positionSeconds">Where in the original the encode starts.</param>
    /// <param name="encodedPath">The short encode.</param>
    /// <param name="frames">How many frames the encode has.</param>
    /// <param name="size">The encode's size.</param>
    /// <param name="outputPath">The mp4 to write.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the clip is written.</returns>
    public async Task StackClipAsync(Video original, double positionSeconds, string encodedPath, int frames, (int Width, int Height) size, string outputPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(original);

        var scale = Math.Min(1, Math.Min(MaxClipWidth / (double)size.Width, MaxClipHeight / (double)size.Height));
        var width = (int)Math.Round(size.Width * scale / 2) * 2;
        var height = (int)Math.Round(size.Height * scale / 2) * 2;
        var toneMap = VideoStream(original)?.VideoRange == VideoRange.HDR;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                await RunClipAsync(ClipArguments(original.Path, positionSeconds, encodedPath, frames, width, height, toneMap, outputPath), cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex) when (toneMap)
            {
                _logger.LogWarning(ex, "Storage Optimizer could not tone-map the HDR clip, so making it without");
                await RunClipAsync(ClipArguments(original.Path, positionSeconds, encodedPath, frames, width, height, false, outputPath), cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            File.Delete(outputPath);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private async Task RunClipAsync(IList<string> args, CancellationToken cancellationToken)
        => await FfmpegRunner.RunWatchedAsync(_mediaEncoder.EncoderPath, args, _stall, _clipTimeout, "make the clip", cancellationToken).ConfigureAwait(false);

    private static List<string> ClipArguments(string originalPath, double positionSeconds, string encodedPath, int frames, int width, int height, bool toneMap, string outputPath)
    {
        var count = frames.ToString(CultureInfo.InvariantCulture);
        var each = "trim=end_frame=" + count + ",setpts=PTS-STARTPTS,scale=" + width.ToString(CultureInfo.InvariantCulture) + ":" + height.ToString(CultureInfo.InvariantCulture)
            + ":flags=lanczos,setsar=1," + (toneMap ? ToneMap : "format=yuv420p,");
        return
        [
            "-hide_banner", "-nostdin", "-y",
            "-ss", positionSeconds.ToString("0.###", CultureInfo.InvariantCulture), "-i", originalPath,
            "-i", encodedPath,
            "-filter_complex", "[0:v]" + each + "null[a];[1:v]" + each + "null[b];[a][b]vstack=inputs=2[out]",
            "-map", "[out]", "-frames:v", count, "-an", "-sn",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "14", "-pix_fmt", "yuv420p",
            "-movflags", "+faststart", "-f", "mp4", outputPath
        ];
    }

    private Video GetVideo(Guid id)
    {
        var video = _libraryManager.GetItemById<Video>(id);
        if (video is null || !File.Exists(video.Path))
        {
            throw new FileNotFoundException("That video is no longer in the library. Scan again to refresh the list.");
        }

        return video;
    }

    private static MediaStream? VideoStream(Video video) => video.GetMediaStreams().FirstOrDefault(s => s.Type == MediaStreamType.Video);

    private static double FramesPerSecond(MediaStream? stream)
    {
        var fps = stream?.RealFrameRate ?? stream?.AverageFrameRate ?? 0;
        return fps is > 1 and < 1000 ? fps : 24;
    }

    private async Task<FrameComparison> CompareFramesAsync(Input first, Input second, MediaStream? firstStream, MediaStream? secondStream, (int Width, int Height)? size, CancellationToken cancellationToken)
    {
        var isHdr = firstStream?.VideoRange == VideoRange.HDR || secondStream?.VideoRange == VideoRange.HDR;
        var isTenBit = (firstStream?.BitDepth ?? 8) > 8 || (secondStream?.BitDepth ?? 8) > 8;
        var frame = new FrameSize(size?.Width ?? firstStream?.Width ?? 0, size?.Height ?? firstStream?.Height ?? 0, isTenBit);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var folder = Path.Combine(Path.GetTempPath(), "datamanager-compare-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            second = second with { Frame = await MatchFrameAsync(first, second, folder, cancellationToken).ConfigureAwait(false) };
            var toneMapped = isHdr;
            string log;
            try
            {
                log = await RunFfmpegAsync(Arguments(first, second, frame, toneMapped, folder), cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex) when (toneMapped)
            {
                _logger.LogWarning(ex, "Storage Optimizer could not tone-map HDR frames, so comparing them without it");
                toneMapped = false;
                log = await RunFfmpegAsync(Arguments(first, second, frame, toneMapped, folder), cancellationToken).ConfigureAwait(false);
            }

            var result = new FrameComparison
            {
                Ssim = ParseScore(SsimPattern().Match(log)),
                Psnr = ParseScore(PsnrPattern().Match(log)),
                ToneMapped = toneMapped,
                FirstImage = await DataUrlAsync(Path.Combine(folder, "first.jpg"), cancellationToken).ConfigureAwait(false),
                SecondImage = await DataUrlAsync(Path.Combine(folder, "second.jpg"), cancellationToken).ConfigureAwait(false)
            };

            // Without a known size the frames can't be read back, so there's no detail map, only the scores.
            if (frame.Width > 0 && frame.Height > 0)
            {
                var detail = DetailMap.Measure(
                    await File.ReadAllBytesAsync(Path.Combine(folder, "first.raw"), cancellationToken).ConfigureAwait(false),
                    await File.ReadAllBytesAsync(Path.Combine(folder, "second.raw"), cancellationToken).ConfigureAwait(false),
                    frame.Width,
                    frame.Height,
                    isHdr);
                if (detail is not null)
                {
                    result.DetailKept = detail.Kept;
                    result.SoftestKept = detail.SoftestKept;
                    result.SoftestX = detail.SoftestX;
                    result.SoftestY = detail.SoftestY;
                    result.DetailMap = Convert.ToBase64String(detail.Map);
                    result.DetailMapWidth = detail.MapWidth;
                    result.DetailMapHeight = detail.MapHeight;
                }
            }

            return result;
        }
        finally
        {
            _gate.Release();
            RemoveFolder(folder);
        }
    }

    // Finds the frame of the second video that best matches the first video's frame, among the expected one and those
    // either side of it, by comparing small grey copies. The expected frame is kept unless another is clearly closer, so
    // a still scene doesn't drift. If the check fails the expected frame is used.
    private async Task<int> MatchFrameAsync(Input first, Input second, string folder, CancellationToken cancellationToken)
    {
        var start = Math.Max(0, second.Frame - MatchRadius);
        var count = second.Frame + MatchRadius + 1 - start;
        try
        {
            await RunFfmpegAsync(MatchArguments(first, second, start, count, folder), cancellationToken).ConfigureAwait(false);
            var target = await File.ReadAllBytesAsync(Path.Combine(folder, "match-first.raw"), cancellationToken).ConfigureAwait(false);
            var candidates = await File.ReadAllBytesAsync(Path.Combine(folder, "match-second.raw"), cancellationToken).ConfigureAwait(false);
            var best = BestMatch(target, candidates, second.Frame - start);
            if (best is { } index && start + index != second.Frame)
            {
                _logger.LogDebug("Storage Optimizer matched frame {Frame} of {Path} instead of {Expected}", start + index, second.Path, second.Frame);
            }

            return best is { } found ? start + found : second.Frame;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException)
        {
            _logger.LogWarning(ex, "Storage Optimizer could not line up the frames of {Path}, so comparing the expected one", second.Path);
            return second.Frame;
        }
    }

    /// <summary>
    /// Picks the candidate frame closest to the target, keeping the expected one unless another is clearly closer.
    /// </summary>
    /// <param name="target">The frame to match, as 8-bit grey pixels.</param>
    /// <param name="candidates">The candidate frames one after another, the same size as the target.</param>
    /// <param name="expected">Which candidate is expected to match.</param>
    /// <returns>The index of the best candidate, or null if there is nothing to compare.</returns>
    internal static int? BestMatch(byte[] target, byte[] candidates, int expected)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(candidates);
        var size = target.Length;
        var count = size == 0 ? 0 : candidates.Length / size;
        if (count == 0)
        {
            return null;
        }

        var differences = new long[count];
        for (var c = 0; c < count; c++)
        {
            var offset = c * size;
            long sum = 0;
            for (var i = 0; i < size; i++)
            {
                sum += Math.Abs(target[i] - candidates[offset + i]);
            }

            differences[c] = sum;
        }

        var best = Array.IndexOf(differences, differences.Min());
        if (expected < 0 || expected >= count)
        {
            return best;
        }

        // Another frame has to be at least 10% closer than the expected one to be chosen instead.
        return differences[best] < differences[expected] * 0.9 ? best : expected;
    }

    private static List<string> MatchArguments(Input first, Input second, int start, int count, string folder)
    {
        var small = "scale=" + MatchWidth.ToString(CultureInfo.InvariantCulture) + ":" + MatchHeight.ToString(CultureInfo.InvariantCulture) + ":flags=area,format=gray";
        var graph = "[0:v]" + Pick(first.Frame, 1) + small + "[ma];[1:v]" + Pick(start, count) + small + "[mb]";
        var args = new List<string> { "-hide_banner", "-nostdin", "-y" };
        AddInputs(args, first, second);
        args.AddRange(
        [
            "-filter_complex", graph,
            "-map", "[ma]", "-frames:v", "1", "-f", "rawvideo", Path.Combine(folder, "match-first.raw"),
            "-map", "[mb]", "-frames:v", count.ToString(CultureInfo.InvariantCulture), "-f", "rawvideo", Path.Combine(folder, "match-second.raw")
        ]);
        return args;
    }

    // Frames counted from where an input starts, with their timestamps reset so the two inputs pair up.
    private static string Pick(int start, int count)
        => "trim=start_frame=" + start.ToString(CultureInfo.InvariantCulture) + ":end_frame=" + (start + count).ToString(CultureInfo.InvariantCulture) + ",setpts=PTS-STARTPTS,";

    private static void AddInputs(List<string> args, Input first, Input second)
    {
        foreach (var input in new[] { first, second })
        {
            if (input.SeekSeconds is { } seek)
            {
                args.AddRange(["-ss", seek.ToString("0.###", CultureInfo.InvariantCulture)]);
            }

            args.AddRange(["-i", input.Path]);
        }
    }

    // Frames spread evenly between the opening and the credits, moved to the nearest keyframe, with the busiest
    // stretch in place of whichever frame is nearest it.
    private async Task<List<(double Seconds, bool IsBusiest)>> PickFramesAsync(string path, double duration, CancellationToken cancellationToken)
    {
        var targets = Enumerable.Range(0, FilmstripFrames).Select(i => duration * (0.06 + (0.88 * i / (FilmstripFrames - 1)))).ToList();
        IReadOnlyList<Keyframe> keyframes = [];
        try
        {
            keyframes = await KeyframeIndex.ReadAsync(_mediaEncoder.ProbePath, path, duration, FilmstripIndexPoints, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(ex, "Storage Optimizer could not read the keyframes of {Path}", path);
        }

        if (keyframes.Count < FilmstripFrames)
        {
            return targets.Select(t => (t, false)).ToList();
        }

        var picked = targets.Select(t => keyframes.MinBy(k => Math.Abs(k.Seconds - t)).Seconds).Distinct().Select(t => (Seconds: t, IsBusiest: false)).ToList();
        var busiest = KeyframeIndex.Stretches(keyframes).Where(s => s.Start < duration * 0.94).MaxBy(s => s.Bitrate);
        if (busiest is not null)
        {
            var nearest = picked.IndexOf(picked.MinBy(p => Math.Abs(p.Seconds - busiest.Start)));
            picked[nearest] = (busiest.Start, true);
        }

        return picked.OrderBy(p => p.Seconds).ToList();
    }

    private static List<string> ThumbnailArguments(string path, List<(double Seconds, bool IsBusiest)> positions, bool toneMap, string folder)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-y" };
        var graph = new List<string>();
        for (var i = 0; i < positions.Count; i++)
        {
            var n = i.ToString(CultureInfo.InvariantCulture);
            args.AddRange(["-ss", positions[i].Seconds.ToString("0.###", CultureInfo.InvariantCulture), "-i", path]);
            graph.Add("[" + n + ":v]trim=end_frame=1,scale=" + ThumbnailWidth.ToString(CultureInfo.InvariantCulture) + ":-2,setsar=1," + (toneMap ? ToneMap : string.Empty) + "null[t" + n + "]");
        }

        args.AddRange(["-filter_complex", string.Join(';', graph)]);
        for (var i = 0; i < positions.Count; i++)
        {
            var n = i.ToString(CultureInfo.InvariantCulture);
            args.AddRange(["-map", "[t" + n + "]", "-frames:v", "1", "-q:v", "5", Path.Combine(folder, "t" + n + ".jpg")]);
        }

        return args;
    }

    private static List<string> Arguments(Input first, Input second, FrameSize frame, bool toneMap, string folder)
    {
        var width = Math.Min(frame.Width > 0 ? frame.Width : MaxImageWidth, MaxImageWidth).ToString(CultureInfo.InvariantCulture);
        var size = frame.Width > 0 && frame.Height > 0
            ? "scale=" + frame.Width.ToString(CultureInfo.InvariantCulture) + ":" + frame.Height.ToString(CultureInfo.InvariantCulture) + ":flags=lanczos,"
            : string.Empty;
        var format = frame.IsTenBit ? "yuv420p10le" : "yuv420p";
        var display = (toneMap ? ToneMap : string.Empty) + "scale=" + width + ":-2";

        // Both frames are scaled to the same size, scored for SSIM and PSNR, saved for display, and saved as raw
        // 16-bit luma (before any tone-mapping) for the detail map.
        var graph = "[0:v]" + Pick(first.Frame, 1) + size + "format=" + format + ",setsar=1[a0];"
            + "[1:v]" + Pick(second.Frame, 1) + size + "format=" + format + ",setsar=1,split=4[b0][b1][b2][b3];"
            + "[a0][b0]ssim[as];[as][b1]psnr[ap];[ap]split=2[ad][ar];"
            + "[ad]" + display + "[outa];[b2]" + display + "[outb];"
            + "[ar]format=gray16le[rawa];[b3]format=gray16le[rawb]";

        var args = new List<string> { "-hide_banner", "-nostdin", "-y" };
        AddInputs(args, first, second);
        args.AddRange(
        [
            "-filter_complex", graph,
            "-map", "[outa]", "-frames:v", "1", "-q:v", "3", Path.Combine(folder, "first.jpg"),
            "-map", "[outb]", "-frames:v", "1", "-q:v", "3", Path.Combine(folder, "second.jpg"),
            "-map", "[rawa]", "-frames:v", "1", "-f", "rawvideo", Path.Combine(folder, "first.raw"),
            "-map", "[rawb]", "-frames:v", "1", "-f", "rawvideo", Path.Combine(folder, "second.raw")
        ]);
        return args;
    }

    private async Task<string> RunFfmpegAsync(IList<string> args, CancellationToken cancellationToken)
        => (await FfmpegRunner.RunWatchedAsync(_mediaEncoder.EncoderPath, args, _stall, _timeout, "take the frames", cancellationToken).ConfigureAwait(false)).Errors;

    private void RemoveFolder(string folder)
    {
        try
        {
            Directory.Delete(folder, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Storage Optimizer could not remove {Folder}", folder);
        }
    }

    private static double? ParseScore(Match match)
        => match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static async Task<string> DataUrlAsync(string path, CancellationToken cancellationToken)
        => "data:image/jpeg;base64," + Convert.ToBase64String(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));

    // ffmpeg logs e.g. "SSIM Y:0.98 (17.1) U:0.99 (20.3) V:0.99 (20.1) All:0.985 (18.2)".
    [GeneratedRegex(@"SSIM .*All:(\d+(?:\.\d+)?)")]
    private static partial Regex SsimPattern();

    // ffmpeg logs e.g. "PSNR y:41.2 u:43.0 v:42.8 average:41.7 min:41.7 max:41.7".
    [GeneratedRegex(@"PSNR .*average:(\d+(?:\.\d+)?)")]
    private static partial Regex PsnrPattern();

    private readonly record struct FrameSize(int Width, int Height, bool IsTenBit);

    // A file to take a frame from, where to seek to first, if anywhere, and which frame after that to take.
    private readonly record struct Input(string Path, double? SeekSeconds, int Frame);
}
