// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.IO;
using Jellyfin.Plugin.DataManager.Services;
using Xunit;

namespace Jellyfin.Plugin.DataManager.Tests;

public sealed class LeftoverTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "so-tests-" + Guid.NewGuid().ToString("N"));

    public LeftoverTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, true);

    [Theory]
    [InlineData("Example Film.sample.mkv", true)]
    [InlineData("sample.mkv", true)]
    [InlineData("Sample/clip.mkv", true)]
    [InlineData("Example Film.mkv", false)]
    [InlineData("Example Film.sample.srt", false)]
    [InlineData("samples.mkv", false)]
    public void IsSample_recognises_sample_clips_only(string relative, bool expected)
        => Assert.Equal(expected, LeftoverCleaner.IsSample(Path.Combine(Path.GetTempPath(), "media", "Example Film", relative)));

    [Fact]
    public void IsOrphanedTrickplay_is_true_when_no_video_is_left()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_folder, "Example Film.trickplay")).FullName;

        Assert.True(LeftoverCleaner.IsOrphanedTrickplay(folder));
    }

    [Fact]
    public void IsOrphanedTrickplay_is_false_while_the_video_is_still_there()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_folder, "Example Film.trickplay")).FullName;
        File.WriteAllText(Path.Combine(_folder, "Example Film.mkv"), string.Empty);

        Assert.False(LeftoverCleaner.IsOrphanedTrickplay(folder));
    }

    [Fact]
    public void IsOrphanedTrickplay_is_false_for_other_folders_and_missing_ones()
    {
        var other = Directory.CreateDirectory(Path.Combine(_folder, "Extras")).FullName;

        Assert.False(LeftoverCleaner.IsOrphanedTrickplay(other));
        Assert.False(LeftoverCleaner.IsOrphanedTrickplay(Path.Combine(_folder, "Gone.trickplay")));
    }
}
