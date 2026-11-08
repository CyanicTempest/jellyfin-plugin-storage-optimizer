// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Generic;
using Jellyfin.Plugin.DataManager.Encoding;
using Xunit;

namespace Jellyfin.Plugin.DataManager.Tests;

public class OutputCheckTests
{
    private static readonly List<TrackInfo> _source =
    [
        new(1, TrackKind.Audio, "eng", "ac3", false, true, 0, 6),
        new(2, TrackKind.Audio, "eng", "aac", false, false, 0, 2, "Commentary"),
        new(3, TrackKind.Subtitle, "eng", "subrip", false, false, 0, 0)
    ];

    private static string Json(params string[] streams) => "{\"streams\":[" + string.Join(',', streams) + "]}";

    private const string Video = "{\"index\":0,\"codec_type\":\"video\",\"codec_name\":\"hevc\",\"disposition\":{\"attached_pic\":0}}";
    private const string Cover = "{\"index\":9,\"codec_type\":\"video\",\"codec_name\":\"mjpeg\",\"disposition\":{\"attached_pic\":1}}";
    private const string Main = "{\"index\":1,\"codec_type\":\"audio\",\"codec_name\":\"ac3\",\"channels\":6,\"tags\":{\"language\":\"eng\"}}";
    private const string Commentary = "{\"index\":2,\"codec_type\":\"audio\",\"codec_name\":\"aac\",\"channels\":2,\"tags\":{\"language\":\"eng\",\"title\":\"Commentary\"}}";
    private const string Sub = "{\"index\":3,\"codec_type\":\"subtitle\",\"codec_name\":\"subrip\",\"tags\":{\"language\":\"eng\"}}";

    private static string? Check(string json, TrackPlan? plan = null) => OutputCheck.Problem(_source, json, plan, l => l);

    [Fact]
    public void A_complete_file_passes_and_cover_art_isnt_counted_as_video()
    {
        Assert.Null(Check(Json(Video, Main, Commentary, Sub)));
        Assert.Null(Check(Json(Video, Cover, Main, Commentary, Sub)));
    }

    [Fact]
    public void Missing_or_extra_video_fails()
    {
        Assert.Equal("The new file has no video.", Check(Json(Main, Commentary, Sub)));
        Assert.Equal("The new file has 2 video streams instead of one.", Check(Json(Video, Video, Main, Commentary, Sub)));
    }

    [Fact]
    public void Track_counts_must_match_the_plan()
    {
        Assert.Equal("The new file has 1 audio track instead of 2 and 0 subtitle tracks instead of 1.", Check(Json(Video, Main)));

        var plan = TrackSelection.Choose(_source, AudioChoice.KeepAll, SubtitleChoice.RemoveAll, []);
        Assert.Null(Check(Json(Video, Main, Commentary), plan));
    }

    [Fact]
    public void A_file_left_with_only_a_commentary_fails()
    {
        var plan = new TrackPlan([_source[1], _source[2]], [_source[0]], false);
        Assert.Equal("The new file has no main audio, only a commentary or none at all.", Check(Json(Video, Commentary, Sub), plan));
    }

    [Fact]
    public void Unreadable_output_fails()
        => Assert.Equal("The new file couldn't be read.", Check("not json"));
}
