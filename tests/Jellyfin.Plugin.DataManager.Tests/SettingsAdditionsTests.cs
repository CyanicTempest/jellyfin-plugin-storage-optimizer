// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.IO;
using Jellyfin.Plugin.DataManager.Configuration;
using Jellyfin.Plugin.DataManager.Encoding;
using Jellyfin.Plugin.DataManager.Services;
using Xunit;

namespace Jellyfin.Plugin.DataManager.Tests;

public sealed class SettingsAdditionsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "so-recycle-" + Guid.NewGuid().ToString("N"));

    public SettingsAdditionsTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, true);

    [Theory]
    [InlineData(false, 1, 7, 12, true)]
    [InlineData(true, 1, 7, 3, true)]
    [InlineData(true, 1, 7, 7, false)]
    [InlineData(true, 1, 7, 0, false)]
    [InlineData(true, 22, 6, 23, true)]
    [InlineData(true, 22, 6, 2, true)]
    [InlineData(true, 22, 6, 12, false)]
    [InlineData(true, 5, 5, 12, true)]
    public void Encodes_start_only_within_the_hours_set(bool limit, int from, int until, int hour, bool starts)
    {
        var config = new PluginConfiguration { LimitEncodingHours = limit, EncodeFromHour = from, EncodeUntilHour = until };
        var message = EncodeService.OutsideHours(config, new DateTime(2026, 11, 8, hour, 30, 0, DateTimeKind.Local));
        Assert.Equal(starts, message is null);
    }

    [Fact]
    public void The_wait_message_names_the_start_hour()
    {
        var config = new PluginConfiguration { LimitEncodingHours = true, EncodeFromHour = 22, EncodeUntilHour = 6 };
        Assert.Equal("Waiting until 10 pm, when encoding is set to start.", EncodeService.OutsideHours(config, new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Local)));
    }

    [Fact]
    public void Recycling_moves_the_file_into_a_hidden_folder_beside_it_and_back()
    {
        var film = Path.Combine(_folder, "Example Film (2008).mkv");
        File.WriteAllText(film, "video");

        var recycled = RecycleBin.Recycle(film, 7, DateTime.UtcNow);

        Assert.False(File.Exists(film));
        Assert.Equal(Path.Combine(_folder, RecycleBin.FolderName, "Example Film (2008).mkv"), recycled);
        Assert.Equal("video", File.ReadAllText(recycled));

        File.WriteAllText(film, "again");
        Assert.EndsWith("Example Film (2008) (2).mkv", RecycleBin.Recycle(film, 7, DateTime.UtcNow), StringComparison.Ordinal);

        RecycleBin.Restore(recycled, film);
        Assert.Equal("video", File.ReadAllText(film));
    }

    [Fact]
    public void New_settings_start_on_what_was_agreed()
    {
        var config = new PluginConfiguration();
        Assert.Equal(0, config.RecycleDays);
        Assert.True(config.LowPriority);
        Assert.False(config.NeverEncodeOnCpu);
        Assert.False(config.LimitEncodingHours);
        Assert.Equal(1, config.KeepFreeGigabytes);
        Assert.Equal(300, config.SuggestMinimumMegabytes);
        Assert.Equal(25, config.SuggestMinimumPercent);
        Assert.Equal(AudioChoice.KeepAll, config.DefaultAudio);
        Assert.Equal(TrackTitleMode.Clear, config.AudioTitles);
        Assert.False(config.CheckHdrBrightness);
        Assert.False(config.CheckFilesWhenQueued);
        Assert.True(config.DeinterlaceWhenEncoding);
    }
}
