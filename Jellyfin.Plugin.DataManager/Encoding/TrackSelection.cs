// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.DataManager.Encoding;

/// <summary>
/// Which audio tracks a re-encode keeps.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AudioChoice
{
    /// <summary>Keep every audio track.</summary>
    KeepAll,

    /// <summary>Keep the tracks in the chosen languages, and any with no language set.</summary>
    KeepLanguages
}

/// <summary>
/// Which subtitle tracks inside the file a re-encode keeps. Subtitle files beside the video are never touched.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SubtitleChoice
{
    /// <summary>Keep every subtitle track.</summary>
    KeepAll,

    /// <summary>Keep the tracks in the chosen languages, and any with no language set.</summary>
    KeepLanguages,

    /// <summary>Keep only forced subtitles, which translate foreign dialogue and signs.</summary>
    KeepForced,

    /// <summary>Remove every subtitle track.</summary>
    RemoveAll
}

/// <summary>
/// The kind of a track the re-encode can keep or remove.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TrackKind
{
    /// <summary>An audio track.</summary>
    Audio,

    /// <summary>A subtitle track.</summary>
    Subtitle
}

/// <summary>
/// An audio or subtitle track inside a file.
/// </summary>
/// <param name="Index">The stream's index in the file, as ffmpeg counts them.</param>
/// <param name="Kind">Audio or subtitle.</param>
/// <param name="Language">The language as <see cref="LanguageCodes.Normalize"/> gives it, or null when it isn't set.</param>
/// <param name="Codec">The codec, such as ac3 or hdmv_pgs_subtitle.</param>
/// <param name="IsForced">Whether it's a forced subtitle track.</param>
/// <param name="IsDefault">Whether players pick it by default.</param>
/// <param name="BitRate">Its bitrate in bits per second, or 0 when the file doesn't say.</param>
/// <param name="Channels">Its audio channels, or 0 when not known.</param>
/// <param name="Title">Its title, such as "Director's commentary", or null.</param>
/// <param name="Profile">The codec's profile, such as DTS-HD MA, or null.</param>
/// <param name="IsHearingImpaired">Whether it's a subtitle track for the deaf and hard of hearing (SDH).</param>
public sealed record TrackInfo(int Index, TrackKind Kind, string? Language, string Codec, bool IsForced, bool IsDefault, long BitRate, int Channels, string? Title = null, string? Profile = null, bool IsHearingImpaired = false)
{
    /// <summary>
    /// Gets what identifies this kind of track across files: the same language, codec, channels and title. A season's
    /// episodes usually share their track layout, so one choice covers the same track in each.
    /// </summary>
    public string Key => string.Join('|', Kind, Language ?? string.Empty, Codec, Profile ?? string.Empty, Channels, Title?.Trim() ?? string.Empty).ToUpperInvariant();

    /// <summary>
    /// Gets a value indicating whether the track looks like a commentary, from its title.
    /// </summary>
    public bool IsCommentary => Title is not null && (Title.Contains("comment", StringComparison.OrdinalIgnoreCase) || Title.Contains("kommentar", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Gets the bitrate, guessed from the codec and channels when the file doesn't say.
    /// </summary>
    public long EffectiveBitRate => BitRate > 0 ? BitRate : TrackSelection.GuessBitRate(this);
}

/// <summary>
/// The tracks a re-encode keeps and removes.
/// </summary>
/// <param name="Kept">The tracks kept, in the file's order.</param>
/// <param name="Removed">The tracks removed.</param>
/// <param name="AudioFallback">Whether no main audio track (one that isn't a commentary) was in the chosen languages, so the main one was kept anyway.</param>
/// <param name="FallbackTrack">The audio track kept because of that, or null.</param>
public sealed record TrackPlan(IReadOnlyList<TrackInfo> Kept, IReadOnlyList<TrackInfo> Removed, bool AudioFallback, TrackInfo? FallbackTrack = null)
{
    /// <summary>Gets the number of audio tracks removed.</summary>
    public int RemovedAudio => Removed.Count(t => t.Kind == TrackKind.Audio);

    /// <summary>Gets the number of subtitle tracks removed.</summary>
    public int RemovedSubtitles => Removed.Count(t => t.Kind == TrackKind.Subtitle);

    /// <summary>Gets the combined bitrate of the audio kept.</summary>
    public long KeptAudioBitRate => Kept.Where(t => t.Kind == TrackKind.Audio).Sum(t => t.EffectiveBitRate);

    /// <summary>Gets the combined bitrate of the subtitles kept.</summary>
    public long KeptSubtitleBitRate => Kept.Where(t => t.Kind == TrackKind.Subtitle).Sum(t => t.EffectiveBitRate);

    /// <summary>Gets the combined bitrate of everything removed.</summary>
    public long RemovedBitRate => Removed.Sum(t => t.EffectiveBitRate);

    /// <summary>
    /// Gets a value indicating whether any styled (ASS/SSA) subtitles are kept, which need the file's fonts.
    /// </summary>
    public bool NeedsFonts => Kept.Any(t => t.Kind == TrackKind.Subtitle && TrackSelection.IsStyled(t.Codec));

    /// <summary>
    /// Describes what was removed, such as "Removed 2 audio tracks and 5 subtitle tracks.", or null if nothing was.
    /// </summary>
    /// <returns>The description.</returns>
    public string? Describe()
    {
        var parts = new List<string>();
        if (RemovedAudio > 0)
        {
            parts.Add(RemovedAudio + (RemovedAudio == 1 ? " audio track" : " audio tracks"));
        }

        if (RemovedSubtitles > 0)
        {
            parts.Add(RemovedSubtitles + (RemovedSubtitles == 1 ? " subtitle track" : " subtitle tracks"));
        }

        var text = parts.Count == 0 ? null : "Removed " + string.Join(" and ", parts) + ".";
        if (AudioFallback)
        {
            text = (text is null ? string.Empty : text + " ") + "None of its main audio was chosen, so its main audio track was kept.";
        }

        return text;
    }
}

/// <summary>
/// Chooses which audio and subtitle tracks a re-encode keeps.
/// </summary>
public static class TrackSelection
{
    /// <summary>
    /// Chooses the tracks to keep. A file is never left without its main audio: when none of its audio, or only a
    /// commentary, is in the chosen languages, its default track that isn't a commentary (or else its first such
    /// track) is kept too. Tracks with no language set are always kept, since there's no telling what they are.
    /// </summary>
    /// <param name="tracks">The file's audio and subtitle tracks.</param>
    /// <param name="audio">Which audio to keep.</param>
    /// <param name="subtitles">Which subtitles to keep.</param>
    /// <param name="languages">The languages to keep, normalized. With none, language choices keep everything.</param>
    /// <param name="excluded">Tracks, by <see cref="TrackInfo.Key"/>, to remove even though their language is kept.</param>
    /// <param name="included">Tracks, by <see cref="TrackInfo.Key"/>, to keep even though their language isn't.</param>
    /// <returns>The plan.</returns>
    public static TrackPlan Choose(IReadOnlyList<TrackInfo> tracks, AudioChoice audio, SubtitleChoice subtitles, IReadOnlyCollection<string> languages, IReadOnlyCollection<string>? excluded = null, IReadOnlyCollection<string>? included = null)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(languages);
        var exclude = new HashSet<string>(excluded ?? [], StringComparer.OrdinalIgnoreCase);
        var include = new HashSet<string>(included ?? [], StringComparer.OrdinalIgnoreCase);

        // A track is kept when its language is (or it has none), unless it was unticked on its own; or when it was
        // ticked on its own.
        bool InLanguages(TrackInfo t)
        {
            if (include.Contains(t.Key))
            {
                return true;
            }

            var byLanguage = (languages.Count == 0 && include.Count == 0) || t.Language is null || languages.Contains(t.Language, StringComparer.OrdinalIgnoreCase);
            return byLanguage && !exclude.Contains(t.Key);
        }

        var keep = new HashSet<int>();
        var audioTracks = tracks.Where(t => t.Kind == TrackKind.Audio).ToList();
        TrackInfo? fallback = null;
        if (audio == AudioChoice.KeepLanguages)
        {
            var kept = audioTracks.Where(InLanguages).ToList();
            if (!kept.Any(t => !t.IsCommentary) && audioTracks.Count > 0)
            {
                var main = audioTracks.Where(t => !t.IsCommentary).ToList();
                var pick = main.FirstOrDefault(t => t.IsDefault) ?? main.FirstOrDefault() ?? audioTracks.FirstOrDefault(t => t.IsDefault) ?? audioTracks[0];
                if (!kept.Contains(pick))
                {
                    kept.Add(pick);
                    fallback = pick;
                }
            }

            keep.UnionWith(kept.Select(t => t.Index));
        }
        else
        {
            keep.UnionWith(audioTracks.Select(t => t.Index));
        }

        foreach (var t in tracks.Where(t => t.Kind == TrackKind.Subtitle))
        {
            var kept = subtitles switch
            {
                SubtitleChoice.KeepLanguages => InLanguages(t),
                SubtitleChoice.KeepForced => t.IsForced && InLanguages(t),
                SubtitleChoice.RemoveAll => false,
                _ => true
            };
            if (kept)
            {
                keep.Add(t.Index);
            }
        }

        var ordered = tracks.OrderBy(t => t.Index).ToList();
        return new TrackPlan(ordered.Where(t => keep.Contains(t.Index)).ToList(), ordered.Where(t => !keep.Contains(t.Index)).ToList(), fallback is not null, fallback);
    }

    /// <summary>
    /// Gets the track that is a file's only main audio, the one sound the film has apart from commentaries, so the
    /// window can warn before it's unticked. Several copies of the same track count as one.
    /// </summary>
    /// <param name="tracks">The file's tracks.</param>
    /// <returns>The key of its only main audio track, or null when it has none or more than one kind.</returns>
    public static string? OnlyMainAudio(IReadOnlyList<TrackInfo> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        var keys = tracks.Where(t => t.Kind == TrackKind.Audio && !t.IsCommentary).Select(t => t.Key).Distinct(StringComparer.Ordinal).ToList();
        return keys.Count == 1 ? keys[0] : null;
    }

    /// <summary>
    /// Gets whether a file's audio still has its film's sound: some audio that isn't a commentary, or only
    /// commentaries if that's all the original had.
    /// </summary>
    /// <param name="original">The original's audio and subtitle tracks.</param>
    /// <param name="output">The new file's.</param>
    /// <returns>Whether the new file's audio is fine.</returns>
    public static bool KeepsMainAudio(IReadOnlyList<TrackInfo> original, IReadOnlyList<TrackInfo> output)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(output);
        var originalAudio = original.Where(t => t.Kind == TrackKind.Audio).ToList();
        var outputAudio = output.Where(t => t.Kind == TrackKind.Audio).ToList();
        if (originalAudio.Count == 0)
        {
            return true;
        }

        return originalAudio.Any(t => !t.IsCommentary) ? outputAudio.Any(t => !t.IsCommentary) : outputAudio.Count > 0;
    }

    /// <summary>
    /// Gets whether a subtitle codec is styled text that uses the file's fonts.
    /// </summary>
    /// <param name="codec">The codec.</param>
    /// <returns>True for ASS and SSA.</returns>
    public static bool IsStyled(string codec) => string.Equals(codec, "ass", StringComparison.OrdinalIgnoreCase) || string.Equals(codec, "ssa", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Guesses a track's bitrate from its codec when the file doesn't say: lossless and high-resolution audio is
    /// big, picture subtitles are noticeable, and text subtitles are tiny.
    /// </summary>
    /// <param name="track">The track.</param>
    /// <returns>The bitrate in bits per second.</returns>
    public static long GuessBitRate(TrackInfo track)
    {
        ArgumentNullException.ThrowIfNull(track);
        var codec = track.Codec; // ffmpeg names codecs in lower case.
        if (track.Kind == TrackKind.Subtitle)
        {
            return codec switch
            {
                "hdmv_pgs_subtitle" or "pgssub" => 30_000,
                "dvd_subtitle" or "dvdsub" or "dvb_subtitle" => 10_000,
                _ => 200
            };
        }

        var channels = track.Channels > 0 ? track.Channels : 2;
        return codec switch
        {
            "truehd" or "mlp" => 3_500_000,
            "flac" or "alac" or "pcm_s16le" or "pcm_s24le" or "pcm_bluray" => 350_000L * channels,
            "dts" => channels > 2 ? 1_509_000 : 768_000,
            "eac3" => channels > 2 ? 768_000 : 224_000,
            "ac3" => channels > 2 ? 640_000 : 192_000,
            "aac" or "opus" or "vorbis" or "mp3" or "mp2" => 64_000L * channels,
            _ => 96_000L * channels
        };
    }
}
