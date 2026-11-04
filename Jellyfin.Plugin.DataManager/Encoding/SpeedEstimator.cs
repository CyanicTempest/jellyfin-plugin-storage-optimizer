// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DataManager.Services;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DataManager.Encoding;

/// <summary>
/// Works out how long encodes take on this server.
/// </summary>
/// <remarks>
/// Busy scenes usually take longer to encode than calm ones. How busy each part of a file is comes from how many
/// bits the original needed there, which the container's index gives without decoding anything. The file is split
/// into its calmest, typical and busiest thirds, and a scene from each is test-encoded, so the estimate is the mix
/// of all three rather than whichever scene happened to be picked. Other files are worked out from their average
/// bitrate, and each finished encode corrects the speed to match the real run.
/// </remarks>
public sealed class SpeedEstimator : IDisposable
{
    private const string ModelsFileName = "encode-speed-models.json";

    // Speeds saved by earlier versions, which counted the encoder's end-of-run rush and read too fast.
    private const string OldSpeedsFileName = "encode-speeds.json";

    // How many points in the file to read the byte position at. Each is one seek in the index.
    private const int ProfilePoints = 64;

    // A test waits this long after the first frames come out, then times frames for at least MinWindow
    // and MinWindowFrames, and stops at MaxWindow whatever it has.
    private const double WarmUpSeconds = 1.5;
    private const double MinWindowSeconds = 4;
    private const int MinWindowFrames = 40;
    private const double MaxWindowSeconds = 6;

    // Tests start at least this far before the end (or a quarter of a short file), so they don't run out of film.
    private const double RunwaySeconds = 120;

    // Files shorter than this are tested a third of the way in, without reading where the busy scenes are.
    private const double MinProfileSeconds = 60;

    // A film whose busiest stretch needs less than this many times the bits of its calmest is tested once.
    private const double DistinctBusyness = 1.15;

    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(90);

    private readonly IMediaEncoder _mediaEncoder;
    private readonly HardwareAcceleration _hardware;
    private readonly ILogger<SpeedEstimator> _logger;
    // Speed tests and previews take turns, so neither slows the other down.
    private readonly SemaphoreSlim _testGate = new(1, 1);
    private readonly Lock _runningTestLock = new();
#pragma warning disable CA2213 // TestAsync owns and disposes it; this only points at it while a test runs.
    private CancellationTokenSource? _runningTest;
#pragma warning restore CA2213
    private readonly Lock _modelsLock = new();
    private Dictionary<string, SpeedModel>? _models;

    /// <summary>
    /// Initializes a new instance of the <see cref="SpeedEstimator"/> class.
    /// </summary>
    /// <param name="mediaEncoder">Jellyfin's media encoder, which knows where ffmpeg and ffprobe are.</param>
    /// <param name="hardware">Sets up the GPU for test encodes.</param>
    /// <param name="logger">The logger.</param>
    public SpeedEstimator(IMediaEncoder mediaEncoder, HardwareAcceleration hardware, ILogger<SpeedEstimator> logger)
    {
        _hardware = hardware;
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    private static string DataFolder => Plugin.Instance?.DataFolderPath ?? throw new InvalidOperationException("Plugin is not loaded.");

    /// <summary>
    /// Gets the key speeds are saved under. Encoding speed depends on the encoder, the effort, the quality, the
    /// size of the original (decoding 4K costs more) and the size it's encoded at.
    /// </summary>
    /// <param name="encoder">The ffmpeg encoder.</param>
    /// <param name="effort">How long to spend encoding.</param>
    /// <param name="quality">The quality preset.</param>
    /// <param name="source">The size of the original.</param>
    /// <param name="output">The size it's encoded at.</param>
    /// <returns>The key.</returns>
    public static string Key(string encoder, EncodeEffort effort, QualityPreset quality, (int Width, int Height) source, (int Width, int Height) output)
        => encoder + "|" + effort + "|" + quality + "|" + Resolutions.Label(source.Width, source.Height) + ">" + Resolutions.Label(output.Width, output.Height);

    /// <summary>
    /// Gets how busy a video is: the bits per pixel its encoder needed, in HEVC terms so codecs compare fairly.
    /// </summary>
    /// <param name="videoBitrate">The video bitrate, in bits per second.</param>
    /// <param name="file">The video.</param>
    /// <returns>The busyness, or 0 if it can't be worked out.</returns>
    public static double Busyness(double videoBitrate, SpeedTestFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var pixelsPerSecond = file.FramesPerSecond * file.Width * (double)file.Height;
        return pixelsPerSecond > 0 ? Math.Max(videoBitrate, 0) * SavingsEstimator.HevcBitrateFactor(file.Codec) / pixelsPerSecond : 0;
    }

    /// <summary>
    /// Gets how busy a whole file is on average, from its size.
    /// </summary>
    /// <param name="file">The video.</param>
    /// <param name="sizeBytes">The file size, in bytes.</param>
    /// <returns>The busyness.</returns>
    public static double AverageBusyness(SpeedTestFile file, long sizeBytes)
    {
        ArgumentNullException.ThrowIfNull(file);
        return file.DurationSeconds > 0 ? Busyness(VideoBitrate(sizeBytes * 8 / file.DurationSeconds, file), file) : 0;
    }

    /// <summary>
    /// Gets the saved speed for a setting.
    /// </summary>
    /// <param name="key">The key, from <see cref="Key"/>.</param>
    /// <returns>The speed, or null if this setting hasn't been tested or used yet.</returns>
    public SpeedModel? Get(string key)
    {
        lock (_modelsLock)
        {
            return Models().GetValueOrDefault(key);
        }
    }

    /// <summary>
    /// Gets the saved speed for a setting, or for the closest setting with the same encoder and effort. Quality
    /// changes the speed a little; effort changes it a lot, so another effort's speed never stands in.
    /// </summary>
    /// <param name="key">The key, from <see cref="Key"/>.</param>
    /// <returns>The speed, or null if this encoder and effort haven't been tested or used yet.</returns>
    public SpeedModel? GetClosest(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_modelsLock)
        {
            var models = Models();
            if (models.TryGetValue(key, out var exact))
            {
                return exact;
            }

            var parts = key.Split('|');
            return Closest(models, parts, 2)?.Value;
        }
    }

    /// <summary>
    /// Gets the saved speed for the closest setting with the same encoder and any effort, for scaling to another effort.
    /// </summary>
    /// <param name="key">The key, from <see cref="Key"/>.</param>
    /// <returns>The effort that was tested and its speed, or null if this encoder hasn't been tested or used yet.</returns>
    public (EncodeEffort Effort, SpeedModel Model)? GetAnyEffort(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_modelsLock)
        {
            var closest = Closest(Models(), key.Split('|'), 1);
            return closest is { } found && Enum.TryParse<EncodeEffort>(found.Key.Split('|')[1], out var effort) ? (effort, found.Value) : null;
        }
    }

    /// <summary>
    /// Gets the correction to the rule-of-thumb video size for a setting, from test encodes of it or, failing that,
    /// of the closest setting with the same encoder.
    /// </summary>
    /// <param name="key">The key, from <see cref="Key"/>.</param>
    /// <returns>The correction, and whether this setting itself was measured. 1 if nothing with this encoder has been.</returns>
    public (double Scale, bool Measured) VideoScale(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_modelsLock)
        {
            var models = Models();
            if (models.TryGetValue(key, out var exact) && exact.VideoScale is { } measured)
            {
                return (measured, true);
            }

            var withSize = models.Where(m => m.Value.VideoScale is not null).ToDictionary(m => m.Key, m => m.Value, StringComparer.Ordinal);
            return Closest(withSize, key.Split('|'), 1) is { } closest ? (closest.Value.VideoScale!.Value, false) : (1, false);
        }
    }

    /// <summary>
    /// Gets the correction to the rule-of-thumb video size from test encodes of exactly this setting.
    /// </summary>
    /// <param name="key">The key, from <see cref="Key"/>.</param>
    /// <returns>The correction, or null if this setting hasn't been measured.</returns>
    public double? MeasuredVideoScale(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_modelsLock)
        {
            return Models().TryGetValue(key, out var model) ? model.VideoScale : null;
        }
    }

    /// <summary>
    /// Test-encodes a calm, a typical and a busy scene of a file, without touching the library, and saves how fast
    /// it went and how big the video came out.
    /// </summary>
    /// <param name="key">The key to save the speed under.</param>
    /// <param name="encoder">The ffmpeg encoder.</param>
    /// <param name="quality">The quality preset.</param>
    /// <param name="effort">How long to spend encoding.</param>
    /// <param name="file">The file to test on.</param>
    /// <param name="expectedVideoBitrate">The rule-of-thumb video bitrate for this file and setting, which the measured size corrects.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The speed, or null if ffmpeg couldn't test it.</returns>
    public async Task<SpeedModel?> TestAsync(string key, string encoder, QualityPreset quality, EncodeEffort effort, SpeedTestFile file, double expectedVideoBitrate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);

        await _testGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var test = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_runningTestLock)
        {
            _runningTest = test;
        }

        try
        {
            // Another request may have tested it while this one waited.
            if (Get(key) is { } testedMeanwhile)
            {
                return testedMeanwhile;
            }

            var stretches = await ProfileAsync(file, test.Token).ConfigureAwait(false);
            var scenes = Scenes(stretches);
            if (scenes.Count == 0)
            {
                scenes.Add(new Scene("a third of the way in", file.DurationSeconds / 3, 1));
            }

            var points = new List<SpeedPoint>();
            double weightedSeconds = 0, weights = 0, weightedBitrate = 0, bitrateWeights = 0;
            foreach (var scene in scenes)
            {
                var from = Math.Min(scene.Start, file.DurationSeconds - Math.Min(RunwaySeconds, file.DurationSeconds / 4));
                var output = Path.Combine(Path.GetTempPath(), "datamanager-speed-" + Guid.NewGuid().ToString("N"));
                var setup = _hardware.Plan(encoder, file.Source);
                TestRun? timed;
                try
                {
                    timed = await TimeEncodeAsync(FfmpegArguments.BuildSpeedTest(file.Path, output, from, 100_000, encoder, quality, effort, file.Source, file.ScaleTo, setup.Arguments), test.Token).ConfigureAwait(false);

                    // The GPU couldn't decode it: time it decoding on the CPU, which is how the encode will run.
                    if (timed is null && setup.Decoder is not null)
                    {
                        var cpu = _hardware.Plan(encoder, file.Source, false);
                        timed = await TimeEncodeAsync(FfmpegArguments.BuildSpeedTest(file.Path, output, from, 100_000, encoder, quality, effort, file.Source, file.ScaleTo, cpu.Arguments), test.Token).ConfigureAwait(false);
                        if (timed is not null)
                        {
                            _hardware.DecodeFailed(setup, file.Source);
                        }
                    }
                }
                finally
                {
                    DeleteQuietly(output);
                }

                if (timed is not { } run)
                {
                    continue;
                }

                // The video's bitrate over the timed frames, which stands for this scene's share of the film.
                if (run.BitsPerFrame is { } bits)
                {
                    weightedBitrate += scene.Weight * bits * file.FramesPerSecond;
                    bitrateWeights += scene.Weight;
                }

                // How busy the frames in the timed window were, from where they sit in the file.
                var windowStart = from + (run.FirstFrame / file.FramesPerSecond);
                var windowEnd = from + (run.LastFrame / file.FramesPerSecond);
                var busyness = stretches.Count > 0
                    ? Busyness(VideoBitrate(BitrateBetween(stretches, windowStart, windowEnd), file), file)
                    : AverageBusyness(file, new FileInfo(file.Path).Length);
                var secondsPerPixel = 1 / (run.FramesPerSecond * file.OutputPixels);
                points.Add(new SpeedPoint(busyness, secondsPerPixel));
                weightedSeconds += scene.Weight * secondsPerPixel;
                weights += scene.Weight;
                _logger.LogInformation(
                    "Storage Optimizer test-encoded {Encoder} on {Path}, {Scene} at {Start}: {Fps:F1} frames a second at {Width}x{Height}, busyness {Busyness:F3}",
                    encoder,
                    file.Path,
                    scene.Name,
                    TimeSpan.FromSeconds(Math.Round(windowStart)),
                    run.FramesPerSecond,
                    file.Output.Width,
                    file.Output.Height,
                    busyness);
            }

            if (points.Count == 0)
            {
                _logger.LogWarning("Storage Optimizer could not measure the speed of {Encoder}", encoder);
                return null;
            }

            // Scaled so this file's own estimate is the mix of its scenes, each counted by how much of the film it stands for.
            var model = new SpeedModel(points.OrderBy(p => p.Busyness).ToList(), 1, SpeedBasis.Tested);
            var average = model.SecondsPerPixel(AverageBusyness(file, new FileInfo(file.Path).Length));
            model = model with
            {
                Scale = average > 0 ? weightedSeconds / weights / average : 1,
                VideoScale = bitrateWeights > 0 && expectedVideoBitrate > 0 ? weightedBitrate / bitrateWeights / expectedVideoBitrate : null
            };
            _logger.LogInformation(
                "Storage Optimizer test encodes of {Encoder} ({Effort}, {Quality}) came out at {Bitrate:F2} Mbit/s against {Expected:F2} expected",
                encoder,
                effort,
                quality,
                bitrateWeights > 0 ? weightedBitrate / bitrateWeights / 1e6 : 0,
                expectedVideoBitrate / 1e6);
            await SaveAsync(key, model).ConfigureAwait(false);
            return model;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Storage Optimizer stopped a speed test because an encode started");
            return null;
        }
        finally
        {
            lock (_runningTestLock)
            {
                _runningTest = null;
            }

            _testGate.Release();
        }
    }

    /// <summary>
    /// Stops the speed test that's running, if any, so it doesn't slow an encode down or read slow because of it.
    /// </summary>
    public void CancelTests()
    {
        lock (_runningTestLock)
        {
            _runningTest?.Cancel();
        }
    }

    /// <summary>
    /// Runs work that needs the CPU to itself, such as a preview encode, taking turns with speed tests.
    /// </summary>
    /// <typeparam name="T">The work's result.</typeparam>
    /// <param name="work">The work.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The work's result.</returns>
    public async Task<T> ExclusiveAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        await _testGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await work().ConfigureAwait(false);
        }
        finally
        {
            _testGate.Release();
        }
    }

    /// <summary>
    /// Corrects the saved speed and size for a setting with a finished encode, which is the truest measure there is.
    /// </summary>
    /// <param name="key">The key, from <see cref="Key"/>.</param>
    /// <param name="busyness">How busy the encoded file is on average.</param>
    /// <param name="pixelsPerSecond">How fast the encode ran, in output pixels a second.</param>
    /// <param name="videoScale">The new file's video bitrate over the rule-of-thumb one, or null if unknown.</param>
    /// <returns>A task that completes when the speed is saved.</returns>
    public async Task RecordEncodeAsync(string key, double busyness, double pixelsPerSecond, double? videoScale)
    {
        if (pixelsPerSecond <= 0)
        {
            return;
        }

        var actual = 1 / pixelsPerSecond;
        var model = Get(key) is { } saved && saved.SecondsPerPixel(busyness) > 0
            ? saved with { Scale = saved.Scale * actual / saved.SecondsPerPixel(busyness), Basis = SpeedBasis.Encoded }
            : new SpeedModel([new SpeedPoint(busyness, actual)], 1, SpeedBasis.Encoded);
        if (videoScale is > 0)
        {
            model = model with { VideoScale = videoScale };
        }

        await SaveAsync(key, model).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose() => _testGate.Dispose();

    private static double VideoBitrate(double overallBitrate, SpeedTestFile file)
        => Math.Max(overallBitrate - file.AudioBitrate, overallBitrate * 0.2);

    // The saved setting most like a key, sharing at least its first parts (1: the encoder, 2: and the effort).
    // Closer is the same effort, then the same quality, then the same output size, then the same original size.
    private static KeyValuePair<string, SpeedModel>? Closest(Dictionary<string, SpeedModel> models, string[] parts, int mustMatch)
    {
        if (parts.Length < 4)
        {
            return null;
        }

        var output = parts[3][(parts[3].IndexOf('>', StringComparison.Ordinal) + 1)..];
        int Score(string[] other) =>
            (other[1] == parts[1] ? 8 : 0)
            + (other[2] == parts[2] ? 4 : 0)
            + (other[3].EndsWith('>' + output, StringComparison.Ordinal) ? 2 : 0)
            + (other[3] == parts[3] ? 1 : 0);

        KeyValuePair<string, SpeedModel>? best = null;
        var bestScore = -1;
        foreach (var model in models)
        {
            var other = model.Key.Split('|');
            if (other.Length != parts.Length || !parts.AsSpan(0, mustMatch).SequenceEqual(other.AsSpan(0, mustMatch)))
            {
                continue;
            }

            var score = Score(other);
            if (score > bestScore)
            {
                best = model;
                bestScore = score;
            }
        }

        return best;
    }

    // Splits the film by bitrate into its calmest, typical and busiest thirds, and picks the middle stretch of each
    // to test. Each test stands for its third. A film that's much the same throughout is tested once.
    private static List<Scene> Scenes(IReadOnlyList<Stretch> stretches)
    {
        if (stretches.Count == 0)
        {
            return [];
        }

        var sorted = stretches.OrderBy(s => s.Bitrate).ToList();
        if (sorted[^1].Bitrate < sorted[0].Bitrate * DistinctBusyness)
        {
            return [new Scene("a typical scene", Median(sorted).Start, 1)];
        }

        string[] names = ["a calm scene", "a typical scene", "a busy scene"];
        var total = sorted.Sum(s => s.Seconds);
        var scenes = new List<Scene>();
        var counted = 0.0;
        var next = 0;
        for (var third = 0; third < 3; third++)
        {
            var group = new List<Stretch>();
            while (next < sorted.Count && (third == 2 || counted < total * (third + 1) / 3))
            {
                group.Add(sorted[next]);
                counted += sorted[next].Seconds;
                next++;
            }

            if (group.Count > 0)
            {
                scenes.Add(new Scene(names[third], Median(group).Start, group.Sum(s => s.Seconds)));
            }
        }

        return scenes;
    }

    // The middle stretch by bitrate, counting each by its length. The list is sorted by bitrate.
    private static Stretch Median(List<Stretch> sorted)
    {
        var total = sorted.Sum(s => s.Seconds);
        var counted = 0.0;
        foreach (var stretch in sorted)
        {
            counted += stretch.Seconds;
            if (counted >= total / 2)
            {
                return stretch;
            }
        }

        return sorted[^1];
    }

    private static double BitrateBetween(IReadOnlyList<Stretch> stretches, double from, double to)
    {
        if (to - from < 0.5)
        {
            to = from + 0.5;
        }

        double bits = 0, seconds = 0;
        foreach (var stretch in stretches)
        {
            var overlap = Math.Min(stretch.Start + stretch.Seconds, to) - Math.Max(stretch.Start, from);
            if (overlap > 0)
            {
                bits += stretch.Bitrate * overlap;
                seconds += overlap;
            }
        }

        // Past the last point read, use the nearest stretch.
        return seconds > 0 ? bits / seconds : stretches.MinBy(s => Math.Abs(s.Start - from))!.Bitrate;
    }

    // How busy each stretch of the file is, from where its keyframes sit.
    private async Task<IReadOnlyList<Stretch>> ProfileAsync(SpeedTestFile file, CancellationToken cancellationToken)
    {
        if (file.DurationSeconds < MinProfileSeconds)
        {
            return [];
        }

        try
        {
            var keyframes = await KeyframeIndex.ReadAsync(_mediaEncoder.ProbePath, file.Path, file.DurationSeconds, ProfilePoints, cancellationToken).ConfigureAwait(false);
            return KeyframeIndex.Stretches(keyframes);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(ex, "Storage Optimizer could not read where the busy scenes are in {Path}", file.Path);
            return [];
        }
    }

    // The encoder holds frames back while it plans ahead and lets them all out at the end, so a short run that
    // counts every frame reads far too fast. This waits until frames come out steadily, times a window of them,
    // then stops ffmpeg before the end.
    internal async Task<TestRun?> TimeEncodeAsync(IList<string> args, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(_mediaEncoder.EncoderPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_testTimeout);
        using var process = new Process { StartInfo = startInfo };
        var clock = Stopwatch.StartNew();
        process.Start();
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var registration = timeout.Token.Register(() => FfmpegRunner.Kill(process));

        long frame = 0;
        long bytes = 0;
        long lastFrame = 0;
        double? flowing = null;
        (long Frame, double Seconds, long Bytes)? from = null;
        (long Frame, double Seconds, long Bytes)? to = null;
        var stopped = false;
        var ended = false;
        while (await process.StandardOutput.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
        {
            if (line.StartsWith("frame=", StringComparison.Ordinal))
            {
                long.TryParse(line.AsSpan(6), NumberStyles.Integer, CultureInfo.InvariantCulture, out frame);
                continue;
            }

            // Bytes written so far. Absent or N/A from encoders that don't report it.
            if (line.StartsWith("total_size=", StringComparison.Ordinal))
            {
                long.TryParse(line.AsSpan(11), NumberStyles.Integer, CultureInfo.InvariantCulture, out bytes);
                continue;
            }

            if (!line.StartsWith("progress=", StringComparison.Ordinal))
            {
                continue;
            }

            if (line == "progress=end")
            {
                ended = true;
                break;
            }

            // Frames often come out in batches, so time from one batch to another rather than at any report.
            var now = clock.Elapsed.TotalSeconds;
            if (frame <= lastFrame)
            {
                continue;
            }

            lastFrame = frame;
            flowing ??= now;
            if (from is null)
            {
                if (now - flowing >= WarmUpSeconds)
                {
                    from = (frame, now, bytes);
                }

                continue;
            }

            to = (frame, now, bytes);
            var window = now - from.Value.Seconds;
            if ((window >= MinWindowSeconds && frame - from.Value.Frame >= MinWindowFrames) || window >= MaxWindowSeconds)
            {
                stopped = true;
                FfmpegRunner.Kill(process);
                break;
            }
        }

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        await stderrTask.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var timedOut = timeout.IsCancellationRequested;
        if (!stopped && !timedOut && !ended && process.ExitCode != 0)
        {
            return null;
        }

        // Ran out of time before the window filled: use what was timed, if it's enough to go on.
        if ((stopped || timedOut) && from is { } a && to is { } b && b.Seconds - a.Seconds >= 2)
        {
            return new TestRun((b.Frame - a.Frame) / (b.Seconds - a.Seconds), a.Frame, b.Frame, BitsPerFrame(b.Bytes - a.Bytes, b.Frame - a.Frame));
        }

        // The film ended first, so the timed frames include the rush at the end. Count from the start instead,
        // which reads a little slow rather than far too fast.
        return frame > 0 ? new TestRun(frame / clock.Elapsed.TotalSeconds, 0, frame, BitsPerFrame(bytes, frame)) : null;
    }

    // Runs encodes together and returns their total speed, in frames a second, or null if any failed. They're timed
    // over one window that they all share, or else one that stopped early would leave the others more of the GPU.
    internal async Task<double?> TimeTogetherAsync(IReadOnlyList<IList<string>> commands, double settleSeconds, double windowSeconds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commands);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_testTimeout);
        var clock = Stopwatch.StartNew();
        var runs = new List<TimedRun>();
        try
        {
            foreach (var args in commands)
            {
                runs.Add(new TimedRun(_mediaEncoder.EncoderPath, args, clock));
            }

            // Encoders take their time to start, more of it with several sharing the GPU.
            while (!runs.All(r => r.Flowing))
            {
                if (runs.Any(r => r.Ended))
                {
                    return null;
                }

                await Task.Delay(100, timeout.Token).ConfigureAwait(false);
            }

            await Task.Delay(TimeSpan.FromSeconds(settleSeconds), timeout.Token).ConfigureAwait(false);
            var from = runs.Select(r => r.Latest).ToList();
            var end = clock.Elapsed.TotalSeconds + windowSeconds;
            while (clock.Elapsed.TotalSeconds < end && !runs.Any(r => r.Ended))
            {
                await Task.Delay(100, timeout.Token).ConfigureAwait(false);
            }

            var to = runs.Select(r => r.Latest).ToList();
            var total = 0.0;
            for (var i = 0; i < runs.Count; i++)
            {
                var seconds = to[i].Seconds - from[i].Seconds;
                if (seconds < 1 || to[i].Frame <= from[i].Frame)
                {
                    return null;
                }

                total += (to[i].Frame - from[i].Frame) / seconds;
            }

            return total;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            foreach (var run in runs)
            {
                await run.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static double? BitsPerFrame(long bytes, long frames) => bytes > 0 && frames > 0 ? bytes * 8.0 / frames : null;

    private void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Storage Optimizer could not delete {Path}", path);
        }
    }

    // Callers hold _modelsLock.
    private Dictionary<string, SpeedModel> Models()
    {
        if (_models is not null)
        {
            return _models;
        }

        _models = new Dictionary<string, SpeedModel>(StringComparer.Ordinal);
        try
        {
            var path = Path.Combine(DataFolder, ModelsFileName);
            if (File.Exists(path))
            {
                _models = JsonSerializer.Deserialize<Dictionary<string, SpeedModel>>(File.ReadAllText(path)) ?? _models;
            }

            var old = Path.Combine(DataFolder, OldSpeedsFileName);
            if (File.Exists(old))
            {
                File.Delete(old);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Storage Optimizer could not read saved encoding speeds");
        }

        return _models;
    }

    private async Task SaveAsync(string key, SpeedModel model)
    {
        string json;
        lock (_modelsLock)
        {
            Models()[key] = model;
            json = JsonSerializer.Serialize(_models);
        }

        try
        {
            await File.WriteAllTextAsync(Path.Combine(DataFolder, ModelsFileName), json, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Storage Optimizer could not save encoding speeds");
        }
    }

    // A scene to test, and how many seconds of the film it stands for.
    private sealed record Scene(string Name, double Start, double Weight);

    // How fast the timed frames encoded, which they were, and how many bits each took on average if known.
    internal readonly record struct TestRun(double FramesPerSecond, long FirstFrame, long LastFrame, double? BitsPerFrame);

    // One ffmpeg of a timed group, keeping track of the frames it has put out.
    private sealed class TimedRun : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly Stopwatch _clock;
        private readonly Task _reader;
        private readonly Task _errors;
        private readonly Lock _lock = new();
        private (long Frame, double Seconds) _latest;
        private volatile bool _ended;

        public TimedRun(string ffmpeg, IList<string> args, Stopwatch clock)
        {
            _clock = clock;
            var startInfo = new ProcessStartInfo(ffmpeg)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            _process = new Process { StartInfo = startInfo };
            _process.Start();
            _errors = _process.StandardError.ReadToEndAsync(CancellationToken.None);
            _reader = Task.Run(ReadAsync);
        }

        public bool Flowing
        {
            get
            {
                lock (_lock)
                {
                    return _latest.Frame > 0;
                }
            }
        }

        public bool Ended => _ended;

        // The last time frames came out. Frames often come out in batches, so a window runs from batch to batch.
        public (long Frame, double Seconds) Latest
        {
            get
            {
                lock (_lock)
                {
                    return _latest;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            FfmpegRunner.Kill(_process);
            await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(_reader, _errors).ConfigureAwait(false);
            _process.Dispose();
        }

        private async Task ReadAsync()
        {
            long frame = 0;
            while (await _process.StandardOutput.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
            {
                if (line.StartsWith("frame=", StringComparison.Ordinal))
                {
                    long.TryParse(line.AsSpan(6), NumberStyles.Integer, CultureInfo.InvariantCulture, out frame);
                }
                else if (line.StartsWith("progress=", StringComparison.Ordinal))
                {
                    lock (_lock)
                    {
                        if (frame > _latest.Frame)
                        {
                            _latest = (frame, _clock.Elapsed.TotalSeconds);
                        }
                    }

                    if (line == "progress=end")
                    {
                        break;
                    }
                }
            }

            _ended = true;
        }
    }
}
