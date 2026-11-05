// SPDX-License-Identifier: GPL-3.0-only
using System.Linq;
using Jellyfin.Plugin.DataManager.Models;
using Jellyfin.Plugin.DataManager.Services;
using Xunit;

namespace Jellyfin.Plugin.DataManager.Tests;

public sealed class SuggestionRulesTests
{
    private const long Mb = 1024 * 1024;

    private static MediaFileInfo File(long sizeMb, long savingMb, string? series = null, string[]? otherVersions = null) => new()
    {
        SizeBytes = sizeMb * Mb,
        EstimatedSavingsBytes = savingMb * Mb,
        SeriesId = series,
        OtherVersionIds = otherVersions ?? []
    };

    [Fact]
    public void A_film_needs_300_MB_and_a_quarter_of_its_size()
    {
        var big = File(1000, 400);
        var small = File(400, 200);
        var thin = File(4000, 400);
        SuggestionRules.Apply([big, small, thin]);

        Assert.True(big.IsRecommended);
        Assert.Equal("Saves over 300 MB", big.SuggestedReason);
        Assert.False(small.IsRecommended);
        Assert.False(thin.IsRecommended);
    }

    [Fact]
    public void A_show_is_suggested_as_a_whole()
    {
        var episodes = Enumerable.Range(0, 16).Select(_ => File(120, 50, "show")).ToList();
        SuggestionRules.Apply(episodes);

        Assert.All(episodes, e => Assert.True(e.IsRecommended));
        Assert.Equal("Saves over 300 MB across the show", episodes[0].SuggestedReason);
    }

    [Fact]
    public void A_short_show_below_the_limit_is_not_suggested()
    {
        var episodes = Enumerable.Range(0, 4).Select(_ => File(120, 50, "show")).ToList();
        SuggestionRules.Apply(episodes);

        Assert.All(episodes, e => Assert.False(e.IsRecommended));
    }

    [Fact]
    public void An_episode_that_barely_shrinks_is_left_out_of_its_show()
    {
        var thin = File(1000, 20, "show");
        var episodes = Enumerable.Range(0, 19).Select(_ => File(100, 50, "show")).Append(thin).ToList();
        SuggestionRules.Apply(episodes);

        Assert.True(episodes[0].IsRecommended);
        Assert.False(thin.IsRecommended);
    }

    [Fact]
    public void Episodes_that_save_nothing_still_count_towards_the_show()
    {
        var episodes = Enumerable.Range(0, 4).Select(_ => File(100, 90, "show")).Concat(Enumerable.Range(0, 90).Select(_ => File(100, 0, "show"))).ToList();
        SuggestionRules.Apply(episodes);

        Assert.All(episodes, e => Assert.False(e.IsRecommended));
    }

    [Fact]
    public void A_title_with_another_version_is_left_out()
    {
        var film = File(1000, 400, null, ["other"]);
        var episodes = Enumerable.Range(0, 16).Select(_ => File(120, 50, "show")).Append(File(120, 50, "show", ["other"])).ToList();
        SuggestionRules.Apply(episodes.Append(film).ToList());

        Assert.False(film.IsRecommended);
        Assert.False(episodes[^1].IsRecommended);
        Assert.True(episodes[0].IsRecommended);
    }
}
