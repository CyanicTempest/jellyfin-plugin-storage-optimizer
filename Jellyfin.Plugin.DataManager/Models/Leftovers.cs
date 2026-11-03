// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.DataManager.Models;

/// <summary>
/// The kinds of leftover the plugin looks for. Jellyfin's own scheduled tasks already clear its cache, transcode,
/// log and temp folders, so only what they miss is listed here.
/// </summary>
public static class LeftoverKinds
{
    /// <summary>A folder in Jellyfin's metadata folder for an item that is no longer in the library.</summary>
    public const string Metadata = "Metadata";

    /// <summary>A folder of trickplay (seek preview) images for a video that is no longer in the library.</summary>
    public const string Trickplay = "Trickplay";

    /// <summary>A "Film.trickplay" folder beside the media with no "Film" video next to it any more.</summary>
    public const string TrickplayBesideMedia = "TrickplayBesideMedia";

    /// <summary>A sample clip, such as "sample.mkv", which Jellyfin hides and never plays.</summary>
    public const string Sample = "Sample";
}

/// <summary>
/// A leftover file or folder.
/// </summary>
public sealed class Leftover
{
    /// <summary>Gets or sets an id made from the path, so requests never carry paths.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the kind, one of <see cref="LeftoverKinds"/>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets the file or folder.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets its size in bytes, with everything inside a folder.</summary>
    public long SizeBytes { get; set; }
}

/// <summary>
/// What the last leftover check found and cleaned.
/// </summary>
public class LeftoverReport
{
    /// <summary>Gets or sets when the check finished.</summary>
    public DateTime? GeneratedAt { get; set; }

    /// <summary>Gets the leftovers still on disk: samples waiting for the user, and anything not cleaned automatically.</summary>
    public IList<Leftover> Items { get; init; } = [];

    /// <summary>Gets or sets how many leftover folders the check cleaned up by itself.</summary>
    public int CleanedCount { get; set; }

    /// <summary>Gets or sets the space the check freed, in bytes.</summary>
    public long CleanedBytes { get; set; }

    /// <summary>
    /// Gets or sets why Jellyfin data that looked orphaned was left alone, or null. Set when so much of it looked
    /// orphaned that something else is more likely wrong, such as a library that's offline.
    /// </summary>
    public string? HeldBack { get; set; }
}

/// <summary>
/// Leftovers the user chose to delete.
/// </summary>
public class LeftoverDeleteRequest
{
    /// <summary>Gets the ids of the leftovers, from the last report.</summary>
    public IList<string> Ids { get; init; } = [];
}
