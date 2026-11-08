// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.DataManager.Encoding;
using Xunit;

namespace Jellyfin.Plugin.DataManager.Tests;

public sealed class JobStoreTests : IDisposable
{
    private const string Marker = ".datamanager-partial.";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "so-jobs-" + Guid.NewGuid().ToString("N"));

    public JobStoreTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, true);

    private static EncodeJob Job(EncodeJobState state) => new()
    {
        ItemId = Guid.NewGuid(),
        Name = "Example Film",
        State = state,
        Codec = TargetCodec.Av1,
        Quality = QualityPreset.Smaller,
        Effort = EncodeEffort.Slower,
        Resolution = ResolutionChoice.Original,
        Audio = AudioChoice.KeepLanguages,
        Languages = ["eng", "jpn"],
        TrackLanguages = new System.Collections.Generic.Dictionary<string, string> { ["AUDIO||AAC||2|"] = "jpn" },
        Hdr = HdrChoice.ToneMapToSdr,
        ToneMapStyle = ToneMapStyle.Bt2390,
        Progress = 42
    };

    private static SavedJob RoundTrip(SavedJob saved)
        => JobStore.Parse(JsonSerializer.Serialize(new[] { saved })).Single();

    [Fact]
    public void A_saved_job_keeps_every_setting_and_its_paths()
    {
        var job = Job(EncodeJobState.Queued);
        var back = RoundTrip(new SavedJob(job, "/media/.x" + Marker + "mkv", "/media/x.mkv"));

        Assert.Equal(job.Id, back.Job.Id);
        Assert.Equal((TargetCodec.Av1, QualityPreset.Smaller, EncodeEffort.Slower, ToneMapStyle.Bt2390), (back.Job.Codec, back.Job.Quality, back.Job.Effort, back.Job.ToneMapStyle));
        Assert.Equal(["eng", "jpn"], back.Job.Languages);
        Assert.Equal("jpn", back.Job.TrackLanguages["AUDIO||AAC||2|"]);
        Assert.Equal("/media/x.mkv", back.PlannedPath);
    }

    [Fact]
    public void Recovering_requeues_waiting_jobs_and_stops_ones_that_were_encoding()
    {
        var partial = Path.Combine(_folder, ".Example Film" + Marker + "mkv");
        File.WriteAllText(partial, "half");
        var original = Path.Combine(_folder, "Example Film.mkv");
        File.WriteAllText(original, "video");

        var jobs = JobStore.Recover(
        [
            RoundTrip(new SavedJob(Job(EncodeJobState.Queued), null, null)),
            RoundTrip(new SavedJob(Job(EncodeJobState.Running), partial, null)),
            RoundTrip(new SavedJob(Job(EncodeJobState.Paused), original, null)),
            RoundTrip(new SavedJob(Job(EncodeJobState.NeedsDecision), Path.Combine(_folder, "gone.mkv"), null)),
            RoundTrip(new SavedJob(Job(EncodeJobState.Completed), null, null))
        ], Marker);

        Assert.Equal([EncodeJobState.Queued, EncodeJobState.Failed, EncodeJobState.Failed, EncodeJobState.Failed, EncodeJobState.Completed], jobs.Select(j => j.State));
        Assert.True(jobs[1].Interrupted);
        Assert.True(jobs[1].CanRestart);
        Assert.Equal(0, jobs[1].Progress);
        Assert.Contains("Restart it", jobs[1].Message, StringComparison.Ordinal);

        // Only the unfinished file goes; anything else, such as a path to the original, is never deleted.
        Assert.False(File.Exists(partial));
        Assert.True(File.Exists(original));
    }

    [Fact]
    public void Unreadable_saved_jobs_are_none()
        => Assert.Empty(JobStore.Parse("not json"));
}
