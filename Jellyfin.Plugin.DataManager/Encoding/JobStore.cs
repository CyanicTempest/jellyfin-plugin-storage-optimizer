// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace Jellyfin.Plugin.DataManager.Encoding;

/// <summary>
/// A job as it's saved, with the paths the page isn't sent.
/// </summary>
/// <param name="Job">The job.</param>
/// <param name="TempPath">Where its new file is being written, or null.</param>
/// <param name="PlannedPath">What the new file will be called, or null.</param>
public sealed record SavedJob(EncodeJob Job, string? TempPath, string? PlannedPath);

/// <summary>
/// Keeps the encode list on disk, so it comes back with every file's settings after the server stops, whether it
/// was shut down or not.
/// </summary>
public static class JobStore
{
    private const string FileName = "encode-queue.json";
    private static readonly Lock _lock = new();

    private static string? StorePath => Plugin.Instance?.DataFolderPath is { } folder ? Path.Combine(folder, FileName) : null;

    /// <summary>
    /// Saves the jobs.
    /// </summary>
    /// <param name="jobs">The jobs.</param>
    public static void Save(IReadOnlyList<EncodeJob> jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        if (StorePath is not { } path)
        {
            return;
        }

        var saved = jobs.Select(j => new SavedJob(j, j.TempPath, j.PlannedPath)).ToList();
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(saved));
                File.Move(path + ".tmp", path, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Saved again on the next change.
            }
        }
    }

    /// <summary>
    /// Loads the jobs saved last.
    /// </summary>
    /// <returns>The jobs, or none if nothing was saved or it can't be read.</returns>
    public static IReadOnlyList<SavedJob> Load()
    {
        lock (_lock)
        {
            try
            {
                return StorePath is { } path && File.Exists(path) ? Parse(File.ReadAllText(path)) : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return [];
            }
        }
    }

    /// <summary>
    /// Reads saved jobs.
    /// </summary>
    /// <param name="json">What <see cref="Save"/> wrote.</param>
    /// <returns>The jobs, or none if it can't be read.</returns>
    public static IReadOnlyList<SavedJob> Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<SavedJob>>(json)?.Where(s => s.Job is not null).ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Turns saved jobs back into the list. Waiting jobs wait again. One that was encoding when the server stopped
    /// is marked as stopped, with Restart to run it again with the same settings; its unfinished file is deleted.
    /// One waiting for a choice stays waiting while its new file is still there.
    /// </summary>
    /// <param name="saved">The saved jobs.</param>
    /// <param name="partialMarker">What an unfinished file's name has in it, so nothing else is ever deleted.</param>
    /// <returns>The jobs to put back.</returns>
    public static IReadOnlyList<EncodeJob> Recover(IReadOnlyList<SavedJob> saved, string partialMarker)
    {
        ArgumentNullException.ThrowIfNull(saved);
        var jobs = new List<EncodeJob>();
        foreach (var (job, tempPath, plannedPath) in saved)
        {
            job.TempPath = tempPath;
            job.PlannedPath = plannedPath;
            switch (job.State)
            {
                case EncodeJobState.Queued:
                    job.Message = "Waiting, after the server restarted.";
                    break;
                case EncodeJobState.Running or EncodeJobState.Paused:
                    if (tempPath is not null && Path.GetFileName(tempPath).Contains(partialMarker, StringComparison.Ordinal) && File.Exists(tempPath))
                    {
                        try
                        {
                            File.Delete(tempPath);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            // The storage scan cleans up unfinished files too.
                        }
                    }

                    job.State = EncodeJobState.Failed;
                    job.Interrupted = true;
                    job.Progress = 0;
                    job.RemainingSeconds = null;
                    job.FinishedAt = DateTime.UtcNow;
                    job.Message = "The server stopped while this was " + (job.IsRemux ? "removing tracks" : "encoding") + ", so it was left unfinished. The original was not changed. Restart it to run it again with the same settings.";
                    break;
                case EncodeJobState.NeedsDecision when tempPath is null || !File.Exists(tempPath):
                    job.State = EncodeJobState.Failed;
                    job.Message = "The new file waiting for your choice is gone, so nothing was changed. Restart it to encode it again.";
                    break;
            }

            jobs.Add(job);
        }

        return jobs;
    }
}
