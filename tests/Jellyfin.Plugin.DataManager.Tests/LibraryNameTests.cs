// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Generic;
using Jellyfin.Plugin.DataManager.Services;
using Xunit;

namespace Jellyfin.Plugin.DataManager.Tests;

public class LibraryNameTests
{
    private static readonly List<(string Name, string? Type, IEnumerable<string> Locations)> _libraries =
    [
        ("Movies", "movies", ["/media/movies"]),
        ("Kids films", "movies", ["/media/movies/kids/"]),
        ("Shows", "tvshows", ["/media/shows", "/mnt/more shows"]),
        ("Collections", "boxsets", ["/media"])
    ];

    [Theory]
    [InlineData("/media/movies/Film (2008)/trailers/Trailer.mkv", "Movies")]
    [InlineData("/media/movies/kids/Film (2010)/extras/Clip.mkv", "Kids films")]
    [InlineData("/mnt/more shows/Show/Season 1/Show S01E01.mkv", "Shows")]
    [InlineData("/media/moviesextra/Film.mkv", null)]
    [InlineData("/elsewhere/Film.mkv", null)]
    public void LibraryByFolder_picks_the_deepest_library_folder(string path, string? expected)
        => Assert.Equal(expected, ReportUpdater.LibraryByFolder(_libraries, path));
}
