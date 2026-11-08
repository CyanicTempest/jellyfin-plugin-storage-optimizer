// SPDX-License-Identifier: GPL-3.0-only
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DataManager.Encoding;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.DataManager.Services;

/// <summary>
/// Puts the encode list back when Jellyfin starts, so waiting encodes carry on without anyone opening the page.
/// </summary>
public sealed class QueueRecovery : IHostedService
{
    private readonly EncodeService _encodes;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueueRecovery"/> class.
    /// </summary>
    /// <param name="encodes">The encode queue.</param>
    public QueueRecovery(EncodeService encodes)
    {
        _encodes = encodes;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _encodes.RecoverJobs();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
