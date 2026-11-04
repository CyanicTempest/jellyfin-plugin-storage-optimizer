// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DataManager.Encoding;
using Jellyfin.Plugin.DataManager.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DataManager.Services;

/// <summary>
/// Deletes single video files the user has confirmed, and removes them from the library.
/// </summary>
/// <remarks>
/// Jellyfin's own delete removes a movie's whole folder, with every version in it, so this deletes only the chosen
/// file, and only the files beside it that the user ticked, then tells Jellyfin the item is gone.
/// </remarks>
public sealed class MediaDeleter
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly EncodeService _encodeService;
    private readonly ILogger<MediaDeleter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaDeleter"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="libraryMonitor">The library monitor, told about each delete so it doesn't scan mid-way.</param>
    /// <param name="encodeService">The re-encode queue, whose files can't be deleted while it works on them.</param>
    /// <param name="logger">The logger.</param>
    public MediaDeleter(ILibraryManager libraryManager, ILibraryMonitor libraryMonitor, EncodeService encodeService, ILogger<MediaDeleter> logger)
    {
        _libraryManager = libraryManager;
        _libraryMonitor = libraryMonitor;
        _encodeService = encodeService;
        _logger = logger;
    }

    /// <summary>
    /// Works out what deleting some videos would do, for the user to confirm. Nothing is deleted.
    /// </summary>
    /// <param name="itemIds">The videos.</param>
    /// <returns>Each video that still exists, with the files beside it and anything stopping its delete.</returns>
    public IReadOnlyList<DeletePlanFile> Plan(IEnumerable<Guid> itemIds)
    {
        ArgumentNullException.ThrowIfNull(itemIds);

        var ids = itemIds.Distinct().ToList();
        var busy = _encodeService.BusyItems();
        var plan = new List<DeletePlanFile>();
        foreach (var id in ids)
        {
            if (_libraryManager.GetItemById<Video>(id) is not { } item || string.IsNullOrEmpty(item.Path) || !File.Exists(item.Path))
            {
                continue;
            }

            var sidecars = Sidecars.Find(item.Path).Select(p => new SidecarFile(p, Size(p))).ToList();
            var othersKept = item.GetAllVersions().Count(v => !v.Id.Equals(item.Id) && !ids.Contains(v.Id));
            plan.Add(new DeletePlanFile(
                id.ToString("N", CultureInfo.InvariantCulture),
                StorageScanner.Title(item),
                item.Path,
                Size(item.Path),
                sidecars,
                othersKept,
                busy.GetValueOrDefault(id)));
        }

        return plan;
    }

    /// <summary>
    /// Deletes videos the user confirmed, with the files beside them that they ticked.
    /// </summary>
    /// <param name="request">The videos and ticked files.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What was deleted, and what couldn't be.</returns>
    public async Task<DeleteResult> DeleteAsync(DeleteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var ticked = request.SidecarPaths.ToHashSet(StringComparer.Ordinal);
        var result = new DeleteResult();

        // Other versions go before the main one, so Jellyfin never makes one of them the main version only to lose it next.
        var plan = Plan(request.ItemIds)
            .Select(p => (Plan: p, Item: _libraryManager.GetItemById<Video>(Guid.Parse(p.ItemId))))
            .OrderBy(p => p.Item?.PrimaryVersionId is null ? 1 : 0)
            .ToList();
        foreach (var (file, item) in plan)
        {
            // Once started, the batch runs to the end even if the page is closed, so every file deleted is also taken out of the library and the report.
            if (item is null)
            {
                continue;
            }

            if (file.Blocked is not null)
            {
                result.Failed.Add(new DeleteFailure(file.Path, file.Blocked));
                continue;
            }

            if (!TryDelete(file.Path, file.SizeBytes, result))
            {
                continue;
            }

            foreach (var sidecar in file.Sidecars.Where(s => ticked.Contains(s.Path)))
            {
                TryDelete(sidecar.Path, sidecar.SizeBytes, result);
            }

            // Jellyfin's own delete, told to leave the disk alone: it removes the item and makes any other version the main one.
            try
            {
                _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false }, true);
            }
#pragma warning disable CA1031 // The file is gone either way; the next library scan tidies up.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogWarning(ex, "Storage Optimizer deleted {Path} but couldn't remove it from the library", file.Path);
            }

            result.DeletedItemIds.Add(file.ItemId);
        }

        await StorageScanner.RemoveFilesAsync([.. result.DeletedItemIds], CancellationToken.None).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Accepts re-encodes the user confirmed. Each original is deleted, and its subtitles, artwork and .nfo files are
    /// kept by moving them to the new file's name. An original is only deleted while its re-encode is on the disk and smaller.
    /// </summary>
    /// <param name="outputIds">The re-encodes.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What was deleted, and what couldn't be.</returns>
    public async Task<DeleteResult> AcceptAsync(IEnumerable<Guid> outputIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outputIds);

        var report = await StorageScanner.LoadAsync(cancellationToken).ConfigureAwait(false);
        var wanted = outputIds.Select(id => id.ToString("N", CultureInfo.InvariantCulture)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var byId = report?.Files.ToDictionary(f => f.ItemId, StringComparer.OrdinalIgnoreCase) ?? [];
        var result = new DeleteResult();
        var moves = new Dictionary<string, (IReadOnlyList<string> Sidecars, string OldBase, string NewBase)>(StringComparer.OrdinalIgnoreCase);
        var originals = new List<Guid>();
        foreach (var pair in SmallerFiles.Find(report).Where(p => wanted.Contains(p.OutputId)))
        {
            var original = byId[pair.OriginalId];
            var output = new FileInfo(byId[pair.OutputId].Path);
            if (!output.Exists || !File.Exists(original.Path) || output.Length >= new FileInfo(original.Path).Length)
            {
                result.Failed.Add(new DeleteFailure(original.Path, "Its re-encode is missing or no longer smaller, so the original was kept."));
                continue;
            }

            moves[pair.OriginalId] = (Sidecars.Find(original.Path), Path.GetFileNameWithoutExtension(original.Path), Path.GetFileNameWithoutExtension(output.Name));
            originals.Add(Guid.Parse(pair.OriginalId));
        }

        var deleted = await DeleteAsync(new DeleteRequest { ItemIds = originals }, cancellationToken).ConfigureAwait(false);
        result.DeletedFiles = deleted.DeletedFiles;
        result.FreedBytes = deleted.FreedBytes;
        foreach (var failure in deleted.Failed)
        {
            result.Failed.Add(failure);
        }

        foreach (var id in deleted.DeletedItemIds)
        {
            result.DeletedItemIds.Add(id);
            var (sidecars, oldBase, newBase) = moves[id];
            foreach (var sidecar in sidecars)
            {
                var renamed = Sidecars.Renamed(sidecar, oldBase, newBase);
                try
                {
                    if (!File.Exists(renamed))
                    {
                        File.Move(sidecar, renamed);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Storage Optimizer couldn't move {Path} to the new file's name", sidecar);
                }
            }
        }

        return result;
    }

    private bool TryDelete(string path, long size, DeleteResult result)
    {
        _libraryMonitor.ReportFileSystemChangeBeginning(path);
        try
        {
            File.Delete(path);
            _logger.LogInformation("Storage Optimizer deleted {Path}, on request", path);
            result.DeletedFiles++;
            result.FreedBytes += size;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Storage Optimizer couldn't delete {Path}", path);
            result.Failed.Add(new DeleteFailure(path, ex.Message));
            return false;
        }
        finally
        {
            _libraryMonitor.ReportFileSystemChangeComplete(path, false);
        }
    }

    private static long Size(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
