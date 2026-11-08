// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.DataManager.Encoding;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.DataManager.Services;

/// <summary>
/// Likely problems with a file, which the media list tags it with. Most come free from what Jellyfin already knows
/// about the file's tracks; a few need the file read, and only run when switched on in the settings.
/// </summary>
public static class FileIssues
{
    /// <summary>An audio or subtitle track has no language set.</summary>
    public const string UnknownLanguage = "UnknownLanguage";

    /// <summary>The only audio is commentary.</summary>
    public const string NoMainAudio = "NoMainAudio";

    /// <summary>There are several audio tracks and none is the default, so players pick the first.</summary>
    public const string NoDefaultAudio = "NoDefaultAudio";

    /// <summary>A subtitle track is titled forced but isn't flagged as forced, so players won't pick it on their own.</summary>
    public const string ForcedNotFlagged = "ForcedNotFlagged";

    /// <summary>The video is likely interlaced.</summary>
    public const string Interlaced = "Interlaced";

    /// <summary>The file has more than one video stream, and a re-encode keeps only the first.</summary>
    public const string ExtraVideo = "ExtraVideo";

    /// <summary>An older container (AVI, MPEG-TS, WMV) that might have broken timestamps.</summary>
    public const string OldContainer = "OldContainer";

    /// <summary>HDR10 video without the brightness information (mastering display and MaxCLL) players use to show it well.</summary>
    public const string HdrNoBrightness = "HdrNoBrightness";

    /// <summary>HDR video whose colour tags are missing, found by reading the file.</summary>
    public const string HdrNoColourTags = "HdrNoColourTags";

    /// <summary>Timestamps that go backwards or are missing, found by reading the file.</summary>
    public const string BrokenTimestamps = "BrokenTimestamps";

    /// <summary>The main audio runs a different length from the video, found by reading the file.</summary>
    public const string AudioDrift = "AudioDrift";

    private static readonly HashSet<string> _oldContainers = new(StringComparer.OrdinalIgnoreCase) { "avi", "ts", "mpegts", "m2ts", "mts", "wmv", "asf" };

    /// <summary>
    /// Gets the issues that Jellyfin's view of the file shows.
    /// </summary>
    /// <param name="streams">The file's streams, as Jellyfin lists them.</param>
    /// <param name="tracks">The file's audio and subtitle tracks.</param>
    /// <param name="container">The container, such as mkv or avi, or the file's extension.</param>
    /// <returns>The issues, in a fixed order.</returns>
    public static IReadOnlyList<string> Find(IEnumerable<MediaStream> streams, IReadOnlyList<TrackInfo> tracks, string? container)
    {
        ArgumentNullException.ThrowIfNull(streams);
        ArgumentNullException.ThrowIfNull(tracks);
        var list = streams.ToList();
        var issues = new List<string>();
        if (tracks.Any(t => t.Language is null))
        {
            issues.Add(UnknownLanguage);
        }

        var audio = tracks.Where(t => t.Kind == TrackKind.Audio).ToList();
        if (audio.Count > 0 && audio.All(t => t.IsCommentary))
        {
            issues.Add(NoMainAudio);
        }

        if (audio.Count > 1 && !audio.Any(t => t.IsDefault))
        {
            issues.Add(NoDefaultAudio);
        }

        if (tracks.Any(t => t.Kind == TrackKind.Subtitle && !t.IsForced && (t.Title?.Contains("forced", StringComparison.OrdinalIgnoreCase) ?? false)))
        {
            issues.Add(ForcedNotFlagged);
        }

        var videos = list.Where(s => s.Type == MediaStreamType.Video).ToList();
        if (videos.FirstOrDefault()?.IsInterlaced == true)
        {
            issues.Add(Interlaced);
        }

        if (videos.Count > 1)
        {
            issues.Add(ExtraVideo);
        }

        if (IsOldContainer(container))
        {
            issues.Add(OldContainer);
        }

        return issues;
    }

    /// <summary>
    /// Gets whether a container is one that might have broken timestamps. A container can list several names, such
    /// as "mpegts,m2ts".
    /// </summary>
    /// <param name="container">The container or extension.</param>
    /// <returns>True for AVI, MPEG-TS and WMV.</returns>
    public static bool IsOldContainer(string? container)
        => (container ?? string.Empty).Split(',').Any(c => _oldContainers.Contains(c.Trim().TrimStart('.')));

    /// <summary>
    /// Reads whether the first video frame carries HDR10 brightness information.
    /// </summary>
    /// <param name="json">ffprobe's output for -show_entries frame=side_data_list of one frame.</param>
    /// <returns>The brightness information, or null if there is none.</returns>
    public static HdrBrightness? ParseBrightness(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("frames", out var frames) || frames.GetArrayLength() == 0
                || !frames[0].TryGetProperty("side_data_list", out var sides))
            {
                return null;
            }

            string? display = null;
            string? light = null;
            foreach (var side in sides.EnumerateArray())
            {
                var type = side.TryGetProperty("side_data_type", out var t) ? t.GetString() : null;
                if (string.Equals(type, "Mastering display metadata", StringComparison.OrdinalIgnoreCase))
                {
                    display = MasterDisplay(side);
                }
                else if (string.Equals(type, "Content light level metadata", StringComparison.OrdinalIgnoreCase)
                    && side.TryGetProperty("max_content", out var maxContent) && side.TryGetProperty("max_average", out var maxAverage))
                {
                    light = maxContent.GetInt32().ToString(CultureInfo.InvariantCulture) + "," + maxAverage.GetInt32().ToString(CultureInfo.InvariantCulture);
                }
            }

            return display is null && light is null ? null : new HdrBrightness(display, light);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    // x265's form: G(x,y)B(x,y)R(x,y)WP(x,y)L(max,min), chromaticity in 0.00002 steps and luminance in 0.0001 nits.
    private static string? MasterDisplay(JsonElement side)
    {
        double? Read(string name)
        {
            if (!side.TryGetProperty(name, out var value))
            {
                return null;
            }

            var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
            var parts = text?.Split('/');
            if (parts is null || parts.Length is 0 or > 2
                || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var top))
            {
                return null;
            }

            var bottom = 1.0;
            return parts.Length == 2 && !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out bottom) ? null : bottom == 0 ? null : top / bottom;
        }

        string Chroma(string name) => Math.Round((Read(name) ?? 0) * 50000).ToString(CultureInfo.InvariantCulture);
        string Light(string name) => Math.Round((Read(name) ?? 0) * 10000).ToString(CultureInfo.InvariantCulture);
        if (Read("red_x") is null || Read("max_luminance") is null)
        {
            return null;
        }

        return "G(" + Chroma("green_x") + "," + Chroma("green_y") + ")B(" + Chroma("blue_x") + "," + Chroma("blue_y") + ")R(" + Chroma("red_x") + "," + Chroma("red_y")
            + ")WP(" + Chroma("white_point_x") + "," + Chroma("white_point_y") + ")L(" + Light("max_luminance") + "," + Light("min_luminance") + ")";
    }
}

/// <summary>
/// HDR10 brightness information, in the form x265 takes it.
/// </summary>
/// <param name="MasterDisplay">The mastering display, such as G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,1), or null.</param>
/// <param name="MaxCll">The maximum content and frame-average light levels, such as "1000,400", or null.</param>
public sealed record HdrBrightness(string? MasterDisplay, string? MaxCll);

/// <summary>
/// The results of the checks that read a file, kept by path and size so a file is only read again once it changes.
/// </summary>
public static class FileChecks
{
    private const string FileName = "file-checks.json";
    private static readonly Lock _lock = new();
    private static Dictionary<string, FileCheck>? _checks;

    /// <summary>
    /// Gets what the checks found for a file, if it hasn't changed since.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="size">Its size now.</param>
    /// <returns>The results, or null if it hasn't been checked as it is.</returns>
    public static FileCheck? Get(string path, long size)
    {
        lock (_lock)
        {
            return Load().TryGetValue(path, out var check) && check.SizeBytes == size ? check : null;
        }
    }

    /// <summary>
    /// Saves what the checks found for a file.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="change">Changes the file's results, which start empty if the file changed.</param>
    /// <param name="size">The file's size.</param>
    public static void Update(string path, long size, Action<FileCheck> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_lock)
        {
            var checks = Load();
            if (!checks.TryGetValue(path, out var check) || check.SizeBytes != size)
            {
                check = new FileCheck { SizeBytes = size };
                checks[path] = check;
            }

            change(check);
            Save(checks);
        }
    }

    private static Dictionary<string, FileCheck> Load()
    {
        if (_checks is not null)
        {
            return _checks;
        }

        _checks = new Dictionary<string, FileCheck>(StringComparer.Ordinal);
        try
        {
            if (Plugin.Instance?.DataFolderPath is { } folder && File.Exists(Path.Combine(folder, FileName)))
            {
                _checks = JsonSerializer.Deserialize<Dictionary<string, FileCheck>>(File.ReadAllText(Path.Combine(folder, FileName))) ?? _checks;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Unreadable results are the same as none: the checks run again.
        }

        return _checks;
    }

    private static void Save(Dictionary<string, FileCheck> checks)
    {
        if (Plugin.Instance?.DataFolderPath is not { } folder)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, FileName);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(checks));
            File.Move(path + ".tmp", path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The results stay in memory until the server restarts.
        }
    }
}

/// <summary>
/// What reading one file found.
/// </summary>
public sealed class FileCheck
{
    /// <summary>Gets or sets the file's size when it was checked.</summary>
    public long SizeBytes { get; set; }

    /// <summary>Gets or sets whether the HDR brightness check found brightness information, or null if it hasn't run.</summary>
    public bool? HasHdrBrightness { get; set; }

    /// <summary>Gets or sets the issues the full read found, or null if it hasn't run.</summary>
    public IReadOnlyList<string>? ReadIssues { get; set; }
}
