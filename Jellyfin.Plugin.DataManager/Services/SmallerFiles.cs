// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DataManager.Models;

namespace Jellyfin.Plugin.DataManager.Services;

/// <summary>
/// One re-encode kept beside its original, which it came out smaller than.
/// </summary>
/// <param name="OriginalId">The original's item id.</param>
/// <param name="OutputId">The re-encode's item id.</param>
public sealed record SmallerFile(string OriginalId, string OutputId);

/// <summary>
/// Finds re-encodes kept beside their originals that came out smaller, for the user to accept or keep both.
/// </summary>
/// <remarks>
/// They're found from the last scan, by the name this plugin gives a kept re-encode, such as "Film - HEVC 1080p.mkv",
/// so encodes from before a restart still count. Nothing is deleted here.
/// </remarks>
public static partial class SmallerFiles
{
    private const string KeptFileName = "kept-both.json";

    private static readonly SemaphoreSlim _keptLock = new(1, 1);

    private static string KeptPath
    {
        get
        {
            var folder = Plugin.Instance?.DataFolderPath ?? throw new InvalidOperationException("Plugin is not loaded.");
            Directory.CreateDirectory(folder);
            return Path.Combine(folder, KeptFileName);
        }
    }

    /// <summary>
    /// Finds whether a file is named as this plugin names a re-encode kept beside its original.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <returns>True if it is.</returns>
    public static bool IsKeptReEncode(string path) => KeptName().IsMatch(Path.GetFileNameWithoutExtension(path));

    /// <summary>
    /// Finds the re-encodes in a report that came out smaller than the original they're a version of.
    /// </summary>
    /// <param name="report">The last scan, or null.</param>
    /// <returns>Each re-encode with its original.</returns>
    public static IReadOnlyList<SmallerFile> Find(StorageReport? report)
    {
        if (report is null)
        {
            return [];
        }

        var byId = report.Files.ToDictionary(f => f.ItemId, StringComparer.OrdinalIgnoreCase);
        var found = new List<SmallerFile>();
        foreach (var file in report.Files.Where(f => !f.IsExtra && IsKeptReEncode(f.Path)))
        {
            // The original is the largest version that isn't a re-encode itself.
            var original = file.OtherVersionIds
                .Select(id => byId.GetValueOrDefault(id))
                .OfType<MediaFileInfo>()
                .Where(f => !IsKeptReEncode(f.Path))
                .MaxBy(f => f.SizeBytes);
            if (original is not null && file.SizeBytes < original.SizeBytes)
            {
                found.Add(new SmallerFile(original.ItemId, file.ItemId));
            }
        }

        return found;
    }

    /// <summary>
    /// Gets the re-encodes the user chose to keep beside their originals, so they aren't asked about them again.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Their paths.</returns>
    public static async Task<HashSet<string>> KeptAsync(CancellationToken cancellationToken)
    {
        await _keptLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadKeptAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _keptLock.Release();
        }
    }

    /// <summary>
    /// Notes that the user chose to keep some re-encodes beside their originals.
    /// </summary>
    /// <param name="paths">The re-encodes' paths.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public static async Task KeepAsync(IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);

        await _keptLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var kept = await ReadKeptAsync(cancellationToken).ConfigureAwait(false);
            kept.UnionWith(paths);
            await File.WriteAllTextAsync(KeptPath, JsonSerializer.Serialize(kept), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _keptLock.Release();
        }
    }

    private static async Task<HashSet<string>> ReadKeptAsync(CancellationToken cancellationToken)
    {
        try
        {
            var path = KeptPath;
            return File.Exists(path)
                ? JsonSerializer.Deserialize<HashSet<string>>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)) ?? new HashSet<string>(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    // The end of the name EncodeService gives a file kept beside its original, as in "Film - HEVC" or "Film - AV1 720p".
    [GeneratedRegex(@" - (?:(?:HEVC|AV1|H264)(?: \S+)?|Remux)$", RegexOptions.CultureInvariant)]
    private static partial Regex KeptName();
}
