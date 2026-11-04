// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DataManager.Encoding;
using Jellyfin.Plugin.DataManager.Models;
using Jellyfin.Plugin.DataManager.ScheduledTasks;
using Jellyfin.Plugin.DataManager.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.DataManager.Api;

/// <summary>
/// Endpoints for the Storage Optimizer overview page.
/// </summary>
[ApiController]
[Route("DataManager")]
[Authorize(Policy = "RequiresElevation")]
[Produces(MediaTypeNames.Application.Json)]
public class DataManagerController : ControllerBase
{
    private readonly SummaryBuilder _summaryBuilder;
    private readonly ITaskManager _taskManager;
    private readonly EncodeService _encodeService;
    private readonly FrameComparer _frameComparer;
    private readonly ResolutionAnalyzer _resolutionAnalyzer;
    private readonly MediaDeleter _mediaDeleter;
    private readonly LeftoverCleaner _leftoverCleaner;
    private readonly WatchHistory _watchHistory;
    private readonly GpuDetector _gpuDetector;
    private readonly GpuTester _gpuTester;

    /// <summary>
    /// Initializes a new instance of the <see cref="DataManagerController"/> class.
    /// </summary>
    /// <param name="summaryBuilder">The summary builder.</param>
    /// <param name="taskManager">The task manager.</param>
    /// <param name="encodeService">The re-encode queue.</param>
    /// <param name="frameComparer">The frame comparer.</param>
    /// <param name="resolutionAnalyzer">The detail analyser.</param>
    /// <param name="mediaDeleter">Deletes files the user confirms.</param>
    /// <param name="leftoverCleaner">Finds and deletes leftover files.</param>
    /// <param name="watchHistory">When each file was last watched.</param>
    /// <param name="gpuDetector">Reads what the GPU is.</param>
    /// <param name="gpuTester">Tests how many encodes at once suit the GPU.</param>
    public DataManagerController(SummaryBuilder summaryBuilder, ITaskManager taskManager, EncodeService encodeService, FrameComparer frameComparer, ResolutionAnalyzer resolutionAnalyzer, MediaDeleter mediaDeleter, LeftoverCleaner leftoverCleaner, WatchHistory watchHistory, GpuDetector gpuDetector, GpuTester gpuTester)
    {
        _gpuDetector = gpuDetector;
        _gpuTester = gpuTester;
        _leftoverCleaner = leftoverCleaner;
        _watchHistory = watchHistory;
        _mediaDeleter = mediaDeleter;
        _resolutionAnalyzer = resolutionAnalyzer;
        _encodeService = encodeService;
        _frameComparer = frameComparer;
        _summaryBuilder = summaryBuilder;
        _taskManager = taskManager;
    }

    /// <summary>
    /// Gets the storage summary from the last scan.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The summary, with an empty result if no scan has run yet.</returns>
    [HttpGet("Summary")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<StorageSummary>> GetSummary(CancellationToken cancellationToken)
    {
        var report = await StorageScanner.LoadAsync(cancellationToken).ConfigureAwait(false);
        var summary = _summaryBuilder.Build(report);
        _watchHistory.Apply(summary.Files);
        var kept = await SmallerFiles.KeptAsync(cancellationToken).ConfigureAwait(false);
        var paths = summary.Files.ToDictionary(f => f.ItemId, f => f.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var file in SmallerFiles.Find(report).Where(f => !kept.Contains(paths[f.OutputId])))
        {
            summary.SmallerFiles.Add(file);
        }

        return Ok(summary);
    }

    /// <summary>
    /// Gets the state of the storage scan task.
    /// </summary>
    /// <returns>The task state and progress.</returns>
    [HttpGet("Scan")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetScanState()
    {
        var worker = _taskManager.ScheduledTasks.FirstOrDefault(t => t.ScheduledTask is StorageScanTask);
        return Ok(new
        {
            State = worker?.State.ToString(),
            Progress = worker?.CurrentProgress
        });
    }

    /// <summary>
    /// Starts a storage scan now.
    /// </summary>
    /// <returns>No content.</returns>
    [HttpPost("Scan")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult StartScan()
    {
        _taskManager.QueueScheduledTask<StorageScanTask>();
        return NoContent();
    }

    /// <summary>
    /// Gets the codecs that can be encoded to, with compatibility notes.
    /// </summary>
    /// <returns>The codec options.</returns>
    [HttpGet("Encode/Options")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<CodecOption>> GetEncodeOptions() => Ok(_encodeService.GetCodecOptions());

    /// <summary>
    /// Queues re-encode jobs.
    /// </summary>
    /// <param name="request">The items and settings.</param>
    /// <returns>The queued jobs.</returns>
    [HttpPost("Encode")]
    [Consumes(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<IReadOnlyList<EncodeJob>> Encode([FromBody] EncodeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ItemIds.Count == 0)
        {
            return BadRequest("No items selected.");
        }

        var jobs = _encodeService.Enqueue(request);
        return jobs.Count == 0 ? Conflict("Those videos already have a re-encode waiting or running.") : Ok(jobs);
    }

    /// <summary>
    /// Estimates the size of the new files and, if asked, how long encoding will take. The first time estimate for a setting runs test encodes.
    /// </summary>
    /// <param name="request">The items and settings.</param>
    /// <param name="includeTime">Whether to work out the time too.</param>
    /// <param name="preferredLanguage">The user's preferred audio language, if set, to suggest keeping.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The estimate.</returns>
    [HttpPost("Encode/Estimate")]
    [Consumes(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<EncodeEstimate>> EstimateEncode([FromBody] EncodeRequest request, [FromQuery] bool includeTime, [FromQuery] string? preferredLanguage, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Ok(await _encodeService.EstimateAsync(request, includeTime, preferredLanguage, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Encodes about a second of a video with the chosen settings and compares a frame of it with the original.
    /// </summary>
    /// <param name="request">The video, where to start and the settings.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The comparison.</returns>
    [HttpPost("Encode/Preview")]
    [Consumes(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<FrameComparison>> PreviewEncode([FromBody] EncodePreviewRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            return Ok(await _encodeService.PreviewAsync(request, cancellationToken).ConfigureAwait(false));
        }
        catch (FileNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            return Problem(ex.Message);
        }
    }

    /// <summary>
    /// Encodes a few seconds of a video with the chosen settings and returns them stacked under the same seconds of
    /// the original, as an mp4 to play side by side.
    /// </summary>
    /// <param name="request">The video, where to start and the settings.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The clip.</returns>
    [HttpPost("Encode/Clip")]
    [Consumes(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> ClipEncode([FromBody] EncodePreviewRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var path = await _encodeService.ClipAsync(request, cancellationToken).ConfigureAwait(false);

            // The clip is only for this response, so it's deleted once sent.
#pragma warning disable CA2000 // The result disposes the stream once it's sent.
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
#pragma warning restore CA2000
            return File(stream, "video/mp4");
        }
        catch (FileNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            return Problem(ex.Message);
        }
    }

    /// <summary>
    /// Gets small pictures of frames spread through a video, to pick previews from.
    /// </summary>
    /// <param name="itemId">The video.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The frames.</returns>
    [HttpGet("Filmstrip/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Filmstrip>> GetFilmstrip([FromRoute] Guid itemId, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _frameComparer.FilmstripAsync(itemId, cancellationToken).ConfigureAwait(false));
        }
        catch (FileNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            return Problem(ex.Message);
        }
    }

    /// <summary>
    /// Finds the smallest size a video can be scaled down to without losing real detail. Saved results are returned straight away.
    /// </summary>
    /// <param name="itemId">The video.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The analysis.</returns>
    [HttpGet("Analyse/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ResolutionAnalysis>> Analyse([FromRoute] Guid itemId, CancellationToken cancellationToken)
    {
        // Closing the re-encode window stops the checks it asked for, whether or not the browser drops the request.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _resolutionAnalyzer.CheckToken);
        try
        {
            return Ok(await _resolutionAnalyzer.AnalyseAsync(itemId, linked.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            return StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (FileNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            return Problem(ex.Message);
        }
    }

    /// <summary>
    /// Stops the detail checks the re-encode window asked for, those running and those waiting. Finished ones are kept.
    /// </summary>
    /// <returns>No content.</returns>
    [HttpDelete("Analyse")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult CancelAnalyses()
    {
        _resolutionAnalyzer.CancelChecks();
        return NoContent();
    }

    /// <summary>
    /// Gets the re-encode jobs, newest first.
    /// </summary>
    /// <returns>The jobs.</returns>
    [HttpGet("Encode/Jobs")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<EncodeJob>> GetJobs() => Ok(_encodeService.GetJobs());

    /// <summary>
    /// Cancels a re-encode job.
    /// </summary>
    /// <param name="jobId">The job id.</param>
    /// <returns>No content, or not found if the job isn't active.</returns>
    [HttpDelete("Encode/Jobs/{jobId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult CancelJob([FromRoute] Guid jobId) => _encodeService.Cancel(jobId) ? NoContent() : NotFound();

    /// <summary>
    /// Gets how many encodes are running, paused and waiting, and who is watching.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The status.</returns>
    [HttpGet("Encode/Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<EncodeStatus>> GetEncodeStatus(CancellationToken cancellationToken)
        => Ok(await _encodeService.StatusAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Lets a paused re-encode carry on now, whatever the setting for viewers says.
    /// </summary>
    /// <param name="jobId">The job id.</param>
    /// <returns>No content, or not found if the job isn't paused.</returns>
    [HttpPost("Encode/Jobs/{jobId}/Resume")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult ResumeJob([FromRoute] Guid jobId) => _encodeService.Resume(jobId) ? NoContent() : NotFound();

    /// <summary>
    /// Gets the GPU Jellyfin encodes on, how many encodes at once suit it, and the last test of it.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What is known about the GPU.</returns>
    [HttpGet("Gpu")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<GpuOverview>> GetGpu(CancellationToken cancellationToken)
    {
        var info = await _gpuDetector.DetectAsync(cancellationToken).ConfigureAwait(false);
        var test = _gpuTester.Status;
        if (test.State == "Idle")
        {
            test = GpuTester.Saved(info);
        }

        var config = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();
        return Ok(new GpuOverview(info, GpuParsing.Recommended(info, config), test));
    }

    /// <summary>
    /// Starts timing one to four encodes at once on the GPU. Encoding is held while it runs.
    /// </summary>
    /// <returns>The test's status, which the GPU endpoint keeps up to date.</returns>
    [HttpPost("Gpu/Test")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<GpuTestStatus> StartGpuTest()
    {
        _gpuTester.Start();
        return Ok(_gpuTester.Status);
    }

    /// <summary>
    /// Stops the GPU test that's running.
    /// </summary>
    /// <returns>No content.</returns>
    [HttpDelete("Gpu/Test")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult StopGpuTest()
    {
        _gpuTester.Cancel();
        return NoContent();
    }

    /// <summary>
    /// Acts on the user's choice for a new file that came out no smaller than the original.
    /// </summary>
    /// <param name="jobId">The job id.</param>
    /// <param name="useNewFile">True to put the new file in place anyway, false to delete it and keep the original.</param>
    /// <returns>No content, or not found if the job isn't waiting for a choice.</returns>
    [HttpPost("Encode/Jobs/{jobId}/Decision")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult DecideJob([FromRoute] Guid jobId, [FromQuery] bool useNewFile) => _encodeService.Decide(jobId, useNewFile) ? NoContent() : NotFound();

    /// <summary>
    /// Finds movies and episodes that are in the library more than once, from the last scan, with the best copy of each.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The titles with more than one copy. Nothing is deleted.</returns>
    [HttpGet("Duplicates")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<DuplicateReport>> GetDuplicates(CancellationToken cancellationToken)
    {
        var report = await StorageScanner.LoadAsync(cancellationToken).ConfigureAwait(false);
        return Ok(DuplicateFinder.Find(report));
    }

    /// <summary>
    /// Accepts smaller re-encodes the user confirmed: each original is deleted, and the files beside it move to the new file's name.
    /// </summary>
    /// <param name="request">The re-encodes.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What was deleted, and what couldn't be.</returns>
    [HttpPost("Encode/Smaller/Accept")]
    [Consumes(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<DeleteResult>> AcceptSmaller([FromBody] DeleteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Ok(await _mediaDeleter.AcceptAsync(request.ItemIds, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Keeps re-encodes beside their originals, so they aren't offered again. Nothing is deleted.
    /// </summary>
    /// <param name="request">The re-encodes.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>No content.</returns>
    [HttpPost("Encode/Smaller/KeepBoth")]
    [Consumes(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> KeepBoth([FromBody] DeleteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var report = await StorageScanner.LoadAsync(cancellationToken).ConfigureAwait(false);
        var ids = request.ItemIds.Select(id => id.ToString("N", System.Globalization.CultureInfo.InvariantCulture)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var paths = report?.Files.Where(f => ids.Contains(f.ItemId)).Select(f => f.Path) ?? [];
        await SmallerFiles.KeepAsync(paths, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Works out what deleting some videos would do, for the user to confirm. Nothing is deleted.
    /// </summary>
    /// <param name="request">The videos.</param>
    /// <returns>Each video, the files beside it, and anything stopping its delete.</returns>
    [HttpPost("Media/DeletePlan")]
    [Consumes(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<DeletePlanFile>> PlanDelete([FromBody] DeleteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Ok(_mediaDeleter.Plan(request.ItemIds));
    }

    /// <summary>
    /// Deletes videos the user confirmed, and the files beside them they ticked.
    /// </summary>
    /// <param name="request">The videos and ticked files.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What was deleted, and what couldn't be.</returns>
    [HttpPost("Media/Delete")]
    [Consumes(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<DeleteResult>> DeleteMedia([FromBody] DeleteRequest request, CancellationToken cancellationToken)
        => Ok(await _mediaDeleter.DeleteAsync(request, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Gets what the last leftover check found and cleaned, with the state of the check.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The report, or an empty one if no check has run yet, and whether one is running.</returns>
    [HttpGet("Leftovers")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> GetLeftovers(CancellationToken cancellationToken)
    {
        var worker = _taskManager.ScheduledTasks.FirstOrDefault(t => t.ScheduledTask is LeftoverCleanupTask);
        return Ok(new
        {
            Report = await LeftoverCleaner.LoadAsync(cancellationToken).ConfigureAwait(false) ?? new LeftoverReport(),
            State = worker?.State.ToString(),
            Progress = worker?.CurrentProgress
        });
    }

    /// <summary>
    /// Starts a leftover check and clean-up now.
    /// </summary>
    /// <returns>No content.</returns>
    [HttpPost("Leftovers/Scan")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult StartLeftoverScan()
    {
        _taskManager.QueueScheduledTask<LeftoverCleanupTask>();
        return NoContent();
    }

    /// <summary>
    /// Deletes leftovers the user confirmed, by their ids in the last report.
    /// </summary>
    /// <param name="request">The leftovers.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What was deleted, and what couldn't be.</returns>
    [HttpPost("Leftovers/Delete")]
    [Consumes(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<DeleteResult>> DeleteLeftovers([FromBody] LeftoverDeleteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Ok(await _leftoverCleaner.DeleteAsync(request.Ids, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Takes the same frame from two versions of a video, with a difference map and similarity scores.
    /// </summary>
    /// <param name="firstId">The first video, usually the original.</param>
    /// <param name="secondId">The second video, usually the re-encode.</param>
    /// <param name="position">Where in the video to take the frame, in seconds.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The comparison.</returns>
    [HttpGet("Compare")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<FrameComparison>> Compare([FromQuery] Guid firstId, [FromQuery] Guid secondId, [FromQuery] double position, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _frameComparer.CompareAsync(firstId, secondId, position, cancellationToken).ConfigureAwait(false));
        }
        catch (FileNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            return Problem(ex.Message);
        }
    }
}
