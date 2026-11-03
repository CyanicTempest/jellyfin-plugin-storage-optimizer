// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DataManager.Services;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.DataManager.ScheduledTasks;

/// <summary>
/// Scheduled task that scans every library for storage usage.
/// </summary>
public class StorageScanTask : IScheduledTask
{
    private readonly StorageScanner _scanner;

    /// <summary>
    /// Initializes a new instance of the <see cref="StorageScanTask"/> class.
    /// </summary>
    /// <param name="scanner">The storage scanner.</param>
    public StorageScanTask(StorageScanner scanner)
    {
        _scanner = scanner;
    }

    /// <inheritdoc />
    public string Name => "Scan storage usage";

    /// <inheritdoc />
    public string Key => "DataManagerStorageScan";

    /// <inheritdoc />
    public string Description => "Measures how much space each library, folder and file uses, and finds files worth re-encoding.";

    /// <inheritdoc />
    public string Category => "Storage Optimizer";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _scanner.ScanAsync(progress, cancellationToken);

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
        };
    }
}
