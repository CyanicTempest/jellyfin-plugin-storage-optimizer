// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.DataManager.Encoding;
using Jellyfin.Plugin.DataManager.Models;
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

    [Fact]
    public void RelativeSpeeds_takes_the_middle_run_so_one_odd_run_does_not_count()
    {
        var totals = new List<IReadOnlyList<double>> { new[] { 100.0, 104, 96 }, new[] { 190.0, 30, 186 } };

        Assert.Equal(new[] { 1.0, 1.86 }, GpuParsing.RelativeSpeeds(totals));
    }

    [Fact]
    public void RelativeSpeeds_is_empty_until_one_at_a_time_is_timed()
    {
        Assert.Empty(GpuParsing.RelativeSpeeds([]));
    }

    [Theory]
    [InlineData(100, 110, false)]
    [InlineData(100, 125, false)]
    [InlineData(100, 160, true)]
    public void Disagree_flags_runs_too_far_apart_to_trust(double first, double second, bool expected)
    {
        Assert.Equal(expected, GpuParsing.Disagree([first, second]));
    }

    [Theory]
    [InlineData("/media/Example Film - HEVC.mkv", true)]
    [InlineData("/media/Example Film - AV1 720p.mkv", true)]
    [InlineData("/media/Example Film - H264 1080p.mkv", true)]
    [InlineData("/media/Example Film - Remux.mkv", true)]
    [InlineData("/media/Example Film.mkv", false)]
    [InlineData("/media/Example - Film.mkv", false)]
    [InlineData("/media/Example Film - 1080p.mkv", false)]
    public void IsKeptReEncode_goes_by_the_name_given_to_a_kept_file(string path, bool expected)
    {
        Assert.Equal(expected, SmallerFiles.IsKeptReEncode(path));
    }

    [Fact]
    public void Find_pairs_a_smaller_re_encode_with_its_original_only()
    {
        var report = new StorageReport();
        report.Files.Add(new MediaFileInfo { ItemId = "a", Path = "/m/Film.mkv", SizeBytes = 100, OtherVersionIds = ["b", "c"] });
        report.Files.Add(new MediaFileInfo { ItemId = "b", Path = "/m/Film - HEVC.mkv", SizeBytes = 40, OtherVersionIds = ["a", "c"] });
        report.Files.Add(new MediaFileInfo { ItemId = "c", Path = "/m/Film - AV1.mkv", SizeBytes = 140, OtherVersionIds = ["a", "b"] });
        report.Files.Add(new MediaFileInfo { ItemId = "d", Path = "/m/Other - HEVC.mkv", SizeBytes = 10 });

        Assert.Equal(new SmallerFile("a", "b"), Assert.Single(SmallerFiles.Find(report)));
    }

    [Fact]
    public void DuplicateFinder_keeps_the_smaller_re_encode_not_the_original()
    {
        var report = new StorageReport();
        report.Files.Add(new MediaFileInfo { ItemId = "a", Path = "/m/Film.mkv", Kind = "Movie", Name = "Film", Width = 1920, Height = 1080, VideoCodec = "h264", Bitrate = 20_000_000, SizeBytes = 100, OtherVersionIds = ["b"] });
        report.Files.Add(new MediaFileInfo { ItemId = "b", Path = "/m/Film - HEVC.mkv", Kind = "Movie", Name = "Film", Width = 1920, Height = 1080, VideoCodec = "hevc", Bitrate = 4_000_000, SizeBytes = 40, OtherVersionIds = ["a"] });

        var group = Assert.Single(DuplicateFinder.Find(report).Groups);

        Assert.Equal("b", group.BestItemId);
        Assert.Equal(100, group.ReclaimableBytes);
    }

    [Fact]
    public void Medians_gives_the_middle_run_of_each_number_at_once()
    {
        var totals = new List<IReadOnlyList<double>> { new[] { 600.0, 640, 100 }, new[] { 1100.0, 1000 }, Array.Empty<double>() };

        Assert.Equal(new[] { 600.0, 1050.0 }, GpuParsing.Medians(totals));
    }

    [Fact]
    public void Diagnose_blames_the_film_when_it_reaches_far_less_than_the_encoder_alone()
    {
        var text = GpuParsing.Diagnose([600, 1100, 1500, 1600], [600, 300, 280, 250]);

        Assert.Contains("only 38%", text, StringComparison.Ordinal);
        Assert.Contains("reading or decoding the film", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Diagnose_blames_the_encoders_when_the_film_keeps_up()
    {
        var text = GpuParsing.Diagnose([600, 1100, 1500, 1600], [590, 1050, 1400, 1500]);

        Assert.Contains("the GPU's encoders are the limit", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Diagnose_says_nothing_without_both_runs()
    {
        Assert.Null(GpuParsing.Diagnose([], [600]));
        Assert.Null(GpuParsing.Diagnose([600], []));
    }

    [Fact]
    public void BuildGeneratedSpeedTest_makes_its_own_picture_and_reads_no_file()
    {
        var args = FfmpegArguments.BuildGeneratedSpeedTest("/tmp/out", 3840, 2160, 23.976, 100_000, "hevc_nvenc", QualityPreset.Balanced, EncodeEffort.Standard, _plainSource).ToList();

        Assert.Equal("lavfi", args[args.IndexOf("-f") + 1]);
        Assert.Equal("testsrc2=size=3840x2160:rate=24:duration=1,loop=loop=-1:size=12:start=0", args[args.IndexOf("-i") + 1]);
        Assert.Equal("/tmp/out", args[^1]);
    }

    [Fact]
    public void HardwareArguments_keep_frames_on_the_card_only_when_asked_and_decoding_there()
    {
        var kept = EncoderSelector.HardwareArguments(MediaBrowser.Model.Entities.HardwareAccelerationType.nvenc, "hevc_nvenc", "cuda", string.Empty, false, true);
        var copied = EncoderSelector.HardwareArguments(MediaBrowser.Model.Entities.HardwareAccelerationType.nvenc, "hevc_nvenc", "cuda", string.Empty, false);
        var cpuDecode = EncoderSelector.HardwareArguments(MediaBrowser.Model.Entities.HardwareAccelerationType.nvenc, "hevc_nvenc", null, string.Empty, false, true);

        Assert.Equal(new[] { "-hwaccel", "cuda", "-hwaccel_output_format", "cuda" }, kept);
        Assert.Equal(new[] { "-hwaccel", "cuda" }, copied);
        Assert.Empty(cpuDecode);
    }

    [Fact]
    public void Build_leaves_out_the_pixel_format_when_frames_stay_on_the_gpu()
    {
        var tenBit = _plainSource with { IsTenBit = true };

        var copied = FfmpegArguments.Build("/in.mkv", "/out.mkv", "hevc_nvenc", QualityPreset.Balanced, EncodeEffort.Standard, tenBit, "mkv").ToList();
        var kept = FfmpegArguments.Build("/in.mkv", "/out.mkv", "hevc_nvenc", QualityPreset.Balanced, EncodeEffort.Standard, tenBit, "mkv", framesOnGpu: true).ToList();

        Assert.Equal("p010le", copied[copied.IndexOf("-pix_fmt") + 1]);
        Assert.DoesNotContain("-pix_fmt", kept);
        Assert.DoesNotContain("-filter:v:0", kept);
    }

    [Fact]
    public void Build_ignores_frames_on_the_gpu_when_the_picture_is_scaled()
    {
        var args = FfmpegArguments.Build("/in.mkv", "/out.mkv", "hevc_nvenc", QualityPreset.Balanced, EncodeEffort.Standard, _plainSource with { IsTenBit = true }, "mkv", (1280, 720), framesOnGpu: true).ToList();

        Assert.Contains("scale=1280:720:flags=lanczos", args[args.IndexOf("-filter:v:0") + 1], StringComparison.Ordinal);
        Assert.Contains("-pix_fmt", args);
    }

    [Fact]
    public async System.Threading.Tasks.Task HardwareFallback_moves_down_one_setup_at_a_time_and_remembers_what_failed()
    {
        HardwareSetup kept = new(["a"], "cuda", true);
        HardwareSetup copied = new(["b"], "cuda");
        HardwareSetup cpu = new([], null);
        var tried = new List<HardwareSetup>();
        var falls = 0;

        var (result, failed) = await HardwareAcceleration.RunAsync(
            [kept, copied, cpu],
            plan =>
            {
                tried.Add(plan);
                return plan == cpu ? System.Threading.Tasks.Task.FromResult("done") : throw new InvalidOperationException("ffmpeg failed");
            },
            (_, _, _) => falls++,
            System.Threading.CancellationToken.None);

        Assert.Equal("done", result);
        Assert.Equal(new[] { kept, copied, cpu }, tried);
        Assert.Equal(new[] { kept, copied }, failed);
        Assert.Equal(2, falls);
    }

    [Fact]
    public async System.Threading.Tasks.Task HardwareFallback_stops_at_the_first_setup_that_works()
    {
        HardwareSetup kept = new(["a"], "cuda", true);
        HardwareSetup copied = new(["b"], "cuda");

        var (result, failed) = await HardwareAcceleration.RunAsync([kept, copied], plan => System.Threading.Tasks.Task.FromResult(plan.FramesOnGpu), null, System.Threading.CancellationToken.None);

        Assert.True(result);
        Assert.Empty(failed);
    }

    [Fact]
    public async System.Threading.Tasks.Task HardwareFallback_gives_up_on_the_last_setup_and_never_retries_a_cancel()
    {
        HardwareSetup first = new(["a"], "cuda");
        HardwareSetup last = new([], null);
        var tried = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => HardwareAcceleration.RunAsync<int>([first, last], _ => { tried++; throw new InvalidOperationException("no"); }, null, System.Threading.CancellationToken.None));
        Assert.Equal(2, tried);

        using var cancelled = new System.Threading.CancellationTokenSource();
        await cancelled.CancelAsync();
        tried = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => HardwareAcceleration.RunAsync<int>([first, last], _ => { tried++; throw new InvalidOperationException("stopped"); }, null, cancelled.Token));
        Assert.Equal(1, tried);
    }

    [Fact]
    public void CompareFramesOnGpu_says_whether_it_was_worth_it()
    {
        Assert.Contains("about 25% faster", GpuParsing.CompareFramesOnGpu([400, 800], [500, 1000]), StringComparison.Ordinal);
        Assert.Contains("worth turning on", GpuParsing.CompareFramesOnGpu([400, 800], [500, 1000]), StringComparison.Ordinal);
        Assert.Contains("about 20% slower", GpuParsing.CompareFramesOnGpu([400, 700], [300, 560]), StringComparison.Ordinal);
        Assert.Contains("little difference", GpuParsing.CompareFramesOnGpu([400, 700], [405, 710]), StringComparison.Ordinal);
        Assert.Contains("didn't work", GpuParsing.CompareFramesOnGpu([400, 700], []), StringComparison.Ordinal);
    }

    [Fact]
    public void Diagnose_treats_a_film_within_a_quarter_of_the_encoder_as_keeping_up()
    {
        Assert.Contains("keeps up", GpuParsing.Diagnose([452, 879, 896], [393, 744, 751]), StringComparison.Ordinal);
        Assert.Contains("only 7%", GpuParsing.Diagnose([452, 879, 903], [66, 54, 45]), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new string[0], false, "Jellyfin isn't set to decode H.264 and HEVC on the GPU")]
    [InlineData(new[] { "h264" }, false, "Jellyfin isn't set to decode HEVC on the GPU")]
    [InlineData(new[] { "h264", "hevc" }, false, "Jellyfin isn't set to decode 10-bit HEVC on the GPU")]
    public void DecodeProblem_names_what_is_off_for_an_nvidia_card_with_no_scan(string[] on, bool tenBit, string expected)
    {
        var problem = GpuParsing.DecodeProblem(MediaBrowser.Model.Entities.HardwareAccelerationType.nvenc, on, tenBit, null, 0);

        Assert.NotNull(problem);
        Assert.Contains(expected, problem.Message, StringComparison.Ordinal);
        Assert.Contains("Dashboard, Playback, Transcoding", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DecodeProblem_only_counts_the_formats_the_library_holds()
    {
        var library = new Dictionary<string, int> { ["h264"] = 300, ["hevc"] = 40, ["av1"] = 2 };

        var problem = GpuParsing.DecodeProblem(MediaBrowser.Model.Entities.HardwareAccelerationType.nvenc, ["hevc"], true, library, 10);

        Assert.NotNull(problem);
        Assert.Equal(new[] { "h264", "av1" }, problem.Codecs);
        Assert.Contains("H.264 and AV1", problem.Message, StringComparison.Ordinal);
        Assert.Contains("which 302 of your files use", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DecodeProblem_is_null_when_everything_in_the_library_decodes_on_the_gpu_or_the_card_does_not_decode_here()
    {
        var library = new Dictionary<string, int> { ["h264"] = 300, ["hevc"] = 40 };

        Assert.Null(GpuParsing.DecodeProblem(MediaBrowser.Model.Entities.HardwareAccelerationType.nvenc, ["h264", "hevc"], true, library, 10));
        Assert.Null(GpuParsing.DecodeProblem(MediaBrowser.Model.Entities.HardwareAccelerationType.vaapi, [], false, library, 10));
        Assert.Null(GpuParsing.DecodeProblem(MediaBrowser.Model.Entities.HardwareAccelerationType.none, [], false, library, 10));
    }

    [Fact]
    public void DecodeProblem_skips_ten_bit_hevc_when_the_library_has_none_of_it()
    {
        var library = new Dictionary<string, int> { ["hevc"] = 40 };

        Assert.Null(GpuParsing.DecodeProblem(MediaBrowser.Model.Entities.HardwareAccelerationType.nvenc, ["hevc"], false, library, 0));
    }
}
