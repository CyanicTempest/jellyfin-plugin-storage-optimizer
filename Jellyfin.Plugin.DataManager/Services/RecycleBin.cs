// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace Jellyfin.Plugin.DataManager.Services;

/// <summary>
/// Keeps originals a re-encode replaced for a while instead of deleting them. Each goes into a hidden folder beside
/// it, on the same disk, so moving it there is instant; Jellyfin leaves folders whose names start with a dot alone.
/// </summary>
public static class RecycleBin
{
    /// <summary>The name of the hidden folder beside the media.</summary>
    public const string FolderName = ".storage-optimizer-recycle";

    private const string IndexName = "recycle-bin.json";
    private static readonly Lock _lock = new();

    /// <summary>
    /// Moves a file into the recycle folder beside it, to be deleted after a number of days.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="days">How many days to keep it.</param>
    /// <param name="now">The time now, in UTC.</param>
    /// <returns>Where it went.</returns>
    public static string Recycle(string path, int days, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(path);
        var target = Place(path);
        File.Move(path, target);
        Add(target, path, now.AddDays(days));
        return target;
    }

    /// <summary>
    /// Copies a file, such as a subtitle about to be renamed, into the recycle folder beside it.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="days">How many days to keep the copy.</param>
    /// <param name="now">The time now, in UTC.</param>
    public static void KeepCopy(string path, int days, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(path);
        var target = Place(path);
        File.Copy(path, target);
        Add(target, path, now.AddDays(days));
    }

    /// <summary>
    /// Puts a recycled file back where it was, if nothing has taken its place.
    /// </summary>
    /// <param name="recycledPath">Where it is in the recycle folder.</param>
    /// <param name="originalPath">Where it was.</param>
    public static void Restore(string recycledPath, string originalPath)
    {
        ArgumentNullException.ThrowIfNull(recycledPath);
        ArgumentNullException.ThrowIfNull(originalPath);
        File.Move(recycledPath, originalPath);
        lock (_lock)
        {
            var entries = Load();
            entries.RemoveAll(e => string.Equals(e.Path, recycledPath, StringComparison.Ordinal));
            Save(entries);
        }
    }

    /// <summary>
    /// Deletes what has been kept long enough, and recycle folders left empty.
    /// </summary>
    /// <param name="now">The time now, in UTC.</param>
    /// <returns>How many files were deleted.</returns>
    public static int Empty(DateTime now)
    {
        lock (_lock)
        {
            var entries = Load();
            var due = entries.Where(e => e.DeleteAfter <= now).ToList();
            var deleted = 0;
            foreach (var entry in due)
            {
                try
                {
                    if (File.Exists(entry.Path))
                    {
                        File.Delete(entry.Path);
                        deleted++;
                    }

                    entries.Remove(entry);
                    var folder = Path.GetDirectoryName(entry.Path);
                    if (folder is not null && Path.GetFileName(folder) == FolderName && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                    {
                        Directory.Delete(folder);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Tried again next time.
                }
            }

            if (due.Count > 0)
            {
                Save(entries);
            }

            return deleted;
        }
    }

    /// <summary>
    /// Gets what's in the recycle folders now.
    /// </summary>
    /// <returns>The files, soonest to go first.</returns>
    public static IReadOnlyList<RecycledFile> List()
    {
        lock (_lock)
        {
            return Load().Where(e => File.Exists(e.Path)).OrderBy(e => e.DeleteAfter).ToList();
        }
    }

    // A free name in the recycle folder beside the file.
    private static string Place(string path)
    {
        var folder = Path.Combine(Path.GetDirectoryName(path) ?? ".", FolderName);
        Directory.CreateDirectory(folder);
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        var target = Path.Combine(folder, name + extension);
        for (var n = 2; File.Exists(target); n++)
        {
            target = Path.Combine(folder, name + " (" + n + ")" + extension);
        }

        return target;
    }

    private static void Add(string path, string originalPath, DateTime deleteAfter)
    {
        lock (_lock)
        {
            var entries = Load();
            entries.Add(new RecycledFile { Path = path, OriginalPath = originalPath, DeleteAfter = deleteAfter });
            Save(entries);
        }
    }

    private static string? IndexPath => Plugin.Instance?.DataFolderPath is { } folder ? System.IO.Path.Combine(folder, IndexName) : null;

    private static List<RecycledFile> Load()
    {
        try
        {
            return IndexPath is { } path && File.Exists(path) ? JsonSerializer.Deserialize<List<RecycledFile>>(File.ReadAllText(path)) ?? [] : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void Save(List<RecycledFile> entries)
    {
        if (IndexPath is not { } path)
        {
            return;
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(entries));
        File.Move(path + ".tmp", path, true);
    }
}

/// <summary>
/// A file kept in a recycle folder.
/// </summary>
public sealed class RecycledFile
{
    /// <summary>Gets or sets where it is now.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets where it was.</summary>
    public string OriginalPath { get; set; } = string.Empty;

    /// <summary>Gets or sets when it's deleted, in UTC.</summary>
    public DateTime DeleteAfter { get; set; }
}
