// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.DataManager.Encoding;

/// <summary>
/// A picture to put in a file.
/// </summary>
/// <param name="Path">The picture.</param>
/// <param name="FileName">Its name in the file, which says what it is to Jellyfin and players, such as cover.jpg.</param>
/// <param name="MimeType">Its type, such as image/jpeg.</param>
public sealed record EmbeddedImage(string Path, string FileName, string MimeType);

/// <summary>
/// Jellyfin's details about an item, as tags and pictures to write into a file.
/// </summary>
/// <param name="Tags">The tags, in order.</param>
/// <param name="Images">The pictures.</param>
public sealed record MediaTags(IReadOnlyList<KeyValuePair<string, string>> Tags, IReadOnlyList<EmbeddedImage> Images)
{
    /// <summary>
    /// Writes the tags as an ffmpeg metadata file.
    /// </summary>
    /// <returns>The file's text.</returns>
    public string ToFfmetadata()
    {
        var text = new StringBuilder(";FFMETADATA1\n");
        foreach (var (key, value) in Tags)
        {
            text.Append(key).Append('=');
            foreach (var c in value.Replace("\r\n", "\n", StringComparison.Ordinal))
            {
                if (c is '=' or ';' or '#' or '\\' or '\n')
                {
                    text.Append('\\');
                }

                if (c != '\r')
                {
                    text.Append(c);
                }
            }

            text.Append('\n');
        }

        return text.ToString();
    }
}

/// <summary>
/// Reads what Jellyfin knows about an item, for naming and tagging its file.
/// </summary>
public static class ItemDetails
{
    // Jellyfin's embedded image extractor reads attachments by name: cover for the poster (or an episode's
    // picture), backdrop and logo. It reads only the poster for episodes.
    private static readonly (ImageType Type, string Name)[] _movieImages = [(ImageType.Primary, "cover"), (ImageType.Backdrop, "backdrop"), (ImageType.Logo, "logo")];

    /// <summary>
    /// Gets what a file's name can be made from.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="resolution">The new file's resolution, such as 1080p.</param>
    /// <param name="codec">The new file's codec, such as HEVC.</param>
    /// <returns>The parts.</returns>
    public static NameParts NameParts(BaseItem item, ILibraryManager libraryManager, string? resolution, string? codec)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(libraryManager);

        if (item is Episode episode)
        {
            var series = SeriesOf(episode, libraryManager);
            return new NameParts(
                true,
                episode.Name,
                episode.ProductionYear ?? episode.PremiereDate?.Year,
                series?.Name ?? (string.IsNullOrEmpty(episode.SeriesName) ? episode.FindSeriesName() : episode.SeriesName),
                series?.ProductionYear,
                episode.ParentIndexNumber,
                episode.IndexNumber,
                episode.IndexNumberEnd,
                series?.GetProviderId(MetadataProvider.Imdb),
                series?.GetProviderId(MetadataProvider.Tmdb),
                series?.GetProviderId(MetadataProvider.Tvdb),
                null,
                resolution,
                codec);
        }

        return new NameParts(
            false,
            item.Name,
            item.ProductionYear ?? item.PremiereDate?.Year,
            null,
            null,
            null,
            null,
            null,
            item.GetProviderId(MetadataProvider.Imdb),
            item.GetProviderId(MetadataProvider.Tmdb),
            item.GetProviderId(MetadataProvider.Tvdb),
            item.IsInMixedFolder ? null : Path.GetFileName(Path.GetDirectoryName(item.Path)),
            resolution,
            codec);
    }

    /// <summary>
    /// Gets the tags and pictures to write into an item's file. Matroska takes its own standard tags, plus the
    /// series, season and episode tags Jellyfin reads back. MP4 takes the tags Apple's players and Jellyfin read,
    /// and only the poster.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="isMp4">Whether the file is an mp4.</param>
    /// <returns>The tags and pictures.</returns>
    public static MediaTags Tags(BaseItem item, ILibraryManager libraryManager, bool isMp4)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(libraryManager);

        var tags = new List<KeyValuePair<string, string>>();
        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                tags.Add(new(key, value.Trim()));
            }
        }

        var episode = item as Episode;
        var series = episode is null ? null : SeriesOf(episode, libraryManager);
        var people = libraryManager.GetPeople(item);
        string Names(params PersonKind[] kinds) => string.Join("; ", people
            .Where(p => kinds.Contains(p.Type) && !string.IsNullOrWhiteSpace(p.Name))
            .Select(p => p.Name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(25));
        string Join(IEnumerable<string>? values) => string.Join("; ", (values ?? []).Where(v => !string.IsNullOrWhiteSpace(v)));

        var date = item.PremiereDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? item.ProductionYear?.ToString(CultureInfo.InvariantCulture);
        var genres = item.Genres is { Length: > 0 } own ? own : series?.Genres;
        var studios = item.Studios is { Length: > 0 } made ? made : series?.Studios;
        var seriesName = series?.Name ?? episode?.SeriesName;
        string? Number(int? value) => value?.ToString(CultureInfo.InvariantCulture);

        Add("title", item.Name);
        if (isMp4)
        {
            Add("synopsis", item.Overview);
            Add("description", string.IsNullOrWhiteSpace(item.Tagline) ? item.Overview : item.Tagline);
            Add("date", date);
            Add("genre", Join(genres));
            Add("composer", Names(PersonKind.Composer));
            if (episode is not null)
            {
                Add("show", seriesName);
                Add("season_number", Number(episode.ParentIndexNumber));
                Add("episode_sort", Number(episode.IndexNumber));
                if (episode.ParentIndexNumber is int season && episode.IndexNumber is int number)
                {
                    Add("episode_id", string.Create(CultureInfo.InvariantCulture, $"S{season:00}E{number:00}"));
                }

                Add("network", studios?.FirstOrDefault());
            }

            Add("media_type", episode is null ? "9" : "10");
        }
        else
        {
            Add("SYNOPSIS", item.Overview);
            Add("DESCRIPTION", item.Tagline);
            Add("DATE_RELEASED", date);
            Add("GENRE", Join(genres));
            Add("DIRECTOR", Names(PersonKind.Director));
            Add("WRITTEN_BY", Names(PersonKind.Writer));
            Add("PRODUCER", Names(PersonKind.Producer));
            Add("COMPOSER", Names(PersonKind.Composer));
            Add("ACTOR", Names(PersonKind.Actor, PersonKind.GuestStar));
            Add("PRODUCTION_STUDIO", Join(studios));
            Add("LAW_RATING", item.OfficialRating ?? series?.OfficialRating);
            if (item.CommunityRating is float rating && rating > 0)
            {
                // Matroska rates from 0 to 5; Jellyfin's community rating is out of 10.
                Add("RATING", (rating / 2).ToString("0.0", CultureInfo.InvariantCulture));
            }

            Add("IMDB", item.GetProviderId(MetadataProvider.Imdb));
            if (episode is null)
            {
                Add("TMDB", Prefixed("movie/", item.GetProviderId(MetadataProvider.Tmdb)));
                Add("TVDB2", Prefixed("movies/", item.GetProviderId(MetadataProvider.Tvdb)));
            }
            else
            {
                Add("TMDB", Prefixed("tv/", series?.GetProviderId(MetadataProvider.Tmdb)));
                Add("TVDB2", Prefixed("episodes/", episode.GetProviderId(MetadataProvider.Tvdb)) ?? Prefixed("series/", series?.GetProviderId(MetadataProvider.Tvdb)));
                Add("SHOW", seriesName);
                Add("SEASON_NUMBER", Number(episode.ParentIndexNumber));
                Add("EPISODE_SORT", Number(episode.IndexNumber));
                Add("PART_NUMBER", Number(episode.IndexNumber));
            }
        }

        var images = new List<EmbeddedImage>();
        if (episode is not null)
        {
            if ((Image(episode, ImageType.Primary, "cover") ?? Image(series, ImageType.Primary, "cover")) is { } cover)
            {
                images.Add(cover);
            }
        }
        else
        {
            foreach (var (type, name) in _movieImages)
            {
                if (Image(item, type, name) is { } image)
                {
                    images.Add(image);
                }
            }
        }

        // An mp4 holds pictures as cover art, which players only take as JPEG or PNG.
        if (isMp4)
        {
            images = images.Where(i => i.FileName.StartsWith("cover", StringComparison.Ordinal) && i.MimeType is "image/jpeg" or "image/png").Take(1).ToList();
        }

        return new MediaTags(tags, images);
    }

    private static string? Prefixed(string prefix, string? id) => string.IsNullOrWhiteSpace(id) ? null : prefix + id.Trim();

    private static Series? SeriesOf(Episode episode, ILibraryManager libraryManager)
    {
        if (episode.Series is { } series)
        {
            return series;
        }

        var id = episode.SeriesId.Equals(Guid.Empty) ? episode.FindSeriesId() : episode.SeriesId;
        return id.Equals(Guid.Empty) ? null : libraryManager.GetItemById(id) as Series;
    }

    // One of the item's own pictures, if Jellyfin has it saved on this server.
    private static EmbeddedImage? Image(BaseItem? item, ImageType type, string name)
    {
        var info = item?.GetImageInfo(type, 0);
        if (info is null || !info.IsLocalFile || string.IsNullOrEmpty(info.Path) || !File.Exists(info.Path))
        {
            return null;
        }

        var (mime, extension) = Path.GetExtension(info.Path).ToUpperInvariant() switch
        {
            ".JPG" or ".JPEG" => ("image/jpeg", ".jpg"),
            ".PNG" => ("image/png", ".png"),
            ".WEBP" => ("image/webp", ".webp"),
            _ => (null, null)
        };
        return mime is null ? null : new EmbeddedImage(info.Path, name + extension, mime);
    }
}
