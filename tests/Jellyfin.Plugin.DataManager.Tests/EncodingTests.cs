// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.DataManager.Encoding;
using Jellyfin.Plugin.DataManager.Services;
using Xunit;

namespace Jellyfin.Plugin.DataManager.Tests;

public sealed class EncodingTests
{
    private static readonly SourceVideo _plainSource = new(IsMatroska: true, IsMp4: false, IsTenBit: false, IsHdr: false, ColorTransfer: null);

    [Fact]
    public void ToFfmetadata_escapes_the_characters_ffmpeg_treats_as_syntax()
    {
        var tags = new MediaTags([new("title", "A=B;C#D\\E\nF")], []);

        Assert.Equal(";FFMETADATA1\ntitle=A\\=B\\;C\\#D\\\\E\\\nF\n", tags.ToFfmetadata());
    }

    [Fact]
    public void Build_passes_a_hostile_file_name_as_one_argument()
    {
        const string Input = "/media/Example \"Film\"; rm -rf ~ $(whoami) (2008).mkv";
        const string Output = "/media/Example 'Film' (2008) - HEVC.mkv";

        var args = FfmpegArguments.Build(Input, Output, "libx265", QualityPreset.Balanced, EncodeEffort.Standard, _plainSource, "mkv");

        var at = args.ToList().IndexOf("-i");
        Assert.True(at >= 0);
        Assert.Equal(Input, args[at + 1]);
        Assert.Equal(Output, args[^1]);
        Assert.Contains("-nostdin", args);
        Assert.Contains("-y", args);
        Assert.Equal(1, args.Count(a => a == Input));
    }

    [Fact]
    public void Build_maps_only_the_main_video_and_the_tracks_kept()
    {
        var tracks = new List<TrackInfo>
        {
            new(1, TrackKind.Audio, "eng", "ac3", false, true, 384000, 6),
            new(2, TrackKind.Audio, "fre", "ac3", false, false, 384000, 6),
            new(3, TrackKind.Subtitle, "eng", "subrip", false, false, 0, 0)
        };
        var plan = TrackSelection.Choose(tracks, AudioChoice.KeepLanguages, SubtitleChoice.KeepLanguages, ["eng"]);

        var args = FfmpegArguments.Build("/media/in.mkv", "/media/out.mkv", FfmpegArguments.CopyVideo, QualityPreset.Balanced, EncodeEffort.Standard, _plainSource, "mkv", tracks: plan);

        Assert.Contains("0:V:0", args);
        Assert.Contains("0:1", args);
        Assert.Contains("0:3", args);
        Assert.DoesNotContain("0:2", args);
    }

    [Fact]
    public void TrackSelection_keeps_chosen_languages_and_tracks_with_no_language()
    {
        var tracks = new List<TrackInfo>
        {
            new(1, TrackKind.Audio, "eng", "ac3", false, true, 0, 6),
            new(2, TrackKind.Audio, "fre", "ac3", false, false, 0, 6),
            new(3, TrackKind.Audio, null, "aac", false, false, 0, 2),
            new(4, TrackKind.Subtitle, "eng", "subrip", false, false, 0, 0),
            new(5, TrackKind.Subtitle, "fre", "subrip", false, false, 0, 0)
        };

        var plan = TrackSelection.Choose(tracks, AudioChoice.KeepLanguages, SubtitleChoice.KeepLanguages, ["eng"]);

        Assert.Equal([1, 3, 4], plan.Kept.Select(t => t.Index).ToList());
        Assert.Equal([2, 5], plan.Removed.Select(t => t.Index).ToList());
        Assert.False(plan.AudioFallback);
    }

    [Fact]
    public void TrackSelection_never_leaves_a_file_without_audio()
    {
        var tracks = new List<TrackInfo>
        {
            new(1, TrackKind.Audio, "fre", "ac3", false, false, 0, 6),
            new(2, TrackKind.Audio, "ger", "ac3", false, true, 0, 6)
        };

        var plan = TrackSelection.Choose(tracks, AudioChoice.KeepLanguages, SubtitleChoice.KeepAll, ["eng"]);

        Assert.Equal([2], plan.Kept.Select(t => t.Index).ToList());
        Assert.True(plan.AudioFallback);
    }

    [Fact]
    public void TrackSelection_can_remove_all_subtitles_or_keep_only_forced_ones()
    {
        var tracks = new List<TrackInfo>
        {
            new(1, TrackKind.Audio, "eng", "ac3", false, true, 0, 6),
            new(2, TrackKind.Subtitle, "eng", "subrip", false, false, 0, 0),
            new(3, TrackKind.Subtitle, "eng", "subrip", true, false, 0, 0)
        };

        Assert.Equal([1], TrackSelection.Choose(tracks, AudioChoice.KeepAll, SubtitleChoice.RemoveAll, []).Kept.Select(t => t.Index).ToList());
        Assert.Equal([1, 3], TrackSelection.Choose(tracks, AudioChoice.KeepAll, SubtitleChoice.KeepForced, []).Kept.Select(t => t.Index).ToList());
    }

    [Theory]
    [InlineData(3840, 2160, 2160)]
    [InlineData(1920, 1080, 1080)]
    [InlineData(1920, 800, 1080)]
    [InlineData(1280, 720, 720)]
    [InlineData(720, 576, 576)]
    [InlineData(640, 480, 480)]
    [InlineData(0, 0, 0)]
    public void ResolutionClass_compares_letterboxed_and_full_frame_copies_fairly(int width, int height, int expected)
        => Assert.Equal(expected, DuplicateFinder.ResolutionClass(width, height));

    [Fact]
    public void SavingsEstimator_guesses_an_audio_bitrate_only_when_it_is_unknown()
    {
        Assert.True(SavingsEstimator.AudioBitrate(0) > 0);
        Assert.Equal(640000, SavingsEstimator.AudioBitrate(640000));
    }

    [Fact]
    public void SavingsEstimator_expects_HEVC_to_need_less_than_older_codecs_do()
    {
        Assert.Equal(0.6, SavingsEstimator.HevcBitrateFactor("h264"));
        Assert.Equal(1.0, SavingsEstimator.HevcBitrateFactor("hevc"));
        Assert.True(SavingsEstimator.HevcBitrateFactor("mpeg2video") < SavingsEstimator.HevcBitrateFactor("h264"));
    }
}
