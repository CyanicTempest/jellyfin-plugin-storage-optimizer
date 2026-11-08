// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.DataManager.Configuration;

namespace Jellyfin.Plugin.DataManager.Encoding;

/// <summary>
/// What re-encodes and remuxes do with track titles.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TrackTitleMode
{
    /// <summary>Keep each title as it is.</summary>
    Leave,

    /// <summary>Clear titles that only repeat the language or format, keeping extra words such as "Director's commentary".</summary>
    Clear,

    /// <summary>Write a full title, such as "English Dolby Digital Plus 5.1 (Director's commentary)".</summary>
    Full
}

/// <summary>
/// How tracks are titled and tagged in a new file, from the settings.
/// </summary>
/// <param name="Audio">What happens to audio titles.</param>
/// <param name="Subtitles">What happens to subtitle titles.</param>
/// <param name="KeepCommentary">Whether words saying a track is a commentary are kept.</param>
/// <param name="KeepAudioDescription">Whether words saying a track is audio description are kept.</param>
/// <param name="KeepOriginal">Whether words saying a track is the original language are kept.</param>
/// <param name="KeepOtherText">Whether any other words in a title are kept.</param>
/// <param name="SetLanguageAndFlags">Whether a missing language is filled in from the title and flags such as commentary and forced are set.</param>
public sealed record TitleRules(TrackTitleMode Audio, TrackTitleMode Subtitles, bool KeepCommentary, bool KeepAudioDescription, bool KeepOriginal, bool KeepOtherText, bool SetLanguageAndFlags)
{
    /// <summary>Gets rules that leave every track as it is.</summary>
    public static TitleRules None { get; } = new(TrackTitleMode.Leave, TrackTitleMode.Leave, true, true, true, true, false);

    /// <summary>Gets a value indicating whether the rules change anything.</summary>
    public bool ChangesAnything => Audio != TrackTitleMode.Leave || Subtitles != TrackTitleMode.Leave || SetLanguageAndFlags;

    /// <summary>
    /// Gets the rules from the settings.
    /// </summary>
    /// <param name="config">The settings.</param>
    /// <returns>The rules.</returns>
    public static TitleRules From(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new(config.AudioTitles, config.SubtitleTitles, config.KeepCommentaryWords, config.KeepAudioDescriptionWords, config.KeepOriginalWords, config.KeepOtherTitleText, config.SetTrackLanguageAndFlags);
    }
}

/// <summary>
/// The changes to one track's details in the new file.
/// </summary>
/// <param name="Index">The track's index in the original.</param>
/// <param name="Kind">Audio or subtitle.</param>
/// <param name="Title">The new title, an empty string to clear it, or null to leave it.</param>
/// <param name="Language">The language tag to write, as a three-letter code, or null to leave it.</param>
/// <param name="Flags">Dispositions to add, such as comment or forced.</param>
public sealed record TrackRewrite(int Index, TrackKind Kind, string? Title, string? Language, IReadOnlyList<string> Flags)
{
    /// <summary>Gets a value indicating whether it changes anything.</summary>
    public bool ChangesAnything => Title is not null || Language is not null || Flags.Count > 0;
}

/// <summary>
/// Tidies track titles. There's no standard for them: Matroska's track name is free text, and the language and the
/// track's role belong in its language tag and flags. Jellyfin builds a name such as "English - Dolby Digital+ - 5.1"
/// from those, and shows a track's title before whatever of that the title doesn't already say, so a title that only
/// repeats the language or format adds nothing. Jellyfin reads the default, forced, hearing impaired and original
/// flags but not commentary or audio description, so those words stay in the title.
/// </summary>
public static partial class TrackTitles
{
    // Words that are also language names or codes somewhere, but in a title mean something else.
    private static readonly HashSet<string> _notLanguages = new(StringComparer.OrdinalIgnoreCase) { "original", "full", "main", "forced", "commentary", "audio", "description", "dolby", "digital", "plus", "stereo", "surround", "mono" };

    private static readonly Dictionary<string, string> _codecNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["truehd"] = "TrueHD", ["mlp"] = "TrueHD", ["eac3"] = "Dolby Digital Plus", ["ac3"] = "Dolby Digital", ["dts"] = "DTS", ["aac"] = "AAC",
        ["flac"] = "FLAC", ["opus"] = "Opus", ["mp3"] = "MP3", ["mp2"] = "MP2", ["vorbis"] = "Vorbis", ["alac"] = "ALAC",
        ["pcm_s16le"] = "PCM", ["pcm_s24le"] = "PCM", ["pcm_bluray"] = "PCM", ["pcm_s32le"] = "PCM"
    };

    /// <summary>
    /// Plans the changes to a track.
    /// </summary>
    /// <param name="track">The track in the original.</param>
    /// <param name="rules">The rules.</param>
    /// <param name="knownLanguage">Gives the three-letter code of a language name or code Jellyfin knows, or null.</param>
    /// <param name="languageName">Gives a language's name, such as English, from its code.</param>
    /// <param name="conversion">How the track is converted, if it is, so a full title names the new format.</param>
    /// <param name="language">A language chosen for the track in the window, as a three-letter code, which is written whatever the rules.</param>
    /// <returns>The changes.</returns>
    public static TrackRewrite Plan(TrackInfo track, TitleRules rules, Func<string, string?> knownLanguage, Func<string, string> languageName, AudioConversion? conversion = null, string? language = null)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(knownLanguage);
        ArgumentNullException.ThrowIfNull(languageName);

        var audio = track.Kind == TrackKind.Audio;
        var mode = audio ? rules.Audio : rules.Subtitles;
        var title = track.Title?.Trim() ?? string.Empty;
        if (HandlerRegex().IsMatch(title))
        {
            // Muxers write "SoundHandler" and the like when there's no real title.
            title = string.Empty;
        }

        // The language the new file says: chosen in the window, the file's own, or one the title names.
        var guessed = track.Language is null && rules.SetLanguageAndFlags ? GuessLanguage(title, knownLanguage) : null;
        var writeLanguage = language ?? guessed;
        var finalLanguage = writeLanguage ?? track.Language;

        // What the title says beyond the language and format.
        var rest = Remainder(title, knownLanguage);
        var commentary = track.IsCommentary || CommentaryRegex().IsMatch(rest);
        var description = audio && DescriptionRegex().IsMatch(rest);
        var original = OriginalRegex().IsMatch(rest);
        var forced = !audio && (track.IsForced || ForcedRegex().IsMatch(rest));
        var hearing = !audio && (track.IsHearingImpaired || HearingRegex().IsMatch(rest));

        var flags = new List<string>();
        if (rules.SetLanguageAndFlags)
        {
            if (audio && commentary)
            {
                flags.Add("comment");
            }

            if (description)
            {
                flags.Add("visual_impaired");
            }

            if (original)
            {
                flags.Add("original");
            }

            if (forced && !track.IsForced)
            {
                flags.Add("forced");
            }

            if (hearing && !track.IsHearingImpaired)
            {
                flags.Add("hearing_impaired");
            }
        }

        string? newTitle = null;
        if (mode != TrackTitleMode.Leave)
        {
            var extra = Extra(rest, rules, audio);
            if (mode == TrackTitleMode.Full)
            {
                var parts = new List<string>();
                if (finalLanguage is not null)
                {
                    parts.Add(languageName(finalLanguage));
                }

                if (audio)
                {
                    parts.Add(conversion?.Label ?? FormatName(track));
                }
                else
                {
                    if (forced)
                    {
                        parts.Add("Forced");
                    }

                    if (hearing)
                    {
                        parts.Add("SDH");
                    }
                }

                var head = string.Join(' ', parts.Where(p => p.Length > 0));
                newTitle = extra.Length == 0 ? head : head.Length == 0 ? extra : head + " (" + extra + ")";
            }
            else
            {
                newTitle = extra;
            }

            if (string.Equals(newTitle, track.Title?.Trim() ?? string.Empty, StringComparison.Ordinal))
            {
                newTitle = null;
            }
        }
        else if (conversion is not null && AudioConverter.TitleNamesFormat(track.Title))
        {
            // A title naming the old format would be wrong once it's converted.
            newTitle = conversion.Label;
        }

        return new TrackRewrite(track.Index, track.Kind, newTitle, writeLanguage, flags);
    }

    /// <summary>
    /// Gets a track's format as words, such as "Dolby Digital Plus 5.1", "TrueHD Atmos 7.1" or "DTS-HD MA 5.1".
    /// </summary>
    /// <param name="track">The track.</param>
    /// <returns>The format.</returns>
    public static string FormatName(TrackInfo track)
    {
        ArgumentNullException.ThrowIfNull(track);
        var codec = _codecNames.TryGetValue(track.Codec, out var name) ? name : track.Codec.ToUpperInvariant();
        var profile = track.Profile ?? string.Empty;
        if (profile.Contains("atmos", StringComparison.OrdinalIgnoreCase))
        {
            codec += " Atmos";
        }
        else if (profile.Contains("DTS:X", StringComparison.OrdinalIgnoreCase) || profile.Contains("DTS-X", StringComparison.OrdinalIgnoreCase))
        {
            codec = "DTS:X";
        }
        else if (profile.StartsWith("DTS-HD", StringComparison.OrdinalIgnoreCase) || profile.StartsWith("DTS-ES", StringComparison.OrdinalIgnoreCase) || profile.StartsWith("DTS 96", StringComparison.OrdinalIgnoreCase))
        {
            codec = profile;
        }

        if (track.Kind != TrackKind.Audio || track.Channels <= 0)
        {
            return codec;
        }

        var channels = track.Channels switch
        {
            1 => "Mono",
            2 => "Stereo",
            3 => "2.1",
            6 => "5.1",
            7 => "6.1",
            8 => "7.1",
            _ => track.Channels.ToString(CultureInfo.InvariantCulture) + " ch"
        };
        return codec + " " + channels;
    }

    /// <summary>
    /// Guesses a track's language from its title, such as "English" or "Japanese 2.0".
    /// </summary>
    /// <param name="title">The title.</param>
    /// <param name="knownLanguage">Gives the code of a language Jellyfin knows, or null.</param>
    /// <returns>The three-letter code, or null if the title names no language, or more than one.</returns>
    public static string? GuessLanguage(string? title, Func<string, string?> knownLanguage)
    {
        ArgumentNullException.ThrowIfNull(knownLanguage);
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var found = LanguageWords(title, knownLanguage).Select(w => w.Code).Distinct(StringComparer.Ordinal).ToList();
        return found.Count == 1 ? found[0] : null;
    }

    // What a title says beyond its language and format, such as "Director's commentary".
    internal static string Remainder(string title, Func<string, string?> knownLanguage)
    {
        if (title.Length == 0)
        {
            return string.Empty;
        }

        var text = FormatRegex().Replace(title, " ");
        foreach (var (word, _) in LanguageWords(text, knownLanguage).OrderByDescending(w => w.Word.Length))
        {
            text = Regex.Replace(text, "(?<![\\p{L}])" + Regex.Escape(word) + "(?![\\p{L}])", " ", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        }

        return Tidy(text);
    }

    // The part of the remainder the rules keep. Forced and SDH are flags Jellyfin shows on its own, so they're left
    // out of a subtitle's title once the flags are set.
    private static string Extra(string rest, TitleRules rules, bool audio)
    {
        if (rest.Length == 0)
        {
            return string.Empty;
        }

        var text = rest;
        if (!audio && rules.SetLanguageAndFlags)
        {
            text = Tidy(HearingRegex().Replace(ForcedRegex().Replace(text, " "), " "));
        }

        if (!rules.KeepCommentary)
        {
            text = Tidy(CommentaryPhraseRegex().Replace(text, " "));
        }

        if (!rules.KeepAudioDescription)
        {
            text = Tidy(DescriptionRegex().Replace(text, " "));
        }

        if (!rules.KeepOriginal)
        {
            text = Tidy(OriginalRegex().Replace(text, " "));
        }

        if (text.Length == 0)
        {
            return string.Empty;
        }

        var known = CommentaryRegex().IsMatch(text) || DescriptionRegex().IsMatch(text) || OriginalRegex().IsMatch(text);
        if (!known && !rules.KeepOtherText)
        {
            return string.Empty;
        }

        return char.ToUpper(text[0], CultureInfo.InvariantCulture) + text[1..];
    }

    // Separators and brackets left over at the ends or doubled up in the middle.
    private static string Tidy(string text)
    {
        text = EmptyBracketsRegex().Replace(text, " ");
        text = SpacesRegex().Replace(text, " ").Trim();
        text = DoubledSeparatorsRegex().Replace(text, " - ");
        text = SeparatorsRegex().Replace(text, string.Empty);
        text = SpacesRegex().Replace(text, " ").Trim();

        // "(Original)" on its own is just "Original".
        while (text.Length > 2 && ((text[0] == '(' && text[^1] == ')') || (text[0] == '[' && text[^1] == ']')) && text.IndexOfAny([')', ']'], 1) == text.Length - 1)
        {
            text = text[1..^1].Trim();
        }

        return LooseSeparatorRegex().IsMatch(text) ? string.Empty : text;
    }

    // Language names of four letters or more, such as "English"; a bare code such as "eng" only as the whole title or in brackets.
    private static List<(string Word, string Code)> LanguageWords(string text, Func<string, string?> knownLanguage)
    {
        var found = new List<(string, string)>();
        var whole = text.Trim(' ', '[', ']', '(', ')', '-', '.', ',');
        if (whole.Length is >= 2 and <= 3 && knownLanguage(whole) is { } code)
        {
            found.Add((whole, code));
            return found;
        }

        foreach (Match m in BracketedCodeRegex().Matches(text))
        {
            if (knownLanguage(m.Groups[1].Value) is { } bracketed)
            {
                found.Add((m.Value, bracketed));
            }
        }

        foreach (Match m in WordRegex().Matches(text))
        {
            if (m.Value.Length >= 4 && !_notLanguages.Contains(m.Value) && knownLanguage(m.Value) is { } named)
            {
                found.Add((m.Value, named));
            }
        }

        return found;
    }

    [GeneratedRegex("^\\w*handler$", RegexOptions.IgnoreCase)]
    private static partial Regex HandlerRegex();

    // Formats, channels, bitrates and other words about how the track is stored.
    [GeneratedRegex(
        "(?<![\\p{L}\\d])(dolby\\s*digital\\s*plus|dolby\\s*digital\\+?|dolby\\s*true\\s*hd|dolby\\s*atmos|dolby|dd\\+|ddp|dd|e-?ac-?3|ac-?3|true\\s*hd|atmos|dts[\\s-]*hd(\\s*(ma|hra|hr|master\\s*audio|high\\s*resolution))?|dts[\\s:-]*x|dts[\\s-]*es|dts|master\\s*audio|lossless|l?pcm|flac|alac|he-?aac(v2)?|aac(-?lc)?|opus|vorbis|mp3|mp2|mpeg\\s*audio|mono|stereo|surround|\\d\\.\\d(\\s*ch(annels?)?)?|\\d+\\s*ch(annels?)?|\\d+\\s*k(b(ps|its?)?)?(\\/s)?|\\d+\\s*khz|\\d+[\\s-]*bits?|srt|subrip|ass|ssa|pgs|sup|vobsub|webvtt|text|subtitles?|subs|audio(?![\\s-]*description)|track\\s*\\d*|default)(?![\\p{L}\\d])",
        RegexOptions.IgnoreCase)]
    private static partial Regex FormatRegex();

    [GeneratedRegex("commentar|kommentar|commentaire|comentario", RegexOptions.IgnoreCase)]
    private static partial Regex CommentaryRegex();

    // The whole phrase, such as "Director's commentary with the cast", so leaving it out leaves nothing dangling.
    [GeneratedRegex("([\\p{L}']+\\s+)?(commentar\\w*|kommentar\\w*|commentaire\\w*|comentario\\w*)(\\s+(with|by|from)\\s+[^,;()\\[\\]]+)?", RegexOptions.IgnoreCase)]
    private static partial Regex CommentaryPhraseRegex();

    [GeneratedRegex("audio[\\s-]*description|described|descriptive|visually\\s*impaired|(?<![\\p{L}])AD(?![\\p{L}])", RegexOptions.IgnoreCase)]
    private static partial Regex DescriptionRegex();

    [GeneratedRegex("(?<![\\p{L}])(original|OV)(?![\\p{L}])", RegexOptions.IgnoreCase)]
    private static partial Regex OriginalRegex();

    [GeneratedRegex("(?<![\\p{L}])forced(?![\\p{L}])", RegexOptions.IgnoreCase)]
    private static partial Regex ForcedRegex();

    [GeneratedRegex("(?<![\\p{L}])(sdh|cc|hearing[\\s-]*impaired|hoh|closed\\s*captions?|deaf)(?![\\p{L}])", RegexOptions.IgnoreCase)]
    private static partial Regex HearingRegex();

    [GeneratedRegex("[\\[(]\\s*([a-z]{2,3})\\s*[\\])]", RegexOptions.IgnoreCase)]
    private static partial Regex BracketedCodeRegex();

    [GeneratedRegex("\\p{L}+")]
    private static partial Regex WordRegex();

    [GeneratedRegex("[\\[(]\\s*[-|/,;:.+·]*\\s*[\\])]")]
    private static partial Regex EmptyBracketsRegex();

    [GeneratedRegex("\\s+")]
    private static partial Regex SpacesRegex();

    // Leading and trailing dashes, bars, slashes, dots and the like.
    [GeneratedRegex("^[\\s\\-|/,;:.+·_]+|[\\s\\-|/,;:.+·_]+$")]
    private static partial Regex SeparatorsRegex();

    [GeneratedRegex("\\s*[-|/·]\\s*([-|/·]\\s*)+")]
    private static partial Regex DoubledSeparatorsRegex();

    [GeneratedRegex("^[\\W_]*$")]
    private static partial Regex LooseSeparatorRegex();
}
