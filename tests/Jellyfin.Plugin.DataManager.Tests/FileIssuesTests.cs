// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.DataManager.Encoding;
using Jellyfin.Plugin.DataManager.Services;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.DataManager.Tests;

public class FileIssuesTests
{
    private static readonly MediaStream _video = new() { Type = MediaStreamType.Video, Index = 0 };

    private static IReadOnlyList<string> Find(IReadOnlyList<TrackInfo> tracks, string container = "mkv", params MediaStream[] streams)
        => FileIssues.Find(streams.Length == 0 ? [_video] : streams, tracks, container);

    [Fact]
    public void A_tidy_file_has_no_issues()
        => Assert.Empty(Find([new(1, TrackKind.Audio, "eng", "ac3", false, true, 0, 6)]));

    [Fact]
    public void Finds_each_issue_from_the_tracks()
    {
        Assert.Equal([FileIssues.UnknownLanguage], Find([new(1, TrackKind.Audio, null, "aac", false, true, 0, 2)]));
        Assert.Equal([FileIssues.NoMainAudio], Find([new(1, TrackKind.Audio, "eng", "aac", false, true, 0, 2, "Commentary")]));
        Assert.Equal([FileIssues.NoDefaultAudio], Find([new(1, TrackKind.Audio, "eng", "ac3", false, false, 0, 6), new(2, TrackKind.Audio, "fre", "ac3", false, false, 0, 6)]));
        Assert.Equal([FileIssues.ForcedNotFlagged], Find([new(1, TrackKind.Audio, "eng", "ac3", false, true, 0, 6), new(2, TrackKind.Subtitle, "eng", "subrip", false, false, 0, 0, "English (Forced)")]));
    }

    [Fact]
    public void Finds_interlacing_extra_video_and_older_containers()
    {
        var audio = new List<TrackInfo> { new(1, TrackKind.Audio, "eng", "ac3", false, true, 0, 6) };
        Assert.Equal([FileIssues.Interlaced], Find(audio, "mkv", new MediaStream { Type = MediaStreamType.Video, IsInterlaced = true }));
        Assert.Equal([FileIssues.ExtraVideo], Find(audio, "mkv", _video, new MediaStream { Type = MediaStreamType.Video, Index = 2 }));
        Assert.Empty(Find(audio, "mkv", _video, new MediaStream { Type = MediaStreamType.EmbeddedImage, Index = 2 }));
        Assert.Equal([FileIssues.OldContainer], Find(audio, "avi"));
        Assert.Equal([FileIssues.OldContainer], Find(audio, "mpegts,m2ts"));
        Assert.Equal([FileIssues.OldContainer], Find(audio, ".wmv"));
    }

    [Fact]
    public void ParseBrightness_reads_the_mastering_display_in_x265_units()
    {
        const string Json = """
            {"frames":[{"side_data_list":[
              {"side_data_type":"Mastering display metadata","red_x":"34000/50000","red_y":"16000/50000","green_x":"13250/50000","green_y":"34500/50000","blue_x":"7500/50000","blue_y":"3000/50000","white_point_x":"15635/50000","white_point_y":"16450/50000","min_luminance":"50/10000","max_luminance":"10000000/10000"},
              {"side_data_type":"Content light level metadata","max_content":1000,"max_average":400}]}]}
            """;

        var brightness = FileIssues.ParseBrightness(Json)!;

        Assert.Equal("G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,50)", brightness.MasterDisplay);
        Assert.Equal("1000,400", brightness.MaxCll);
        Assert.Null(FileIssues.ParseBrightness("{\"frames\":[{}]}"));
        Assert.Null(FileIssues.ParseBrightness("nonsense"));
    }

    [Fact]
    public void Drift_compares_the_audio_and_video_lengths_from_either_place_they_are_kept()
    {
        const string Json = """
            {"streams":[
              {"index":0,"codec_type":"video","tags":{"DURATION":"00:10:00.000000000"}},
              {"index":1,"codec_type":"audio","tags":{"DURATION-eng":"00:10:03.500000000"}},
              {"index":2,"codec_type":"audio","duration":"600.25"}]}
            """;

        Assert.Equal(3.5, OutputCheck.Drift(Json)!.Value, 3);
        Assert.Equal(0.25, OutputCheck.Drift(Json, 2)!.Value, 3);
        Assert.Null(OutputCheck.Drift("{\"streams\":[{\"codec_type\":\"video\"}]}"));
    }

    [Fact]
    public void The_new_file_must_stay_in_step()
    {
        var source = new List<TrackInfo> { new(1, TrackKind.Audio, "eng", "ac3", false, true, 0, 6) };
        const string Original = """{"streams":[{"index":0,"codec_type":"video","duration":"600"},{"index":1,"codec_type":"audio","codec_name":"ac3","channels":6,"duration":"600.1","tags":{"language":"eng"}}]}""";
        const string Drifted = """{"streams":[{"index":0,"codec_type":"video","duration":"600"},{"index":1,"codec_type":"audio","codec_name":"ac3","channels":6,"duration":"604","tags":{"language":"eng"}}]}""";

        Assert.Null(OutputCheck.Problem(source, Original, null, l => l, Original));
        Assert.Equal("The new file's audio runs 4.0s longer than its video, so it would play out of step.", OutputCheck.Problem(source, Drifted, null, l => l, Original));
        Assert.Null(OutputCheck.Problem(source, Drifted, null, l => l));
    }

    [Theory]
    [InlineData("[matroska @ 0x1] Application provided invalid, non monotonically increasing dts to muxer in stream 1: 100 >= 90", true)]
    [InlineData("[avi @ 0x1] Timestamps are unset in a packet for stream 0.", true)]
    [InlineData("[h264 @ 0x1] error while decoding MB 1 2", false)]
    public void IsTimestampError_spots_timestamp_complaints(string line, bool expected)
        => Assert.Equal(expected, OutputCheck.IsTimestampError(line));

    [Fact]
    public void HasColourTags_needs_all_three()
    {
        Assert.True(OutputCheck.HasColourTags("""{"streams":[{"codec_type":"video","color_primaries":"bt2020","color_transfer":"smpte2084","color_space":"bt2020nc"}]}"""));
        Assert.False(OutputCheck.HasColourTags("""{"streams":[{"codec_type":"video","color_transfer":"smpte2084"}]}"""));
    }

    [Fact]
    public void Build_deinterlaces_rebuilds_timestamps_and_writes_brightness_when_asked()
    {
        var hdr = new SourceVideo(IsMatroska: true, IsMp4: false, IsTenBit: true, IsHdr: true, ColorTransfer: "smpte2084");
        var extras = EncodeExtras.None with { Deinterlace = true, RebuildTimestamps = true, HdrBrightness = new HdrBrightness("G(1,2)B(3,4)R(5,6)WP(7,8)L(9,10)", "1000,400") };

        var args = FfmpegArguments.Build("/in.ts", "/out.mkv", "libx265", QualityPreset.Balanced, EncodeEffort.Standard, hdr, "mkv", extras: extras).ToList();

        Assert.True(args.IndexOf("+genpts") < args.IndexOf("-i"));
        Assert.StartsWith("bwdif=", args[args.IndexOf("-filter:v:0") + 1], System.StringComparison.Ordinal);
        Assert.Contains("master-display=G(1,2)B(3,4)R(5,6)WP(7,8)L(9,10)", args[args.IndexOf("-x265-params") + 1], System.StringComparison.Ordinal);
        Assert.Contains("max-cll=1000,400", args[args.IndexOf("-x265-params") + 1], System.StringComparison.Ordinal);

        var plain = FfmpegArguments.Build("/in.mkv", "/out.mkv", "libx265", QualityPreset.Balanced, EncodeEffort.Standard, hdr, "mkv").ToList();
        Assert.DoesNotContain("+genpts", plain);
        Assert.DoesNotContain("-filter:v:0", plain);
    }
}
