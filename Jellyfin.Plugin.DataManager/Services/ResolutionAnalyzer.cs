// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DataManager.Encoding;
using Jellyfin.Plugin.DataManager.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DataManager.Services;

/// <summary>
/// Finds the smallest size a video can be scaled down to without losing real detail.
/// </summary>
/// <remarks>
/// Sample frames are shrunk to each smaller standard size and scaled back up. If the round trip
/// leaves them almost identical (by SSIM), the extra pixels held no detail of their own, which is
/// common for 4K releases made from a 2K master, or DVDs and TV upscaled to 1080p.
/// </remarks>
public sealed partial class ResolutionAnalyzer : IDisposable
{
    private const string CacheFileName = "resolution-analysis.json";

    // Thresholds that separated real from upscaled detail in testing: an upscale scores about 0.997, real detail 0.98 to 0.99.
    private const double AverageThreshold = 0.993;
    private const double FrameThreshold = 0.985;

    private static readonly double[] _samplePoints = [0.15, 0.3, 0.5, 0.7, 0.85];
    private static readonly TimeSpan _timeout = TimeSpan.FromMinutes(2);

    private readonly ILibraryManager _libraryManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<ResolutionAnalyzer> _logger;

    // How many ffmpegs run at once, whatever number of videos and sample frames are waiting: each decodes and
    // rescales full-size frames, and a library check shouldn't take the whole machine from encoding and playback.
    private readonly SemaphoreSlim _processes = new(Math.Clamp(Environment.ProcessorCount / 2, 2, 4));
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly Lock _cacheLock = new();
    private readonly Lock _checkLock = new();
    private CancellationTokenSource _checks = new();
    private Dictionary<string, ResolutionAnalysis>? _cache;

    /// <summary>
    /// Initializes a new instance of the <see cref="ResolutionAnalyzer"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="mediaEncoder">Jellyfin's media encoder, which knows where ffmpeg is.</param>
    /// <param name="logger">The logger.</param>
    public ResolutionAnalyzer(ILibraryManager libraryManager, IMediaEncoder mediaEncoder, ILogger<ResolutionAnalyzer> logger)
    {
        _libraryManager = libraryManager;
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    /// <summary>
    /// Gets a token that is cancelled when <see cref="CancelChecks"/> is called. Checks the user asked for take it, so
    /// closing the re-encode window stops them, and the checks an encode does for itself don't.
    /// </summary>
    public CancellationToken CheckToken
    {
        get
        {
            lock (_checkLock)
            {
                return _checks.Token;
            }
        }
    }

    private static string CachePath => Path.Combine(Plugin.Instance?.DataFolderPath ?? throw new InvalidOperationException("Plugin is not loaded."), CacheFileName);

    /// <summary>
    /// Gets the saved analysis of a video, if the file hasn't changed since.
    /// </summary>
    /// <param name="video">The video.</param>
    /// <returns>The analysis, or null.</returns>
    public ResolutionAnalysis? GetCached(Video video)
    {
        ArgumentNullException.ThrowIfNull(video);

        var cache = LoadCache();
        ResolutionAnalysis? analysis;
        lock (_cacheLock)
        {
            cache.TryGetValue(video.Id.ToString("N", CultureInfo.InvariantCulture), out analysis);
        }

        if (analysis is null || !string.Equals(analysis.Path, video.Path, StringComparison.Ordinal))
        {
            return null;
        }

        var file = new FileInfo(video.Path);
        return file.Exists && file.Length == analysis.SizeBytes && file.LastWriteTimeUtc == analysis.ModifiedUtc ? analysis : null;
    }

    /// <summary>
    /// Stops the checks the user asked for, those that are running and those waiting. Finished checks are kept.
    /// </summary>
    public void CancelChecks()
    {
        CancellationTokenSource old;
        lock (_checkLock)
        {
            old = _checks;
            _checks = new CancellationTokenSource();
        }

        old.Cancel();
    }

    /// <summary>
    /// Gets every saved analysis, keyed by file path.
    /// </summary>
    /// <returns>The analyses.</returns>
    public IReadOnlyDictionary<string, ResolutionAnalysis> GetAllByPath()
    {
        var cache = LoadCache();
        lock (_cacheLock)
        {
            return cache.Values.GroupBy(a => a.Path, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Analyses a video, or returns the saved result if the file hasn't changed.
    /// </summary>
    /// <param name="itemId">The video's item id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The analysis.</returns>
    public async Task<ResolutionAnalysis> AnalyseAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var video = _libraryManager.GetItemById<Video>(itemId);
        if (video is null || !File.Exists(video.Path))
        {
            throw new FileNotFoundException("That video is no longer in the library. Scan again to refresh the list.");
        }

        if (GetCached(video) is { } cached)
        {
            return cached;
        }

        var stream = video.GetMediaStreams().FirstOrDefault(s => s.Type == MediaStreamType.Video);
        var width = stream?.Width ?? 0;
        var height = stream?.Height ?? 0;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("Jellyfin doesn't know this video's size yet. Refresh its metadata, then try again.");
        }

        var file = new FileInfo(video.Path);
        var analysis = new ResolutionAnalysis
        {
            ItemId = video.Id.ToString("N", CultureInfo.InvariantCulture),
            Path = video.Path,
            SizeBytes = file.Length,
            ModifiedUtc = file.LastWriteTimeUtc,
            Width = width,
            Height = height,
            DetailWidth = width,
            DetailHeight = height,
            AnalysedAt = DateTime.UtcNow
        };

        var candidates = Resolutions.SmallerThan(width, height).Select(box => Resolutions.Fit(width, height, box)).ToList();

        if (candidates.Count > 0)
        {
            var scores = candidates.Select(_ => new List<double>()).ToList();
            var duration = TimeSpan.FromTicks(video.RunTimeTicks ?? 0).TotalSeconds;

            // The sample frames are independent, so they're taken together, within the limit on ffmpegs at once.
            using var sampling = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var samples = (duration > 10 ? _samplePoints : [0.0]).Select(async point =>
            {
                try
                {
                    return await SampleAsync(Arguments(video.Path, duration * point, width, height, candidates), sampling.Token).ConfigureAwait(false);
                }
                catch (Exception) when (!sampling.IsCancellationRequested)
                {
                    // One failing is the end of the check, so the others stop rather than carry on for nothing.
                    await sampling.CancelAsync().ConfigureAwait(false);
                    throw;
                }
            }).ToList();
            var all = Task.WhenAll(samples);
            string[] logs;
            try
            {
                logs = await all.ConfigureAwait(false);
            }
            catch (Exception) when (all.Exception is { } failures && !cancellationToken.IsCancellationRequested)
            {
                // The samples that stopped because another failed aren't the reason.
                var cause = failures.InnerExceptions.FirstOrDefault(e => e is not OperationCanceledException) ?? failures.InnerExceptions[0];
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cause).Throw();
                throw;
            }

            foreach (var log in logs)
            {
                foreach (Match match in SsimPattern().Matches(log))
                {
                    var index = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                    if (index < scores.Count && double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    {
                        scores[index].Add(value);
                    }
                }
            }

            if (scores.Any(s => s.Count == 0))
            {
                throw new InvalidOperationException("ffmpeg didn't report how alike the frames were.");
            }

            // Walk down from the largest smaller size, stopping at the first that loses detail.
            var keepsDetail = true;
            for (var i = 0; i < candidates.Count; i++)
            {
                var check = new ResolutionCheck(candidates[i].Width, candidates[i].Height, scores[i].Average(), scores[i].Min());
                analysis.Checks.Add(check);
                keepsDetail = keepsDetail && check.Similarity >= AverageThreshold && check.LowestSimilarity >= FrameThreshold;
                if (keepsDetail)
                {
                    analysis.DetailWidth = check.Width;
                    analysis.DetailHeight = check.Height;
                }
            }
        }

        await SaveAsync(analysis, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Storage Optimizer found {Path} holds about {Width}x{Height} of real detail",
            video.Path,
            analysis.DetailWidth,
            analysis.DetailHeight);
        return analysis;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _processes.Dispose();
        _saveLock.Dispose();
        _checks.Dispose();
    }

    private static List<string> Arguments(string path, double position, int width, int height, List<(int Width, int Height)> candidates)
    {
        string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

        // One frame, in greyscale (detail lives in brightness), split once per smaller size. Each copy is shrunk,
        // scaled back up and compared with the untouched frame. Named ssim filters (ssim@c0, ssim@c1...) label the results.
        var graph = new StringBuilder();
        graph.Append("[0:v]trim=end_frame=1,scale=").Append(Number(width)).Append(':').Append(Number(height))
            .Append(",format=gray,setsar=1,split=").Append(Number(candidates.Count * 2));
        for (var i = 0; i < candidates.Count; i++)
        {
            graph.Append("[o").Append(Number(i)).Append("][x").Append(Number(i)).Append(']');
        }

        for (var i = 0; i < candidates.Count; i++)
        {
            var n = Number(i);
            graph.Append(";[x").Append(n).Append("]scale=").Append(Number(candidates[i].Width)).Append(':').Append(Number(candidates[i].Height))
                .Append(":flags=lanczos,scale=").Append(Number(width)).Append(':').Append(Number(height)).Append(":flags=lanczos[r").Append(n).Append(']')
                .Append(";[o").Append(n).Append("][r").Append(n).Append("]ssim@c").Append(n).Append("[s").Append(n).Append(']');
        }

        var args = new List<string>
        {
            "-hide_banner", "-nostdin",
            "-ss", position.ToString("0.###", CultureInfo.InvariantCulture), "-i", path,
            "-filter_complex", graph.ToString()
        };
        for (var i = 0; i < candidates.Count; i++)
        {
            args.AddRange(["-map", "[s" + Number(i) + "]", "-f", "null", "-"]);
        }

        return args;
    }

    // Waits for a free place among the ffmpegs, then takes the frame.
    private async Task<string> SampleAsync(IList<string> args, CancellationToken cancellationToken)
    {
        await _processes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunFfmpegAsync(args, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _processes.Release();
        }
    }

    private async Task<string> RunFfmpegAsync(IList<string> args, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(_mediaEncoder.EncoderPath)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        using var registration = timeout.Token.Register(() =>
        {
            try
            {
                process.Kill(true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }
        });

        var log = await process.StandardError.ReadToEndAsync(CancellationToken.None).ConfigureAwait(false);
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (timeout.IsCancellationRequested)
        {
            throw new TimeoutException("Checking the frames took longer than two minutes.");
        }

        if (process.ExitCode != 0)
        {
            var tail = log.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(3);
            throw new InvalidOperationException("ffmpeg could not check the frames: " + string.Join(" ", tail));
        }

        return log;
    }

    private Dictionary<string, ResolutionAnalysis> LoadCache()
    {
        lock (_cacheLock)
        {
            if (_cache is not null)
            {
                return _cache;
            }

            _cache = new Dictionary<string, ResolutionAnalysis>(StringComparer.Ordinal);
            try
            {
                if (File.Exists(CachePath))
                {
                    _cache = JsonSerializer.Deserialize<Dictionary<string, ResolutionAnalysis>>(File.ReadAllText(CachePath)) ?? _cache;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Storage Optimizer could not read saved resolution checks, so starting afresh");
            }

            return _cache;
        }
    }

    private async Task SaveAsync(ResolutionAnalysis analysis, CancellationToken cancellationToken)
    {
        var cache = LoadCache();
        string json;
        lock (_cacheLock)
        {
            cache[analysis.ItemId] = analysis;
            json = JsonSerializer.Serialize(cache);
        }

        // One write at a time, to a file beside the saved one that's moved over it, so a reader never sees half of it.
        await _saveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var tempPath = CachePath + ".tmp";
            await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, CachePath, true);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    // ffmpeg logs e.g. "[ssim@c1 @ 0x55d0] SSIM Y:0.996776 (24.9) All:0.996776 (24.9)".
    [GeneratedRegex(@"\[ssim@c(\d+) @ [^\]]*\] SSIM Y:(\d+(?:\.\d+)?)")]
    private static partial Regex SsimPattern();
}
