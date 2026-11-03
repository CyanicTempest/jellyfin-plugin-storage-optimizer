// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.DataManager.Services;
using Xunit;

namespace Jellyfin.Plugin.DataManager.Tests;

public sealed class SidecarsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "so-tests-" + Guid.NewGuid().ToString("N"));

    public SidecarsTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, true);

    [Theory]
    [InlineData("Film.mkv", true)]
    [InlineData("Film.MKV", true)]
    [InlineData("Film.mp4", true)]
    [InlineData("Film.en.srt", false)]
    [InlineData("Film.nfo", false)]
    [InlineData("Film", false)]
    public void IsVideo_goes_by_extension(string name, bool expected) => Assert.Equal(expected, Sidecars.IsVideo(name));

    [Fact]
    public void Find_returns_only_files_named_after_the_video()
    {
        var video = Touch("Example Film (2008).mkv");
        Touch("Example Film (2008).en.srt");
        Touch("Example Film (2008).nfo");
        Touch("Example Film (2008)-poster.jpg");
        Touch("Example Film (2008) 2.srt");
        Touch("Other.en.srt");
        Touch("notes.txt");

        var names = Sidecars.Find(video).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(["Example Film (2008)-poster.jpg", "Example Film (2008).en.srt", "Example Film (2008).nfo"], names);
    }

    [Fact]
    public void Find_returns_nothing_when_another_video_shares_the_name()
    {
        var video = Touch("Example Film.mkv");
        Touch("Example Film.mp4");
        Touch("Example Film.en.srt");

        Assert.Empty(Sidecars.Find(video));
    }

    [Fact]
    public void Find_gives_a_file_to_the_video_with_the_longer_name()
    {
        var shorter = Touch("Example Film.mkv");
        var longer = Touch("Example Film.Extended.mkv");
        Touch("Example Film.en.srt");
        Touch("Example Film.Extended.en.srt");

        Assert.Equal(["Example Film.en.srt"], Sidecars.Find(shorter).Select(Path.GetFileName).ToList());
        Assert.Equal(["Example Film.Extended.en.srt"], Sidecars.Find(longer).Select(Path.GetFileName).ToList());
    }

    [Fact]
    public void Find_returns_nothing_for_a_missing_folder() => Assert.Empty(Sidecars.Find(Path.Combine(_folder, "missing", "Film.mkv")));

    [Fact]
    public void Renamed_keeps_the_part_after_the_video_name()
    {
        var old = Path.Combine(_folder, "example.film.2008.en.srt");

        var renamed = Sidecars.Renamed(old, "example.film.2008", "Example Film (2008)");

        Assert.Equal(Path.Combine(_folder, "Example Film (2008).en.srt"), renamed);
    }

    private string Touch(string name)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, string.Empty);
        return path;
    }
}
