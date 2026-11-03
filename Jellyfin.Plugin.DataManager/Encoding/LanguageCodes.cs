// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;

namespace Jellyfin.Plugin.DataManager.Encoding;

/// <summary>
/// Turns the many ways files name a language (eng, en, en-US, English, ger, deu) into one code per language, using
/// Jellyfin's own language list, so tracks can be matched to the languages the user keeps.
/// </summary>
public sealed class LanguageCodes
{
    // Codes that say the language isn't known, or that the track has no language.
    private static readonly HashSet<string> _unknown = new(StringComparer.OrdinalIgnoreCase) { "und", "unk", "zxx", "mis", "mul", "qaa", "none", "unknown" };

    private readonly ILocalizationManager _localization;
    private readonly ConcurrentDictionary<string, CultureDto?> _cultures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="LanguageCodes"/> class.
    /// </summary>
    /// <param name="localization">Jellyfin's localization manager, which knows the languages.</param>
    public LanguageCodes(ILocalizationManager localization)
    {
        _localization = localization;
    }

    /// <summary>
    /// Gets the code a language is matched by: its three-letter code as Jellyfin lists it, or the code as given
    /// when Jellyfin doesn't know it.
    /// </summary>
    /// <param name="language">The language as a file or setting names it.</param>
    /// <returns>The code, or null when no language is set.</returns>
    public string? Normalize(string? language)
    {
        var code = language?.Trim();
        if (string.IsNullOrEmpty(code) || _unknown.Contains(code))
        {
            return null;
        }

        var culture = Find(code);
        if (culture is null)
        {
            // en-US and pt_BR are matched by their language alone.
            var dash = code.IndexOfAny(['-', '_']);
            if (dash > 0)
            {
                code = code[..dash];
                culture = Find(code);
            }
        }

        // Language codes are lower case by convention (ISO 639), and the page compares them as they are.
#pragma warning disable CA1308
        return (culture?.ThreeLetterISOLanguageName ?? code).ToLowerInvariant();
#pragma warning restore CA1308
    }

    /// <summary>
    /// Gets a language's name, such as English, for a normalized code.
    /// </summary>
    /// <param name="code">The code from <see cref="Normalize"/>.</param>
    /// <returns>The name, or the code when Jellyfin doesn't know it.</returns>
    public string DisplayName(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return Find(code)?.DisplayName ?? code;
    }

    /// <summary>
    /// Gets a file's audio and subtitle tracks from what Jellyfin knows about it. Subtitle files beside the video
    /// aren't included: a re-encode leaves them alone.
    /// </summary>
    /// <param name="streams">The file's streams, as Jellyfin lists them.</param>
    /// <returns>The tracks.</returns>
    public IReadOnlyList<TrackInfo> Tracks(IEnumerable<MediaStream> streams)
    {
        ArgumentNullException.ThrowIfNull(streams);
        return streams
            .Where(s => !s.IsExternal && (s.Type == MediaStreamType.Audio || s.Type == MediaStreamType.Subtitle))
            .Select(s => new TrackInfo(
                s.Index,
                s.Type == MediaStreamType.Audio ? TrackKind.Audio : TrackKind.Subtitle,
                Normalize(s.Language),
                s.Codec ?? string.Empty,
                s.IsForced,
                s.IsDefault,
                s.BitRate ?? 0,
                s.Channels ?? 0,
                string.IsNullOrWhiteSpace(s.Title) ? null : s.Title,
                string.IsNullOrWhiteSpace(s.Profile) ? null : s.Profile,
                s.IsHearingImpaired))
            .ToList();
    }

    private CultureDto? Find(string code) => _cultures.GetOrAdd(code, c => _localization.FindLanguageInfo(c));
}
