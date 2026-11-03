// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Session;

namespace Jellyfin.Plugin.DataManager.Encoding;

/// <summary>
/// One stream Jellyfin is transcoding for someone.
/// </summary>
/// <param name="User">Who is watching.</param>
/// <param name="Device">What they are watching on, such as Living-room TV.</param>
/// <param name="Title">What they are watching.</param>
/// <param name="TranscodesVideo">Whether the video itself is transcoded, which is what uses the GPU. Direct play and remuxing barely do.</param>
public sealed record LiveTranscode(string User, string Device, string Title, bool TranscodesVideo);

/// <summary>
/// Finds the streams Jellyfin is transcoding right now.
/// </summary>
public sealed class ViewerMonitor
{
    private readonly ISessionManager _sessionManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="ViewerMonitor"/> class.
    /// </summary>
    /// <param name="sessionManager">Jellyfin's session manager, which lists who is watching what.</param>
    public ViewerMonitor(ISessionManager sessionManager)
    {
        _sessionManager = sessionManager;
    }

    /// <summary>
    /// Lists the streams being transcoded.
    /// </summary>
    /// <param name="videoOnly">Whether to leave out streams that only transcode the audio.</param>
    /// <returns>The streams, which is none if Jellyfin's sessions can't be read.</returns>
    public IReadOnlyList<LiveTranscode> Streams(bool videoOnly)
    {
        try
        {
            return _sessionManager.Sessions
                .Where(s => s.NowPlayingItem is not null && s.TranscodingInfo is not null)
                .Select(s => new LiveTranscode(
                    string.IsNullOrWhiteSpace(s.UserName) ? "Someone" : s.UserName,
                    string.IsNullOrWhiteSpace(s.DeviceName) ? s.Client ?? "a device" : s.DeviceName,
                    Title(s),
                    !s.TranscodingInfo!.IsVideoDirect))
                .Where(s => !videoOnly || s.TranscodesVideo)
                .ToList();
        }
        catch (InvalidOperationException)
        {
            // The list changed while it was being read. The next look will see it.
            return [];
        }
    }

    /// <summary>
    /// Says who is watching, such as Sam, Sam and Alex, or 3 people.
    /// </summary>
    /// <param name="streams">The streams.</param>
    /// <returns>The people, in words.</returns>
    public static string People(IReadOnlyList<LiveTranscode> streams)
    {
        ArgumentNullException.ThrowIfNull(streams);

        var users = streams.Select(s => s.User).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return users.Count switch
        {
            0 => "someone",
            1 => users[0],
            2 => users[0] + " and " + users[1],
            _ => users.Count + " people"
        };
    }

    private static string Title(SessionInfo session)
    {
        var item = session.NowPlayingItem;
        if (item is null)
        {
            return string.Empty;
        }

        return string.IsNullOrWhiteSpace(item.SeriesName) ? item.Name ?? string.Empty : item.SeriesName + (string.IsNullOrWhiteSpace(item.Name) ? string.Empty : ": " + item.Name);
    }
}
