// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.DataManager.Configuration;
using Jellyfin.Plugin.DataManager.Models;
using Jellyfin.Plugin.DataManager.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DataManager.Encoding;

/// <summary>
/// Where a GPU test is.
/// </summary>
/// <param name="State">Idle when none has run, Running, Done or Failed.</param>
/// <param name="Step">How many encodes at once have been timed so far.</param>
/// <param name="Steps">How many will be timed.</param>
/// <param name="Speeds">The total speed with 1, 2, 3 and so on at once, each against one at a time.</param>
/// <param name="SweetSpot">The number at once that gets nine tenths of the best total, or 0 when there is no result.</param>
/// <param name="Message">What to tell the user.</param>
/// <param name="Clip">What was encoded for the test, or null.</param>
/// <param name="TestedAt">When the result was measured, or null.</param>
/// <param name="Passes">The total speed of each kind of run, in frames a second, with 1, 2, 3 and so on at once.</param>
/// <param name="Decoder">GPU or CPU: where the film was decoded, or null when not known.</param>
/// <param name="FilmFrameRate">The film's frames a second, which turns speeds into times real time, or 0 when not known.</param>
public sealed record GpuTestStatus(string State, int Step, int Steps, IReadOnlyList<double> Speeds, int SweetSpot, string Message, string? Clip, DateTime? TestedAt = null, IReadOnlyList<GpuTestPass>? Passes = null, string? Decoder = null, double FilmFrameRate = 0)
{
    /// <summary>Gets the status of a GPU that hasn't been tested.</summary>
    public static GpuTestStatus Idle { get; } = new("Idle", 0, GpuParsing.MostAtOnce, [], 0, string.Empty, null);
}

/// <summary>
/// One kind of run in a GPU test.
/// </summary>
/// <param name="Name">Encoder (a generated picture, so only the encoder is timed), Film (a scene of the film) or OnGpu (the film with its frames kept on the GPU).</param>
/// <param name="Label">What to call it on the page.</param>
/// <param name="Fps">The total frames a second with 1, 2, 3 and so on at once.</param>
public sealed record GpuTestPass(string Name, string Label, IReadOnlyList<double> Fps);

/// <summary>
/// What a finished test keeps besides its speeds, so it can be shown again later.
/// </summary>
/// <param name="Passes">The runs.</param>
/// <param name="Decoder">GPU or CPU.</param>
/// <param name="FilmFrameRate">The film's frames a second.</param>
/// <param name="Message">What the test said.</param>
public sealed record GpuTestDetails(IReadOnlyList<GpuTestPass> Passes, string Decoder, double FilmFrameRate, string Message);

/// <summary>
/// The GPU, how many encodes at once it suggests, and the last test of it.
/// </summary>
/// <param name="Gpu">What is known about the GPU.</param>
/// <param name="Recommended">The number of encodes to run at once: the last test's sweet spot, or else what the GPU's engines suggest.</param>
/// <param name="Test">The test that's running, or the last result for this GPU.</param>
public sealed record GpuOverview(GpuInfo Gpu, int Recommended, GpuTestStatus Test);

/// <summary>
/// Finds how many encodes at once suit the GPU, by timing one, two, three and four at once on a scene from the library.
/// </summary>
/// <remarks>
/// Encoding is held while it runs, so nothing else is using the GPU. It reports how much faster the GPU gets in total with each
/// extra encode, which is how far more at once is worth it. All the encodes of a run are timed over the same few seconds
/// of the same scene, as total frames a second, so the numbers only change with how many share the GPU.
/// </remarks>
public sealed class GpuTester : IDisposable
{
    // Short clips run out before the speed settles, so a film of at least this long is best.
    private const double GoodClipSeconds = 300;
    private const double LeastClipSeconds = 60;

    // Encodes start this far before the end of the film at the latest, so they don't run out while being timed.
    private const double RunwaySeconds = 120;

    // How much of the film is copied to test with. It plays round and round, so a fast GPU can't run out of it.
    private const double SceneSeconds = 60;

    // How long encodes run before timing starts, once all of them are putting out frames, and how long they're timed.
    private const double SettleSeconds = 3;
    private const double WindowSeconds = 8;

    // The extra runs are timed once each rather than twice, so they're a little shorter.
    private const double ExtraSettleSeconds = 2;
    private const double ExtraWindowSeconds = 6;

    private static readonly TimeSpan _pauseWait = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan _copyTimeout = TimeSpan.FromMinutes(5);

    private readonly ILibraryManager _libraryManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly EncodeService _encodes;
    private readonly SpeedEstimator _speed;
    private readonly HardwareAcceleration _hardware;
    private readonly GpuDetector _gpu;
    private readonly ViewerMonitor _viewers;
    private readonly ILogger<GpuTester> _logger;
    private readonly Lock _lock = new();
    private GpuTestStatus _status = GpuTestStatus.Idle;
    private CancellationTokenSource? _running;

    /// <summary>
    /// Initializes a new instance of the <see cref="GpuTester"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager, to find a scene to encode.</param>
    /// <param name="mediaEncoder">Jellyfin's media encoder, which knows where ffmpeg is.</param>
    /// <param name="encodes">The re-encode queue, which is held while the test runs.</param>
    /// <param name="speed">Times the encodes.</param>
    /// <param name="hardware">Sets up the GPU for test encodes.</param>
    /// <param name="gpu">Reads what the GPU is.</param>
    /// <param name="viewers">Finds streams that are using the GPU.</param>
    /// <param name="logger">The logger.</param>
    public GpuTester(ILibraryManager libraryManager, IMediaEncoder mediaEncoder, EncodeService encodes, SpeedEstimator speed, HardwareAcceleration hardware, GpuDetector gpu, ViewerMonitor viewers, ILogger<GpuTester> logger)
    {
        _libraryManager = libraryManager;
        _mediaEncoder = mediaEncoder;
        _encodes = encodes;
        _speed = speed;
        _hardware = hardware;
        _gpu = gpu;
        _viewers = viewers;
        _logger = logger;
    }

    /// <summary>
    /// Gets where the test is.
    /// </summary>
    public GpuTestStatus Status
    {
        get
        {
            lock (_lock)
            {
                return _status;
            }
        }
    }

    /// <summary>
    /// Gets the last result for this GPU, or an idle status when this card hasn't been tested.
    /// </summary>
    /// <param name="info">The GPU.</param>
    /// <returns>The result.</returns>
    public static GpuTestStatus Saved(GpuInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var speeds = GpuParsing.ParseSpeeds(config.GpuTestSpeeds);
        if (speeds.Count == 0 || !string.Equals(config.GpuTestedDevice, GpuParsing.DeviceKey(info), StringComparison.Ordinal))
        {
            return GpuTestStatus.Idle;
        }

        var details = ReadDetails(config.GpuTestDetails);
        return new GpuTestStatus("Done", speeds.Count, GpuParsing.MostAtOnce, speeds, GpuParsing.SweetSpot(speeds), details?.Message ?? Summary(speeds), null, config.GpuTestedAt, details?.Passes, details?.Decoder, details?.FilmFrameRate ?? 0);
    }

    private static GpuTestDetails? ReadDetails(string json)
    {
        try
        {
            return string.IsNullOrWhiteSpace(json) ? null : System.Text.Json.JsonSerializer.Deserialize<GpuTestDetails>(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Starts a test, unless one is running.
    /// </summary>
    /// <returns>True if it started, false if one was already running.</returns>
    public bool Start()
    {
        lock (_lock)
        {
            if (_running is not null)
            {
                return false;
            }

            _running = new CancellationTokenSource();
            _status = new GpuTestStatus("Running", 0, GpuParsing.MostAtOnce, [], 0, "Getting ready", null);
            var token = _running.Token;
            _ = Task.Run(() => RunAsync(token), CancellationToken.None);
            return true;
        }
    }

    /// <summary>
    /// Stops the test that's running, if any.
    /// </summary>
    public void Cancel()
    {
        lock (_lock)
        {
            _running?.Cancel();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lock)
        {
            _running?.Cancel();
            _running?.Dispose();
            _running = null;
        }
    }

    /// <summary>
    /// Says what a result means, such as "2 at once is the sweet spot".
    /// </summary>
    /// <param name="speeds">The total speed with 1, 2, 3 and so on at once.</param>
    /// <returns>A sentence.</returns>
    public static string Summary(IReadOnlyList<double> speeds)
    {
        ArgumentNullException.ThrowIfNull(speeds);

        if (speeds.Count == 0)
        {
            return string.Empty;
        }

        var spot = GpuParsing.SweetSpot(speeds);
        var gain = speeds[spot - 1];
        if (spot == 1)
        {
            return speeds.Count == 1
                ? "Only one encode could be tested on this card."
                : "One at a time is the sweet spot: running more at once didn't get it much further.";
        }

        return string.Create(CultureInfo.InvariantCulture, $"{spot} at once is the sweet spot: about {gain:0.#} times the work of one, and more than that doesn't get it much further.");
    }

    // Picks a film to encode: close to 1080p, long enough for the speed to settle, SDR so nothing is tone mapped.
    private (Video Item, MediaFileInfo File)? PickClip()
    {
        static int HeightOf(Video item) => item.GetMediaStreams().FirstOrDefault(s => s.Type == MediaStreamType.Video)?.Height ?? item.Height;

        var query = new InternalItemsQuery { MediaTypes = [MediaType.Video], IsVirtualItem = false, Recursive = true };
        var candidates = _libraryManager.GetItemList(query)
            .OfType<Video>()
            .Where(v => !string.IsNullOrEmpty(v.Path)
                && (v.RunTimeTicks ?? 0) >= TimeSpan.FromSeconds(LeastClipSeconds).Ticks
                && v.GetMediaStreams().FirstOrDefault(s => s.Type == MediaStreamType.Video) is { } video
                && video.VideoRange != VideoRange.HDR
                && HeightOf(v) is >= 480 and <= 2200)
            .OrderByDescending(v => v.RunTimeTicks >= TimeSpan.FromSeconds(GoodClipSeconds).Ticks)
            .ThenBy(v => Math.Abs(HeightOf(v) - 1080))
            .ThenByDescending(v => v.RunTimeTicks);

        foreach (var item in candidates)
        {
            if (File.Exists(item.Path))
            {
                return (item, EncodeService.Describe(item).File);
            }
        }

        return null;
    }

    private void Update(GpuTestStatus status)
    {
        lock (_lock)
        {
            _status = status;
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await TestAsync(cancellationToken).ConfigureAwait(false);
            Update(result);
        }
        catch (OperationCanceledException)
        {
            Update(Failed("The test was stopped."));
        }
#pragma warning disable CA1031 // A background test must report any failure rather than end the task silently.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "Storage Optimizer's GPU test failed");
            Update(Failed("The test couldn't finish: " + ex.Message));
        }
        finally
        {
            lock (_lock)
            {
                _running?.Dispose();
                _running = null;
            }
        }
    }

    private static GpuTestStatus Failed(string message) => new("Failed", 0, GpuParsing.MostAtOnce, [], 0, message, null);

    private async Task<GpuTestStatus> TestAsync(CancellationToken cancellationToken)
    {
        var info = await _gpu.DetectAsync(cancellationToken).ConfigureAwait(false);
        if (!info.HardwareEncoding)
        {
            return Failed("Jellyfin isn't set up to encode on a GPU, so there is nothing to test. Turn on hardware encoding in Jellyfin's Transcoding settings first.");
        }

        var encoder = _hardware.PickEncoder(TargetCodec.Hevc, out var isHardware);
        if (encoder is null || !isHardware)
        {
            encoder = _hardware.PickEncoder(TargetCodec.H264, out isHardware);
        }

        if (encoder is null || !isHardware)
        {
            return Failed("This server's ffmpeg can't encode H.265 or H.264 on the GPU.");
        }

        var watching = _viewers.Streams(true);
        if (watching.Count > 0)
        {
            return Failed(ViewerMonitor.People(watching) + (watching.Count == 1 ? " is" : " are") + " transcoding now, which would use the GPU and spoil the numbers. Try again when the stream ends.");
        }

        if (PickClip() is not { } clip)
        {
            return Failed("The test needs a film in your library that's at least a minute long, and none was found.");
        }

        var clipName = clip.Item.Name + " (" + clip.File.Height + "p)";
        var frameRate = (double?)clip.Item.GetMediaStreams().FirstOrDefault(s => s.Type == MediaStreamType.Video)?.RealFrameRate ?? 24;
        if (frameRate < 1)
        {
            frameRate = 24;
        }

        var source = EncodeService.SourceInfo(clip.Item, new FileInfo(clip.Item.Path));
        var duration = clip.File.DurationSeconds;

        Update(new GpuTestStatus("Running", 0, GpuParsing.MostAtOnce, [], 0, "Pausing encodes", clipName));
        using var hold = _encodes.HoldEncoding("the GPU test");
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (_encodes.RunningCount() > 0)
        {
            if (waited.Elapsed > _pauseWait)
            {
                return Failed("An encode wouldn't pause. Try again when the queue is empty.");
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        // A moment for the frozen encodes to let go of the GPU.
        await Task.Delay(500, cancellationToken).ConfigureAwait(false);

        // A speed test of the estimator's would share the GPU, so it stops, and none starts until this is done.
        _speed.CancelTests();
        var speeds = await _speed.ExclusiveAsync(() => TrialsAsync(encoder, clip, source, clipName, frameRate, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (speeds.Failure is { } failure)
        {
            return Failed(failure);
        }

        var finished = DateTime.UtcNow;
        var summary = string.Join(
            ' ',
            new[]
            {
                Summary(speeds.Speeds),
                speeds.DecoderNote,
                speeds.Diagnosis,
                speeds.Steady ? null : "Some runs gave different speeds, so something else may have been using the GPU. It's worth testing again when the server is quiet."
            }.Where(t => !string.IsNullOrEmpty(t)));
        var config = Plugin.Instance?.Configuration;
        if (config is not null)
        {
            config.GpuTestedDevice = GpuParsing.DeviceKey(info);
            config.GpuTestedAt = finished;
            config.GpuTestSpeeds = GpuParsing.FormatSpeeds(speeds.Speeds);
            config.GpuTestDetails = System.Text.Json.JsonSerializer.Serialize(new GpuTestDetails(speeds.Passes, speeds.Decoder, frameRate, summary));
            Plugin.Instance!.SaveConfiguration();
        }

        _logger.LogInformation("Storage Optimizer's GPU test of {Gpu} on {Clip} with {Encoder}: {Speeds}", info.Name, clipName, encoder, GpuParsing.FormatSpeeds(speeds.Speeds));
        return new GpuTestStatus("Done", speeds.Speeds.Count, GpuParsing.MostAtOnce, speeds.Speeds, GpuParsing.SweetSpot(speeds.Speeds), summary, clipName, finished, speeds.Passes, speeds.Decoder, frameRate);
    }

    // Each number at once is timed twice: one, two, three, four, then back down. Whatever drifts during the test, such
    // as the card warming up, then counts the same for every number. Two runs that disagree get a third to settle it.
    private async Task<Trials> TrialsAsync(string encoder, (Video Item, MediaFileInfo File) clip, SourceVideo source, string clipName, double frameRate, CancellationToken cancellationToken)
    {
        // Several encodes reading one film from a slow drive or a network share can go slower in total than one,
        // whatever the GPU can do, so the test encodes a copy of a scene on this server's own disk.
        Update(new GpuTestStatus("Running", 0, GpuParsing.MostAtOnce, [], 0, "Copying a scene to test with", clipName));
        var scene = await CopySceneAsync(clip, cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunTrialsAsync(encoder, scene ?? clip.Item.Path, scene is null ? SceneStart(clip.File.DurationSeconds) : 0, scene is not null, source, clip.File, clipName, frameRate, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            DeleteQuietly(scene);
        }
    }

    // Copies a stretch of the film's video, unchanged, or returns null if there's no room or it can't be done.
    private async Task<string?> CopySceneAsync((Video Item, MediaFileInfo File) clip, CancellationToken cancellationToken)
    {
        var duration = clip.File.DurationSeconds;
        var needed = duration > 0 ? clip.File.SizeBytes * (SceneSeconds / duration) * 2 : long.MaxValue;
        var folder = Path.GetTempPath();
        var output = Path.Combine(folder, "datamanager-gputest-" + Guid.NewGuid().ToString("N") + ".mkv");
        try
        {
            if (new DriveInfo(folder).AvailableFreeSpace < needed)
            {
                _logger.LogInformation("Storage Optimizer's GPU test has no room to copy a scene, so it reads the film directly");
                return null;
            }

            await FfmpegRunner.RunAsync(
                _mediaEncoder.EncoderPath,
                FfmpegArguments.BuildSceneCopy(clip.Item.Path, output, SceneStart(duration), SceneSeconds),
                _copyTimeout,
                "copy a scene",
                cancellationToken).ConfigureAwait(false);
            return output;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or TimeoutException)
        {
            _logger.LogInformation(ex, "Storage Optimizer's GPU test couldn't copy a scene, so it reads the film directly");
            DeleteQuietly(output);
            return null;
        }
    }

    private async Task<Trials> RunTrialsAsync(string encoder, string path, double start, bool loop, SourceVideo source, MediaFileInfo film, string clipName, double frameRate, CancellationToken cancellationToken)
    {
        (string Path, double Start, bool Loop) scene = (path, start, loop);

        // The first run isn't counted. It wakes the GPU and, when the film is read directly, brings the scene into
        // the disk cache. It also tells which way the GPU decodes this film: on the GPU if that works, otherwise on the CPU.
        Update(new GpuTestStatus("Running", 0, GpuParsing.MostAtOnce, [], 0, "Warming up", clipName));
        var useDecoder = true;
        var warmUp = await TimeTogetherAsync(1, encoder, scene, source, useDecoder, cancellationToken).ConfigureAwait(false);
        if (warmUp is null)
        {
            useDecoder = false;
            warmUp = await TimeTogetherAsync(1, encoder, scene, source, useDecoder, cancellationToken).ConfigureAwait(false);
        }

        if (warmUp is null)
        {
            return Trials.Fail("ffmpeg couldn't encode the test scene on the GPU. Check Jellyfin's Transcoding settings and its ffmpeg log.");
        }

        // Where the film was decoded, for the page: the GPU only if Jellyfin's settings let it and it worked.
        var gpuDecodes = useDecoder && _hardware.Plan(encoder, source).Decoder is not null;
        var decoder = gpuDecodes ? "GPU" : "CPU";
        var decoderNote = gpuDecodes
            ? "Your film was decoded on the GPU."
            : useDecoder ? "Your film was decoded on the CPU, as Jellyfin's hardware decoding isn't set up for its format." : "Your film was decoded on the CPU, because the GPU couldn't decode it.";

        var totals = new List<List<double>>();
        for (var count = 1; count <= GpuParsing.MostAtOnce; count++)
        {
            Update(new GpuTestStatus("Running", count - 1, GpuParsing.MostAtOnce, GpuParsing.RelativeSpeeds(totals), 0, count == 1 ? "Timing one encode" : $"Timing {count} at once", clipName));
            if (await TimeTogetherAsync(count, encoder, scene, source, useDecoder, cancellationToken).ConfigureAwait(false) is not { } total)
            {
                if (count == 1)
                {
                    return Trials.Fail("ffmpeg couldn't encode the test scene on the GPU. Check Jellyfin's Transcoding settings and its ffmpeg log.");
                }

                _logger.LogInformation("Storage Optimizer's GPU test stopped at {Count} at once: ffmpeg couldn't start that many", count);
                break;
            }

            totals.Add([total]);
        }

        for (var count = totals.Count; count >= 1; count--)
        {
            Update(new GpuTestStatus("Running", totals.Count, GpuParsing.MostAtOnce, GpuParsing.RelativeSpeeds(totals), 0, count == 1 ? "Checking one encode again" : $"Checking {count} at once again", clipName));
            if (await TimeTogetherAsync(count, encoder, scene, source, useDecoder, cancellationToken).ConfigureAwait(false) is { } again)
            {
                totals[count - 1].Add(again);
            }
        }

        var steady = true;
        for (var count = 1; count <= totals.Count; count++)
        {
            if (!GpuParsing.Disagree(totals[count - 1]))
            {
                continue;
            }

            Update(new GpuTestStatus("Running", totals.Count, GpuParsing.MostAtOnce, GpuParsing.RelativeSpeeds(totals), 0, count == 1 ? "One more run of one encode" : $"One more run of {count} at once", clipName));
            if (await TimeTogetherAsync(count, encoder, scene, source, useDecoder, cancellationToken).ConfigureAwait(false) is { } third)
            {
                totals[count - 1].Add(third);
            }

            steady = false;
        }

        var speeds = GpuParsing.RelativeSpeeds(totals);
        var passes = new List<GpuTestPass> { new("Film", "Your film", GpuParsing.Medians(totals.ConvertAll(t => (IReadOnlyList<double>)t))) };

        // The same again with a generated picture, which takes the film's drive and decoding out of it. Next to the
        // film's numbers, it shows whether the GPU or what feeds it is the limit.
        var encoderOnly = await TimePassAsync("Timing the encoder alone", speeds, passes, count => TimeGeneratedAsync(count, encoder, film, frameRate, source, cancellationToken), clipName, frameRate, decoder, cancellationToken).ConfigureAwait(false);
        if (encoderOnly.Count > 0)
        {
            passes.Add(new GpuTestPass("Encoder", "Encoder alone", encoderOnly));
        }

        return new Trials(speeds, null, steady, passes, decoder, decoderNote, GpuParsing.Diagnose(encoderOnly, passes[0].Fps));
    }

    // Times one, two, three and four at once, once each, and returns the total frames a second of each. It stops at
    // the first number that can't be run, and returns what it has.
    private async Task<IReadOnlyList<double>> TimePassAsync(string message, IReadOnlyList<double> speeds, List<GpuTestPass> passes, Func<int, Task<double?>> time, string clipName, double frameRate, string decoder, CancellationToken cancellationToken)
    {
        var results = new List<double>();
        for (var count = 1; count <= GpuParsing.MostAtOnce; count++)
        {
            Update(new GpuTestStatus("Running", GpuParsing.MostAtOnce, GpuParsing.MostAtOnce, speeds, 0, count == 1 ? message : $"{message}, {count} at once", clipName, null, passes.ToList(), decoder, frameRate));
            if (await time(count).ConfigureAwait(false) is not { } total)
            {
                break;
            }

            results.Add(total);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return results;
    }

    // Runs that many encodes of a generated picture, the size of the film's, and returns their total speed, or null if one failed.
    private async Task<double?> TimeGeneratedAsync(int count, string encoder, MediaFileInfo film, double frameRate, SourceVideo source, CancellationToken cancellationToken)
    {
        var setup = _hardware.Plan(encoder, source, false);
        var outputs = new List<string>();
        try
        {
            var commands = new List<IList<string>>();
            for (var i = 0; i < count; i++)
            {
                var output = Path.Combine(Path.GetTempPath(), "datamanager-gputest-" + Guid.NewGuid().ToString("N"));
                outputs.Add(output);
                commands.Add(FfmpegArguments.BuildGeneratedSpeedTest(output, film.Width, film.Height, frameRate, 100_000, encoder, QualityPreset.Balanced, EncodeEffort.Standard, source, setup.Arguments));
            }

            var total = await _speed.TimeTogetherAsync(commands, ExtraSettleSeconds, ExtraWindowSeconds, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Storage Optimizer's GPU test: {Count} at once with {Encoder}, encoding a generated picture: {Fps:F0} frames a second in total", count, encoder, total ?? 0);
            return total;
        }
        finally
        {
            foreach (var output in outputs)
            {
                DeleteQuietly(output);
            }
        }
    }

    // Runs that many encodes together and returns their total speed, in frames a second, or null if any failed.
    // Every encode starts from the same point in the film, so the only thing that changes between runs is how many
    // share the GPU. Different scenes can differ in speed by more than the thing being measured.
    private async Task<double?> TimeTogetherAsync(int count, string encoder, (string Path, double Start, bool Loop) scene, SourceVideo source, bool gpuDecode, CancellationToken cancellationToken)
    {
        var setup = _hardware.Plan(encoder, source, gpuDecode);
        IReadOnlyList<string> beforeInput = scene.Loop ? [.. setup.Arguments, "-stream_loop", "-1"] : setup.Arguments;
        var outputs = new List<string>();
        try
        {
            var commands = new List<IList<string>>();
            for (var i = 0; i < count; i++)
            {
                var output = Path.Combine(Path.GetTempPath(), "datamanager-gputest-" + Guid.NewGuid().ToString("N"));
                outputs.Add(output);
                commands.Add(FfmpegArguments.BuildSpeedTest(scene.Path, output, scene.Start, 100_000, encoder, QualityPreset.Balanced, EncodeEffort.Standard, source, null, beforeInput));
            }

            var usage = NvidiaUsageAsync(encoder, cancellationToken);
            var total = await _speed.TimeTogetherAsync(commands, SettleSeconds, WindowSeconds, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Storage Optimizer's GPU test: {Count} at once with {Encoder}, decoding on the {Decoder}: {Fps:F0} frames a second in total. GPU, encoder, decoder and memory use partway through: {Usage}",
                count,
                encoder,
                gpuDecode && setup.Decoder is not null ? "GPU" : "CPU",
                total ?? 0,
                await usage.ConfigureAwait(false));
            return total;
        }
        finally
        {
            foreach (var output in outputs)
            {
                DeleteQuietly(output);
            }
        }
    }

    // What an NVIDIA card says it's doing partway through a run, which shows whether the encoders or something else
    // is the limit. It's only for the log, so it never gets in the way of the test.
    private static async Task<string> NvidiaUsageAsync(string encoder, CancellationToken cancellationToken)
    {
        if (!encoder.EndsWith("_nvenc", StringComparison.Ordinal))
        {
            return "not read";
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(SettleSeconds + (WindowSeconds / 2)), cancellationToken).ConfigureAwait(false);
            var (output, _) = await FfmpegRunner.RunAsync(
                "nvidia-smi",
                ["--query-gpu=utilization.gpu,utilization.encoder,utilization.decoder,memory.used", "--format=csv,noheader"],
                TimeSpan.FromSeconds(5),
                "read the GPU's use",
                cancellationToken).ConfigureAwait(false);
            return output.Trim();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or TimeoutException)
        {
            return "not read";
        }
    }

    private static double SceneStart(double duration) => Math.Max(0, Math.Min(duration * 0.3, duration - RunwaySeconds));

    private static void DeleteQuietly(string? path)
    {
        try
        {
            if (path is not null)
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A leftover temp file is swept up with the system's temp folder.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }

    private sealed record Trials(IReadOnlyList<double> Speeds, string? Failure, bool Steady, IReadOnlyList<GpuTestPass> Passes, string Decoder, string? DecoderNote, string? Diagnosis)
    {
        public static Trials Fail(string message) => new([], message, false, [], string.Empty, null, null);
    }
}
