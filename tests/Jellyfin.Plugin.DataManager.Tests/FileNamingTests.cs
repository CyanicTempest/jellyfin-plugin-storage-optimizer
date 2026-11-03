// SPDX-License-Identifier: GPL-3.0-only
using Jellyfin.Plugin.DataManager.Configuration;
using Jellyfin.Plugin.DataManager.Encoding;
using Xunit;

namespace Jellyfin.Plugin.DataManager.Tests;

public sealed class FileNamingTests
{
    [Fact]
    public void Expand_fills_in_a_movie_template() => Assert.Equal("Example Film (2008)", FileNaming.Expand("{Title} ({Year})", Movie("Example Film", 2008)));

    [Fact]
    public void Expand_leaves_out_a_group_when_everything_in_it_is_unknown() => Assert.Equal("Example Film", FileNaming.Expand("{Title} ({Year})", Movie("Example Film", null)));

    [Fact]
    public void Expand_gives_null_when_a_plain_token_is_unknown() => Assert.Null(FileNaming.Expand("{Title} {Resolution}", Movie("Example Film", 2008)));

    [Fact]
    public void Expand_gives_null_for_an_empty_template()
    {
        Assert.Null(FileNaming.Expand(null, Movie("Example Film", 2008)));
        Assert.Null(FileNaming.Expand("  ", Movie("Example Film", 2008)));
    }

    [Fact]
    public void Expand_makes_a_name_safe_for_every_file_system()
    {
        Assert.Equal("Example - Title (2008)", FileNaming.Expand("{Title} ({Year})", Movie("Example: Title", 2008)));
        Assert.Equal("What (2008)", FileNaming.Expand("{Title} ({Year})", Movie("What?*", 2008)));
        Assert.Equal("A-B (2008)", FileNaming.Expand("{Title} ({Year})", Movie("A/B", 2008)));
    }

    [Fact]
    public void Expand_keeps_a_name_short_enough_for_the_file_system()
    {
        var name = FileNaming.Expand("{Title}", Movie(new string('a', 400), 2008));

        Assert.NotNull(name);
        Assert.True(name.Length <= 180);
    }

    [Fact]
    public void Value_pads_season_and_episode_and_joins_a_double_episode()
    {
        var parts = Episode(season: 1, episode: 1, episodeEnd: 2);

        Assert.Equal("01", FileNaming.Value("Season", parts));
        Assert.Equal("01-E02", FileNaming.Value("Episode", parts));
        Assert.Null(FileNaming.Value("Resolution", parts));
        Assert.Null(FileNaming.Value("NotAToken", parts));
    }

    [Fact]
    public void BaseName_keeps_the_current_name_by_default() => Assert.Null(FileNaming.BaseName(new PluginConfiguration(), Movie("Example Film", 2008)));

    [Fact]
    public void BaseName_uses_Jellyfins_naming_for_movies_and_episodes()
    {
        var config = new PluginConfiguration { Naming = NamingStyle.Jellyfin };

        Assert.Equal("Example Film (2008)", FileNaming.BaseName(config, Movie("Example Film", 2008)));
        Assert.Equal("Example Show S01E03", FileNaming.BaseName(config, Episode(season: 1, episode: 3)));
    }

    [Fact]
    public void BaseName_follows_a_movie_folder_that_already_has_the_name_and_provider_ids()
    {
        var config = new PluginConfiguration { Naming = NamingStyle.Jellyfin };
        var parts = Movie("Example Film", 2008) with { OwnFolder = "Example Film (2008) [imdbid-tt1234567]" };

        Assert.Equal("Example Film (2008) [imdbid-tt1234567]", FileNaming.BaseName(config, parts));
    }

    [Fact]
    public void BaseName_ignores_a_folder_named_after_a_release()
    {
        var config = new PluginConfiguration { Naming = NamingStyle.Jellyfin };
        var parts = Movie("Example Film", 2008) with { OwnFolder = "Example.Film.2008.1080p" };

        Assert.Equal("Example Film (2008)", FileNaming.BaseName(config, parts));
    }

    [Fact]
    public void BaseName_uses_the_custom_template()
    {
        var config = new PluginConfiguration { Naming = NamingStyle.Custom, MovieNameTemplate = "{Title} - {Year}" };

        Assert.Equal("Example Film - 2008", FileNaming.BaseName(config, Movie("Example Film", 2008)));
    }

    private static NameParts Movie(string? title, int? year)
        => new(false, title, year, null, null, null, null, null, null, null, null, null, null, null);

    private static NameParts Episode(int season, int episode, int? episodeEnd = null)
        => new(true, "Pilot", 2020, "Example Show", 2020, season, episode, episodeEnd, null, null, null, null, null, null);
}
