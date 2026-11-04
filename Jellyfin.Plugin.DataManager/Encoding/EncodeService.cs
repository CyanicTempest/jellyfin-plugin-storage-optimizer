// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.DataManager.Configuration;
using Jellyfin.Plugin.DataManager.Models;
using Jellyfin.Plugin.DataManager.Services;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DataManager.Encoding;

/// <summary>
/// Queues re-encode jobs and runs them one at a time.
/// </summary>
public sealed class EncodeService : IDisposable
{
    private const int MaxFinishedJobsKept = 100;

    // Hidden in-progress files are named ".<original name>.datamanager-partial.<ext>".
    private const string PartialMarker = ".datamanager-partial.";

    // Room left on the drive after an encode, so the library never fills the disk completely.
    private const long SafetyMarginBytes = 1L * 1024 * 1024 * 1024;

    // A new file has to be at least this much smaller to be worth the little re-encoding loses. One that isn't is
    // held back until the user chooses what to do; the page doesn't offer settings expected to miss it.
    private const double MinimumSaving = 0.05;

    // A preview is about a second of video; slow CPUs take a while over a second of 4K, more so at the slowest effort.
    private static readonly TimeSpan _previewTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan _clipTimeout = TimeSpan.FromMinutes(12);

    // How long a working file has to sit untouched before it counts as left behind.
    private static readonly TimeSpan _unfinishedFileAge = TimeSpan.FromHours(1);

    // How long a clip to play side by side: long enough to see motion, short enough to encode quickly.
    private const double ClipSeconds = 4;

    // How often the dispatcher looks at who is watching while there are encodes to run.
    private static readonly TimeSpan _dispatchEvery = TimeSpan.FromSeconds(3);

    private readonly ILibraryManager _libraryManager;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IFileSystem _fileSystem;
    private readonly ResolutionAnalyzer _resolutionAnalyzer;
    private readonly SpeedEstimator _speedEstimator;
    private readonly FrameComparer _frameComparer;
    private readonly HardwareAcceleration _hardware;
    private readonly LanguageCodes _languages;
    private readonly ReportUpdater _reports;
    private readonly IServerConfigurationManager _serverConfig;
    private readonly ILogger<EncodeService> _logger;
    private readonly ViewerMonitor _viewers;
    private readonly GpuDetector _gpu;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly List<EncodeJob> _jobs = [];
    private readonly Lock _jobsLock = new();
    private readonly Lock _viewerLock = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _dispatcher;
    private volatile string? _testHold;
    private int _heldStreams;
    private DateTime _heldAt = DateTime.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="EncodeService"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="libraryMonitor">The library monitor.</param>
    /// <param name="mediaEncoder">Jellyfin's media encoder, which knows where ffmpeg is.</param>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="resolutionAnalyzer">Finds how far each video can be scaled down.</param>
    /// <param name="speedEstimator">Works out how long encodes take.</param>
    /// <param name="frameComparer">Compares frames, for previews.</param>
    /// <param name="hardware">Picks encoders and sets up the GPU.</param>
    /// <param name="languages">Matches track languages to the ones the user keeps.</param>
    /// <param name="reports">Puts finished encodes into the saved report.</param>
    /// <param name="serverConfig">The server's settings, for its preferred language.</param>
    /// <param name="viewers">Lists the streams being transcoded, so encoding can make room for them.</param>
    /// <param name="gpu">Finds the GPU, for how many encodes to run at once.</param>
    /// <param name="logger">The logger.</param>
    public EncodeService(
        ILibraryManager libraryManager,
        ILibraryMonitor libraryMonitor,
        IMediaEncoder mediaEncoder,
        IFileSystem fileSystem,
        ResolutionAnalyzer resolutionAnalyzer,
        SpeedEstimator speedEstimator,
        FrameComparer frameComparer,
        HardwareAcceleration hardware,
        LanguageCodes languages,
        ReportUpdater reports,
        IServerConfigurationManager serverConfig,
        ViewerMonitor viewers,
        GpuDetector gpu,
        ILogger<EncodeService> logger)
    {
        _viewers = viewers;
        _gpu = gpu;
        _languages = languages;
        _reports = reports;
        _serverConfig = serverConfig;
        _hardware = hardware;
        _frameComparer = frameComparer;
        _resolutionAnalyzer = resolutionAnalyzer;
        _speedEstimator = speedEstimator;
        _libraryManager = libraryManager;
        _libraryMonitor = libraryMonitor;
        _mediaEncoder = mediaEncoder;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    /// <summary>
    /// Gets the codecs the user can pick, with the encoder each would use and its compatibility notes.
    /// </summary>
    /// <returns>The codec options.</returns>
    public IReadOnlyList<CodecOption> GetCodecOptions()
    {
        return
        [
            Option(
                TargetCodec.Hevc,
                "HEVC (H.265)",
                "About 40% smaller than H.264. Plays on most devices.",
                "Plays directly on most TVs, phones and streaming boxes from about 2017 on, recent Apple devices, and Chrome, Edge and Safari. Firefox and older devices can't, so Jellyfin transcodes for them, which uses your server's CPU or GPU while they watch."),
            Option(
                TargetCodec.Av1,
                "AV1",
                "Smallest files. Newer devices only.",
                "Plays directly on Google TV and Android TV devices from about 2021, iPhone 15 Pro, M3 Macs and newer, and recent smart TVs. Older devices need Jellyfin to transcode. Encoding is very slow without a recent GPU."),
            Option(
                TargetCodec.H264,
                "H.264",
                "Plays everywhere. Saves little unless the file is in an older codec.",
                "Every device and browser plays H.264. It only saves space on files in older codecs like MPEG-2 or VC-1, or with a very high bitrate.")
        ];
    }

    /// <summary>
    /// Gets all jobs, newest first.
    /// </summary>
    /// <returns>A snapshot of the jobs.</returns>
    public IReadOnlyList<EncodeJob> GetJobs()
    {
        lock (_jobsLock)
        {
            return _jobs.OrderByDescending(j => j.QueuedAt).ToList();
        }
    }

    /// <summary>
    /// Queues a job for each requested item.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>The new jobs.</returns>
    public IReadOnlyList<EncodeJob> Enqueue(EncodeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // A video that already has a re-encode going isn't queued again: two jobs would share one hidden file.
        var busy = BusyItems();
        var created = new List<EncodeJob>();
        var languages = NormalizeLanguages(request.Languages);
        var batch = Guid.NewGuid();
        foreach (var itemId in request.ItemIds.Distinct().Where(id => !busy.ContainsKey(id)))
        {
            var item = _libraryManager.GetItemById<Video>(itemId);
            var own = request.Remux ? null : request.Overrides.LastOrDefault(o => o.ItemId == itemId);
            var job = new EncodeJob
            {
                ItemId = itemId,
                Name = item is null ? itemId.ToString() : StorageScanner.Title(item),
                SourcePath = item?.Path ?? string.Empty,
                Codec = own?.Codec ?? request.Codec,
                Quality = own?.Quality ?? request.Quality,
                Effort = own?.Effort ?? request.Effort,
                Mode = request.Mode,
                Resolution = own?.Resolution ?? request.Resolution,
                Audio = request.Audio,
                Subtitles = request.Subtitles,
                Languages = languages,
                ExcludedTracks = request.ExcludedTracks.ToList(),
                IncludedTracks = request.IncludedTracks.ToList(),
                IsRemux = request.Remux,
                AudioFormat = request.AudioFormat,
                StereoAudio = request.StereoAudio,
                Hdr = request.Hdr,
                StripCoverArt = request.StripCoverArt,
                StripFonts = request.StripFonts,
                SourceBytes = item?.Size ?? 0,
                BatchId = batch
            };
            if (item is Episode episode)
            {
                var seriesId = episode.SeriesId.Equals(Guid.Empty) ? episode.FindSeriesId() : episode.SeriesId;
                job.SeriesName = StorageScanner.SeriesName(episode);
                job.SeriesId = seriesId.Equals(Guid.Empty) ? null : seriesId.ToString("N", CultureInfo.InvariantCulture);
                job.SeasonName = string.IsNullOrEmpty(episode.SeasonName) ? episode.FindSeasonName() : episode.SeasonName;
                job.SeasonNumber = episode.ParentIndexNumber;
                job.EpisodeNumber = episode.IndexNumber;
                job.EpisodeName = episode.Name;
            }

            lock (_jobsLock)
            {
                _jobs.Add(job);
                TrimFinishedJobs();
            }

            created.Add(job);
        }

        lock (_jobsLock)
        {
            _dispatcher ??= Task.Run(() => RunDispatcherAsync(_shutdown.Token));
        }

        Wake();
        return created;
    }

    /// <summary>
    /// Cancels a queued or running job.
    /// </summary>
    /// <param name="jobId">The job id.</param>
    /// <returns>True if the job was found and still active.</returns>
    public bool Cancel(Guid jobId)
    {
        EncodeJob? job;
        lock (_jobsLock)
        {
            job = _jobs.FirstOrDefault(j => j.Id == jobId);
        }

        if (job is null || !job.CanCancel)
        {
            return false;
        }

        job.Cancellation.Cancel();
        if (job.State == EncodeJobState.Queued)
        {
            Finish(job, EncodeJobState.Cancelled, "Cancelled before it started.");
        }

        Wake();
        return true;
    }

    /// <summary>
    /// Estimates the size of the new files and how long encoding them will take, for the chosen settings and for
    /// each other quality and effort, so the choices can be weighed against each other.
    /// </summary>
    /// <remarks>
    /// Time comes from this server's real speed: saved from earlier encodes or tests, the encode running now,
    /// or else test encodes of a calm, a typical and a busy scene of the file that will take longest. The same
    /// tests measure how big the video comes out, which corrects the rule of thumb for size.
    /// </remarks>
    /// <param name="request">The request to estimate.</param>
    /// <param name="includeTime">Whether to work out the time too, which may run test encodes. Without it the size comes back straight away.</param>
    /// <param name="preferredLanguage">The user's preferred audio language, if they've set one, to suggest keeping.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The estimate.</returns>
    public async Task<EncodeEstimate> EstimateAsync(EncodeRequest request, bool includeTime, string? preferredLanguage, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Files with settings of their own are estimated in groups that share settings, and the groups added up.
        var groups = request.Remux
            ? []
            : request.ItemIds.Distinct()
                .GroupBy(id =>
                {
                    var own = request.Overrides.LastOrDefault(o => o.ItemId == id);
                    return (Codec: own?.Codec ?? request.Codec, Quality: own?.Quality ?? request.Quality, Effort: own?.Effort ?? request.Effort, Resolution: own?.Resolution ?? request.Resolution);
                })
                .ToList();
        EncodeEstimate result;
        if (groups.Count < 2)
        {
            var only = groups.Count == 1 ? groups[0].Key : (request.Codec, request.Quality, request.Effort, request.Resolution);
            result = await EstimateGroupAsync(groups.Count == 1 ? WithSettings(request, groups[0].ToList(), only.Codec, only.Quality, only.Effort, only.Resolution) : request, includeTime, preferredLanguage, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // The group with the request's own settings goes first: its size and time rows are what the others are measured against.
            var parts = new List<EncodeEstimate>();
            foreach (var group in groups.OrderByDescending(g => g.Key == (request.Codec, request.Quality, request.Effort, request.Resolution)).ThenByDescending(g => g.Count()))
            {
                parts.Add(await EstimateGroupAsync(WithSettings(request, group.ToList(), group.Key.Codec, group.Key.Quality, group.Key.Effort, group.Key.Resolution), includeTime, preferredLanguage, cancellationToken).ConfigureAwait(false));
            }

            result = Combine(parts);
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        result.Slots = await SlotsAsync(config, cancellationToken).ConfigureAwait(false);
        foreach (var speedup in await ParallelSpeedupsAsync(config, result.IsHardware, cancellationToken).ConfigureAwait(false))
        {
            result.Speedups.Add(speedup);
        }

        return result;
    }

    // The same request for some of its files, with those files' settings.
    private static EncodeRequest WithSettings(EncodeRequest request, IList<Guid> ids, TargetCodec codec, QualityPreset quality, EncodeEffort effort, ResolutionChoice resolution)
        => new()
        {
            ItemIds = ids,
            Codec = codec,
            Quality = quality,
            Effort = effort,
            Mode = request.Mode,
            Resolution = resolution,
            Audio = request.Audio,
            Subtitles = request.Subtitles,
            Languages = request.Languages,
            ExcludedTracks = request.ExcludedTracks,
            IncludedTracks = request.IncludedTracks,
            Remux = request.Remux,
            AudioFormat = request.AudioFormat,
            StereoAudio = request.StereoAudio,
            Hdr = request.Hdr,
            StripCoverArt = request.StripCoverArt,
            StripFonts = request.StripFonts
        };

    // Adds up estimates for groups of files that have different settings. Only the totals are kept: the rows for other levels,
    // speeds and tracks only make sense for files that share settings, so callers ask for those without any overrides.
    private static EncodeEstimate Combine(List<EncodeEstimate> parts)
    {
        var first = parts[0];
        var total = new EncodeEstimate
        {
            IsHardware = parts.All(p => p.IsHardware),
            SizeMeasured = parts.All(p => p.SizeMeasured),
            CanRemoveDolbyVision = first.CanRemoveDolbyVision,
            SpeedSource = first.SpeedSource,
            Seconds = parts.All(p => p.Seconds is not null) ? parts.Sum(p => p.Seconds) : null
        };
        foreach (var part in parts)
        {
            total.OutputBytes += part.OutputBytes;
            total.SourceBytes += part.SourceBytes;
            total.FilesToAnalyse += part.FilesToAnalyse;
            total.RemovedTrackBytes += part.RemovedTrackBytes;
            total.RemovedAudioTracks += part.RemovedAudioTracks;
            total.RemovedSubtitleTracks += part.RemovedSubtitleTracks;
            total.AudioFallbackFiles += part.AudioFallbackFiles;
            total.ConvertedAudioTracks += part.ConvertedAudioTracks;
            total.AudioSavingBytes += part.AudioSavingBytes;
            total.ConvertedObjectAudioTracks += part.ConvertedObjectAudioTracks;
            total.HdrFiles += part.HdrFiles;
            total.DolbyVisionFiles += part.DolbyVisionFiles;
            total.DolbyVisionLayerFiles += part.DolbyVisionLayerFiles;
            total.DolbyVisionOnlyFiles += part.DolbyVisionOnlyFiles;
            foreach (var name in part.Names.Where(_ => total.Names.Count < 3))
            {
                total.Names.Add(name);
            }
        }

        return total;
    }

    // How much faster a batch goes with 1, 2, 3 and 4 encodes at once: what the last GPU test found, or else what the GPU's
    // engines suggest. An encode on the CPU already uses every core, so running more doesn't speed it up.
    private async Task<IReadOnlyList<double>> ParallelSpeedupsAsync(PluginConfiguration config, bool isHardware, CancellationToken cancellationToken)
    {
        var speedups = new List<double>();
        if (!isHardware)
        {
            for (var i = 0; i < GpuParsing.MostAtOnce; i++)
            {
                speedups.Add(1);
            }

            return speedups;
        }

        var info = await _gpu.DetectAsync(cancellationToken).ConfigureAwait(false);
        var tested = GpuParsing.ParseSpeeds(config.GpuTestSpeeds);
        if (tested.Count > 0 && string.Equals(config.GpuTestedDevice, GpuParsing.DeviceKey(info), StringComparison.Ordinal))
        {
            // Past what was tested, more at once got nothing more.
            for (var i = 0; i < GpuParsing.MostAtOnce; i++)
            {
                speedups.Add(tested[Math.Min(i, tested.Count - 1)]);
            }

            return speedups;
        }

        var engines = Math.Max(1, info.Engines);
        for (var i = 0; i < GpuParsing.MostAtOnce; i++)
        {
            speedups.Add(Math.Min(i + 1, engines));
        }

        return speedups;
    }

    private async Task<EncodeEstimate> EstimateGroupAsync(EncodeRequest request, bool includeTime, string? preferredLanguage, CancellationToken cancellationToken)
    {

        var encoder = PickEncoder(request.Codec, out var isHardware);
        var estimate = new EncodeEstimate { IsHardware = isHardware, CanRemoveDolbyVision = DolbyVisionStripFilter() is not null };
        var convertsAudio = request.AudioFormat != AudioFormat.Keep || request.StereoAudio;
        var plans = new List<EstimatePlan>();
        var languages = NormalizeLanguages(request.Languages);
        var choosing = request.Audio != AudioChoice.KeepAll || request.Subtitles != SubtitleChoice.KeepAll;
        var found = new Dictionary<string, (int AudioTracks, long AudioBytes, int SubtitleTracks, long SubtitleBytes)>(StringComparer.Ordinal);
        var kinds = new Dictionary<string, (TrackInfo First, bool IsDefault, int Files, long Bytes)>(StringComparer.Ordinal);
        foreach (var id in request.ItemIds.Distinct())
        {
            var item = _libraryManager.GetItemById<Video>(id);
            if (item is null || !File.Exists(item.Path))
            {
                continue;
            }

            var (file, audioBitrate) = Describe(item);
            var size = TargetSize(item, file, request.Resolution, out var needsAnalysis);
            if (needsAnalysis)
            {
                estimate.FilesToAnalyse++;
            }

            estimate.SourceBytes += file.SizeBytes;
            var speedFile = SpeedFile(item, file, audioBitrate, size);
            estimate.HdrFiles += speedFile.Source.IsHdr ? 1 : 0;
            estimate.DolbyVisionFiles += speedFile.Source.DolbyVisionProfile is not null ? 1 : 0;
            estimate.DolbyVisionLayerFiles += speedFile.Source.DolbyVisionLayer ? 1 : 0;
            estimate.DolbyVisionOnlyFiles += speedFile.Source.DolbyVisionOnly ? 1 : 0;

            // Every track's language is listed so the user can choose; the plan says what the choice removes.
            var tracks = _languages.Tracks(item.GetMediaStreams());
            foreach (var track in tracks)
            {
                var key = track.Language ?? string.Empty;
                var bytes = (long)(track.EffectiveBitRate * file.DurationSeconds / 8);
                found.TryGetValue(key, out var sum);
                found[key] = track.Kind == TrackKind.Audio
                    ? (sum.AudioTracks + 1, sum.AudioBytes + bytes, sum.SubtitleTracks, sum.SubtitleBytes)
                    : (sum.AudioTracks, sum.AudioBytes, sum.SubtitleTracks + 1, sum.SubtitleBytes + bytes);
                kinds[track.Key] = kinds.TryGetValue(track.Key, out var kind)
                    ? (kind.First, kind.IsDefault || track.IsDefault, kind.Files + 1, kind.Bytes + bytes)
                    : (track, track.IsDefault, 1, bytes);
            }

            TrackPlan? trackPlan = null;
            if (choosing)
            {
                trackPlan = TrackSelection.Choose(tracks, request.Audio, request.Subtitles, languages, request.ExcludedTracks.ToList(), request.IncludedTracks.ToList());
                estimate.RemovedAudioTracks += trackPlan.RemovedAudio;
                estimate.RemovedSubtitleTracks += trackPlan.RemovedSubtitles;
                estimate.RemovedTrackBytes += (long)(trackPlan.RemovedBitRate * file.DurationSeconds / 8);
                estimate.AudioFallbackFiles += trackPlan.AudioFallback ? 1 : 0;
            }

            // Converted audio keeps its place but shrinks. Without a choice of tracks every one is kept.
            long? outputAudio = trackPlan?.KeptAudioBitRate;
            if (convertsAudio)
            {
                var keptTracks = trackPlan?.Kept ?? tracks;
                var conversions = AudioConverter.PlanAll(keptTracks, request.AudioFormat, request.StereoAudio);
                var saving = conversions.Sum(c => keptTracks.First(t => t.Index == c.Index).EffectiveBitRate - c.BitRate);
                estimate.ConvertedAudioTracks += conversions.Count;
                estimate.ConvertedObjectAudioTracks += conversions.Count(c => IsObjectAudio(keptTracks.First(t => t.Index == c.Index)));
                estimate.AudioSavingBytes += (long)(saving * file.DurationSeconds / 8);
                outputAudio = keptTracks.Where(t => t.Kind == TrackKind.Audio).Sum(t => t.EffectiveBitRate) - saving;
            }

            plans.Add(new EstimatePlan(speedFile, file, audioBitrate, outputAudio, trackPlan?.KeptSubtitleBitRate ?? 0));

            // What the first few new files will be called, so the user sees the naming before starting.
            if (estimate.Names.Count < 3)
            {
                var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
                var scaledTo = speedFile.ScaleTo is null ? null : Resolutions.Label(size.Width, size.Height);
                estimate.Names.Add(new PlannedName(
                    Path.GetFileName(item.Path),
                    Path.GetFileName(PlanPath(item, OutputMode.KeepBoth, request.Codec, size, scaledTo, Container(OutputMode.KeepBoth, speedFile.Source), config, request.Remux)),
                    Path.GetFileName(PlanPath(item, OutputMode.Replace, request.Codec, size, scaledTo, Container(OutputMode.Replace, speedFile.Source), config, request.Remux))));
            }
        }

        foreach (var (code, sum) in found.OrderByDescending(f => f.Value.AudioBytes + f.Value.SubtitleBytes))
        {
            estimate.Languages.Add(new LanguageOption(code, code.Length == 0 ? "No language set" : _languages.DisplayName(code), sum.AudioTracks, sum.AudioBytes, sum.SubtitleTracks, sum.SubtitleBytes));
        }

        // Audio first, then the biggest, so the main tracks lead each language.
        foreach (var (key, kind) in kinds.OrderBy(k => k.Value.First.Kind).ThenByDescending(k => k.Value.Bytes))
        {
            var t = kind.First;
            estimate.Tracks.Add(new TrackOption(key, t.Kind, t.Language ?? string.Empty, t.Codec, t.Profile, t.Channels, t.Title, kind.IsDefault, t.IsForced, t.IsHearingImpaired, t.IsCommentary, kind.Files, kind.Bytes));
        }

        // The user's own audio language, then the server's, where the files have tracks in them.
        foreach (var code in new[] { preferredLanguage, _serverConfig.Configuration.PreferredMetadataLanguage }.Select(_languages.Normalize))
        {
            if (code is not null && found.ContainsKey(code) && !estimate.SuggestedLanguages.Contains(code))
            {
                estimate.SuggestedLanguages.Add(code);
            }
        }

        // A remux copies the video, so the new files are the originals less the tracks removed, and it takes
        // about as long as copying them.
        if (request.Remux)
        {
            estimate.OutputBytes = Math.Max(estimate.SourceBytes - estimate.RemovedTrackBytes - estimate.AudioSavingBytes, 0);
            return estimate;
        }

        if (includeTime && encoder is not null && plans.Count > 0)
        {
            (estimate.Seconds, estimate.SpeedSource) = await EstimateSecondsAsync(encoder, request.Codec, request.Quality, request.Effort, plans, cancellationToken).ConfigureAwait(false);
            foreach (var effort in FfmpegArguments.Efforts(encoder))
            {
                estimate.Efforts.Add(effort == request.Effort && estimate.Seconds is not null
                    ? new EffortEstimate(effort, estimate.Seconds, true)
                    : SecondsWith(encoder, request.Quality, effort, plans));
            }
        }

        // Sizes come after any test, which measures them.
        foreach (var quality in Enum.GetValues<QualityPreset>())
        {
            var (bytes, measured) = OutputBytes(encoder, request.Codec, quality, request.Effort, plans);
            estimate.Levels.Add(new LevelEstimate(quality, bytes, measured));
            if (quality == request.Quality)
            {
                estimate.OutputBytes = bytes;
                estimate.SizeMeasured = measured;
            }
        }

        return estimate;
    }

    /// <summary>
    /// Encodes about a second of a video from a point with the chosen settings, without touching the library, and
    /// compares a frame from the middle of it with the original. The middle frame is a typical one: an encode starts
    /// with a keyframe, which encoders give more bits than the frames after it.
    /// </summary>
    /// <param name="request">The video, where to start and the settings.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The original and preview frames, the detail map and the similarity scores.</returns>
    public async Task<FrameComparison> PreviewAsync(EncodePreviewRequest request, CancellationToken cancellationToken)
    {
        var plan = await PlanPreviewAsync(request, cancellationToken).ConfigureAwait(false);
        var frames = (int)Math.Clamp(Math.Round(plan.File.FramesPerSecond), 12, 60);
        var folder = Path.Combine(Path.GetTempPath(), "datamanager-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var output = Path.Combine(folder, "preview.mkv");
            await _speedEstimator.ExclusiveAsync(
                () => _hardware.RunAsync(
                    plan.Encoder,
                    plan.Source,
                    hw => FfmpegRunner.RunAsync(
                        _mediaEncoder.EncoderPath,
                        FfmpegArguments.BuildPreview(plan.Item.Path, output, plan.Position, frames, plan.Encoder, request.Quality, request.Effort, plan.Source, plan.File.ScaleTo, hw.Arguments, request.Hdr == HdrChoice.ToneMapToSdr),
                        _previewTimeout,
                        "encode the preview",
                        cancellationToken),
                    null,
                    cancellationToken),
                cancellationToken).ConfigureAwait(false);
            return await _frameComparer.ComparePreviewAsync(plan.Item, plan.Position, output, frames / 2, plan.File.FramesPerSecond, plan.File.ScaleTo, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            RemoveFolder(folder);
        }
    }

    /// <summary>
    /// Encodes a few seconds of a video from a point with the chosen settings, and stacks them under the same seconds
    /// of the original as one video any browser can play, so the two can be watched side by side in motion.
    /// </summary>
    /// <param name="request">The video, where to start and the settings.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The path of the clip, an mp4 in the temp folder, which the caller deletes.</returns>
    public async Task<string> ClipAsync(EncodePreviewRequest request, CancellationToken cancellationToken)
    {
        var plan = await PlanPreviewAsync(request, cancellationToken).ConfigureAwait(false);
        var frames = (int)Math.Clamp(Math.Round(plan.File.FramesPerSecond * ClipSeconds), 24, 360);
        var folder = Path.Combine(Path.GetTempPath(), "datamanager-clip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var encoded = Path.Combine(folder, "encoded.mkv");
            var output = Path.Combine(Path.GetTempPath(), "datamanager-clip-" + Guid.NewGuid().ToString("N") + ".mp4");

            // Both steps encode, so both wait their turn with speed tests and previews.
            await _speedEstimator.ExclusiveAsync(
                async () =>
                {
                    await _hardware.RunAsync(
                        plan.Encoder,
                        plan.Source,
                        hw => FfmpegRunner.RunAsync(
                            _mediaEncoder.EncoderPath,
                            FfmpegArguments.BuildPreview(plan.Item.Path, encoded, plan.Position, frames, plan.Encoder, request.Quality, request.Effort, plan.Source, plan.File.ScaleTo, hw.Arguments, request.Hdr == HdrChoice.ToneMapToSdr),
                            _clipTimeout,
                            "encode the clip",
                            cancellationToken),
                        null,
                        cancellationToken).ConfigureAwait(false);
                    await _frameComparer.StackClipAsync(plan.Item, plan.Position, encoded, frames, plan.File.Output, output, cancellationToken).ConfigureAwait(false);
                    return output;
                },
                cancellationToken).ConfigureAwait(false);
            return output;
        }
        finally
        {
            RemoveFolder(folder);
        }
    }

    /// <summary>
    /// Deletes the hidden working files left in some folders by re-encodes that never finished, such as when
    /// Jellyfin restarted mid-way. Only files this plugin writes are touched, and never one a job is using or one
    /// written to recently, which another server sharing the folder could still be encoding.
    /// </summary>
    /// <param name="folders">The folders holding library files.</param>
    public void RemoveUnfinishedFiles(IEnumerable<string> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);

        var inUse = TempPathsInUse();
        var cutoff = DateTime.UtcNow - _unfinishedFileAge;
        foreach (var folder in folders.Distinct(StringComparer.Ordinal))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder, ".*" + PartialMarker + "*"))
                {
                    if (!inUse.Contains(file) && File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        _logger.LogInformation("Storage Optimizer removing unfinished file {Path}", file);
                        File.Delete(file);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Storage Optimizer could not clean up unfinished files in {Folder}", folder);
            }
        }
    }

    /// <summary>
    /// Gets the items a job is working on or waiting about, which can't be deleted until it's done.
    /// </summary>
    /// <returns>Each item's id and why it's busy.</returns>
    public IReadOnlyDictionary<Guid, string> BusyItems()
    {
        Dictionary<Guid, string> busy;
        lock (_jobsLock)
        {
            busy = _jobs
                .Where(j => j.State is EncodeJobState.Queued or EncodeJobState.Running or EncodeJobState.Paused or EncodeJobState.NeedsDecision)
                .GroupBy(j => j.ItemId)
                .ToDictionary(g => g.Key, g => g.First().State switch
                {
                    EncodeJobState.Queued => "It's waiting to be re-encoded. Cancel that first.",
                    EncodeJobState.Running or EncodeJobState.Paused => "It's being re-encoded. Cancel that first.",
                    _ => "A re-encode of it is waiting for you to choose what to do with the new file."
                });
        }

        return busy;
    }

    /// <summary>
    /// Acts on the user's choice for a new file that came out no smaller than the original.
    /// </summary>
    /// <param name="jobId">The job.</param>
    /// <param name="useNewFile">True to put the new file in place anyway, false to delete it and keep the original as it is.</param>
    /// <returns>True if the job was waiting for a choice.</returns>
    public bool Decide(Guid jobId, bool useNewFile)
    {
        EncodeJob? job;
        lock (_jobsLock)
        {
            job = _jobs.FirstOrDefault(j => j.Id == jobId && j.State == EncodeJobState.NeedsDecision);
            if (job is not null)
            {
                job.State = EncodeJobState.Running;
                job.IsPlacing = true;
            }
        }

        if (job is null)
        {
            return false;
        }

        if (!useNewFile)
        {
            job.IsPlacing = false;
            try
            {
                if (job.TempPath is not null && File.Exists(job.TempPath))
                {
                    File.Delete(job.TempPath);
                }

                Finish(job, EncodeJobState.Discarded, "Kept the original as it was, and deleted the new file, as you chose.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Finish(job, EncodeJobState.Failed, "The new file couldn't be deleted: " + ex.Message);
            }

            return true;
        }

        job.Message = "Putting the new file in place…";
        _ = Task.Run(
            async () =>
            {
                try
                {
                    var item = _libraryManager.GetItemById<Video>(job.ItemId) ?? throw new InvalidOperationException("The item no longer exists in the library. The new file is still there, hidden, as " + job.TempPath + ".");
                    await PlaceAsync(job, item, _shutdown.Token).ConfigureAwait(false);
                }
#pragma warning disable CA1031 // Reported on the job.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    _logger.LogError(ex, "Storage Optimizer could not put the new file for {Path} in place", job.SourcePath);
                    Finish(job, EncodeJobState.Failed, ex.Message);
                }
            },
            CancellationToken.None);
        return true;
    }

    // Hidden files that jobs are writing, or holding until the user chooses.
    private HashSet<string> TempPathsInUse()
    {
        HashSet<string> paths;
        lock (_jobsLock)
        {
            paths = _jobs.Where(j => j.State is (EncodeJobState.Running or EncodeJobState.Paused or EncodeJobState.NeedsDecision) && j.TempPath is not null)
                .Select(j => j.TempPath!)
                .ToHashSet(StringComparer.Ordinal);
        }

        return paths;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
        _signal.Dispose();
    }

    // What a preview or clip needs: the video, the encoder, the size and where to start.
    private async Task<PreviewPlan> PlanPreviewAsync(EncodePreviewRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var item = _libraryManager.GetItemById<Video>(request.ItemId);
        if (item is null || !File.Exists(item.Path))
        {
            throw new FileNotFoundException("That video is no longer in the library. Scan again to refresh the list.");
        }

        var encoder = PickEncoder(request.Codec, out _) ?? throw new InvalidOperationException("This server's ffmpeg can't encode " + Label(request.Codec) + ".");
        var sourceInfo = SourceInfo(item, new FileInfo(item.Path));
        CheckVideo(sourceInfo, request.Codec, request.Hdr);

        var (file, audioBitrate) = Describe(item);
        if (request.Resolution == ResolutionChoice.Auto && _resolutionAnalyzer.GetCached(item) is null)
        {
            await _resolutionAnalyzer.AnalyseAsync(item.Id, cancellationToken).ConfigureAwait(false);
        }

        var speedFile = SpeedFile(item, file, audioBitrate, TargetSize(item, file, request.Resolution, out _));
        var position = Math.Clamp(request.PositionSeconds, 0, Math.Max(file.DurationSeconds - ClipSeconds - 1, 0));
        return new PreviewPlan(item, encoder, sourceInfo, speedFile, position);
    }

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

    private CodecOption Option(TargetCodec codec, string label, string summary, string compatibility)
    {
        var encoder = PickEncoder(codec, out var isHardware);
        return new CodecOption(
            codec,
            label,
            encoder ?? "not available",
            isHardware,
            summary,
            compatibility,
            encoder is null ? "This server's ffmpeg has no encoder for this codec." : null,
            encoder is null ? [] : FfmpegArguments.Efforts(encoder));
    }

    private string? PickEncoder(TargetCodec codec, out bool isHardware) => _hardware.PickEncoder(codec, out isHardware);

    /// <summary>
    /// Works out how many encodes the settings allow at once, and what is being done about viewers.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The live status.</returns>
    public async Task<EncodeStatus> StatusAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var slots = await SlotsAsync(config, cancellationToken).ConfigureAwait(false);
        var streams = _viewers.Streams(config.OnlyCountVideoTranscodes);
        return BuildStatus(config, slots, streams, HeldStreams(config, streams.Count, false));
    }

    /// <summary>
    /// Lets a job that was paused for a viewer carry on now, although they are still watching.
    /// </summary>
    /// <param name="jobId">The job.</param>
    /// <returns>True if the job was paused.</returns>
    public bool Resume(Guid jobId)
    {
        EncodeJob? job;
        lock (_jobsLock)
        {
            job = _jobs.FirstOrDefault(j => j.Id == jobId && j.State == EncodeJobState.Paused);
        }

        if (job is null)
        {
            return false;
        }

        job.ResumeOverride = true;
        Thaw(job);
        Wake();
        return true;
    }

    /// <summary>
    /// Holds every encode while something needs the GPU to itself, such as the GPU test. Encodes carry on from the same frame afterwards.
    /// </summary>
    /// <param name="reason">What is using the GPU, such as "the GPU test".</param>
    /// <returns>Something to dispose of when done, which lets encoding carry on.</returns>
    internal IDisposable HoldEncoding(string reason)
    {
        _testHold = reason;
        Wake();
        return new Hold(this);
    }

    /// <summary>
    /// Gets how many encodes are using the GPU now, not counting any that are paused. A remux copies the video, so it doesn't count.
    /// </summary>
    /// <returns>The number running.</returns>
    internal int RunningCount()
    {
        lock (_jobsLock)
        {
            return _jobs.Count(j => j.State == EncodeJobState.Running && !j.IsRemux && j.Process is not null);
        }
    }

    private void Wake()
    {
        if (_signal.CurrentCount == 0)
        {
            try
            {
                _signal.Release();
            }
            catch (ObjectDisposedException)
            {
                // Shutting down.
            }
        }
    }

    private async Task RunDispatcherAsync(CancellationToken shutdown)
    {
        while (!shutdown.IsCancellationRequested)
        {
            try
            {
                await DispatchAsync(shutdown).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
#pragma warning disable CA1031 // One bad look at the queue must not stop it for good.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogError(ex, "Storage Optimizer could not manage its encode queue");
            }

            bool active;
            lock (_jobsLock)
            {
                active = _jobs.Any(j => j.State is EncodeJobState.Queued or EncodeJobState.Running or EncodeJobState.Paused);
            }

            try
            {
                // Asleep until something changes, or while there are encodes, until it's time to look at who is watching.
                await _signal.WaitAsync(active ? _dispatchEvery : Timeout.InfiniteTimeSpan, shutdown).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    // The number of encodes the settings allow, which is the recommended number unless one was chosen.
    private async Task<int> SlotsAsync(PluginConfiguration config, CancellationToken cancellationToken)
    {
        if (config.EncodesAtOnce > 0)
        {
            return Math.Clamp(config.EncodesAtOnce, 1, 8);
        }

        return GpuParsing.Recommended(await _gpu.DetectAsync(cancellationToken).ConfigureAwait(false), config);
    }

    // How many streams to make room for. A stream that ends is still counted until nobody has been watching for the wait the settings give.
    private int HeldStreams(PluginConfiguration config, int current, bool update)
    {
        var wait = TimeSpan.FromSeconds(Math.Clamp(config.ResumeAfterSeconds, 0, 3600));
        var now = DateTime.UtcNow;
        lock (_viewerLock)
        {
            var held = _heldStreams;
            if (current >= held)
            {
                held = current;
                if (update)
                {
                    _heldStreams = current;
                    _heldAt = now;
                }
            }
            else if (now - _heldAt >= wait)
            {
                held = current;
                if (update)
                {
                    _heldStreams = current;
                    _heldAt = now;
                }
            }

            return held;
        }
    }

    private static int Allowed(PluginConfiguration config, int slots, int streams) => config.WhenWatching switch
    {
        WatchingPolicy.KeepEncoding => slots,
        WatchingPolicy.Pause => streams > 0 ? 0 : slots,
        _ => Math.Max(0, slots - streams)
    };

    private EncodeStatus BuildStatus(PluginConfiguration config, int slots, IReadOnlyList<LiveTranscode> streams, int held)
    {
        var allowed = _testHold is not null ? 0 : Allowed(config, slots, held);
        int running, paused, queued;
        lock (_jobsLock)
        {
            running = _jobs.Count(j => j.State == EncodeJobState.Running);
            paused = _jobs.Count(j => j.State == EncodeJobState.Paused);
            queued = _jobs.Count(j => j.State == EncodeJobState.Queued);
        }

        string summary;
        if (_testHold is not null)
        {
            summary = "Encoding is paused while " + _testHold + " runs.";
        }
        else if (streams.Count == 0)
        {
            summary = "Nobody is transcoding right now.";
        }
        else
        {
            var who = ViewerMonitor.People(streams);
            var verb = streams.Select(s => s.User).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1 ? "is" : "are";
            var what = streams.Count == 1 && streams[0].Title.Length > 0 ? " " + streams[0].Title + (streams[0].Device.Length > 0 ? " on " + streams[0].Device : string.Empty) : string.Empty;
            summary = who + " " + verb + " transcoding" + what + "." + (config.WhenWatching == WatchingPolicy.KeepEncoding
                ? " Encoding carries on as normal."
                : running + paused > 0 || queued > 0 ? " Encoding is using " + running + " of " + slots + " slots." : string.Empty);
        }

        return new EncodeStatus(slots, allowed, running, paused, queued, config.WhenWatching, streams, summary);
    }

    private async Task DispatchAsync(CancellationToken shutdown)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var slots = await SlotsAsync(config, shutdown).ConfigureAwait(false);
        var streams = _viewers.Streams(config.OnlyCountVideoTranscodes);
        var held = HeldStreams(config, streams.Count, true);
        var status = BuildStatus(config, slots, streams, held);
        var allowed = status.Allowed;

        List<EncodeJob> running, paused, queued;
        lock (_jobsLock)
        {
            running = _jobs.Where(j => j.State == EncodeJobState.Running).ToList();
            paused = _jobs.Where(j => j.State == EncodeJobState.Paused).OrderBy(j => j.StartedAt).ToList();
            queued = _jobs.Where(j => j.State == EncodeJobState.Queued).OrderBy(j => j.QueuedAt).ToList();
        }

        // Too many are going: freeze the ones that began last, as far as they can be frozen. A job that's checking
        // its file or putting its result in place has nothing to freeze and is left to finish.
        var active = running.Count;
        var why = _testHold ?? ViewerMonitor.People(streams);
        foreach (var job in running.Where(j => !j.IsRemux).OrderByDescending(j => j.StartedAt))
        {
            if (active <= allowed)
            {
                break;
            }

            if (Freeze(job, why, _testHold is not null))
            {
                active--;
            }
        }

        // Then room: frozen ones carry on first, oldest first, and then the queue starts.
        foreach (var job in paused)
        {
            if (active < allowed || job.ResumeOverride)
            {
                Thaw(job);
                active++;
            }
            else
            {
                job.Message = PauseMessage(job, streams, held, config);
            }
        }

        foreach (var job in queued)
        {
            if (active >= allowed)
            {
                if (job.Message != "Waiting for a free slot.")
                {
                    job.Message = "Waiting for a free slot.";
                }

                continue;
            }

            StartJob(job);
            active++;
        }
    }

    private void StartJob(EncodeJob job)
    {
        lock (_jobsLock)
        {
            if (job.State != EncodeJobState.Queued)
            {
                return;
            }

            job.State = EncodeJobState.Running;
            job.StartedAt = DateTime.UtcNow;
            job.Message = null;
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await RunQueuedJobAsync(job, _shutdown.Token).ConfigureAwait(false);
                }
                finally
                {
                    Wake();
                }
            },
            CancellationToken.None);
    }

    private bool Freeze(EncodeJob job, string why, bool forTest)
    {
        var process = job.Process;
        if (process is null || job.IsPlacing || job.ResumeOverride || !process.Freeze())
        {
            return false;
        }

        job.NotePaused();
        job.MessageBeforePause = job.Message;
        job.PausedFor = why;
        job.State = EncodeJobState.Paused;
        job.RemainingSeconds = null;
        job.Message = forTest
            ? string.Create(CultureInfo.InvariantCulture, $"Paused at {job.Progress:F0}% while {why} runs. It carries on when that's done.")
            : string.Create(CultureInfo.InvariantCulture, $"Paused at {job.Progress:F0}% while {why} {(why.Contains(" and ", StringComparison.Ordinal) || why.EndsWith("people", StringComparison.Ordinal) ? "are" : "is")} transcoding. It carries on when they finish.");
        _logger.LogInformation("Storage Optimizer paused the re-encode of {Path} at {Progress:F0}% for {Why}", job.SourcePath, job.Progress, why);
        return true;
    }

    private void Thaw(EncodeJob job)
    {
        if (job.State != EncodeJobState.Paused)
        {
            return;
        }

        job.Process?.Thaw();
        job.NoteResumed();
        job.PausedFor = null;
        job.State = EncodeJobState.Running;
        job.Message = job.MessageBeforePause;
        _logger.LogInformation("Storage Optimizer carried on with the re-encode of {Path} from {Progress:F0}%", job.SourcePath, job.Progress);
    }

    // What a paused job says: who it's paused for, or that it's about to carry on.
    private static string PauseMessage(EncodeJob job, IReadOnlyList<LiveTranscode> streams, int held, PluginConfiguration config)
    {
        var progress = string.Create(CultureInfo.InvariantCulture, $"Paused at {job.Progress:F0}%");
        if (streams.Count == 0 && held > 0)
        {
            var wait = config.ResumeAfterSeconds >= 120 ? config.ResumeAfterSeconds / 60 + " minutes" : config.ResumeAfterSeconds >= 60 ? "a minute" : config.ResumeAfterSeconds + " seconds";
            return progress + ". Nobody is watching now. It carries on once that's been so for " + wait + ".";
        }

        if (streams.Count == 0)
        {
            return progress + ". It carries on in a moment.";
        }

        var who = ViewerMonitor.People(streams);
        var verb = streams.Select(s => s.User).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1 ? "is" : "are";
        return progress + " while " + who + " " + verb + " transcoding. It carries on when they finish.";
    }

    private async Task RunQueuedJobAsync(EncodeJob job, CancellationToken shutdown)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(job.Cancellation.Token, shutdown);
        try
        {
            await RunJobAsync(job, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Finish(job, EncodeJobState.Cancelled, "Cancelled. The original file was not changed.");
        }
#pragma warning disable CA1031 // One failed job must not stop the queue.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogError(ex, "Storage Optimizer re-encode of {Path} failed", job.SourcePath);
            Finish(job, EncodeJobState.Failed, ex.Message);
        }
    }

    private sealed class Hold : IDisposable
    {
        private readonly EncodeService _service;

        public Hold(EncodeService service)
        {
            _service = service;
        }

        public void Dispose()
        {
            _service._testHold = null;
            _service.Wake();
        }
    }

    private async Task RunJobAsync(EncodeJob job, CancellationToken cancellationToken)
    {
        var item = _libraryManager.GetItemById<Video>(job.ItemId) ?? throw new InvalidOperationException("The item no longer exists in the library.");
        var source = new FileInfo(item.Path);
        if (!source.Exists)
        {
            throw new FileNotFoundException("The source file is missing.", item.Path);
        }

        job.SourceBytes = source.Length;
        if (job.IsRemux)
        {
            await RunRemuxAsync(job, item, source, cancellationToken).ConfigureAwait(false);
            return;
        }

        var encoder = PickEncoder(job.Codec, out _) ?? throw new InvalidOperationException("This server's ffmpeg can't encode " + job.Codec + ".");
        job.Encoder = encoder;

        var sourceInfo = SourceInfo(item, source);
        CheckVideo(sourceInfo, job.Codec, job.Hdr);

        var (file, audioBitrate) = Describe(item);
        if (job.Resolution == ResolutionChoice.Auto && _resolutionAnalyzer.GetCached(item) is null)
        {
            job.Message = "Checking how much detail the picture holds…";
            await _resolutionAnalyzer.AnalyseAsync(item.Id, cancellationToken).ConfigureAwait(false);
        }

        var size = TargetSize(item, file, job.Resolution, out _);
        var speedFile = SpeedFile(item, file, audioBitrate, size);
        job.SpeedKey = SpeedEstimator.Key(encoder, job.Effort, job.Quality, (speedFile.Width, speedFile.Height), size);
        var scaleTo = speedFile.ScaleTo;
        job.ScaledTo = scaleTo is null ? null : Resolutions.Label(size.Width, size.Height);

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var folder = source.DirectoryName!;
        var container = Container(job.Mode, sourceInfo);
        var finalPath = PlanPath(item, job.Mode, job.Codec, size, job.ScaledTo, container, config);
        job.PlannedPath = finalPath;
        CheckNameFree(job, source, finalPath);
        var tracks = await ChooseTracksAsync(job, item, source, cancellationToken).ConfigureAwait(false);
        var extras = await PlanExtrasAsync(job, source, sourceInfo, container, tracks, cancellationToken).ConfigureAwait(false);
        var keptAudioBitrate = tracks is null ? (long?)null : tracks.KeptAudioBitRate - AudioSaving(tracks, extras);

        // The new file is written next to the original, but hidden, so Jellyfin ignores it until it's finished.
        var tempPath = Path.Combine(folder, "." + Path.GetFileNameWithoutExtension(source.Name) + PartialMarker + container);
        job.TempPath = tempPath;
        var expectedVideoBitrate = SavingsEstimator.EstimateVideoBitrate(file, audioBitrate, job.Codec, size.Width, size.Height, job.Quality, job.Effort);
        CheckFreeSpace(folder, job, SavingsEstimator.EstimateSize(file, audioBitrate, job.Codec, size.Width, size.Height, job.Quality, job.Effort, _speedEstimator.VideoScale(job.SpeedKey).Scale, keptAudioBitrate, tracks?.KeptSubtitleBitRate ?? 0));

        string? tagsPath = null;
        var encoded = false;
        try
        {
            var tags = await WriteTagsAsync(job, item, container, extras, config, cancellationToken).ConfigureAwait(false);
            tagsPath = tags?.TagsPath;

            job.Message = "Encoding with " + encoder + (extras.ToneMap ? ", turning HDR into SDR…" : "…");
            var duration = TimeSpan.FromTicks(item.RunTimeTicks ?? 0);
            _speedEstimator.CancelTests();
            await _hardware.RunAsync(
                encoder,
                sourceInfo,
                async hw =>
                {
                    if (hw.Decoder is not null)
                    {
                        job.Message = "Encoding with " + encoder + ", decoding on the GPU…";
                    }

                    var args = FfmpegArguments.Build(source.FullName, tempPath, encoder, job.Quality, job.Effort, sourceInfo, container, scaleTo, tags, hw.Arguments, tracks, extras);
                    await RunFfmpegAsync(args, duration, (long)size.Width * size.Height, job, cancellationToken).ConfigureAwait(false);
                    return true;
                },
                () =>
                {
                    job.Progress = 0;
                    job.RemainingSeconds = null;
                    job.Message = "The GPU couldn't decode this file, so decoding on the CPU and encoding with " + encoder + "…";
                },
                cancellationToken).ConfigureAwait(false);

            // The new file's video, without the copied audio, against what the rule of thumb expected. Tone mapping
            // slows encoding down, so its speed would mislead estimates for plain encodes and isn't kept.
            if (!extras.ToneMap)
            {
                var copiedBitrate = tracks is null ? SavingsEstimator.AudioBitrate(audioBitrate) : keptAudioBitrate!.Value + tracks.KeptSubtitleBitRate;
                var videoBitrate = file.DurationSeconds > 0 ? (new FileInfo(tempPath).Length * 8 / file.DurationSeconds) - copiedBitrate : 0;
                await _speedEstimator.RecordEncodeAsync(
                    job.SpeedKey,
                    SpeedEstimator.AverageBusyness(speedFile, file.SizeBytes),
                    job.PixelRate,
                    videoBitrate > 0 && expectedVideoBitrate > 0 ? videoBitrate / expectedVideoBitrate : null).ConfigureAwait(false);
            }

            job.Message = "Checking the new file…";
            await VerifyAsync(source.FullName, tempPath, cancellationToken).ConfigureAwait(false);
            encoded = true;
            job.OutputBytes = new FileInfo(tempPath).Length;

            // A new file that isn't smaller isn't worth having, but nothing is deleted without asking: the original
            // stays as it is and the new file stays hidden until the user chooses.
            if (job.OutputBytes > job.SourceBytes * (1 - MinimumSaving))
            {
                var change = (job.OutputBytes.Value - job.SourceBytes) * 100.0 / job.SourceBytes;
                var how = change >= 0.5
                    ? string.Create(CultureInfo.InvariantCulture, $"{change:F0}% bigger than")
                    : change > -0.5 ? "the same size as" : string.Create(CultureInfo.InvariantCulture, $"only {-change:F0}% smaller than");
                job.State = EncodeJobState.NeedsDecision;
                job.Message = "The new file came out " + FormatBytes(job.OutputBytes.Value) + ", " + how + " the original, so nothing has been changed. Choose whether to use it anyway or delete it." + (job.TracksNote is null ? string.Empty : " " + job.TracksNote);
                _logger.LogInformation("Storage Optimizer re-encode of {Path} came out no smaller; waiting for the user to choose", job.SourcePath);
                return;
            }
        }
        finally
        {
            DeleteQuietly(tagsPath);
            // An encode that failed, was cancelled or came out broken leaves a file that can't be used.
            if (!encoded && File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }

        await PlaceAsync(job, item, cancellationToken).ConfigureAwait(false);
    }

    // Removes tracks without touching the video: the video and the tracks kept are copied as they are.
    private async Task RunRemuxAsync(EncodeJob job, Video item, FileInfo source, CancellationToken cancellationToken)
    {
        job.Encoder = FfmpegArguments.CopyVideo;
        var sourceInfo = SourceInfo(item, source);
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var folder = source.DirectoryName!;
        var container = Container(job.Mode, sourceInfo);
        var finalPath = PlanPath(item, job.Mode, job.Codec, (0, 0), null, container, config, remux: true);
        job.PlannedPath = finalPath;
        CheckNameFree(job, source, finalPath);

        if (job.Hdr == HdrChoice.ToneMapToSdr)
        {
            throw new InvalidOperationException("Turning HDR into SDR changes the picture, so it needs a re-encode rather than a remux. The original was not changed.");
        }

        var tracks = await ChooseTracksAsync(job, item, source, cancellationToken).ConfigureAwait(false);
        var extras = await PlanExtrasAsync(job, source, sourceInfo, container, tracks, cancellationToken).ConfigureAwait(false);
        var removesAttachments = extras.Attachments is not null && extras.Attachments.Count < extras.SourceAttachmentCount;
        if ((tracks is null || tracks.Removed.Count == 0) && extras.Audio.Count == 0 && extras.DolbyVisionStrip is null && !removesAttachments)
        {
            throw new InvalidOperationException("There's nothing to change: these choices keep every track and attachment in this file as it is. The original was not changed.");
        }

        var tempPath = Path.Combine(folder, "." + Path.GetFileNameWithoutExtension(source.Name) + PartialMarker + container);
        job.TempPath = tempPath;
        var duration = TimeSpan.FromTicks(item.RunTimeTicks ?? 0);
        var removedBitrate = (tracks?.RemovedBitRate ?? 0) + (tracks is null ? 0 : AudioSaving(tracks, extras));
        CheckFreeSpace(folder, job, Math.Max(source.Length - (long)(removedBitrate * duration.TotalSeconds / 8), 0));

        string? tagsPath = null;
        var finished = false;
        try
        {
            var tags = await WriteTagsAsync(job, item, container, extras, config, cancellationToken).ConfigureAwait(false);
            tagsPath = tags?.TagsPath;
            job.Message = extras.Audio.Count > 0 ? "Copying the video and converting the audio…" : "Copying the video and the tracks kept…";
            var args = FfmpegArguments.Build(source.FullName, tempPath, FfmpegArguments.CopyVideo, job.Quality, job.Effort, sourceInfo, container, null, tags, null, tracks, extras);
            await RunFfmpegAsync(args, duration, 0, job, cancellationToken).ConfigureAwait(false);

            job.Message = "Checking the new file…";
            await VerifyAsync(source.FullName, tempPath, cancellationToken).ConfigureAwait(false);
            finished = true;
            job.OutputBytes = new FileInfo(tempPath).Length;

            // Removing tracks always makes a file smaller, unless Jellyfin's details added more than was removed.
            if (job.OutputBytes >= job.SourceBytes)
            {
                job.State = EncodeJobState.NeedsDecision;
                job.Message = "The new file came out " + FormatBytes(job.OutputBytes.Value) + ", no smaller than the original, so nothing has been changed. Choose whether to use it anyway or delete it. " + job.TracksNote;
                return;
            }
        }
        finally
        {
            DeleteQuietly(tagsPath);
            if (!finished && File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }

        await PlaceAsync(job, item, cancellationToken).ConfigureAwait(false);
    }

    private static void CheckNameFree(EncodeJob job, FileInfo source, string finalPath)
    {
        if (job.Mode == OutputMode.KeepBoth && File.Exists(finalPath))
        {
            throw new InvalidOperationException("A file named " + Path.GetFileName(finalPath) + " already exists.");
        }

        if (job.Mode == OutputMode.Replace && File.Exists(finalPath) && !string.Equals(finalPath, source.FullName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A different file named " + Path.GetFileName(finalPath) + " already exists.");
        }
    }

    // Which audio and subtitle tracks to keep, from Jellyfin's list of them, which the choices were made from. The
    // file itself is checked to have the same tracks at the same numbers, so the right ones are removed.
    private async Task<TrackPlan?> ChooseTracksAsync(EncodeJob job, Video item, FileInfo source, CancellationToken cancellationToken)
    {
        // Converting audio needs the list of tracks too, to know which ones to convert.
        if (job.Audio == AudioChoice.KeepAll && job.Subtitles == SubtitleChoice.KeepAll && job.AudioFormat == AudioFormat.Keep && !job.StereoAudio)
        {
            return null;
        }

        var listed = _languages.Tracks(item.GetMediaStreams());
        var probed = await ProbeTracksAsync(source.FullName, cancellationToken).ConfigureAwait(false);
        if (listed.Count != probed.Count || listed.Any(t => !probed.Any(p => p.Index == t.Index && p.Kind == t.Kind)))
        {
            throw new InvalidOperationException("Jellyfin's list of this file's audio and subtitle tracks doesn't match the file. Refresh the item's metadata in Jellyfin and try again. The original was not changed.");
        }

        var tracks = TrackSelection.Choose(listed, job.Audio, job.Subtitles, job.Languages, job.ExcludedTracks, job.IncludedTracks);
        job.TracksNote = tracks.Describe();
        return tracks;
    }

    // Jellyfin's details go into the file only when the settings say so.
    private async Task<EmbeddedTags?> WriteTagsAsync(EncodeJob job, Video item, string container, EncodeExtras extras, PluginConfiguration config, CancellationToken cancellationToken)
    {
        if (!config.WriteMetadata)
        {
            return null;
        }

        var mediaTags = ItemDetails.Tags(item, _libraryManager, container == "mp4");
        var tagsPath = Path.Combine(Path.GetTempPath(), "datamanager-tags-" + job.Id.ToString("N", CultureInfo.InvariantCulture) + ".txt");
        await File.WriteAllTextAsync(tagsPath, mediaTags.ToFfmetadata(), cancellationToken).ConfigureAwait(false);
        return new EmbeddedTags(tagsPath, mediaTags.Images, extras.Attachments?.Count ?? 0);
    }

    // The audio to convert, the attachments to keep, and what happens to HDR, with a note of what changes.
    private async Task<EncodeExtras> PlanExtrasAsync(EncodeJob job, FileInfo source, SourceVideo sourceInfo, string container, TrackPlan? tracks, CancellationToken cancellationToken)
    {
        var notes = new List<string>();
        var audio = tracks is null ? [] : AudioConverter.PlanAll(tracks.Kept, job.AudioFormat, job.StereoAudio);
        if (audio.Count > 0 && job.AudioFormat == AudioFormat.Opus && !_mediaEncoder.SupportsEncoder("libopus"))
        {
            throw new InvalidOperationException("This server's ffmpeg can't encode Opus. Choose Dolby Digital Plus or AAC. The original was not changed.");
        }

        foreach (var group in audio.GroupBy(a => a.Label))
        {
            notes.Add("Converted " + group.Count() + (group.Count() == 1 ? " audio track" : " audio tracks") + " to " + group.Key + ".");
        }

        // Every attachment is listed, so fonts and pictures can be kept or left out one by one. Fonts go only when
        // asked, or when no subtitles that use them are left; pictures only when asked.
        // Fonts and other files attached to an mkv are copied, unless left out. Cover art inside an mkv reads as a
        // picture stream that ffmpeg can't write back as an attachment, so it's never copied (Jellyfin keeps its own
        // artwork beside the file); Jellyfin's pictures are written in instead when the settings say so, unless
        // cover art is left out.
        IReadOnlyList<int>? keptAttachments = null;
        var sourceAttachments = 0;
        if (sourceInfo.IsMatroska && container == "mkv")
        {
            var attachments = (await ProbeAttachmentsAsync(source.FullName, cancellationToken).ConfigureAwait(false)).Where(a => !a.IsAttachedPicture).ToList();
            sourceAttachments = attachments.Count;
            var fontsWanted = !job.StripFonts && (tracks is null || tracks.RemovedSubtitles == 0 || tracks.NeedsFonts);
            var kept = attachments.Where(a => a.IsImage ? !job.StripCoverArt : fontsWanted).ToList();
            keptAttachments = kept.Select(a => a.Index).ToList();
            var fonts = attachments.Count(a => a.IsFont && !kept.Contains(a));
            if (job.StripFonts && fonts > 0)
            {
                notes.Add("Left out " + fonts + (fonts == 1 ? " font." : " fonts."));
            }
        }

        string? strip = null;
        if (job.Hdr == HdrChoice.RemoveDolbyVision && sourceInfo.DolbyVisionProfile is not null)
        {
            if (sourceInfo.DolbyVisionOnly)
            {
                throw new InvalidOperationException("This video is only Dolby Vision, with no HDR10 picture beneath it, so removing Dolby Vision would leave the colours wrong. The original was not changed.");
            }

            strip = DolbyVisionStripFilter();
            if (strip is null && job.IsRemux)
            {
                throw new InvalidOperationException("This server's ffmpeg can't remove Dolby Vision. Jellyfin 10.11's ffmpeg can. The original was not changed.");
            }

            // Re-encoding never keeps the enhancement layer, so without the filter only the metadata might stay.
            if (strip is not null)
            {
                notes.Add(sourceInfo.DolbyVisionLayer ? "Removed Dolby Vision and its enhancement layer." : "Removed Dolby Vision.");
            }
        }

        var toneMap = job.Hdr == HdrChoice.ToneMapToSdr && sourceInfo.IsHdr && !job.IsRemux;
        if (toneMap)
        {
            notes.Add("Turned HDR into SDR.");
        }

        if (notes.Count > 0)
        {
            job.TracksNote = string.Join(" ", new[] { job.TracksNote }.Concat(notes).Where(n => !string.IsNullOrEmpty(n)));
        }

        return new EncodeExtras(audio, toneMap, strip, keptAttachments, job.StripCoverArt, sourceAttachments);
    }

    // The bitstream filter that removes Dolby Vision: FFmpeg's own, or jellyfin-ffmpeg's older one, if either is there.
    private string? DolbyVisionStripFilter()
    {
        if (_mediaEncoder.SupportsBitStreamFilterWithOption(BitStreamFilterOptionType.DoviRpuStrip))
        {
            return "dovi_rpu=strip=1";
        }

        return _mediaEncoder.SupportsBitStreamFilterWithOption(BitStreamFilterOptionType.HevcMetadataRemoveDovi) ? "hevc_metadata=remove_dovi=1" : null;
    }

    // What converting audio saves, in bits per second.
    private static long AudioSaving(TrackPlan tracks, EncodeExtras extras)
        => extras.Audio.Sum(c => tracks.Kept.First(t => t.Index == c.Index).EffectiveBitRate - c.BitRate);

    // Dolby Atmos and DTS:X carry height channels that converting loses.
    private static bool IsObjectAudio(TrackInfo track)
        => (track.Profile?.Contains("atmos", StringComparison.OrdinalIgnoreCase) ?? false)
            || (track.Profile?.Contains("DTS:X", StringComparison.OrdinalIgnoreCase) ?? false)
            || (track.Title?.Contains("atmos", StringComparison.OrdinalIgnoreCase) ?? false);

    // Checks the video can be encoded with these choices.
    private static void CheckVideo(SourceVideo source, TargetCodec codec, HdrChoice hdr)
    {
        if (source.DolbyVisionOnly)
        {
            throw new InvalidOperationException("This video is only Dolby Vision, with no HDR10 picture beneath it. Re-encoding it would leave the colours wrong, so it's left as it is.");
        }

        if (source.IsHdr && codec == TargetCodec.H264 && hdr != HdrChoice.ToneMapToSdr)
        {
            throw new InvalidOperationException("H.264 can't keep HDR. Choose HEVC or AV1 for HDR files, or turn HDR into SDR.");
        }
    }

    // Moves a finished, checked file into place and tells the library.
    private async Task PlaceAsync(EncodeJob job, Video item, CancellationToken cancellationToken)
    {
        var source = job.SourcePath;
        var finalPath = job.PlannedPath ?? throw new InvalidOperationException("The new file's name was never worked out.");
        var tempPath = job.TempPath ?? throw new InvalidOperationException("The new file is missing.");
        if (!File.Exists(tempPath))
        {
            throw new FileNotFoundException("The new file is missing.", tempPath);
        }

        job.IsPlacing = true;
        job.Message = "Putting the new file in place…";
        var folder = Path.GetDirectoryName(source)!;
        var oldBase = Path.GetFileNameWithoutExtension(source);
        var newBase = Path.GetFileNameWithoutExtension(finalPath);
        var replaceInPlace = string.Equals(finalPath, source, StringComparison.Ordinal);

        // A renamed original's subtitles, artwork and .nfo are renamed to match it.
        var sidecars = job.Mode == OutputMode.Replace && !string.Equals(oldBase, newBase, StringComparison.Ordinal) ? Sidecars.Find(source) : [];

        _libraryMonitor.ReportFileSystemChangeBeginning(finalPath);
        if (job.Mode == OutputMode.Replace && !replaceInPlace)
        {
            _libraryMonitor.ReportFileSystemChangeBeginning(source);
        }

        try
        {
            if (job.Mode == OutputMode.Replace && !replaceInPlace && File.Exists(finalPath))
            {
                throw new InvalidOperationException("A different file named " + Path.GetFileName(finalPath) + " appeared while encoding. The new file is still there, hidden, as " + tempPath + ".");
            }

            // With the same name, the new file takes the original's place in one step. With a new name it's moved in
            // first, so the original is only deleted once the new file is there. Replace was the user's choice.
            try
            {
                File.Move(tempPath, finalPath, replaceInPlace);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException("The new file couldn't be moved into place (" + ex.Message + "). It's still there, hidden, as " + tempPath + ", and the original wasn't changed.", ex);
            }

            if (job.Mode == OutputMode.Replace && !replaceInPlace)
            {
                try
                {
                    File.Delete(source);
                    foreach (var sidecar in sidecars)
                    {
                        var renamed = Sidecars.Renamed(sidecar, oldBase, newBase);
                        if (!File.Exists(renamed))
                        {
                            File.Move(sidecar, renamed);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new InvalidOperationException("The new file is in place as " + Path.GetFileName(finalPath) + ", but the original or its subtitles couldn't be changed: " + ex.Message, ex);
                }
            }
        }
        finally
        {
            _libraryMonitor.ReportFileSystemChangeComplete(finalPath, false);
            if (job.Mode == OutputMode.Replace && !replaceInPlace)
            {
                _libraryMonitor.ReportFileSystemChangeComplete(source, false);
            }
        }

        job.OutputPath = finalPath;
        job.Message = "Updating the library…";
        await RefreshAsync(item, folder, cancellationToken).ConfigureAwait(false);

        job.OutputItemId = job.Mode == OutputMode.KeepBoth ? await LinkAsVersionAsync(item, finalPath, cancellationToken).ConfigureAwait(false) : null;
        try
        {
            await _reports.EncodeFinishedAsync(item.Id, source, finalPath, job.OutputItemId).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The new file is in place either way, and the next scan picks it up.
            _logger.LogWarning(ex, "Storage Optimizer couldn't add {Path} to the storage report", finalPath);
        }

        var saved = job.SourceBytes - (job.OutputBytes ?? 0);
        var savedText = saved > 0 ? "saved " + FormatBytes(saved) : "saved nothing";
        if (job.Mode == OutputMode.KeepBoth)
        {
            Finish(job, EncodeJobState.Completed, "Added as a new version" + (saved > 0 ? ". Deleting the original later would save " + FormatBytes(saved) + "." : ".") + (job.TracksNote is null ? string.Empty : " " + job.TracksNote));
        }
        else
        {
            Finish(job, EncodeJobState.Completed, "Replaced the original and " + savedText + "." + (job.TracksNote is null ? string.Empty : " " + job.TracksNote));
        }

        job.IsPlacing = false;
    }

    // The new file's container: mp4 only when replacing an mp4, so its players keep working; otherwise mkv, which holds everything.
    private static string Container(OutputMode mode, SourceVideo source) => mode == OutputMode.Replace && source.IsMp4 ? "mp4" : "mkv";

    // Where the new file goes: beside the original, named by the settings, with the codec and size after a dash
    // when it's kept as another version, as Jellyfin's docs name versions.
    private string PlanPath(Video item, OutputMode mode, TargetCodec codec, (int Width, int Height) size, string? scaledTo, string container, PluginConfiguration config, bool remux = false)
    {
        var folder = Path.GetDirectoryName(item.Path)!;
        var currentBase = Path.GetFileNameWithoutExtension(item.Path);

        // A remux keeps the file's name: the picture is unchanged. Kept beside the original, it's marked as a remux.
        if (remux)
        {
            return Path.Combine(folder, currentBase + (mode == OutputMode.KeepBoth ? " - Remux" : string.Empty) + "." + container);
        }

        var parts = ItemDetails.NameParts(item, _libraryManager, Resolutions.Label(size.Width, size.Height), Label(codec));
        var baseName = FileNaming.BaseName(config, parts) ?? currentBase;

        // A name that only changes case is left alone: on some drives it's the same file, on others a different one.
        if (string.Equals(baseName, currentBase, StringComparison.OrdinalIgnoreCase))
        {
            baseName = currentBase;
        }

        var versionName = Label(codec) + (scaledTo is null ? string.Empty : " " + scaledTo);
        return mode == OutputMode.KeepBoth
            ? Path.Combine(folder, baseName + " - " + versionName + "." + container)
            : Path.Combine(folder, baseName + "." + container);
    }

    private List<string> NormalizeLanguages(IEnumerable<string> languages)
        => languages.Select(_languages.Normalize).OfType<string>().Distinct(StringComparer.Ordinal).ToList();

    // A file's audio and subtitle tracks, as ffprobe numbers them.
    private async Task<IReadOnlyList<TrackInfo>> ProbeTracksAsync(string path, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(_mediaEncoder.ProbePath)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in new[] { "-v", "error", "-show_entries", "stream=index,codec_type,codec_name,channels,bit_rate:stream_tags:stream_disposition=default,forced", "-of", "json", path })
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start ffprobe.");
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("ffprobe couldn't read the file's audio and subtitle tracks.");
        }

        return ParseTracks(output, _languages.Normalize);
    }

    /// <summary>
    /// Reads the audio and subtitle tracks from ffprobe's JSON.
    /// </summary>
    /// <param name="json">ffprobe's output with -show_entries stream=index,codec_type,codec_name,channels,bit_rate:stream_tags:stream_disposition.</param>
    /// <param name="normalize">Turns a language tag into the code it's matched by.</param>
    /// <returns>The tracks.</returns>
    internal static IReadOnlyList<TrackInfo> ParseTracks(string json, Func<string?, string?> normalize)
    {
        using var doc = JsonDocument.Parse(json);
        var tracks = new List<TrackInfo>();
        if (!doc.RootElement.TryGetProperty("streams", out var streams))
        {
            return tracks;
        }

        static string? Text(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()) : null;
        static long Number(string? text) => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

        foreach (var stream in streams.EnumerateArray())
        {
            var type = Text(stream, "codec_type");
            if (type != "audio" && type != "subtitle")
            {
                continue;
            }

            stream.TryGetProperty("tags", out var tags);
            stream.TryGetProperty("disposition", out var disposition);
            var title = Text(tags, "title") ?? string.Empty;

            // mkvmerge writes each track's real bitrate as a BPS tag; containers that know it give bit_rate.
            var bitRate = Number(Text(stream, "bit_rate"));
            if (bitRate <= 0)
            {
                bitRate = Number(Text(tags, "BPS") ?? Text(tags, "BPS-eng"));
            }

            tracks.Add(new TrackInfo(
                (int)Number(Text(stream, "index")),
                type == "audio" ? TrackKind.Audio : TrackKind.Subtitle,
                normalize(Text(tags, "language")),
                Text(stream, "codec_name") ?? string.Empty,
                Text(disposition, "forced") == "1" || title.Contains("forced", StringComparison.OrdinalIgnoreCase),
                Text(disposition, "default") == "1",
                bitRate,
                (int)Number(Text(stream, "channels"))));
        }

        return tracks;
    }

    // The fonts, cover art and other files attached to an mkv.
    private async Task<IReadOnlyList<AttachmentInfo>> ProbeAttachmentsAsync(string path, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(_mediaEncoder.ProbePath)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in new[] { "-v", "error", "-show_entries", "stream=index,codec_type:stream_disposition=attached_pic:stream_tags=mimetype,filename", "-of", "json", path })
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start ffprobe.");
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("ffprobe couldn't read the file's attachments.");
        }

        return ParseAttachments(output);
    }

    /// <summary>
    /// Reads an mkv's attachments from ffprobe's JSON.
    /// </summary>
    /// <param name="json">ffprobe's output with -show_entries stream=index,codec_type:stream_disposition=attached_pic:stream_tags=mimetype,filename.</param>
    /// <returns>The attachments, and the pictures attached as cover art, which ffmpeg reads as video streams.</returns>
    internal static IReadOnlyList<AttachmentInfo> ParseAttachments(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var attachments = new List<AttachmentInfo>();
        if (!doc.RootElement.TryGetProperty("streams", out var streams))
        {
            return attachments;
        }

        static string? Text(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()) : null;
        foreach (var stream in streams.EnumerateArray())
        {
            stream.TryGetProperty("tags", out var tags);
            stream.TryGetProperty("disposition", out var disposition);
            var type = Text(stream, "codec_type");
            var picture = type == "video" && Text(disposition, "attached_pic") == "1";
            if ((type == "attachment" || picture) && int.TryParse(Text(stream, "index"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            {
                attachments.Add(new AttachmentInfo(index, Text(tags, "mimetype"), Text(tags, "filename"), picture));
            }
        }

        return attachments;
    }

    private static void DeleteQuietly(string? path)
    {
        try
        {
            if (path is not null && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A small temporary file; it's in the system's temp folder.
        }
    }

    private static void CheckFreeSpace(string folder, EncodeJob job, long estimatedBytes)
    {
        long free;
        try
        {
            free = new DriveInfo(folder).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return; // Unknown free space; ffmpeg will fail cleanly if the disk fills.
        }

        // Estimates can run low on grainy films, so leave a fifth more room than expected.
        var needed = (long)(estimatedBytes * 1.2) + SafetyMarginBytes;
        if (free < needed)
        {
            var advice = job.Mode == OutputMode.KeepBoth ? " Try Replace instead, or free up some space." : " Free up some space and try again.";
            throw new InvalidOperationException("Not enough free space: about " + FormatBytes(needed) + " is needed and " + FormatBytes(free) + " is free." + advice);
        }
    }

    // What the estimates need to know about a library video.
    internal static (MediaFileInfo File, long AudioBitrate) Describe(Video item)
    {
        var streams = item.GetMediaStreams();
        var video = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
        var file = new MediaFileInfo
        {
            SizeBytes = new FileInfo(item.Path).Length,
            DurationSeconds = TimeSpan.FromTicks(item.RunTimeTicks ?? 0).TotalSeconds,
            VideoCodec = video?.Codec ?? string.Empty,
            Width = video?.Width ?? item.Width,
            Height = video?.Height ?? item.Height,
            IsHdr = video?.VideoRange == VideoRange.HDR
        };
        return (file, streams.Where(s => s.Type == MediaStreamType.Audio).Sum(s => (long)(s.BitRate ?? 0)));
    }

    internal static SourceVideo SourceInfo(Video item, FileInfo source)
    {
        var video = item.GetMediaStreams().FirstOrDefault(s => s.Type == MediaStreamType.Video);
        return new SourceVideo(
            IsMatroska: source.Extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase),
            IsMp4: source.Extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) || source.Extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase),
            IsTenBit: (video?.BitDepth ?? 8) >= 10,
            IsHdr: video?.VideoRange == VideoRange.HDR,
            ColorTransfer: video?.ColorTransfer,
            Codec: video?.Codec,
            DolbyVisionProfile: video?.DvProfile is > 0 ? video.DvProfile : null,
            DolbyVisionOnly: video?.DvProfile is > 0 && video.DvBlSignalCompatibilityId == 0,
            DolbyVisionLayer: video?.DvProfile is > 0 && video.ElPresentFlag == 1);
    }

    internal static double FrameRate(Video item)
    {
        var video = item.GetMediaStreams().FirstOrDefault(s => s.Type == MediaStreamType.Video);
        var rate = video?.RealFrameRate ?? video?.AverageFrameRate ?? 0;
        return rate is > 1 and < 300 ? rate : 24;
    }

    // The size a file will be encoded at. Auto uses the saved detail analysis, or the file's own size until it has one.
    private (int Width, int Height) TargetSize(Video item, MediaFileInfo file, ResolutionChoice choice, out bool needsAnalysis)
    {
        needsAnalysis = false;
        var width = file.Width > 0 ? file.Width : 1920;
        var height = file.Height > 0 ? file.Height : 1080;
        if (choice == ResolutionChoice.Auto)
        {
            var analysis = _resolutionAnalyzer.GetCached(item);
            needsAnalysis = analysis is null;
            return analysis is null ? (width, height) : (analysis.DetailWidth, analysis.DetailHeight);
        }

        return Resolutions.Box(choice) is { } box ? Resolutions.Fit(width, height, box) : (width, height);
    }

    private static SpeedTestFile SpeedFile(Video item, MediaFileInfo file, long audioBitrate, (int Width, int Height) size)
        => new(
            item.Path,
            file.DurationSeconds,
            FrameRate(item),
            file.Width > 0 ? file.Width : size.Width,
            file.Height > 0 ? file.Height : size.Height,
            file.VideoCodec,
            audioBitrate,
            SourceInfo(item, new FileInfo(item.Path)),
            size);

    // Each file takes its frames, times the pixels in each, times the seconds per pixel for how busy it is.
    // A setting with no saved speed is tested on the file that will take longest, unless an encode is running,
    // which would slow the test down; then the running encode's speed stands in.
    private async Task<(double? Seconds, string? Source)> EstimateSecondsAsync(string encoder, TargetCodec codec, QualityPreset quality, EncodeEffort effort, List<EstimatePlan> plans, CancellationToken cancellationToken)
    {
        var work = plans.Select(p => (
            Plan: p,
            Key: SpeedEstimator.Key(encoder, effort, quality, (p.File.Width, p.File.Height), p.File.Output),
            Busyness: SpeedEstimator.AverageBusyness(p.File, p.Info.SizeBytes),
            Pixels: Pixels(p.File))).ToList();

        EncodeJob? running;
        lock (_jobsLock)
        {
            running = _jobs.FirstOrDefault(j => j.State == EncodeJobState.Running);
        }

        var untested = work.Where(p => _speedEstimator.Get(p.Key) is null).GroupBy(p => p.Key).MaxBy(g => g.Sum(p => p.Pixels));
        if (untested is not null && running is null)
        {
            var longest = untested.MaxBy(p => p.Pixels).Plan;
            var expected = SavingsEstimator.EstimateVideoBitrate(longest.Info, longest.AudioBitrate, codec, longest.File.Output.Width, longest.File.Output.Height, quality, effort);
            await _speedEstimator.TestAsync(untested.Key, encoder, quality, effort, longest.File, expected, cancellationToken).ConfigureAwait(false);
        }

        var runningRate = running is not null && running.Encoder == encoder && running.Quality == quality && running.Effort == effort ? running.PixelRate : 0;
        var seconds = 0.0;
        var pixelsBySource = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var p in work)
        {
            string source;
            if (_speedEstimator.Get(p.Key) is { } exact)
            {
                seconds += p.Pixels * exact.SecondsPerPixel(p.Busyness);
                source = exact.Basis.ToString();
            }
            else if (runningRate > 0 && running!.SpeedKey == p.Key)
            {
                seconds += p.Pixels / runningRate;
                source = "Running";
            }
            else if (_speedEstimator.GetClosest(p.Key) is { } closest)
            {
                seconds += p.Pixels * closest.SecondsPerPixel(p.Busyness);
                source = closest.Basis.ToString();
            }
            else if (runningRate > 0)
            {
                seconds += p.Pixels / runningRate;
                source = "Running";
            }
            else
            {
                return (null, null);
            }

            pixelsBySource[source] = pixelsBySource.GetValueOrDefault(source) + p.Pixels;
        }

        return (seconds, pixelsBySource.MaxBy(s => s.Value).Key);
    }

    // The time with another effort, without testing it: its own saved speed if it has one, or else another
    // effort's scaled by how much slower its presets usually are.
    private EffortEstimate SecondsWith(string encoder, QualityPreset quality, EncodeEffort effort, List<EstimatePlan> plans)
    {
        var seconds = 0.0;
        var measured = true;
        foreach (var p in plans)
        {
            var key = SpeedEstimator.Key(encoder, effort, quality, (p.File.Width, p.File.Height), p.File.Output);
            var busyness = SpeedEstimator.AverageBusyness(p.File, p.Info.SizeBytes);
            if (_speedEstimator.GetClosest(key) is { } own)
            {
                seconds += Pixels(p.File) * own.SecondsPerPixel(busyness);
            }
            else if (_speedEstimator.GetAnyEffort(key) is { } other)
            {
                seconds += Pixels(p.File) * other.Model.SecondsPerPixel(busyness) * EffortTime(encoder, effort) / EffortTime(encoder, other.Effort);
                measured = false;
            }
            else
            {
                return new EffortEstimate(effort, null, false);
            }
        }

        return new EffortEstimate(effort, seconds, measured);
    }

    // The size of the new files at a quality: the rule of thumb, corrected by test encodes of this setting or, failing
    // that, the closest one tested with the same encoder.
    private (long Bytes, bool Measured) OutputBytes(string? encoder, TargetCodec codec, QualityPreset quality, EncodeEffort effort, List<EstimatePlan> plans)
    {
        long total = 0;
        var measured = encoder is not null && plans.Count > 0;
        foreach (var p in plans)
        {
            var scale = 1.0;
            if (encoder is not null)
            {
                (scale, var own) = _speedEstimator.VideoScale(SpeedEstimator.Key(encoder, effort, quality, (p.File.Width, p.File.Height), p.File.Output));
                measured &= own;
            }

            total += SavingsEstimator.EstimateSize(p.Info, p.AudioBitrate, codec, p.File.Output.Width, p.File.Output.Height, quality, effort, scale, p.OutputAudioBitrate, p.SubtitleBitrate);
        }

        return (total, measured);
    }

    private static double Pixels(SpeedTestFile file) => file.DurationSeconds * file.FramesPerSecond * file.OutputPixels;

    // How long each effort usually takes against Standard with an encoder's presets. Only a stand-in until this
    // server has timed the effort itself, which it does as soon as the effort is chosen.
    private static double EffortTime(string encoder, EncodeEffort effort)
    {
        ReadOnlySpan<double> ratios = encoder switch
        {
            "libx265" => [0.5, 1, 2, 8],
            "libx264" => [0.6, 1, 2, 4],
            "libsvtav1" => [0.5, 1, 4, 6],
            _ when encoder.EndsWith("_nvenc", StringComparison.Ordinal) => [0.8, 1, 1.3, 1.8],
            _ when encoder.EndsWith("_qsv", StringComparison.Ordinal) => [0.7, 1, 1.4, 2],
            _ => [0.8, 1, 1, 1]
        };
        return ratios[(int)effort];
    }

    private async Task RunFfmpegAsync(IList<string> args, TimeSpan duration, long pixelsPerFrame, EncodeJob job, CancellationToken cancellationToken)
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

        _logger.LogInformation("Storage Optimizer running {Encoder} {Arguments}", startInfo.FileName, string.Join(' ', args));

        using var process = new Process { StartInfo = startInfo };
        var clock = Stopwatch.StartNew();
        process.Start();
        var handle = new EncodeProcess(process);
        job.Process = handle;

        // Keep the end of stderr for the error message, and drain it so ffmpeg never blocks on a full pipe.
        var errorTail = new Queue<string>();
        var stderrTask = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
            {
                errorTail.Enqueue(line);
                if (errorTail.Count > 20)
                {
                    errorTail.Dequeue();
                }
            }
        },
        CancellationToken.None);

        using var registration = cancellationToken.Register(() =>
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

        // ffmpeg's -progress output is key=value lines; out_time_us is how far into the video it is, speed how fast it's going.
        var encodedSeconds = 0.0;
        while (await process.StandardOutput.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
        {
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal)
                && long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds)
                && duration > TimeSpan.Zero)
            {
                encodedSeconds = microseconds / 1_000_000.0;
                job.Progress = Math.Clamp(encodedSeconds / duration.TotalSeconds * 100, 0, 99.9);
            }
            else if (line.StartsWith("frame=", StringComparison.Ordinal)
                && long.TryParse(line.AsSpan(6), NumberStyles.Integer, CultureInfo.InvariantCulture, out var frame)
                && frame > 0)
            {
                // Time spent frozen for a viewer isn't encoding time, so it doesn't count against the speed.
                var seconds = (clock.Elapsed - job.TimePaused()).TotalSeconds;
                if (seconds > 0)
                {
                    job.PixelRate = frame * (double)pixelsPerFrame / seconds;
                }
            }
            else if (line.StartsWith("speed=", StringComparison.Ordinal)
                && double.TryParse(line.AsSpan(6).TrimEnd('x'), NumberStyles.Float, CultureInfo.InvariantCulture, out var speed)
                && speed > 0
                && duration > TimeSpan.Zero)
            {
                job.RemainingSeconds = Math.Max(duration.TotalSeconds - encodedSeconds, 0) / speed;
            }
        }

        handle.MarkExited();
        job.Process = null;
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        await stderrTask.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("ffmpeg failed (exit code " + process.ExitCode + "): " + string.Join(" ", errorTail.TakeLast(3)));
        }

        job.Progress = 100;
        job.RemainingSeconds = null;
    }

    private async Task VerifyAsync(string sourcePath, string outputPath, CancellationToken cancellationToken)
    {
        var sourceDuration = await ProbeDurationAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        var outputDuration = await ProbeDurationAsync(outputPath, cancellationToken).ConfigureAwait(false);
        var tolerance = Math.Max(2, sourceDuration * 0.01);
        if (outputDuration <= 0 || Math.Abs(sourceDuration - outputDuration) > tolerance)
        {
            throw new InvalidOperationException(string.Format(
                CultureInfo.InvariantCulture,
                "The new file is {0:F0}s long but the original is {1:F0}s, so it was discarded and the original kept.",
                outputDuration,
                sourceDuration));
        }
    }

    private async Task<double> ProbeDurationAsync(string path, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(_mediaEncoder.ProbePath)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in new[] { "-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", path })
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start ffprobe.");
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : 0;
    }

    private async Task RefreshAsync(Video item, string folderPath, CancellationToken cancellationToken)
    {
        var folder = _libraryManager.FindByPath(folderPath, true) as Folder ?? item.GetParent() as Folder;
        if (folder is null)
        {
            return;
        }

        var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem));
        await folder.ValidateChildren(new Progress<double>(), options, true, false, cancellationToken).ConfigureAwait(false);
    }

    // Finds the new file's library item and links it to the original, with the original kept as the primary version.
    // Jellyfin groups a file named like a version of its neighbour under one title by itself, but it only makes the
    // file an item of its own when that title is refreshed, so until then it can't be found, compared or listed.
    private async Task<Guid?> LinkAsVersionAsync(Video original, string newPath, CancellationToken cancellationToken)
    {
        original = _libraryManager.GetItemById<Video>(original.Id) ?? original;
        var added = FindVersion(original, newPath);
        if (added is null)
        {
            var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem));
            await original.RefreshMetadata(options, cancellationToken).ConfigureAwait(false);
            original = _libraryManager.GetItemById<Video>(original.Id) ?? original;
            added = FindVersion(original, newPath);
        }

        if (added is null || added.Id.Equals(original.Id))
        {
            _logger.LogWarning("Storage Optimizer could not find {Path} in the library to link it as a version", newPath);
            return null;
        }

        if (original.GetAllVersions().Any(v => v.Id.Equals(added.Id)))
        {
            return added.Id;
        }

        // Same steps as Jellyfin's own "merge versions" action.
        added.SetPrimaryVersionId(original.Id);
        await added.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        await _libraryManager.RerouteLinkedChildReferencesAsync(added.Id, original.Id).ConfigureAwait(false);

        original.LinkedAlternateVersions =
        [
            .. original.LinkedAlternateVersions,
            new LinkedChild { ItemId = added.Id, Type = LinkedChildType.LinkedAlternateVersion }
        ];
        await original.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        return added.Id;
    }

    private Video? FindVersion(Video original, string path)
        => _libraryManager.FindByPath(path, false) as Video
            ?? original.GetAllVersions().FirstOrDefault(v => string.Equals(v.Path, path, StringComparison.Ordinal));

    private void Finish(EncodeJob job, EncodeJobState state, string message)
    {
        job.State = state;
        job.Message = message;
        job.FinishedAt = DateTime.UtcNow;
        _logger.LogInformation("Storage Optimizer re-encode of {Path}: {State}. {Message}", job.SourcePath, state, message);
    }

    private void TrimFinishedJobs()
    {
        var finished = _jobs.Where(j => j.FinishedAt.HasValue).OrderBy(j => j.FinishedAt).ToList();
        foreach (var old in finished.Take(Math.Max(finished.Count - MaxFinishedJobsKept, 0)))
        {
            _jobs.Remove(old);
            old.Cancellation.Dispose();
        }
    }

    private static string Label(TargetCodec codec) => codec switch
    {
        TargetCodec.Av1 => "AV1",
        TargetCodec.H264 => "H264",
        _ => "HEVC"
    };

    private static string FormatBytes(long bytes)
    {
        var gb = bytes / 1024.0 / 1024 / 1024;
        return gb >= 1
            ? gb.ToString("F1", CultureInfo.InvariantCulture) + " GB"
            : (bytes / 1024.0 / 1024).ToString("F0", CultureInfo.InvariantCulture) + " MB";
    }

    private sealed record PreviewPlan(Video Item, string Encoder, SourceVideo Source, SpeedTestFile File, double Position);

    // What an estimate needs to know about one file.
    private sealed record EstimatePlan(SpeedTestFile File, MediaFileInfo Info, long AudioBitrate, long? OutputAudioBitrate, long SubtitleBitrate);
}
