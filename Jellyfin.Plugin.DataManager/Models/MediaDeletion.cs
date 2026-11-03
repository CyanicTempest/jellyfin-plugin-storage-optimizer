// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.DataManager.Models;

/// <summary>
/// Videos to delete, with the files beside them that the user ticked.
/// </summary>
public class DeleteRequest
{
    /// <summary>Gets the videos to delete.</summary>
    public IList<Guid> ItemIds { get; init; } = [];

    /// <summary>Gets the subtitle, artwork and .nfo files to delete too. Only files that belong to one of the videos are deleted.</summary>
    public IList<string> SidecarPaths { get; init; } = [];
}

/// <summary>
/// A file beside a video.
/// </summary>
/// <param name="Path">The file.</param>
/// <param name="SizeBytes">Its size in bytes.</param>
public sealed record SidecarFile(string Path, long SizeBytes);

/// <summary>
/// What deleting a video would do, for the user to confirm.
/// </summary>
/// <param name="ItemId">The video's id.</param>
/// <param name="Name">The video's name.</param>
/// <param name="Path">The video file.</param>
/// <param name="SizeBytes">Its size in bytes.</param>
/// <param name="Sidecars">The files beside it that belong to it alone, which are only deleted if the user ticks them.</param>
/// <param name="OtherVersionsKept">How many other versions of it stay in the library.</param>
/// <param name="Blocked">Why it can't be deleted now, such as a re-encode of it running, or null.</param>
public sealed record DeletePlanFile(string ItemId, string Name, string Path, long SizeBytes, IReadOnlyList<SidecarFile> Sidecars, int OtherVersionsKept, string? Blocked);

/// <summary>
/// A file that couldn't be deleted.
/// </summary>
/// <param name="Path">The file.</param>
/// <param name="Message">Why.</param>
public sealed record DeleteFailure(string Path, string Message);

/// <summary>
/// What was deleted.
/// </summary>
public class DeleteResult
{
    /// <summary>Gets the ids of the videos deleted.</summary>
    public IList<string> DeletedItemIds { get; init; } = [];

    /// <summary>Gets or sets how many files were deleted, videos and the files beside them.</summary>
    public int DeletedFiles { get; set; }

    /// <summary>Gets or sets the space freed, in bytes.</summary>
    public long FreedBytes { get; set; }

    /// <summary>Gets the files that couldn't be deleted.</summary>
    public IList<DeleteFailure> Failed { get; init; } = [];
}
