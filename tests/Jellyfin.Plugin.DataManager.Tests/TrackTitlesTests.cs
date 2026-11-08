// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.DataManager.Encoding;
using Xunit;

namespace Jellyfin.Plugin.DataManager.Tests;

public class TrackTitlesTests
{
    private static readonly Dictionary<string, string> _languages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["english"] = "eng", ["eng"] = "eng", ["en"] = "eng",
        ["japanese"] = "jpn", ["jpn"] = "jpn", ["ja"] = "jpn",
        ["german"] = "ger", ["deutsch"] = "ger", ["ger"] = "ger",
        ["hr"] = "hrv", ["it"] = "ita", ["ma"] = "mah"
    };

    private static readonly Dictionary<string, string> _names = new() { ["eng"] = "English", ["jpn"] = "Japanese", ["ger"] = "German" };

    private static readonly TitleRules _tidy = new(TrackTitleMode.Clear, TrackTitleMode.Clear, true, true, true, true, true);

    private static string? Known(string word) => _languages.GetValueOrDefault(word);

    private static string Name(string code) => _names.GetValueOrDefault(code, code);

    private static TrackInfo Audio(string? title, string? language = "eng", string codec = "eac3", int channels = 6, string? profile = null)
        => new(1, TrackKind.Audio, language, codec, false, false, 0, channels, title, profile);

    private static TrackInfo Subtitle(string? title, string? language = "eng", bool forced = false, bool sdh = false)
        => new(3, TrackKind.Subtitle, language, "subrip", forced, false, 0, 0, title, null, sdh);

    [Theory]
    [InlineData("English", "")]
    [InlineData("English - Dolby Digital Plus 5.1", "")]
    [InlineData("DTS-HD MA 5.1", "")]
    [InlineData("English 5.1 @ 640 kbps", "")]
    [InlineData("Surround 5.1", "")]
    [InlineData("SoundHandler", "")]
    [InlineData("English - Director's Commentary", "Director's Commentary")]
    [InlineData("Commentary with the cast [eng]", "Commentary with the cast")]
    [InlineData("Audio Description", "Audio Description")]
    [InlineData("English (Original)", "Original")]
    [InlineData("Theatrical mix", "Theatrical mix")]
    [InlineData("DTS-HD HR 7.1", "")]
    public void Clear_keeps_only_what_the_title_adds(string title, string expected)
    {
        var rewrite = TrackTitles.Plan(Audio(title), _tidy, Known, Name);
        Assert.Equal(expected == title ? null : expected, rewrite.Title);
    }

    [Fact]
    public void Clear_leaves_an_empty_title_alone()
        => Assert.Null(TrackTitles.Plan(Audio(null), _tidy, Known, Name).Title);

    [Fact]
    public void Full_writes_language_format_and_channels_with_extras_in_brackets()
    {
        var full = _tidy with { Audio = TrackTitleMode.Full, Subtitles = TrackTitleMode.Full };

        Assert.Equal("English Dolby Digital Plus 5.1", TrackTitles.Plan(Audio("Surround"), full, Known, Name).Title);
        Assert.Equal("English Dolby Digital Plus 5.1 (Director's commentary)", TrackTitles.Plan(Audio("Director's commentary"), full, Known, Name).Title);
        Assert.Equal("English TrueHD Atmos 7.1", TrackTitles.Plan(Audio(null, codec: "truehd", channels: 8, profile: "TrueHD + Atmos"), full, Known, Name).Title);
        Assert.Equal("English DTS:X", TrackTitles.Plan(Audio("DTS:X", codec: "dts", channels: 0, profile: "DTS:X"), full, Known, Name).Title);
        Assert.Equal("English Forced", TrackTitles.Plan(Subtitle("Forced", forced: true), full, Known, Name).Title);
        Assert.Equal("English SDH", TrackTitles.Plan(Subtitle("SDH"), full, Known, Name).Title);
        Assert.Null(TrackTitles.Plan(Subtitle("English SDH"), full, Known, Name).Title);
    }

    [Fact]
    public void A_converted_track_is_named_by_its_new_format()
    {
        var full = _tidy with { Audio = TrackTitleMode.Full };
        var conversion = new AudioConversion(1, "eac3", 640_000, 6, "Dolby Digital Plus 5.1");
        var track = Audio("TrueHD Atmos 7.1", codec: "truehd", channels: 8);

        Assert.Equal("English Dolby Digital Plus 5.1", TrackTitles.Plan(track, full, Known, Name, conversion).Title);
        Assert.Equal(string.Empty, TrackTitles.Plan(track, _tidy, Known, Name, conversion).Title);
        Assert.Equal("Dolby Digital Plus 5.1", TrackTitles.Plan(track, TitleRules.None, Known, Name, conversion).Title);
    }

    [Fact]
    public void Leave_changes_nothing()
    {
        var rewrite = TrackTitles.Plan(Audio("English 5.1", language: null), TitleRules.None, Known, Name);
        Assert.False(rewrite.ChangesAnything);
    }

    [Fact]
    public void Flags_and_a_missing_language_come_from_the_title()
    {
        var commentary = TrackTitles.Plan(Audio("Japanese commentary", language: null), _tidy, Known, Name);
        Assert.Equal("jpn", commentary.Language);
        Assert.Equal(["comment"], commentary.Flags);
        Assert.Equal("Commentary", commentary.Title);

        Assert.Equal(["visual_impaired"], TrackTitles.Plan(Audio("Audio description"), _tidy, Known, Name).Flags);
        Assert.Equal(["original"], TrackTitles.Plan(Audio("Original"), _tidy, Known, Name).Flags);

        var forced = TrackTitles.Plan(Subtitle("English (Forced)"), _tidy, Known, Name);
        Assert.Equal(["forced"], forced.Flags);
        Assert.Equal(string.Empty, forced.Title);

        var sdh = TrackTitles.Plan(Subtitle("SDH", sdh: true), _tidy, Known, Name);
        Assert.Empty(sdh.Flags);
        Assert.Equal(string.Empty, sdh.Title);

        Assert.Equal("Signs and songs", TrackTitles.Plan(Subtitle("English Signs and songs"), _tidy, Known, Name).Title);
    }

    [Fact]
    public void A_language_chosen_in_the_window_is_written_whatever_the_rules()
        => Assert.Equal("ger", TrackTitles.Plan(Audio(null, language: "eng"), TitleRules.None, Known, Name, language: "ger").Language);

    [Fact]
    public void Leaving_words_out_follows_the_settings()
    {
        var bare = _tidy with { KeepCommentary = false, KeepOtherText = false };
        Assert.Equal(string.Empty, TrackTitles.Plan(Audio("Director's commentary with the cast"), bare, Known, Name).Title);
        Assert.Equal(string.Empty, TrackTitles.Plan(Audio("Theatrical mix"), bare, Known, Name).Title);
        Assert.Null(TrackTitles.Plan(Audio("Audio description"), bare, Known, Name).Title);
    }

    [Theory]
    [InlineData("English", "eng")]
    [InlineData("eng", "eng")]
    [InlineData("Japanese 2.0", "jpn")]
    [InlineData("English / German", null)]
    [InlineData("DTS-HD HR", null)]
    [InlineData("It's commentary", null)]
    [InlineData("", null)]
    public void GuessLanguage_reads_one_language_name_from_a_title(string title, string? expected)
        => Assert.Equal(expected, TrackTitles.GuessLanguage(title, Known));

    [Fact]
    public void Build_writes_titles_languages_and_flags_by_place_in_the_new_file()
    {
        var tracks = new List<TrackInfo>
        {
            new(1, TrackKind.Audio, "eng", "ac3", false, true, 0, 6, "English 5.1"),
            new(2, TrackKind.Audio, "fre", "ac3", false, false, 0, 6),
            new(3, TrackKind.Audio, null, "aac", false, false, 0, 2, "Japanese commentary"),
            new(4, TrackKind.Subtitle, "eng", "subrip", false, false, 0, 0, "Forced")
        };
        var plan = TrackSelection.Choose(tracks, AudioChoice.KeepLanguages, SubtitleChoice.KeepAll, ["eng"]);
        var rewrites = plan.Kept.Select(t => TrackTitles.Plan(t, _tidy, Known, Name)).Where(r => r.ChangesAnything).ToList();
        var source = new SourceVideo(IsMatroska: true, IsMp4: false, IsTenBit: false, IsHdr: false, ColorTransfer: null);

        var args = FfmpegArguments.Build("/in.mkv", "/out.mkv", FfmpegArguments.CopyVideo, QualityPreset.Balanced, EncodeEffort.Standard, source, "mkv", tracks: plan, extras: EncodeExtras.None with { Rewrites = rewrites }).ToList();

        string After(string option) => args[args.IndexOf(option) + 1];
        Assert.Equal("title=", args[args.IndexOf("-metadata:s:a:0") + 1]);
        Assert.Contains("title=Commentary", args);
        Assert.Contains("language=jpn", args);
        Assert.Equal("+comment", After("-disposition:a:1"));
        Assert.Equal("+forced", After("-disposition:s:0"));
    }
}
