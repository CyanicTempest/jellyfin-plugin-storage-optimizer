// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DataManager.Models;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DataManager.Services;

/// <summary>
/// Finds files Jellyfin leaves behind that its own scheduled tasks don't clear, and cleans up the ones it can make again.
/// </summary>
/// <remarks>
/// Jellyfin's "Clean Cache Directory", "Clean Transcode Directory" and "Clean Log Directory" tasks already clear its
/// cache, transcode, temp and log folders, and when a library scan removes an item, Jellyfin deletes its metadata and
/// trickplay folders. What's left is metadata and trickplay folders that missed that (as after a crash or an item
/// removed while Jellyfin was stopped), "Film.trickplay" folders saved beside media whose video was deleted or renamed
/// outside Jellyfin (it never deletes those), and sample clips, which Jellyfin hides. Metadata and trickplay folders
/// are deleted without asking, as Jellyfin makes them again if needed. Sample clips might be wanted, so they're only
/// listed for the user to confirm.
/// </remarks>
public sealed class LeftoverCleaner
{
    private const string ReportFileName = "leftovers.json";

    // Anything changed in the last day is left alone, in case Jellyfin is still writing it.
    private static readonly TimeSpan _minimumAge = TimeSpan.FromDays(1);

    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = false };

    private static readonly SemaphoreSlim _lock = new(1, 1);

    // Folders NAS systems and Jellyfin keep for themselves, which never hold media.
    private static readonly HashSet<string> _skippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "@eaDir", "eaDir", "#recycle", "@Recycle", ".snapshot", ".snapshots", "lost+found", ".@__thumb", "metadata"
    };

    private readonly ILibraryManager _libraryManager;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly IServerApplicationPaths _applicationPaths;
    private readonly ILogger<LeftoverCleaner> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LeftoverCleaner"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="libraryMonitor">The library monitor, told about each delete in a library folder.</param>
    /// <param name="applicationPaths">Jellyfin's folders.</param>
    /// <param name="logger">The logger.</param>
    public LeftoverCleaner(ILibraryManager libraryManager, ILibraryMonitor libraryMonitor, IServerApplicationPaths applicationPaths, ILogger<LeftoverCleaner> logger)
    {
        _libraryManager = libraryManager;
        _libraryMonitor = libraryMonitor;
        _applicationPaths = applicationPaths;
        _logger = logger;
    }

    private static string ReportPath
    {
        get
        {
            var folder = Plugin.Instance?.DataFolderPath ?? throw new InvalidOperationException("Plugin is not loaded.");
            Directory.CreateDirectory(folder);
            return Path.Combine(folder, ReportFileName);
        }
    }

    /// <summary>
    /// Looks for leftovers, deletes the ones Jellyfin can make again, and saves what's left for the user.
    /// </summary>
    /// <param name="progress">Progress from 0 to 100.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The new report.</returns>
    public async Task<LeftoverReport> RunAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var report = new LeftoverReport();
        var held = new List<string>();

        // Item folders: metadata/library/ab/abcd... and data/trickplay/ab/abcd-... are named after the item's id.
        var metadata = FindOrphanedItemFolders(Path.Combine(_applicationPaths.InternalMetadataPath, "library"), LeftoverKinds.Metadata, cancellationToken);
        progress.Report(30);
        var trickplay = FindOrphanedItemFolders(Path.Combine(_applicationPaths.DataPath, "trickplay"), LeftoverKinds.Trickplay, cancellationToken);
        progress.Report(40);
        var found = new List<Leftover>();
        foreach (var (orphans, total, what) in new[] { (metadata.Orphans, metadata.Total, "metadata"), (trickplay.Orphans, trickplay.Total, "trickplay") })
        {
            // If most of a folder looks orphaned, the library is more likely broken or offline than really emptied.
            if (orphans.Count > 20 && orphans.Count * 2 > total)
            {
                held.Add(string.Create(CultureInfo.InvariantCulture, $"{orphans.Count} of {total} {what} folders"));
                foreach (var orphan in orphans)
                {
                    report.Items.Add(orphan);
                }
            }
            else
            {
                found.AddRange(orphans);
            }
        }

        FindInLibraries(found, report.Items, cancellationToken);
        progress.Report(80);

        foreach (var leftover in found)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (StillLeftOver(leftover) && TryDelete(leftover))
            {
                report.CleanedCount++;
                report.CleanedBytes += leftover.SizeBytes;
            }
        }

        if (held.Count > 0)
        {
            report.HeldBack = "So much Jellyfin data looked orphaned (" + string.Join(" and ", held)
                + ") that a library may be offline or Jellyfin's database may have changed, so none of it was deleted.";
            _logger.LogWarning("Storage Optimizer left orphaned-looking Jellyfin data alone: {Reason}", report.HeldBack);
        }

        report.GeneratedAt = DateTime.UtcNow;
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveAsync(report, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }

        progress.Report(100);
        _logger.LogInformation(
            "Storage Optimizer cleaned up {Count} leftover folders ({Bytes} bytes); {Waiting} leftovers wait for the user",
            report.CleanedCount,
            report.CleanedBytes,
            report.Items.Count);
        return report;
    }

    /// <summary>
    /// Loads the last saved report.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The report, or null if no check has run yet.</returns>
    public static async Task<LeftoverReport?> LoadAsync(CancellationToken cancellationToken)
    {
        var path = ReportPath;
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var stream = File.OpenRead(path);
            await using (stream.ConfigureAwait(false))
            {
                return await JsonSerializer.DeserializeAsync<LeftoverReport>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Deletes leftovers the user chose from the last report. Each is checked again first, so nothing that has since
    /// come back into use is deleted.
    /// </summary>
    /// <param name="ids">The leftovers' ids.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What was deleted, and what couldn't be.</returns>
    public async Task<DeleteResult> DeleteAsync(IEnumerable<string> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var wanted = ids.ToHashSet(StringComparer.Ordinal);
        var result = new DeleteResult();
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await LoadAsync(cancellationToken).ConfigureAwait(false) is not { } report)
            {
                return result;
            }

            foreach (var leftover in report.Items.Where(l => wanted.Contains(l.Id)).ToList())
            {
                if (!StillLeftOver(leftover))
                {
                    // In use again, or already gone: it no longer belongs in the list either way.
                    report.Items.Remove(leftover);
                    continue;
                }

                if (TryDelete(leftover))
                {
                    report.Items.Remove(leftover);
                    result.DeletedFiles++;
                    result.FreedBytes += leftover.SizeBytes;
                }
                else
                {
                    result.Failed.Add(new DeleteFailure(leftover.Path, "It couldn't be deleted. Check the Jellyfin log."));
                }
            }

            await SaveAsync(report, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }

        return result;
    }

    /// <summary>
    /// Gets whether a file is a sample clip by its name, as Jellyfin decides when hiding it: "sample.mkv",
    /// "Film.sample.mkv", or any video in a folder named "sample".
    /// </summary>
    /// <param name="path">The file.</param>
    /// <returns>True for a sample.</returns>
    public static bool IsSample(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!Sidecars.IsVideo(path))
        {
            return false;
        }

        var name = Path.GetFileNameWithoutExtension(path);
        var folder = Path.GetFileName(Path.GetDirectoryName(path));
        return string.Equals(name, "sample", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".sample", StringComparison.OrdinalIgnoreCase)
            || string.Equals(folder, "sample", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets whether a "Film.trickplay" folder has lost its video: no "Film" video sits beside it.
    /// </summary>
    /// <param name="folder">The .trickplay folder.</param>
    /// <returns>True if no video it belongs to is left.</returns>
    public static bool IsOrphanedTrickplay(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var trimmed = Path.TrimEndingDirectorySeparator(folder);
        var parent = Path.GetDirectoryName(trimmed);
        if (parent is null || !trimmed.EndsWith(".trickplay", StringComparison.OrdinalIgnoreCase) || !Directory.Exists(trimmed))
        {
            return false;
        }

        var baseName = Path.GetFileNameWithoutExtension(trimmed);
        try
        {
            return !Directory.EnumerateFiles(parent)
                .Any(f => Sidecars.IsVideo(f) && string.Equals(Path.GetFileNameWithoutExtension(f), baseName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string IdFor(string path)
        => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path)), 0, 12);

    private static bool OldEnough(FileSystemInfo info)
        => DateTime.UtcNow - info.LastWriteTimeUtc > _minimumAge;

    private static long SizeOf(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return new FileInfo(path).Length;
            }

            return new DirectoryInfo(path)
                .EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 })
                .Sum(f => f.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static Leftover Describe(string path, string kind)
        => new() { Id = IdFor(path), Kind = kind, Path = path, SizeBytes = SizeOf(path) };

    private static async Task SaveAsync(LeftoverReport report, CancellationToken cancellationToken)
    {
        var tempPath = ReportPath + ".tmp";
        var stream = File.Create(tempPath);
        await using (stream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(stream, report, _jsonOptions, cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, ReportPath, true);
    }

    // Folders named after an item id, two levels down, as "ab/abcdef...". A folder whose id isn't in the library is an orphan.
    private (List<Leftover> Orphans, int Total) FindOrphanedItemFolders(string root, string kind, CancellationToken cancellationToken)
    {
        var orphans = new List<Leftover>();
        var total = 0;
        if (!Directory.Exists(root))
        {
            return (orphans, total);
        }

        try
        {
            foreach (var shard in new DirectoryInfo(root).EnumerateDirectories())
            {
                foreach (var folder in shard.EnumerateDirectories())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!Guid.TryParse(folder.Name, out var id))
                    {
                        continue;
                    }

                    total++;
                    if (_libraryManager.GetItemById(id) is null && OldEnough(folder))
                    {
                        orphans.Add(Describe(folder.FullName, kind));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Storage Optimizer couldn't look through {Folder} for leftovers", root);
        }

        return (orphans, total);
    }

    // Walks every library folder for trickplay folders that lost their video, and for sample clips.
    private void FindInLibraries(List<Leftover> generated, IList<Leftover> forUser, CancellationToken cancellationToken)
    {
        var roots = _libraryManager.GetVirtualFolders()
            .SelectMany(f => f.Locations ?? [])
            .Where(Directory.Exists)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(roots);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = pending.Pop();
            if (!seen.Add(folder))
            {
                continue;
            }

            try
            {
                var info = new DirectoryInfo(folder);
                foreach (var file in info.EnumerateFiles())
                {
                    if (IsSample(file.FullName))
                    {
                        forUser.Add(Describe(file.FullName, LeftoverKinds.Sample));
                    }
                }

                foreach (var sub in info.EnumerateDirectories())
                {
                    if (_skippedFolders.Contains(sub.Name) || sub.LinkTarget is not null)
                    {
                        continue;
                    }

                    if (sub.Name.EndsWith(".trickplay", StringComparison.OrdinalIgnoreCase))
                    {
                        if (OldEnough(sub) && IsOrphanedTrickplay(sub.FullName))
                        {
                            generated.Add(Describe(sub.FullName, LeftoverKinds.TrickplayBesideMedia));
                        }

                        continue;
                    }

                    pending.Push(sub.FullName);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Storage Optimizer couldn't look through {Folder} for leftovers", folder);
            }
        }
    }

    // Checked again just before deleting, as the library may have changed since the report.
    private bool StillLeftOver(Leftover leftover)
    {
        switch (leftover.Kind)
        {
            case LeftoverKinds.Metadata:
            case LeftoverKinds.Trickplay:
                return Directory.Exists(leftover.Path)
                    && Guid.TryParse(Path.GetFileName(Path.TrimEndingDirectorySeparator(leftover.Path)), out var id)
                    && _libraryManager.GetItemById(id) is null;
            case LeftoverKinds.TrickplayBesideMedia:
                return IsOrphanedTrickplay(leftover.Path);
            case LeftoverKinds.Sample:
                return File.Exists(leftover.Path) && IsSample(leftover.Path);
            default:
                return false;
        }
    }

    private bool TryDelete(Leftover leftover)
    {
        var inLibrary = leftover.Kind is LeftoverKinds.TrickplayBesideMedia or LeftoverKinds.Sample;
        if (inLibrary)
        {
            _libraryMonitor.ReportFileSystemChangeBeginning(leftover.Path);
        }

        try
        {
            if (leftover.Kind == LeftoverKinds.Sample)
            {
                File.Delete(leftover.Path);
                _logger.LogInformation("Storage Optimizer deleted the sample {Path}, on request", leftover.Path);
            }
            else
            {
                Directory.Delete(leftover.Path, true);
                _logger.LogInformation("Storage Optimizer deleted leftover {Kind} folder {Path}", leftover.Kind, leftover.Path);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Storage Optimizer couldn't delete leftover {Path}", leftover.Path);
            return false;
        }
        finally
        {
            if (inLibrary)
            {
                _libraryMonitor.ReportFileSystemChangeComplete(leftover.Path, false);
            }
        }
    }
}
