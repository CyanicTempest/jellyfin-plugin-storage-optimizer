// SPDX-License-Identifier: GPL-3.0-only
using System;
using Jellyfin.Plugin.DataManager.Encoding;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.DataManager.Configuration;

/// <summary>
/// Settings for the Storage Optimizer plugin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether the original file is kept as a separate version after re-encoding.
    /// This is only what the re-encode window starts on. Each run can still change it.
    /// </summary>
    public bool KeepOriginalWhenSpaceAllows { get; set; } = true;

    /// <summary>
    /// Gets or sets the quality the re-encode window starts on. Each run can still change it.
    /// </summary>
    public QualityPreset DefaultQuality { get; set; } = QualityPreset.Balanced;

    /// <summary>
    /// Gets or sets the codec the re-encode window starts on, when the server can encode it. Each run can still change it.
    /// </summary>
    public TargetCodec DefaultCodec { get; set; } = TargetCodec.Hevc;

    /// <summary>
    /// Gets or sets the speed the re-encode window starts on, when the chosen codec's encoder has it. Each run can still change it.
    /// </summary>
    public EncodeEffort DefaultEffort { get; set; } = EncodeEffort.Standard;

    /// <summary>
    /// Gets or sets how the dashboard's Encoding section starts.
    /// </summary>
    public SectionStart EncodingSectionStart { get; set; } = SectionStart.Remember;

    /// <summary>
    /// Gets or sets how the dashboard's Clean up section starts.
    /// </summary>
    public SectionStart CleanUpSectionStart { get; set; } = SectionStart.Remember;

    /// <summary>
    /// Gets or sets a value indicating whether re-encoded files get Jellyfin's details and artwork written into them.
    /// Off unless it's switched on in the settings.
    /// </summary>
    public bool WriteMetadata { get; set; }

    /// <summary>
    /// Gets or sets how re-encoded files are named. They keep their current names unless this is changed in the settings.
    /// </summary>
    public NamingStyle Naming { get; set; } = NamingStyle.KeepCurrent;

    /// <summary>
    /// Gets or sets the name for re-encoded movies when <see cref="Naming"/> is custom.
    /// </summary>
    public string MovieNameTemplate { get; set; } = FileNaming.JellyfinMovie;

    /// <summary>
    /// Gets or sets the name for re-encoded episodes when <see cref="Naming"/> is custom.
    /// </summary>
    public string EpisodeNameTemplate { get; set; } = FileNaming.JellyfinEpisode;

    /// <summary>
    /// Gets or sets how many encodes run at once, or 0 to use the number recommended for this server's GPU.
    /// </summary>
    public int EncodesAtOnce { get; set; }

    /// <summary>
    /// Gets or sets what encoding does while someone is watching something Jellyfin transcodes.
    /// </summary>
    public WatchingPolicy WhenWatching { get; set; } = WatchingPolicy.LeaveRoom;

    /// <summary>
    /// Gets or sets how many seconds to wait after the last stream ends before encoding carries on.
    /// </summary>
    public int ResumeAfterSeconds { get; set; } = 60;

    /// <summary>
    /// Gets or sets a value indicating whether only streams that transcode the video count. Direct play and remuxing barely use the GPU.
    /// </summary>
    public bool OnlyCountVideoTranscodes { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether, on an NVIDIA card, decoded frames stay on the GPU for the encoder instead of
    /// being copied to system memory and back. Off unless it's switched on in the settings. It's only used for encodes that
    /// don't scale the picture or tone map it, and a file it can't be done for is encoded the usual way.
    /// </summary>
    public bool KeepFramesOnGpu { get; set; }

    /// <summary>
    /// Gets or sets how many days an original that a re-encode replaced is kept in a hidden recycle folder beside it
    /// before it's deleted, or 0 to delete it straight away.
    /// </summary>
    public int RecycleDays { get; set; }

    /// <summary>Gets or sets a value indicating whether encodes only start between <see cref="EncodeFromHour"/> and <see cref="EncodeUntilHour"/>.</summary>
    public bool LimitEncodingHours { get; set; }

    /// <summary>Gets or sets the hour, 0 to 23 in the server's time, from which encodes may start.</summary>
    public int EncodeFromHour { get; set; } = 1;

    /// <summary>Gets or sets the hour, 0 to 23 in the server's time, after which no more encodes start. Ones running finish.</summary>
    public int EncodeUntilHour { get; set; } = 7;

    /// <summary>Gets or sets a value indicating whether ffmpeg runs at low priority, so the server stays responsive.</summary>
    public bool LowPriority { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether encodes may only run on the GPU, never on the CPU.</summary>
    public bool NeverEncodeOnCpu { get; set; }

    /// <summary>Gets or sets which audio the re-encode and remux windows start on.</summary>
    public AudioChoice DefaultAudio { get; set; } = AudioChoice.KeepAll;

    /// <summary>Gets or sets which subtitles the re-encode and remux windows start on.</summary>
    public SubtitleChoice DefaultSubtitles { get; set; } = SubtitleChoice.KeepAll;

    /// <summary>
    /// Gets or sets the languages the windows start ticked, as codes separated by commas, such as "eng,jpn". Empty
    /// starts on the Jellyfin user's own audio language and the server's.
    /// </summary>
    public string DefaultLanguages { get; set; } = string.Empty;

    /// <summary>Gets or sets what the windows start converting big audio to.</summary>
    public AudioFormat DefaultAudioFormat { get; set; } = AudioFormat.Keep;

    /// <summary>Gets or sets a value indicating whether the windows start on mixing surround audio down to stereo.</summary>
    public bool DefaultStereo { get; set; }

    /// <summary>Gets or sets how many gigabytes are always left free on a drive: an encode that would leave less doesn't start.</summary>
    public int KeepFreeGigabytes { get; set; } = 1;

    /// <summary>Gets or sets the least a file must save, in megabytes, to be suggested.</summary>
    public int SuggestMinimumMegabytes { get; set; } = 300;

    /// <summary>Gets or sets the least share of its size, in percent, a file must save to be suggested.</summary>
    public int SuggestMinimumPercent { get; set; } = 25;

    /// <summary>
    /// Gets or sets what re-encodes and remuxes do with audio track titles. They're tidied unless this is changed.
    /// </summary>
    public TrackTitleMode AudioTitles { get; set; } = TrackTitleMode.Clear;

    /// <summary>
    /// Gets or sets what re-encodes and remuxes do with subtitle track titles. They're tidied unless this is changed.
    /// </summary>
    public TrackTitleMode SubtitleTitles { get; set; } = TrackTitleMode.Clear;

    /// <summary>Gets or sets a value indicating whether tidied titles keep words saying a track is a commentary.</summary>
    public bool KeepCommentaryWords { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether tidied titles keep words saying a track is audio description.</summary>
    public bool KeepAudioDescriptionWords { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether tidied titles keep words saying a track is the original language.</summary>
    public bool KeepOriginalWords { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether tidied titles keep any other words, such as "Signs and songs".</summary>
    public bool KeepOtherTitleText { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether a track with no language gets the one its title names, and tracks get
    /// the commentary, audio description, original, forced and hearing impaired flags their titles describe.
    /// </summary>
    public bool SetTrackLanguageAndFlags { get; set; } = true;

    /// <summary>
    /// Gets or sets the file issue tags the media list leaves out, by name, such as OldContainer. All are shown unless
    /// switched off.
    /// </summary>
#pragma warning disable CA1819 // Jellyfin saves settings as XML, which needs an array here.
    public string[] HiddenIssueTags { get; set; } = [];
#pragma warning restore CA1819

    /// <summary>
    /// Gets or sets a value indicating whether the storage scan reads the first frame of each HDR10 file to see
    /// whether it has its brightness information. It takes about a second a file, once. Off unless switched on.
    /// </summary>
    public bool CheckHdrBrightness { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a file is read through before it's encoded, to find broken timestamps,
    /// audio out of step with the video and HDR without colour tags. Off unless switched on.
    /// </summary>
    public bool CheckFilesWhenQueued { get; set; }

    /// <summary>Gets or sets a value indicating whether interlaced frames are deinterlaced when encoding.</summary>
    public bool DeinterlaceWhenEncoding { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether HDR10 brightness information is copied into the new file and checked.</summary>
    public bool CopyHdrBrightness { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether timestamps are rebuilt for AVI, MPEG-TS and WMV files, and files a read
    /// found broken timestamps in, and the new file's audio is checked to be in step with its video.
    /// </summary>
    public bool RebuildTimestamps { get; set; } = true;

    /// <summary>
    /// Gets or sets the ids of the drives the overview leaves out. Drives not listed are shown, so a new drive shows up
    /// on its own. Encodes still check a hidden drive's free space.
    /// </summary>
#pragma warning disable CA1819 // Jellyfin saves settings as XML, which needs an array here.
    public string[] HiddenDrives { get; set; } = [];
#pragma warning restore CA1819

    /// <summary>
    /// Gets or sets the GPU the last test ran on, so a test of a different card isn't trusted.
    /// </summary>
    public string GpuTestedDevice { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when the GPU was last tested, or null if it hasn't been.
    /// </summary>
    public DateTime? GpuTestedAt { get; set; }

    /// <summary>
    /// Gets or sets the speed with 1, 2, 3 and 4 encodes at once from the last test, comma separated, such as "1.0,1.9,2.0,2.0".
    /// </summary>
    public string GpuTestSpeeds { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the rest of the last GPU test, as JSON: the speeds of each pass in frames a second, how the film was
    /// decoded, the film's frame rate and what the test said, so the page can show them again after a restart.
    /// </summary>
    public string GpuTestDetails { get; set; } = string.Empty;
}
