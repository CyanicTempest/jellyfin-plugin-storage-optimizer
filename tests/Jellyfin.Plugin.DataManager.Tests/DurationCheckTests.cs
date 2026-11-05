// SPDX-License-Identifier: GPL-3.0-only
using Jellyfin.Plugin.DataManager.Encoding;
using Xunit;

namespace Jellyfin.Plugin.DataManager.Tests;

public sealed class DurationCheckTests
{
    [Fact]
    public void Parse_reads_a_matroska_video_length_from_its_tag()
    {
        // What ffprobe says for The Wild Robot: a removed Polish track makes the file 146s longer than its video.
        const string Json = """
            { "streams": [ { "index": 0, "tags": { "DURATION": "01:41:41.095000000" } } ], "format": { "duration": "6246.848000" } }
            """;

        var lengths = DurationCheck.Parse(Json);

        Assert.Equal(6101.095, lengths.VideoSeconds!.Value, 3);
        Assert.Equal(6246.848, lengths.FileSeconds, 3);
    }

    [Fact]
    public void Parse_reads_a_tag_with_a_language_in_its_name()
    {
        var lengths = DurationCheck.Parse("""{ "streams": [ { "tags": { "DURATION-eng": "00:10:00.500000000" } } ], "format": { "duration": "601" } }""");

        Assert.Equal(600.5, lengths.VideoSeconds!.Value, 3);
    }

    [Fact]
    public void Parse_reads_an_mp4_video_length_from_the_stream()
    {
        var lengths = DurationCheck.Parse("""{ "streams": [ { "duration": "6072.399667" } ], "format": { "duration": "6072.483084" } }""");

        Assert.Equal(6072.399667, lengths.VideoSeconds!.Value, 6);
    }

    [Fact]
    public void Parse_reads_a_length_over_a_day()
    {
        var lengths = DurationCheck.Parse("""{ "streams": [ { "tags": { "DURATION": "25:00:00.000000000" } } ], "format": {} }""");

        Assert.Equal(90000, lengths.VideoSeconds!.Value, 3);
    }

    [Theory]
    [InlineData("""{ "streams": [ {} ], "format": { "duration": "100.0" } }""")]
    [InlineData("""{ "streams": [ { "duration": "N/A", "tags": { "DURATION": "nonsense" } } ], "format": { "duration": "100.0" } }""")]
    public void Parse_leaves_only_the_file_for_a_video_with_no_length(string json)
    {
        var lengths = DurationCheck.Parse(json);

        Assert.Null(lengths.VideoSeconds);
        Assert.Equal(100, lengths.FileSeconds, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    public void Parse_finds_no_length_in_unreadable_output(string json)
    {
        var lengths = DurationCheck.Parse(json);

        Assert.Null(lengths.VideoSeconds);
        Assert.Equal(0, lengths.FileSeconds);
    }

    [Fact]
    public void Matches_ignores_a_longer_track_that_was_removed()
    {
        var source = new MediaLengths(6101.095, 6246.848);
        var output = new MediaLengths(6101.095, 6101.120);

        Assert.True(DurationCheck.Matches(source, output));
    }

    [Fact]
    public void Matches_is_not_fooled_by_a_longer_kept_track()
    {
        // The file's length comes out the same, but the video stops 10 minutes early.
        var source = new MediaLengths(6101.095, 6246.848);
        var output = new MediaLengths(5500, 6246.848);

        Assert.False(DurationCheck.Matches(source, output));
    }

    [Fact]
    public void Matches_rejects_a_cut_off_video()
    {
        Assert.False(DurationCheck.Matches(new MediaLengths(6000, 6000), new MediaLengths(5000, 5000)));
    }

    [Fact]
    public void Matches_compares_the_files_without_a_video_length()
    {
        Assert.True(DurationCheck.Matches(new MediaLengths(null, 6000), new MediaLengths(6000, 6000.5)));
        Assert.False(DurationCheck.Matches(new MediaLengths(null, 6000), new MediaLengths(5000, 5000)));
    }

    [Fact]
    public void Matches_rejects_an_empty_output()
    {
        Assert.False(DurationCheck.Matches(new MediaLengths(0.5, 0.5), new MediaLengths(null, 0)));
    }

    [Theory]
    [InlineData(6000, 5941, true)]
    [InlineData(6000, 5939, false)]
    [InlineData(60, 58.5, true)]
    [InlineData(60, 57, false)]
    public void Matches_allows_one_percent_or_two_seconds(double source, double output, bool matches)
    {
        Assert.Equal(matches, DurationCheck.Matches(new MediaLengths(source, source), new MediaLengths(output, output)));
    }
}
