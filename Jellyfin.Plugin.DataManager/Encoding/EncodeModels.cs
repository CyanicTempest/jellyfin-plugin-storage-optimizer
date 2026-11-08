// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.DataManager.Encoding;

/// <summary>
/// The video codecs the plugin can encode to.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TargetCodec
{
    /// <summary>H.264 / AVC.</summary>
    H264,

    /// <summary>H.265 / HEVC.</summary>
    Hevc,

    /// <summary>AV1.</summary>
    Av1
}

/// <summary>
/// How hard to compress: how much of the original's detail to keep, and so how big the new file is.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QualityPreset
{
    /// <summary>Keeps grain and fine texture. Often barely smaller than the original.</summary>
    NearIdentical,

    /// <summary>Hard to tell from the original, even paused up close.</summary>
    VeryHigh,

    /// <summary>Very close to the original, with slight softening in grain.</summary>
    High,

    /// <summary>A good balance for most libraries.</summary>
    Balanced,

    /// <summary>Smaller files, with softer backgrounds and grain.</summary>
    Smaller,

    /// <summary>The smallest files, clearly softer.</summary>
    Smallest
}

/// <summary>
/// How long to spend encoding. Slower settings search harder for a way to keep detail, so the same quality
/// keeps more texture, at the cost of time.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EncodeEffort
{
    /// <summary>The quickest, losing some texture for the size.</summary>
    Faster,

    /// <summary>The usual balance of time and detail.</summary>
    Standard,

    /// <summary>Takes longer and keeps noticeably more texture.</summary>
    Slower,

    /// <summary>Takes much longer, for the last bit of detail.</summary>
    Slowest
}

/// <summary>
/// What happens to the original file.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OutputMode
{
    /// <summary>Keep the original and add the new file as another version of the same item.</summary>
    KeepBoth,

    /// <summary>Replace the original once the new file has been checked.</summary>
    Replace
}

/// <summary>
/// The resolution to encode at.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ResolutionChoice
{
    /// <summary>Keep each file's resolution.</summary>
    Original,

    /// <summary>Shrink each file to the smallest resolution that still holds all of its real detail.</summary>
    Auto,

    /// <summary>Scale anything larger down to 4K.</summary>
    P2160,

    /// <summary>Scale anything larger down to 1440p.</summary>
    P1440,

    /// <summary>Scale anything larger down to 1080p.</summary>
    P1080,

    /// <summary>Scale anything larger down to 720p.</summary>
    P720,

    /// <summary>Scale anything larger down to 480p.</summary>
    P480
}

/// <summary>
/// The state of an encode job.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EncodeJobState
{
    /// <summary>Waiting in the queue.</summary>
    Queued,

    /// <summary>Encoding now.</summary>
    Running,

    /// <summary>Finished successfully.</summary>
    Completed,

    /// <summary>Stopped with an error.</summary>
    Failed,

    /// <summary>Cancelled by the user.</summary>
    Cancelled,

    /// <summary>Encoded, but the new file isn't smaller, so nothing has been changed until the user chooses what to do.</summary>
    NeedsDecision,

    /// <summary>The user chose to keep the original and delete the new file.</summary>
    Discarded,

    /// <summary>Stopped part way, with ffmpeg frozen where it was, so a viewer's stream has the GPU. It carries on by itself.</summary>
    Paused
}

/// <summary>
/// What encoding does while someone is watching something that Jellyfin transcodes.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WatchingPolicy
{
    /// <summary>Carry on at full speed. A viewer's stream can stutter if the GPU is busy.</summary>
    KeepEncoding,

    /// <summary>Each stream that transcodes takes one slot, and that many encodes pause.</summary>
    LeaveRoom,

    /// <summary>Every encode pauses while anyone transcodes.</summary>
    Pause
}

/// <summary>
/// A request to re-encode one or more items.
/// </summary>
public class EncodeRequest
{
    /// <summary>Gets the ids of the items to re-encode.</summary>
    public IList<Guid> ItemIds { get; init; } = [];

    /// <summary>Gets or sets the target codec.</summary>
    public TargetCodec Codec { get; set; } = TargetCodec.Hevc;

    /// <summary>Gets or sets the quality preset.</summary>
    public QualityPreset Quality { get; set; } = QualityPreset.Balanced;

    /// <summary>Gets or sets how long to spend encoding.</summary>
    public EncodeEffort Effort { get; set; } = EncodeEffort.Standard;

    /// <summary>Gets or sets what happens to the original.</summary>
    public OutputMode Mode { get; set; } = OutputMode.KeepBoth;

    /// <summary>Gets or sets the resolution to encode at.</summary>
    public ResolutionChoice Resolution { get; set; } = ResolutionChoice.Original;

    /// <summary>Gets or sets which audio tracks to keep.</summary>
    public AudioChoice Audio { get; set; } = AudioChoice.KeepAll;

    /// <summary>Gets or sets which subtitle tracks inside the file to keep.</summary>
    public SubtitleChoice Subtitles { get; set; } = SubtitleChoice.KeepAll;

    /// <summary>Gets the languages to keep when audio or subtitles are kept by language, such as eng.</summary>
    public IList<string> Languages { get; init; } = [];

    /// <summary>Gets the tracks, by <see cref="TrackInfo.Key"/>, to remove even though their language is kept.</summary>
    public IList<string> ExcludedTracks { get; init; } = [];

    /// <summary>Gets the tracks, by <see cref="TrackInfo.Key"/>, to keep even though their language isn't.</summary>
    public IList<string> IncludedTracks { get; init; } = [];

    /// <summary>Gets languages chosen for tracks, by <see cref="TrackInfo.Key"/>, such as eng, which the new files are tagged with.</summary>
    public IDictionary<string, string> TrackLanguages { get; init; } = new Dictionary<string, string>();

    /// <summary>Gets or sets a value indicating whether to only remove tracks, copying the video as it is, rather than re-encode it.</summary>
    public bool Remux { get; set; }

    /// <summary>Gets or sets what lossless and DTS audio tracks are converted to.</summary>
    public AudioFormat AudioFormat { get; set; } = AudioFormat.Keep;

    /// <summary>Gets or sets a value indicating whether to mix surround audio tracks down to stereo.</summary>
    public bool StereoAudio { get; set; }

    /// <summary>Gets or sets what happens to HDR and Dolby Vision.</summary>
    public HdrChoice Hdr { get; set; } = HdrChoice.Keep;

    /// <summary>Gets or sets how HDR is fitted into SDR when it's tone mapped, or null for the settings' style.</summary>
    public ToneMapStyle? ToneMapStyle { get; set; }

    /// <summary>Gets or sets a value indicating whether to leave out cover art and other pictures attached to the file.</summary>
    public bool StripCoverArt { get; set; }

    /// <summary>Gets or sets a value indicating whether to leave out fonts attached to the file, which styled subtitles use.</summary>
    public bool StripFonts { get; set; }

    /// <summary>Gets the files that have settings of their own, which replace the ones above for those files only.</summary>
    public IList<FileSettings> Overrides { get; init; } = [];
}

/// <summary>
/// Settings one file has of its own. Anything left out follows the request.
/// </summary>
public class FileSettings
{
    /// <summary>Gets or sets the file these settings are for.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the codec, or null to follow the request.</summary>
    public TargetCodec? Codec { get; set; }

    /// <summary>Gets or sets the quality, or null to follow the request.</summary>
    public QualityPreset? Quality { get; set; }

    /// <summary>Gets or sets the effort, or null to follow the request.</summary>
    public EncodeEffort? Effort { get; set; }

    /// <summary>Gets or sets the resolution, or null to follow the request.</summary>
    public ResolutionChoice? Resolution { get; set; }
}

/// <summary>
/// A request to preview one frame of a video encoded with some settings.
/// </summary>
public class EncodePreviewRequest
{
    /// <summary>Gets or sets the video.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets where to start, in seconds. A keyframe from the filmstrip is best.</summary>
    public double PositionSeconds { get; set; }

    /// <summary>Gets or sets the target codec.</summary>
    public TargetCodec Codec { get; set; } = TargetCodec.Hevc;

    /// <summary>Gets or sets the quality preset.</summary>
    public QualityPreset Quality { get; set; } = QualityPreset.Balanced;

    /// <summary>Gets or sets how long to spend encoding.</summary>
    public EncodeEffort Effort { get; set; } = EncodeEffort.Standard;

    /// <summary>Gets or sets the resolution to encode at.</summary>
    public ResolutionChoice Resolution { get; set; } = ResolutionChoice.Original;

    /// <summary>Gets or sets what happens to HDR. Only tone mapping changes the preview.</summary>
    public HdrChoice Hdr { get; set; } = HdrChoice.Keep;

    /// <summary>Gets or sets how HDR is fitted into SDR when it's tone mapped, or null for the settings' style.</summary>
    public ToneMapStyle? ToneMapStyle { get; set; }
}

/// <summary>
/// What clearing the queue removed.
/// </summary>
/// <param name="Waiting">How many jobs were still waiting to start.</param>
/// <param name="Finished">How many failed or cancelled jobs were cleared from the list.</param>
public sealed record ClearResult(int Waiting, int Finished);

/// <summary>
/// One re-encode job.
/// </summary>
public class EncodeJob
{
    /// <summary>Gets the job id.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Gets the item being re-encoded.</summary>
    public Guid ItemId { get; init; }

    /// <summary>Gets or sets the item name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the source path.</summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>Gets or sets which request queued the job, as a season's episodes are shown together.</summary>
    public Guid BatchId { get; set; }

    /// <summary>Gets or sets the series, for an episode.</summary>
    public string? SeriesName { get; set; }

    /// <summary>Gets or sets the series id, for an episode.</summary>
    public string? SeriesId { get; set; }

    /// <summary>Gets or sets the season's name, for an episode.</summary>
    public string? SeasonName { get; set; }

    /// <summary>Gets or sets the season number, for an episode.</summary>
    public int? SeasonNumber { get; set; }

    /// <summary>Gets or sets the episode number, for an episode.</summary>
    public int? EpisodeNumber { get; set; }

    /// <summary>Gets or sets the episode's own title, for an episode.</summary>
    public string? EpisodeName { get; set; }

    /// <summary>Gets or sets the final output path, once known.</summary>
    public string? OutputPath { get; set; }

    /// <summary>Gets or sets the new file's library item, when it was kept beside the original.</summary>
    public Guid? OutputItemId { get; set; }

    /// <summary>Gets or sets the target codec.</summary>
    public TargetCodec Codec { get; set; }

    /// <summary>Gets or sets the quality preset.</summary>
    public QualityPreset Quality { get; set; }

    /// <summary>Gets or sets how long was spent encoding.</summary>
    public EncodeEffort Effort { get; set; }

    /// <summary>Gets or sets what happens to the original.</summary>
    public OutputMode Mode { get; set; }

    /// <summary>Gets or sets the resolution choice.</summary>
    public ResolutionChoice Resolution { get; set; }

    /// <summary>Gets or sets which audio tracks to keep.</summary>
    public AudioChoice Audio { get; set; }

    /// <summary>Gets or sets which subtitle tracks inside the file to keep.</summary>
    public SubtitleChoice Subtitles { get; set; }

    /// <summary>Gets or sets the languages to keep, normalized.</summary>
    public IReadOnlyList<string> Languages { get; set; } = [];

    /// <summary>Gets or sets the tracks to remove even though their language is kept.</summary>
    public IReadOnlyList<string> ExcludedTracks { get; set; } = [];

    /// <summary>Gets or sets the tracks to keep even though their language isn't.</summary>
    public IReadOnlyList<string> IncludedTracks { get; set; } = [];

    /// <summary>Gets or sets languages chosen for tracks, by <see cref="TrackInfo.Key"/>, which the new file is tagged with.</summary>
    public IReadOnlyDictionary<string, string> TrackLanguages { get; set; } = new Dictionary<string, string>();

    /// <summary>Gets or sets a value indicating whether the job only removes tracks, copying the video as it is.</summary>
    public bool IsRemux { get; set; }

    /// <summary>Gets or sets a value indicating whether the job was stopped part way by the server stopping.</summary>
    public bool Interrupted { get; set; }

    /// <summary>Gets a value indicating whether the job can be run again with the same settings.</summary>
    public bool CanRestart => State is EncodeJobState.Failed or EncodeJobState.Cancelled;

    /// <summary>Gets or sets where the replaced original went in the recycle folder, or null if it was deleted or kept.</summary>
    public string? RecycledPath { get; set; }

    /// <summary>Gets or sets what lossless and DTS audio tracks are converted to.</summary>
    public AudioFormat AudioFormat { get; set; }

    /// <summary>Gets or sets a value indicating whether surround audio is mixed down to stereo.</summary>
    public bool StereoAudio { get; set; }

    /// <summary>Gets or sets what happens to HDR and Dolby Vision.</summary>
    public HdrChoice Hdr { get; set; }

    /// <summary>Gets or sets how HDR is fitted into SDR when it's tone mapped.</summary>
    public ToneMapStyle ToneMapStyle { get; set; }

    /// <summary>Gets or sets a value indicating whether cover art is left out.</summary>
    public bool StripCoverArt { get; set; }

    /// <summary>Gets or sets a value indicating whether attached fonts are left out.</summary>
    public bool StripFonts { get; set; }

    /// <summary>Gets or sets what was changed besides the video and the tracks, such as "Converted 1 audio track to Dolby Digital Plus 5.1.".</summary>
    public string? ExtrasNote { get; set; }

    /// <summary>Gets or sets what was removed besides the video's size, such as "Removed 2 audio tracks.".</summary>
    public string? TracksNote { get; set; }

    /// <summary>Gets or sets the new size, such as 1080p, when the video is scaled down.</summary>
    public string? ScaledTo { get; set; }

    /// <summary>Gets or sets the ffmpeg encoder used, such as libx265.</summary>
    public string? Encoder { get; set; }

    /// <summary>Gets or sets the job state.</summary>
    public EncodeJobState State { get; set; } = EncodeJobState.Queued;

    /// <summary>Gets or sets the progress from 0 to 100.</summary>
    public double Progress { get; set; }

    /// <summary>Gets or sets the estimated seconds left while encoding, or null if not known yet.</summary>
    public double? RemainingSeconds { get; set; }

    /// <summary>Gets or sets the original size in bytes.</summary>
    public long SourceBytes { get; set; }

    /// <summary>Gets or sets the new file size in bytes, once finished.</summary>
    public long? OutputBytes { get; set; }

    /// <summary>Gets or sets a message for the user, such as the reason a job failed.</summary>
    public string? Message { get; set; }

    /// <summary>Gets or sets when the job was queued.</summary>
    public DateTime QueuedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Gets or sets when the job finished.</summary>
    public DateTime? FinishedAt { get; set; }

    /// <summary>Gets or sets the hidden file being written while the job runs.</summary>
    [JsonIgnore]
    public string? TempPath { get; set; }

    /// <summary>Gets or sets where the new file goes once it's finished.</summary>
    [JsonIgnore]
    public string? PlannedPath { get; set; }

    /// <summary>Gets the new file's name, once it's known.</summary>
    public string? PlannedName => PlannedPath is null ? null : System.IO.Path.GetFileName(PlannedPath);

    /// <summary>Gets or sets a value indicating whether the new file is being put in place, which can't be cancelled.</summary>
    [JsonIgnore]
    public bool IsPlacing { get; set; }

    /// <summary>Gets a value indicating whether the job can still be cancelled.</summary>
    public bool CanCancel => State == EncodeJobState.Queued || State == EncodeJobState.Paused || (State == EncodeJobState.Running && !IsPlacing);

    /// <summary>Gets or sets who the job is paused for, such as Sam, or null when it isn't paused for a viewer.</summary>
    public string? PausedFor { get; set; }

    /// <summary>Gets or sets a value indicating whether the user chose to carry on although someone is watching.</summary>
    [JsonIgnore]
    public bool ResumeOverride { get; set; }

    /// <summary>Gets or sets what the job's message was before it paused, to put back when it carries on.</summary>
    [JsonIgnore]
    public string? MessageBeforePause { get; set; }

    /// <summary>Gets or sets when the job started encoding, which decides which job pauses first.</summary>
    [JsonIgnore]
    public DateTime? StartedAt { get; set; }

    /// <summary>Gets or sets the running ffmpeg, which can be frozen and thawed, or null when none is running.</summary>
    [JsonIgnore]
    internal EncodeProcess? Process { get; set; }

    private long _pausedTicks;
    private long _pausedSinceTicks;

    /// <summary>
    /// Notes that the job was frozen, so the time spent frozen can be left out of its speed.
    /// </summary>
    internal void NotePaused()
    {
        System.Threading.Interlocked.Exchange(ref _pausedSinceTicks, DateTime.UtcNow.Ticks);
    }

    /// <summary>
    /// Notes that the job carries on.
    /// </summary>
    internal void NoteResumed()
    {
        var since = System.Threading.Interlocked.Exchange(ref _pausedSinceTicks, 0);
        if (since > 0)
        {
            System.Threading.Interlocked.Add(ref _pausedTicks, Math.Max(0, DateTime.UtcNow.Ticks - since));
        }
    }

    /// <summary>
    /// Gets how long the job has spent frozen, including a freeze that's still going on.
    /// </summary>
    /// <returns>The time frozen.</returns>
    internal TimeSpan TimePaused()
    {
        var since = System.Threading.Interlocked.Read(ref _pausedSinceTicks);
        var now = since > 0 ? Math.Max(0, DateTime.UtcNow.Ticks - since) : 0;
        return TimeSpan.FromTicks(System.Threading.Interlocked.Read(ref _pausedTicks) + now);
    }

    /// <summary>Gets or sets the pixels encoded per second so far, used to estimate other jobs.</summary>
    [JsonIgnore]
    public double PixelRate { get; set; }

    /// <summary>Gets or sets the key this job's encoding speed is saved under.</summary>
    [JsonIgnore]
    public string SpeedKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the token source used to cancel the job.</summary>
    [JsonIgnore]
    public System.Threading.CancellationTokenSource Cancellation { get; set; } = new();
}

/// <summary>
/// A codec the user can choose, with what it means for playback.
/// </summary>
/// <param name="Codec">The codec.</param>
/// <param name="Label">The display name.</param>
/// <param name="Encoder">The ffmpeg encoder that will be used.</param>
/// <param name="IsHardware">Whether the encoder runs on the GPU.</param>
/// <param name="Summary">A one-line description.</param>
/// <param name="Compatibility">Which devices can and can't play it, shown on request.</param>
/// <param name="Warning">Why the codec can't be used, or null if it can.</param>
/// <param name="Efforts">The efforts the encoder can tell apart, quickest first.</param>
public record CodecOption(TargetCodec Codec, string Label, string Encoder, bool IsHardware, string Summary, string Compatibility, string? Warning, IReadOnlyList<EncodeEffort> Efforts);

/// <summary>
/// The expected result of a re-encode request, before it runs.
/// </summary>
public class EncodeEstimate
{
    /// <summary>Gets or sets the expected total size of the new files, in bytes.</summary>
    public long OutputBytes { get; set; }

    /// <summary>Gets or sets the size of the originals, in bytes.</summary>
    public long SourceBytes { get; set; }

    /// <summary>Gets or sets the expected encoding time in seconds, or null if it can't be measured.</summary>
    public double? Seconds { get; set; }

    /// <summary>Gets or sets where the speed came from: Tested (test encodes of a calm, a typical and a busy scene), Encoded (a finished encode) or Running (the current encode).</summary>
    public string? SpeedSource { get; set; }

    /// <summary>Gets or sets a value indicating whether the encoder runs on the GPU.</summary>
    public bool IsHardware { get; set; }

    /// <summary>Gets or sets the number of files that will be checked for detail before encoding, because no analysis is saved for them yet.</summary>
    public int FilesToAnalyse { get; set; }

    /// <summary>Gets or sets a value indicating whether the size comes from test encodes of these settings, rather than a rule of thumb.</summary>
    public bool SizeMeasured { get; set; }

    /// <summary>Gets the expected size of the new files at each quality, with the chosen effort.</summary>
    public IList<LevelEstimate> Levels { get; init; } = [];

    /// <summary>Gets the expected time with each effort the encoder has, at the chosen quality. Only filled in when time is asked for.</summary>
    public IList<EffortEstimate> Efforts { get; init; } = [];

    /// <summary>Gets what the first few new files will be called, with each choice of what happens to the original.</summary>
    public IList<PlannedName> Names { get; init; } = [];

    /// <summary>Gets the languages of the audio and subtitle tracks in the files, most space first.</summary>
    public IList<LanguageOption> Languages { get; init; } = [];

    /// <summary>Gets each kind of audio and subtitle track in the files, such as English TrueHD 7.1, so single tracks can be kept or removed.</summary>
    public IList<TrackOption> Tracks { get; init; } = [];

    /// <summary>Gets the languages to tick to begin with: the user's and the server's preferred languages, where the files have them.</summary>
    public IList<string> SuggestedLanguages { get; init; } = [];

    /// <summary>Gets or sets the expected space the removed tracks take up, in bytes.</summary>
    public long RemovedTrackBytes { get; set; }

    /// <summary>Gets or sets the number of audio tracks that will be removed.</summary>
    public int RemovedAudioTracks { get; set; }

    /// <summary>Gets or sets the number of subtitle tracks that will be removed.</summary>
    public int RemovedSubtitleTracks { get; set; }

    /// <summary>Gets or sets the number of files with no audio in the chosen languages, which keep their main audio track.</summary>
    public int AudioFallbackFiles { get; set; }

    /// <summary>Gets the files counted in <see cref="AudioFallbackFiles"/>, with the track each keeps.</summary>
    public IList<AudioFallbackFile> AudioFallbacks { get; init; } = [];

    /// <summary>Gets or sets the number of audio tracks that will be converted.</summary>
    public int ConvertedAudioTracks { get; set; }

    /// <summary>Gets or sets the expected space converting audio saves, in bytes.</summary>
    public long AudioSavingBytes { get; set; }

    /// <summary>Gets or sets the number of converted tracks that are Dolby Atmos or DTS:X, whose height channels are lost.</summary>
    public int ConvertedObjectAudioTracks { get; set; }

    /// <summary>Gets or sets the number of HDR files.</summary>
    public int HdrFiles { get; set; }

    /// <summary>Gets or sets the number of files with Dolby Vision.</summary>
    public int DolbyVisionFiles { get; set; }

    /// <summary>Gets or sets the number of files with a Dolby Vision enhancement layer, which removing Dolby Vision drops.</summary>
    public int DolbyVisionLayerFiles { get; set; }

    /// <summary>Gets or sets the number of files that are only Dolby Vision, with no HDR10 picture beneath, which can't be re-encoded or have Dolby Vision removed.</summary>
    public int DolbyVisionOnlyFiles { get; set; }

    /// <summary>Gets or sets a value indicating whether this server's ffmpeg can remove Dolby Vision.</summary>
    public bool CanRemoveDolbyVision { get; set; }

    /// <summary>Gets or sets how many encodes the settings run at once.</summary>
    public int Slots { get; set; } = 1;

    /// <summary>Gets the total speed with 1, 2, 3 and so on encodes at once, each against one at a time, so the time for a batch can be worked out.</summary>
    public IList<double> Speedups { get; init; } = [];
}

/// <summary>
/// A language that tracks in the chosen files are in.
/// </summary>
/// <param name="Code">The normalized code, such as eng, or an empty string for tracks with no language set.</param>
/// <param name="Name">The name, such as English.</param>
/// <param name="AudioTracks">How many audio tracks are in it.</param>
/// <param name="AudioBytes">The space those audio tracks take up.</param>
/// <param name="SubtitleTracks">How many subtitle tracks are in it.</param>
/// <param name="SubtitleBytes">The space those subtitle tracks take up.</param>
public sealed record LanguageOption(string Code, string Name, int AudioTracks, long AudioBytes, int SubtitleTracks, long SubtitleBytes);

/// <summary>
/// One kind of track in the chosen files: the same language, codec, channels and title, in one or more files.
/// </summary>
/// <param name="Key">What identifies it, as <see cref="TrackInfo.Key"/>.</param>
/// <param name="Kind">Audio or subtitle.</param>
/// <param name="Language">The normalized language, or an empty string when none is set.</param>
/// <param name="Codec">The codec, such as truehd.</param>
/// <param name="Profile">The codec's profile, such as DTS-HD MA, or null.</param>
/// <param name="Channels">The audio channels, or 0.</param>
/// <param name="Title">The title, or null.</param>
/// <param name="IsDefault">Whether it's the default track in any of the files.</param>
/// <param name="IsForced">Whether it's a forced subtitle track.</param>
/// <param name="IsHearingImpaired">Whether it's a subtitle track for the deaf and hard of hearing.</param>
/// <param name="IsCommentary">Whether its title says it's a commentary.</param>
/// <param name="Files">How many of the chosen files have it.</param>
/// <param name="Bytes">The space it takes in all of them.</param>
/// <param name="FileNames">The names of the files that have it.</param>
/// <param name="OnlyMainAudioIn">The names of the files where it's the only main audio, which keep it even if it's unticked.</param>
public sealed record TrackOption(string Key, TrackKind Kind, string Language, string Codec, string? Profile, int Channels, string? Title, bool IsDefault, bool IsForced, bool IsHearingImpaired, bool IsCommentary, int Files, long Bytes, IReadOnlyList<string> FileNames, IReadOnlyList<string> OnlyMainAudioIn);

/// <summary>
/// A file that keeps an audio track the choices remove, because none of its main audio was chosen.
/// </summary>
/// <param name="Name">The file's title, such as "Example Show S01E01: Pilot".</param>
/// <param name="TrackKey">The track it keeps, as <see cref="TrackInfo.Key"/>, which is in the estimate's tracks.</param>
public sealed record AudioFallbackFile(string Name, string TrackKey);

/// <summary>
/// What a new file will be called.
/// </summary>
/// <param name="Current">The original's name.</param>
/// <param name="KeepBoth">The new file's name when the original is kept.</param>
/// <param name="Replace">The new file's name when it replaces the original.</param>
public sealed record PlannedName(string Current, string KeepBoth, string Replace);

/// <summary>
/// The expected size of the new files at one quality.
/// </summary>
/// <param name="Quality">The quality.</param>
/// <param name="OutputBytes">The expected total size, in bytes.</param>
/// <param name="Measured">Whether test encodes of this quality measured it, rather than a rule of thumb or another quality's tests.</param>
public sealed record LevelEstimate(QualityPreset Quality, long OutputBytes, bool Measured);

/// <summary>
/// The expected encoding time with one effort.
/// </summary>
/// <param name="Effort">The effort.</param>
/// <param name="Seconds">The expected time, or null if nothing with this encoder has been timed yet.</param>
/// <param name="Measured">Whether this server has timed this effort, rather than scaling another effort's time.</param>
public sealed record EffortEstimate(EncodeEffort Effort, double? Seconds, bool Measured);

/// <summary>
/// Where a saved encoding speed came from.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SpeedBasis
{
    /// <summary>Test encodes of the busiest and a typical scene of a file.</summary>
    Tested,

    /// <summary>A finished encode, which corrected the tests.</summary>
    Encoded
}

/// <summary>
/// One tested scene: how busy it was, and how long it took to encode.
/// </summary>
/// <param name="Busyness">How busy the scene is, from <see cref="SpeedEstimator.Busyness"/>.</param>
/// <param name="SecondsPerPixel">Seconds to encode each output pixel.</param>
public sealed record SpeedPoint(double Busyness, double SecondsPerPixel);

/// <summary>
/// How long encoding takes with one setting, in seconds per output pixel, depending on how busy the picture is.
/// </summary>
/// <param name="Points">The tested scenes, calmest first.</param>
/// <param name="Scale">A correction from the whole-film mix of scenes, or from a finished encode.</param>
/// <param name="Basis">Where the speed came from.</param>
/// <param name="VideoScale">How big the video came out against the rule of thumb for it, or null if not measured.</param>
public sealed record SpeedModel(IReadOnlyList<SpeedPoint> Points, double Scale, SpeedBasis Basis, double? VideoScale = null)
{
    /// <summary>
    /// Gets the seconds per output pixel for a picture this busy: in proportion between the two tested scenes
    /// either side of it, or the nearest tested scene past either end.
    /// </summary>
    /// <param name="busyness">How busy the picture is.</param>
    /// <returns>The seconds per pixel.</returns>
    public double SecondsPerPixel(double busyness)
    {
        var below = Points.LastOrDefault(p => p.Busyness <= busyness);
        var above = Points.FirstOrDefault(p => p.Busyness >= busyness);
        var value = (below, above) switch
        {
            (null, null) => 0,
            (null, _) => above.SecondsPerPixel,
            (_, null) => below.SecondsPerPixel,
            _ when above.Busyness - below.Busyness <= 0 => below.SecondsPerPixel,
            _ => below.SecondsPerPixel + ((above.SecondsPerPixel - below.SecondsPerPixel) * (busyness - below.Busyness) / (above.Busyness - below.Busyness))
        };
        return value * Scale;
    }
}

/// <summary>
/// What a speed test needs to know about a file.
/// </summary>
/// <param name="Path">The file.</param>
/// <param name="DurationSeconds">Its length.</param>
/// <param name="FramesPerSecond">Its frame rate.</param>
/// <param name="Width">Its width.</param>
/// <param name="Height">Its height.</param>
/// <param name="Codec">Its video codec.</param>
/// <param name="AudioBitrate">All its audio tracks together, in bits per second.</param>
/// <param name="Source">What ffmpeg needs to know about its video.</param>
/// <param name="Output">The size it will be encoded at.</param>
public sealed record SpeedTestFile(string Path, double DurationSeconds, double FramesPerSecond, int Width, int Height, string Codec, long AudioBitrate, SourceVideo Source, (int Width, int Height) Output)
{
    /// <summary>Gets the size to scale to, or null when it keeps its size.</summary>
    public (int Width, int Height)? ScaleTo => Output.Width < Width || Output.Height < Height ? Output : null;

    /// <summary>Gets the number of pixels in each encoded frame.</summary>
    public double OutputPixels => Output.Width * (double)Output.Height;
}

/// <summary>
/// How many encodes run at once, and what is being done for people who are watching.
/// </summary>
/// <param name="Slots">How many encodes the settings allow at once.</param>
/// <param name="Allowed">How many may run right now, after making room for viewers.</param>
/// <param name="Running">How many are encoding.</param>
/// <param name="Paused">How many are paused for viewers or a test.</param>
/// <param name="Queued">How many are waiting.</param>
/// <param name="Policy">What encoding does while someone is watching.</param>
/// <param name="Streams">The streams Jellyfin is transcoding.</param>
/// <param name="Summary">The status in a sentence, such as "Sam is transcoding Example Film. Encoding is using 1 of 2 slots."</param>
public sealed record EncodeStatus(int Slots, int Allowed, int Running, int Paused, int Queued, WatchingPolicy Policy, IReadOnlyList<LiveTranscode> Streams, string Summary);
