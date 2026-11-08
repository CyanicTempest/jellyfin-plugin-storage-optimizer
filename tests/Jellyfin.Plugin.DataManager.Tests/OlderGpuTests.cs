// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Linq;
using Jellyfin.Plugin.DataManager.Configuration;
using Jellyfin.Plugin.DataManager.Encoding;
using Xunit;

namespace Jellyfin.Plugin.DataManager.Tests;

public class OlderGpuTests
{
    private static readonly SourceVideo _hdr = new(IsMatroska: true, IsMp4: false, IsTenBit: true, IsHdr: true, ColorTransfer: "smpte2084");
    private static readonly SourceVideo _tenBitSdr = new(IsMatroska: true, IsMp4: false, IsTenBit: true, IsHdr: false, ColorTransfer: null);
    private static readonly SourceVideo _eightBit = new(IsMatroska: true, IsMp4: false, IsTenBit: false, IsHdr: false, ColorTransfer: null);

    private static string Describe(VideoVariant v) => v.Encoder + (v.EightBit ? " 8-bit" : string.Empty) + (v.ConstantQp ? " CQP" : string.Empty);

    [Fact]
    public void Qsv_tries_ICQ_then_a_constant_quantizer_then_the_CPU_for_HDR()
    {
        var variants = EncodeService.Variants("hevc_qsv", _hdr, false, new PluginConfiguration(), "gpu", "libx265");
        Assert.Equal(["hevc_qsv", "hevc_qsv CQP", "libx265"], variants.Select(Describe));
        Assert.Null(variants[0].Note);
        Assert.Contains("which keeps HDR", variants[2].Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Tone_mapped_HDR_falls_back_to_8_bit_on_the_GPU_before_the_CPU()
    {
        var variants = EncodeService.Variants("hevc_qsv", _hdr, true, new PluginConfiguration(), "gpu", "libx265");
        Assert.Equal(["hevc_qsv", "hevc_qsv CQP", "hevc_qsv 8-bit", "hevc_qsv 8-bit CQP", "libx265"], variants.Select(Describe));
    }

    [Fact]
    public void A_GPU_known_to_refuse_skips_straight_to_what_works()
    {
        var config = new PluginConfiguration { GpuNoTenBit = "gpu", GpuNoIcq = "gpu" };
        Assert.Equal(["hevc_qsv 8-bit CQP", "libx265"], EncodeService.Variants("hevc_qsv", _hdr, true, config, "gpu", "libx265").Select(Describe));
        Assert.Equal(["hevc_qsv CQP"], EncodeService.Variants("hevc_qsv", _eightBit, false, config, "gpu", "libx265").Select(Describe));

        // Another GPU isn't held to what this one couldn't do.
        Assert.Equal("hevc_qsv", Describe(EncodeService.Variants("hevc_qsv", _hdr, true, config, "other", "libx265")[0]));
    }

    [Fact]
    public void Ten_bit_SDR_only_goes_to_8_bit_on_the_GPU_when_allowed()
    {
        var config = new PluginConfiguration { GpuNoTenBit = "gpu" };
        Assert.Equal(["libx265"], EncodeService.Variants("hevc_nvenc", _tenBitSdr, false, config, "gpu", "libx265").Select(Describe));

        config.AllowEightBitOnGpu = true;
        Assert.Equal(["hevc_nvenc 8-bit", "libx265"], EncodeService.Variants("hevc_nvenc", _tenBitSdr, false, config, "gpu", "libx265").Select(Describe));
    }

    [Fact]
    public void Without_the_CPU_a_GPU_that_cant_do_10_bit_says_why()
    {
        var config = new PluginConfiguration { GpuNoTenBit = "gpu", NeverEncodeOnCpu = true };
        var error = Assert.Throws<InvalidOperationException>(() => EncodeService.Variants("hevc_nvenc", _hdr, false, config, "gpu", "libx265"));
        Assert.Contains("can't encode 10-bit", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Software_and_8_bit_encodes_have_one_way()
    {
        Assert.Single(EncodeService.Variants("libx265", _hdr, false, new PluginConfiguration(), "gpu", "libx265"));
        Assert.Equal(["hevc_nvenc"], EncodeService.Variants("hevc_nvenc", _eightBit, false, new PluginConfiguration(), "gpu", "libx265").Select(Describe));
    }

    [Fact]
    public void Qsv_constant_quantizer_and_8_bit_change_the_arguments()
    {
        var icq = FfmpegArguments.BuildPreview("/in.mkv", "/out.mkv", 10, 24, "hevc_qsv", QualityPreset.Balanced, EncodeEffort.Standard, _tenBitSdr, null).ToList();
        Assert.Contains("-global_quality", icq);
        Assert.Equal("p010le", icq[icq.IndexOf("-pix_fmt") + 1]);

        var cqp = FfmpegArguments.BuildPreview("/in.mkv", "/out.mkv", 10, 24, "hevc_qsv", QualityPreset.Balanced, EncodeEffort.Standard, _tenBitSdr, null, variant: new VideoVariant("hevc_qsv", true, true)).ToList();
        Assert.DoesNotContain("-global_quality", cqp);
        Assert.Equal("25", cqp[cqp.IndexOf("-q:v") + 1]);
        Assert.DoesNotContain("-pix_fmt", cqp);
    }

    [Fact]
    public void Tone_mapping_uses_the_chosen_style_and_8_bit_drops_the_HDR_tags()
    {
        var args = FfmpegArguments.BuildPreview("/in.mkv", "/out.mkv", 10, 24, "hevc_qsv", QualityPreset.Balanced, EncodeEffort.Standard, _hdr, null, toneMap: true, variant: new VideoVariant("hevc_qsv", true), style: ToneMapStyle.Bt2390).ToList();
        var filter = args[args.IndexOf("-filter:v:0") + 1];
        Assert.StartsWith("tonemapx=tonemap=bt2390", filter, StringComparison.Ordinal);
        Assert.EndsWith("format=yuv420p", filter, StringComparison.Ordinal);
        Assert.Equal("bt709", args[args.IndexOf("-color_primaries") + 1]);

        Assert.Contains("tonemap=tonemap=mobius", ToneMapping.Filter("yuv420p", ToneMapStyle.Mobius), StringComparison.Ordinal);
        Assert.Contains("tonemap=tonemap=reinhard", ToneMapping.Filter("yuv420p", ToneMapStyle.Reinhard), StringComparison.Ordinal);
        Assert.Contains("tonemap=tonemap=hable", ToneMapping.Filter("yuv420p"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_capability_test_encodes_a_generated_picture_in_the_depth_asked()
    {
        var args = FfmpegArguments.BuildCapabilityTest("hevc_qsv", QualityPreset.Smallest, true, ["-init_hw_device", "qsv=hw"]).ToList();
        Assert.Contains("testsrc2=size=1280x720:rate=24,format=yuv420p10le", args);
        Assert.Equal("p010le", args[args.IndexOf("-pix_fmt") + 1]);
        Assert.Equal("31", args[args.IndexOf("-global_quality") + 1]);
        Assert.Equal("-", args[^1]);
    }
}
