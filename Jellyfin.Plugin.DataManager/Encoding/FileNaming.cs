// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.DataManager.Configuration;

namespace Jellyfin.Plugin.DataManager.Encoding;

/// <summary>
/// How re-encoded files are named.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NamingStyle
{
    /// <summary>Keep the file's current name.</summary>
    KeepCurrent,

    /// <summary>Jellyfin's recommended naming, from its docs.</summary>
    Jellyfin,

    /// <summary>The movie and episode names from the settings.</summary>
    Custom
}

/// <summary>
/// What a file's name can be made from.
/// </summary>
/// <param name="IsEpisode">Whether the item is an episode.</param>
/// <param name="Title">The movie's name, or the episode's title.</param>
/// <param name="Year">The year it came out.</param>
/// <param name="Series">The series, for an episode.</param>
/// <param name="SeriesYear">The year the series started.</param>
/// <param name="Season">The season number.</param>
/// <param name="Episode">The episode number.</param>
/// <param name="EpisodeEnd">The last episode number, for a file with more than one episode.</param>
/// <param name="ImdbId">The IMDb id: the movie's, or the series'.</param>
/// <param name="TmdbId">The TMDB id: the movie's, or the series'.</param>
/// <param name="TvdbId">The TVDB id: the movie's, or the series'.</param>
/// <param name="OwnFolder">The folder's name, when a movie has a folder of its own.</param>
/// <param name="Resolution">The new file's resolution, such as 1080p.</param>
/// <param name="Codec">The new file's codec, such as HEVC.</param>
public sealed record NameParts(
    bool IsEpisode,
    string? Title,
    int? Year,
    string? Series,
    int? SeriesYear,
    int? Season,
    int? Episode,
    int? EpisodeEnd,
    string? ImdbId,
    string? TmdbId,
    string? TvdbId,
    string? OwnFolder,
    string? Resolution,
    string? Codec);

/// <summary>
/// Names re-encoded files from Jellyfin's details. A name is a template such as "{Title} ({Year})": a part in
/// brackets or parentheses is left out when everything in it is unknown, and if anything else is unknown the
/// file keeps its current name.
/// </summary>
public static partial class FileNaming
{
    /// <summary>
    /// Jellyfin's recommended movie name: the name and year, as in "Film (2018)". A movie in its own folder is
    /// named after the folder, as Jellyfin's docs ask, when the folder already has that name.
    /// </summary>
    public const string JellyfinMovie = "{Title} ({Year})";

    /// <summary>
    /// Jellyfin's recommended episode name, as in "Series S01E01", or "Series S01E01-E02" for a file with two episodes.
    /// </summary>
    public const string JellyfinEpisode = "{Series} S{Season}E{Episode}";

    // A name is kept short of the 255 bytes most file systems allow, leaving room for a version and extension.
    private const int MaxLength = 180;

    /// <summary>
    /// Gets the name, without extension, that the settings give a file.
    /// </summary>
    /// <param name="config">The settings.</param>
    /// <param name="parts">What the name can be made from.</param>
    /// <returns>The name, or null to keep the current one.</returns>
    public static string? BaseName(PluginConfiguration config, NameParts parts)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(parts);

        switch (config.Naming)
        {
            case NamingStyle.Jellyfin:
                var name = Expand(parts.IsEpisode ? JellyfinEpisode : JellyfinMovie, parts);
                // The docs ask for a movie's files to share its folder's name, which may also hold provider ids.
                if (name is not null && !parts.IsEpisode && parts.OwnFolder is { Length: > 0 } folder && IsNamedFolder(folder, name))
                {
                    return Clean(folder);
                }

                return name;
            case NamingStyle.Custom:
                return Expand(parts.IsEpisode ? config.EpisodeNameTemplate : config.MovieNameTemplate, parts);
            default:
                return null;
        }
    }

    /// <summary>
    /// Fills in a template.
    /// </summary>
    /// <param name="template">The template, such as "{Title} ({Year})".</param>
    /// <param name="parts">What the name can be made from.</param>
    /// <returns>The name, or null if something it needs is unknown.</returns>
    public static string? Expand(string? template, NameParts parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (string.IsNullOrWhiteSpace(template))
        {
            return null;
        }

        var result = new StringBuilder();
        var i = 0;
        while (i < template.Length)
        {
            var c = template[i];
            if (c is '(' or '[')
            {
                var close = template.IndexOf(c == '(' ? ')' : ']', i + 1);
                if (close > i)
                {
                    // A group of only unknown values is left out, brackets and all.
                    var (text, tokens, known) = Fill(template[(i + 1)..close], parts);
                    if (tokens == 0 || known > 0)
                    {
                        result.Append(c).Append(text).Append(template[close]);
                    }

                    i = close + 1;
                    continue;
                }
            }

            var end = template.IndexOfAny(['(', '['], i + 1);
            var (plain, plainTokens, plainKnown) = Fill(end < 0 ? template[i..] : template[i..end], parts);
            if (plainKnown < plainTokens)
            {
                return null;
            }

            result.Append(plain);
            i = end < 0 ? template.Length : end;
        }

        var name = Clean(result.ToString());
        return name.Length == 0 ? null : name;
    }

    /// <summary>
    /// Gets a token's value.
    /// </summary>
    /// <param name="token">The token's name, such as Title.</param>
    /// <param name="parts">What the name can be made from.</param>
    /// <returns>The value, or null if it's unknown.</returns>
    public static string? Value(string token, NameParts parts)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(parts);
        return token.ToUpperInvariant() switch
        {
            "TITLE" => parts.Title,
            "YEAR" => parts.Year?.ToString(CultureInfo.InvariantCulture),
            "SERIES" => parts.Series,
            "SERIESYEAR" => parts.SeriesYear?.ToString(CultureInfo.InvariantCulture),
            "SEASON" => parts.Season?.ToString("00", CultureInfo.InvariantCulture),
            "EPISODE" => parts.Episode is int episode
                ? episode.ToString("00", CultureInfo.InvariantCulture) + (parts.EpisodeEnd is int last && last > episode ? "-E" + last.ToString("00", CultureInfo.InvariantCulture) : string.Empty)
                : null,
            "IMDBID" => parts.ImdbId,
            "TMDBID" => parts.TmdbId,
            "TVDBID" => parts.TvdbId,
            "RESOLUTION" => parts.Resolution,
            "CODEC" => parts.Codec,
            _ => null
        } is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
    }

    // Fills in the tokens in some text, counting them and how many were known.
    private static (string Text, int Tokens, int Known) Fill(string text, NameParts parts)
    {
        var result = new StringBuilder();
        int tokens = 0, known = 0, i = 0;
        while (i < text.Length)
        {
            var close = text[i] == '{' ? text.IndexOf('}', i + 1) : -1;
            if (close < 0)
            {
                result.Append(text[i]);
                i++;
                continue;
            }

            tokens++;
            if (Value(text[(i + 1)..close], parts) is { } value)
            {
                known++;
                result.Append(value);
            }

            i = close + 1;
        }

        return (result.ToString(), tokens, known);
    }

    // Makes a name safe on every file system: a colon becomes a dash, as in "Example: Title" becoming "Example - Title", and characters
    // Windows doesn't allow are dropped.
    private static string Clean(string name)
    {
        var text = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            switch (c)
            {
                case ':':
                    text.Append(i + 1 < name.Length && name[i + 1] == ' ' ? " -" : "-");
                    break;
                case '/' or '\\':
                    text.Append('-');
                    break;
                case '"':
                    text.Append('\'');
                    break;
                case '<' or '>' or '|' or '?' or '*':
                    break;
                default:
                    if (!char.IsControl(c))
                    {
                        text.Append(c);
                    }

                    break;
            }
        }

        var cleaned = string.Join(' ', text.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim(' ', '.', '-', '_');
        return cleaned.Length > MaxLength ? cleaned[..MaxLength].TrimEnd(' ', '.', '-', '_') : cleaned;
    }

    // Whether a folder is named like "Film (2018)", ignoring punctuation, with nothing after but provider ids such as
    // "[imdbid-tt1254207]". "Film.2018.1080p" isn't.
    private static bool IsNamedFolder(string folder, string name)
    {
        var wanted = name.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToList();
        var matched = 0;
        var i = 0;
        for (; i < folder.Length && matched < wanted.Count; i++)
        {
            if (!char.IsLetterOrDigit(folder[i]))
            {
                continue;
            }

            if (char.ToUpperInvariant(folder[i]) != wanted[matched])
            {
                return false;
            }

            matched++;
        }

        return matched == wanted.Count && ProviderIds().IsMatch(folder[i..]);
    }

    [GeneratedRegex(@"^[\s)\]}]*([\[({]\s*[a-z]+(id)?-[^\])}]+[\])}]\s*)*$", RegexOptions.IgnoreCase)]
    private static partial Regex ProviderIds();

    /// <summary>
    /// Gets every token a template can use.
    /// </summary>
    public static IReadOnlyList<string> Tokens { get; } = ["Title", "Year", "Series", "SeriesYear", "Season", "Episode", "ImdbId", "TmdbId", "TvdbId", "Resolution", "Codec"];
}
