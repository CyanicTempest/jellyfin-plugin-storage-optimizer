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
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
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
public sealed record GpuTestStatus(string State, int Step, int Steps, IReadOnlyList<double> Speeds, int SweetSpot, string Message, string? Clip, DateTime? TestedAt = null)
{
    /// <summary>Gets the status of a GPU that hasn't been tested.</summary>
    public static GpuTestStatus Idle { get; } = new("Idle", 0, GpuParsing.MostAtOnce, [], 0, string.Empty, null);
}

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

    // How long encodes run before timing starts, once all of them are putting out frames, and how long they're timed.
    private const double SettleSeconds = 3;
    private const double WindowSeconds = 8;

    private static readonly TimeSpan _pauseWait = TimeSpan.FromSeconds(15);

    private readonly ILibraryManager _libraryManager;
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
    /// <param name="encodes">The re-encode queue, which is held while the test runs.</param>
    /// <param name="speed">Times the encodes.</param>
    /// <param name="hardware">Sets up the GPU for test encodes.</param>
    /// <param name="gpu">Reads what the GPU is.</param>
    /// <param name="viewers">Finds streams that are using the GPU.</param>
    /// <param name="logger">The logger.</param>
    public GpuTester(ILibraryManager libraryManager, EncodeService encodes, SpeedEstimator speed, HardwareAcceleration hardware, GpuDetector gpu, ViewerMonitor viewers, ILogger<GpuTester> logger)
    {
        _libraryManager = libraryManager;
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

        return new GpuTestStatus("Done", speeds.Count, GpuParsing.MostAtOnce, speeds, GpuParsing.SweetSpot(speeds), Summary(speeds), null, config.GpuTestedAt);
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
        var speeds = await _speed.ExclusiveAsync(() => TrialsAsync(encoder, clip, source, clipName, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (speeds.Failure is { } failure)
        {
            return Failed(failure);
        }

        var finished = DateTime.UtcNow;
        var config = Plugin.Instance?.Configuration;
        if (config is not null)
        {
            config.GpuTestedDevice = GpuParsing.DeviceKey(info);
            config.GpuTestedAt = finished;
            config.GpuTestSpeeds = GpuParsing.FormatSpeeds(speeds.Speeds);
            Plugin.Instance!.SaveConfiguration();
        }

        _logger.LogInformation("Storage Optimizer's GPU test of {Gpu} on {Clip} with {Encoder}: {Speeds}", info.Name, clipName, encoder, GpuParsing.FormatSpeeds(speeds.Speeds));
        var summary = Summary(speeds.Speeds) + (speeds.Steady ? string.Empty : " Some runs gave different speeds, so something else may have been using the GPU. It's worth testing again when the server is quiet.");
        return new GpuTestStatus("Done", speeds.Speeds.Count, GpuParsing.MostAtOnce, speeds.Speeds, GpuParsing.SweetSpot(speeds.Speeds), summary, clipName, finished);
    }

    // Each number at once is timed twice: one, two, three, four, then back down. Whatever drifts during the test, such
    // as the card warming up, then counts the same for every number. Two runs that disagree get a third to settle it.
    private async Task<Trials> TrialsAsync(string encoder, (Video Item, MediaFileInfo File) clip, SourceVideo source, string clipName, CancellationToken cancellationToken)
    {
        // The first run isn't counted. It reads the scene from a cold disk, which on a slow drive or a network share
        // can hold a fast GPU back more than anything being measured, and the runs after it find the scene in the
        // disk cache. It also wakes the GPU, and tells which way it decodes this film: on the GPU if that works,
        // otherwise on the CPU.
        Update(new GpuTestStatus("Running", 0, GpuParsing.MostAtOnce, [], 0, "Warming up", clipName));
        var useDecoder = true;
        var warmUp = await TimeTogetherAsync(1, encoder, clip, source, useDecoder, cancellationToken).ConfigureAwait(false);
        if (warmUp is null)
        {
            useDecoder = false;
            warmUp = await TimeTogetherAsync(1, encoder, clip, source, useDecoder, cancellationToken).ConfigureAwait(false);
        }

        if (warmUp is null)
        {
            return new Trials([], "ffmpeg couldn't encode the test scene on the GPU. Check Jellyfin's Transcoding settings and its ffmpeg log.", false);
        }

        var totals = new List<List<double>>();
        for (var count = 1; count <= GpuParsing.MostAtOnce; count++)
        {
            Update(new GpuTestStatus("Running", count - 1, GpuParsing.MostAtOnce, GpuParsing.RelativeSpeeds(totals), 0, count == 1 ? "Timing one encode" : $"Timing {count} at once", clipName));
            if (await TimeTogetherAsync(count, encoder, clip, source, useDecoder, cancellationToken).ConfigureAwait(false) is not { } total)
            {
                if (count == 1)
                {
                    return new Trials([], "ffmpeg couldn't encode the test scene on the GPU. Check Jellyfin's Transcoding settings and its ffmpeg log.", false);
                }

                _logger.LogInformation("Storage Optimizer's GPU test stopped at {Count} at once: ffmpeg couldn't start that many", count);
                break;
            }

            totals.Add([total]);
        }

        for (var count = totals.Count; count >= 1; count--)
        {
            Update(new GpuTestStatus("Running", totals.Count, GpuParsing.MostAtOnce, GpuParsing.RelativeSpeeds(totals), 0, count == 1 ? "Checking one encode again" : $"Checking {count} at once again", clipName));
            if (await TimeTogetherAsync(count, encoder, clip, source, useDecoder, cancellationToken).ConfigureAwait(false) is { } again)
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
            if (await TimeTogetherAsync(count, encoder, clip, source, useDecoder, cancellationToken).ConfigureAwait(false) is { } third)
            {
                totals[count - 1].Add(third);
            }

            steady = false;
        }

        return new Trials(GpuParsing.RelativeSpeeds(totals), null, steady);
    }

    // Runs that many encodes together and returns their total speed, in frames a second, or null if any failed.
    // Every encode starts from the same point in the film, so the only thing that changes between runs is how many
    // share the GPU. Different scenes can differ in speed by more than the thing being measured.
    private async Task<double?> TimeTogetherAsync(int count, string encoder, (Video Item, MediaFileInfo File) clip, SourceVideo source, bool gpuDecode, CancellationToken cancellationToken)
    {
        var setup = _hardware.Plan(encoder, source, gpuDecode);
        var duration = clip.File.DurationSeconds;
        var start = Math.Max(0, Math.Min(duration * 0.3, duration - RunwaySeconds));
        var outputs = new List<string>();
        try
        {
            var commands = new List<IList<string>>();
            for (var i = 0; i < count; i++)
            {
                var output = Path.Combine(Path.GetTempPath(), "datamanager-gputest-" + Guid.NewGuid().ToString("N"));
                outputs.Add(output);
                commands.Add(FfmpegArguments.BuildSpeedTest(clip.Item.Path, output, start, 100_000, encoder, QualityPreset.Balanced, EncodeEffort.Standard, source, null, setup.Arguments));
            }

            return await _speed.TimeTogetherAsync(commands, SettleSeconds, WindowSeconds, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            foreach (var output in outputs)
            {
                try
                {
                    File.Delete(output);
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
        }
    }

    private sealed record Trials(IReadOnlyList<double> Speeds, string? Failure, bool Steady);
}
