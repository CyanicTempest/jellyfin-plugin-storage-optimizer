// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.DataManager.Services;

/// <summary>
/// Finds the files beside a video that belong to it alone: its subtitles, external audio, .nfo and artwork, named
/// after it as in "Film.en.srt", "Film.nfo" and "Film-poster.jpg".
/// </summary>
public static class Sidecars
{
    private static readonly HashSet<string> _videoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".ts", ".m2ts", ".mts", ".webm", ".mpg", ".mpeg", ".flv",
        ".vob", ".iso", ".ogv", ".3gp", ".divx", ".xvid", ".rmvb", ".asf", ".strm"
    };

    private static readonly HashSet<string> _sidecarExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".ass", ".ssa", ".vtt", ".sub", ".idx", ".sup", ".smi", ".ttml", ".nfo", ".xml", ".txt", ".edl", ".bif",
        ".jpg", ".jpeg", ".png", ".webp", ".gif", ".tbn", ".mka", ".mp3", ".aac", ".ac3", ".eac3", ".dts", ".flac",
        ".opus", ".ogg", ".m4a", ".wav"
    };

    // Artwork named after a video, from Jellyfin's list of image names, as in "Film-poster.jpg".
    private static readonly string[] _imageSuffixes =
        ["-poster", "-thumb", "-fanart", "-backdrop", "-background", "-banner", "-logo", "-clearlogo", "-landscape", "-clearart", "-disc", "-discart", "-cdart", "-art"];

    /// <summary>
    /// Gets whether a file is a video.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <returns>True for a video.</returns>
    public static bool IsVideo(string path) => _videoExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// Finds the files that belong to a video alone. A file named after two videos, such as "Film.Extended.en.srt"
    /// beside "Film.mkv" and "Film.Extended.mkv", belongs to the longer name; if another video has the same name, as
    /// "Film.mkv" and "Film.mp4" do, the files are shared and none are returned.
    /// </summary>
    /// <param name="videoPath">The video.</param>
    /// <returns>The files, by full path.</returns>
    public static IReadOnlyList<string> Find(string videoPath)
    {
        ArgumentNullException.ThrowIfNull(videoPath);
        var folder = Path.GetDirectoryName(videoPath);
        if (folder is null || !Directory.Exists(folder))
        {
            return [];
        }

        var baseName = Path.GetFileNameWithoutExtension(videoPath);
        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var otherBases = files
            .Where(f => IsVideo(f) && !string.Equals(f, videoPath, StringComparison.Ordinal))
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .ToList();
        if (otherBases.Contains(baseName, StringComparer.OrdinalIgnoreCase))
        {
            return [];
        }

        var longer = otherBases.Where(b => b.Length > baseName.Length && b.StartsWith(baseName, StringComparison.OrdinalIgnoreCase)).ToList();
        return files
            .Where(f => !IsVideo(f) && _sidecarExtensions.Contains(Path.GetExtension(f)))
            .Where(f => NamedAfter(Path.GetFileName(f), baseName) && !longer.Any(b => NamedAfter(Path.GetFileName(f), b)))
            .ToList();
    }

    /// <summary>
    /// Gets a sidecar's name for a video's new name, as "Film (2018).en.srt" for "film.2018.en.srt".
    /// </summary>
    /// <param name="sidecarPath">The sidecar.</param>
    /// <param name="oldBaseName">The video's old name, without extension.</param>
    /// <param name="newBaseName">The video's new name, without extension.</param>
    /// <returns>The sidecar's new full path.</returns>
    public static string Renamed(string sidecarPath, string oldBaseName, string newBaseName)
    {
        ArgumentNullException.ThrowIfNull(sidecarPath);
        ArgumentNullException.ThrowIfNull(oldBaseName);
        var name = Path.GetFileName(sidecarPath);
        return Path.Combine(Path.GetDirectoryName(sidecarPath) ?? string.Empty, newBaseName + name[oldBaseName.Length..]);
    }

    // "Film.en.srt", "Film.nfo" and "Film-poster.jpg" are named after Film; "Film 2.srt" isn't.
    private static bool NamedAfter(string fileName, string baseName)
    {
        if (fileName.Length <= baseName.Length || !fileName.StartsWith(baseName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = fileName[baseName.Length..];
        return rest[0] == '.' || _imageSuffixes.Any(s => rest.StartsWith(s, StringComparison.OrdinalIgnoreCase)
            && rest.Length > s.Length && (rest[s.Length] == '.' || char.IsDigit(rest[s.Length]) || rest[s.Length] == '-'));
    }
}
