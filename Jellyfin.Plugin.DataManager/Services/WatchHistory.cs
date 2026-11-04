// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.DataManager.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DataManager.Services;

/// <summary>
/// Adds when each file was last watched, by anyone on the server, so media nobody watches any more can be found.
/// </summary>
/// <remarks>
/// It's read fresh each time the overview loads, not saved with the scan, so something watched since the last scan
/// drops off the list straight away.
/// </remarks>
public sealed class WatchHistory
{
    private const int BatchSize = 500;

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILogger<WatchHistory> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WatchHistory"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="userDataManager">Each user's play history.</param>
    /// <param name="logger">The logger.</param>
    public WatchHistory(ILibraryManager libraryManager, IUserManager userManager, IUserDataManager userDataManager, ILogger<WatchHistory> logger)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _logger = logger;
    }

    /// <summary>
    /// Fills in when each file was added and last watched, and how many times it's been played, across every user.
    /// The versions of one title share their history, as watching any of them means the title is still watched.
    /// </summary>
    /// <param name="files">The files from the last scan.</param>
    public void Apply(IList<MediaFileInfo> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        // One query for each batch, as one lookup for each file is what makes a big library slow to open.
        var ids = new Dictionary<Guid, MediaFileInfo>();
        foreach (var file in files)
        {
            if (Guid.TryParse(file.ItemId, out var id))
            {
                ids[id] = file;
            }
        }

        var items = new Dictionary<string, BaseItem>(StringComparer.Ordinal);
        foreach (var batch in ids.Keys.Chunk(BatchSize))
        {
            var query = new InternalItemsQuery { ItemIds = batch, IncludeAlternateVersions = true, IncludeOwnedItems = true };
            foreach (var item in _libraryManager.GetItemList(query))
            {
                if (ids.TryGetValue(item.Id, out var file))
                {
                    items[file.ItemId] = item;
                    file.DateAdded = item.DateCreated == default ? null : item.DateCreated;
                }
            }
        }

        var lastPlayed = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        var plays = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            var list = items.Values.ToList();
            foreach (var user in _userManager.GetUsers())
            {
                for (var start = 0; start < list.Count; start += BatchSize)
                {
                    var batch = list.GetRange(start, Math.Min(BatchSize, list.Count - start));
                    var data = _userDataManager.GetUserDataBatch(batch, user);
                    foreach (var item in batch)
                    {
                        if (!data.TryGetValue(item.Id, out var userData) || userData is null)
                        {
                            continue;
                        }

                        var key = Key(item.Id);
                        plays[key] = plays.GetValueOrDefault(key) + userData.PlayCount;

                        // Some clients mark an item played without a date, so a played item with no date still counts
                        // as watched, at the time it was added.
                        var when = userData.LastPlayedDate ?? (userData.Played ? item.DateCreated : (DateTime?)null);
                        if (when is DateTime date && (!lastPlayed.TryGetValue(key, out var seen) || date > seen))
                        {
                            lastPlayed[key] = date;
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // The overview still works without the watch history.
            _logger.LogWarning(ex, "Storage Optimizer couldn't read the watch history");
            return;
        }

        foreach (var file in files)
        {
            var group = file.OtherVersionIds.Append(file.ItemId).ToList();
            file.PlayCount = group.Sum(id => plays.GetValueOrDefault(id));
            var dates = group.Where(lastPlayed.ContainsKey).Select(id => lastPlayed[id]).ToList();
            file.LastPlayed = dates.Count == 0 ? null : dates.Max();
            file.HistoryKnown = true;
        }
    }

    private static string Key(Guid id) => id.ToString("N", System.Globalization.CultureInfo.InvariantCulture);
}
