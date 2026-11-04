// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DataManager.Services;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.DataManager.ScheduledTasks;

/// <summary>
/// Scheduled task that cleans up what Jellyfin's own clean-up tasks miss, and finds sample clips for the user to review.
/// </summary>
public class LeftoverCleanupTask : IScheduledTask
{
    private readonly LeftoverCleaner _cleaner;

    /// <summary>
    /// Initializes a new instance of the <see cref="LeftoverCleanupTask"/> class.
    /// </summary>
    /// <param name="cleaner">The leftover cleaner.</param>
    public LeftoverCleanupTask(LeftoverCleaner cleaner)
    {
        _cleaner = cleaner;
    }

    /// <inheritdoc />
    public string Name => "Find leftover files";

    /// <inheritdoc />
    public string Key => "DataManagerLeftoverCleanup";

    /// <inheritdoc />
    public string Description => "Finds metadata and trickplay folders left behind by videos that are gone, and sample clips, for you to review in Leftovers. Nothing is deleted until you press Delete there. It does not run on its own until you add a trigger.";

    /// <inheritdoc />
    public string Category => "Storage Optimizer";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _cleaner.RunAsync(progress, cancellationToken);

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];
}
