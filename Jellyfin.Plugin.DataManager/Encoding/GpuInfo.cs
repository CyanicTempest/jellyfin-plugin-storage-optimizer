// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.DataManager.Encoding;

/// <summary>
/// What the server knows about the GPU it encodes on, and how many encodes it suggests running at once.
/// </summary>
/// <param name="State">Found when the card is known, Unreadable when Jellyfin is set up for a GPU that couldn't be read, CpuOnly when no GPU is used.</param>
/// <param name="Vendor">Nvidia, Intel, Amd, Apple, Unknown or None.</param>
/// <param name="Acceleration">How Jellyfin uses it: NVENC, QSV, AMF, VAAPI, VideoToolbox or CPU.</param>
/// <param name="Name">The card's name, or null if it couldn't be read.</param>
/// <param name="Memory">Its memory, such as 16 GB or Shared memory, or null.</param>
/// <param name="Driver">The driver version, or null.</param>
/// <param name="Placement">Integrated or Dedicated, or null when that isn't known.</param>
/// <param name="Engines">How many encode engines it has, or 0 when unknown.</param>
/// <param name="EngineLabel">The engines in words, such as 2 NVENC engines.</param>
/// <param name="Codecs">What this server's ffmpeg can encode with it.</param>
/// <param name="HardwareEncoding">Whether Jellyfin encodes on the GPU.</param>
/// <param name="Experimental">Whether encoding with it is new and untried on many cards.</param>
/// <param name="Recommended">The number of encodes to run at once, before any test.</param>
/// <param name="RecommendedWhy">Why that number.</param>
/// <param name="TestHint">What to say about testing it.</param>
/// <param name="Note">Something to know about this setup, or null.</param>
public sealed record GpuInfo(
    string State,
    string Vendor,
    string Acceleration,
    string? Name,
    string? Memory,
    string? Driver,
    string? Placement,
    int Engines,
    string EngineLabel,
    IReadOnlyList<string> Codecs,
    bool HardwareEncoding,
    bool Experimental,
    int Recommended,
    string RecommendedWhy,
    string TestHint,
    string? Note);

/// <summary>
/// Formats Jellyfin isn't set to decode on the GPU, so re-encodes of them decode on the CPU.
/// </summary>
/// <param name="Codecs">The codecs, as Jellyfin names them, such as h264.</param>
/// <param name="TenBitHevc">Whether 10-bit HEVC is off too, though HEVC is on.</param>
/// <param name="Message">What to tell the user, with where to turn it on.</param>
public sealed record DecodingProblem(IReadOnlyList<string> Codecs, bool TenBitHevc, string Message);

/// <summary>
/// A graphics device the system lists.
/// </summary>
/// <param name="Vendor">Nvidia, Intel, Amd or Unknown.</param>
/// <param name="PciAddress">Where it sits on the PCI bus, such as 0000:00:02.0, or empty.</param>
/// <param name="DeviceId">The PCI device id, such as a780, or empty.</param>
/// <param name="Name">Its name, or null.</param>
/// <param name="VideoMemoryBytes">Its own memory, or null when it has none or it isn't known.</param>
/// <param name="Driver">The driver version, or null.</param>
internal sealed record GpuDevice(string Vendor, string PciAddress, string DeviceId, string? Name, long? VideoMemoryBytes, string? Driver);

/// <summary>
/// Reads what tools say about a GPU, and works out what to suggest. Nothing here touches the system, so it can be tested alone.
/// </summary>
internal static partial class GpuParsing
{
    /// <summary>The most encodes the page offers at once.</summary>
    public const int MostAtOnce = 4;

    /// <summary>
    /// Reads the line nvidia-smi prints for the first card, from its csv output without units.
    /// </summary>
    /// <param name="output">The output of nvidia-smi --query-gpu=name,memory.total,driver_version --format=csv,noheader,nounits.</param>
    /// <returns>The card, or null if there was nothing to read.</returns>
    public static GpuDevice? ParseNvidiaSmi(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (line is null)
        {
            return null;
        }

        var parts = line.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 1 || parts[0].Length == 0)
        {
            return null;
        }

        long? bytes = parts.Length > 1 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var mebibytes) && mebibytes > 0
            ? (long)(mebibytes * 1024 * 1024)
            : null;
        return new GpuDevice("Nvidia", string.Empty, string.Empty, parts[0], bytes, parts.Length > 2 && parts[2].Length > 0 ? parts[2] : null);
    }

    /// <summary>
    /// Gets how many NVENC encode engines a card has, from its name. Cards not listed here have one.
    /// </summary>
    /// <param name="name">The card's name, such as NVIDIA GeForce RTX 4080 SUPER.</param>
    /// <returns>The number of engines.</returns>
    public static int NvencEngines(string? name)
    {
        var n = (name ?? string.Empty).ToUpperInvariant();

        // GeForce RTX 50 series: the 5090 has three, the 5080 and 5070 Ti two.
        if (n.Contains("RTX 5090", StringComparison.Ordinal))
        {
            return 3;
        }

        if (n.Contains("RTX 5080", StringComparison.Ordinal) || n.Contains("RTX 5070 TI", StringComparison.Ordinal))
        {
            return 2;
        }

        // GeForce RTX 40 series: dual encoders from the 4070 Ti up. The workstation Ada cards and the L40 have three.
        if (n.Contains("RTX 6000 ADA", StringComparison.Ordinal) || n.Contains("L40", StringComparison.Ordinal))
        {
            return 3;
        }

        if (n.Contains("RTX 4090", StringComparison.Ordinal) || n.Contains("RTX 4080", StringComparison.Ordinal) || n.Contains("RTX 4070 TI", StringComparison.Ordinal) || n.Contains("RTX 5000 ADA", StringComparison.Ordinal))
        {
            return 2;
        }

        // Pascal's bigger chips: the GTX 1070 and up, and the Titan X.
        if (n.Contains("GTX 1080", StringComparison.Ordinal) || n.Contains("GTX 1070", StringComparison.Ordinal) || n.Contains("TITAN X", StringComparison.Ordinal) || n.Contains("TITAN XP", StringComparison.Ordinal))
        {
            return 2;
        }

        return 1;
    }

    /// <summary>
    /// Reads a PCI device name out of lspci's machine-readable output, such as "Raptor Lake-S GT1 [UHD Graphics 770] [a780]".
    /// </summary>
    /// <param name="output">The output of lspci -mm -nn for one device.</param>
    /// <returns>The marketing name when there is one in brackets, otherwise the chip's name, or null.</returns>
    public static string? ParseLspciName(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var fields = QuotedField().Matches(output).Select(m => m.Groups[1].Value).ToList();

        // Class, vendor, then the device. The device ends with its own id in brackets.
        if (fields.Count < 3)
        {
            return null;
        }

        var device = TrailingId().Replace(fields[2], string.Empty).Trim();
        var marketing = Bracketed().Matches(device).Select(m => m.Groups[1].Value).LastOrDefault();
        var name = string.IsNullOrWhiteSpace(marketing) ? device : marketing;
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>
    /// Reads the video controllers Windows lists.
    /// </summary>
    /// <param name="json">PowerShell's JSON for Win32_VideoController: one object or a list.</param>
    /// <returns>The controllers.</returns>
    public static IReadOnlyList<GpuDevice> ParseWindowsControllers(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        var devices = new List<GpuDevice>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return devices;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var items = document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement.EnumerateArray().ToList() : [document.RootElement];
            foreach (var item in items)
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var name = item.TryGetProperty("Name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var driver = item.TryGetProperty("DriverVersion", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                devices.Add(new GpuDevice(VendorOf(name), string.Empty, string.Empty, name.Trim(), null, driver));
            }
        }
        catch (JsonException)
        {
            // Not JSON: nothing to read.
        }

        return devices;
    }

    /// <summary>
    /// Gets the vendor from a card's name or PCI vendor id.
    /// </summary>
    /// <param name="text">A name such as NVIDIA GeForce RTX 4080, or a vendor id such as 0x8086.</param>
    /// <returns>Nvidia, Intel, Amd or Unknown.</returns>
    public static string VendorOf(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var t = text.Trim().ToUpperInvariant();
        if (t is "0X10DE" or "10DE" || t.Contains("NVIDIA", StringComparison.Ordinal) || t.Contains("GEFORCE", StringComparison.Ordinal))
        {
            return "Nvidia";
        }

        if (t is "0X8086" or "8086" || t.Contains("INTEL", StringComparison.Ordinal))
        {
            return "Intel";
        }

        if (t is "0X1002" or "1002" || t.Contains("AMD", StringComparison.Ordinal) || t.Contains("RADEON", StringComparison.Ordinal) || t.Contains("ADVANCED MICRO DEVICES", StringComparison.Ordinal))
        {
            return "Amd";
        }

        return "Unknown";
    }

    /// <summary>
    /// Says whether a card is built into the processor, when its name tells.
    /// </summary>
    /// <param name="vendor">Nvidia, Intel or Amd.</param>
    /// <param name="name">The card's name, or null.</param>
    /// <param name="videoMemoryBytes">Its own memory, or null when it has none or it isn't known.</param>
    /// <returns>Integrated, Dedicated, or null when it can't be told.</returns>
    public static string? PlacementOf(string vendor, string? name, long? videoMemoryBytes)
    {
        ArgumentNullException.ThrowIfNull(vendor);

        var n = (name ?? string.Empty).ToUpperInvariant();
        if (vendor == "Nvidia" || n.Contains("ARC", StringComparison.Ordinal))
        {
            return "Dedicated";
        }

        if (vendor == "Intel")
        {
            return n.Contains("UHD", StringComparison.Ordinal) || n.Contains("IRIS", StringComparison.Ordinal) || n.Contains("HD GRAPHICS", StringComparison.Ordinal) ? "Integrated" : null;
        }

        if (vendor == "Amd")
        {
            if (n.Contains(" RX ", StringComparison.Ordinal) || n.Contains("RADEON PRO", StringComparison.Ordinal) || n.Contains("RADEON VII", StringComparison.Ordinal))
            {
                return "Dedicated";
            }

            if (n.Contains("RADEON(TM) GRAPHICS", StringComparison.Ordinal) || n.Contains("RADEON GRAPHICS", StringComparison.Ordinal) || n.Contains("VEGA", StringComparison.Ordinal) && !n.Contains("FRONTIER", StringComparison.Ordinal) || videoMemoryBytes is > 0 and <= 2L * 1024 * 1024 * 1024)
            {
                return "Integrated";
            }
        }

        return null;
    }

    /// <summary>
    /// Writes a memory size the way a graphics card is sold: 16 GB.
    /// </summary>
    /// <param name="bytes">The size.</param>
    /// <returns>The size in gigabytes, or in megabytes when under one.</returns>
    public static string MemoryLabel(long bytes)
    {
        var gigabytes = bytes / (1024.0 * 1024 * 1024);
        return gigabytes >= 0.95
            ? Math.Round(gigabytes, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + " GB"
            : Math.Round(bytes / (1024.0 * 1024), MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + " MB";
    }

    /// <summary>
    /// Gets how many encodes to run at once when none was chosen: what the last test of this card found, or else what its engines suggest.
    /// </summary>
    /// <param name="info">The GPU.</param>
    /// <param name="config">The settings, which hold the last test.</param>
    /// <returns>The number at once, from 1 to <see cref="MostAtOnce"/>.</returns>
    public static int Recommended(GpuInfo? info, Configuration.PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var tested = ParseSpeeds(config.GpuTestSpeeds);
        if (info is not null && tested.Count > 0 && string.Equals(config.GpuTestedDevice, DeviceKey(info), StringComparison.Ordinal))
        {
            return Math.Clamp(SweetSpot(tested), 1, MostAtOnce);
        }

        return Math.Clamp(info?.Recommended ?? 1, 1, MostAtOnce);
    }

    /// <summary>
    /// Names a GPU and how it's used, so a test of one card isn't taken for another's.
    /// </summary>
    /// <param name="info">The GPU.</param>
    /// <returns>A key.</returns>
    public static string DeviceKey(GpuInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return info.Acceleration + ":" + info.Name;
    }

    /// <summary>
    /// Finds how many encodes at once is the sweet spot: the fewest that get nine tenths of the best speed there was.
    /// </summary>
    /// <param name="speeds">The total speed with 1, 2, 3 and so on at once, each against one at a time.</param>
    /// <returns>The number at once, at least 1.</returns>
    public static int SweetSpot(IReadOnlyList<double> speeds)
    {
        ArgumentNullException.ThrowIfNull(speeds);

        if (speeds.Count == 0)
        {
            return 1;
        }

        var best = speeds.Max();
        for (var i = 0; i < speeds.Count; i++)
        {
            if (speeds[i] >= best * 0.9)
            {
                return i + 1;
            }
        }

        return speeds.Count;
    }

    /// <summary>
    /// Turns timed runs into speeds against one at a time, taking the middle run of each number at once, so one odd run doesn't count.
    /// </summary>
    /// <param name="totals">The total frames a second of each run: first the runs of one at a time, then of two, and so on.</param>
    /// <returns>The speed with 1, 2, 3 and so on at once, or none if one at a time wasn't timed.</returns>
    public static IReadOnlyList<double> RelativeSpeeds(IReadOnlyList<IReadOnlyList<double>> totals)
    {
        ArgumentNullException.ThrowIfNull(totals);

        if (totals.Count == 0 || totals[0].Count == 0)
        {
            return [];
        }

        var single = Middle(totals[0]);
        return totals.Select(runs => Math.Round(Middle(runs) / single, 2)).ToList();
    }

    /// <summary>
    /// Finds whether runs of the same thing are too far apart to trust, which a quiet GPU doesn't do.
    /// </summary>
    /// <param name="runs">The total frames a second of each run.</param>
    /// <returns>True if the fastest is over a quarter faster than the slowest.</returns>
    public static bool Disagree(IReadOnlyList<double> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        return runs.Count > 1 && runs.Max() > runs.Min() * 1.25;
    }

    /// <summary>
    /// Finds the formats in the library that Jellyfin isn't set to decode on the GPU, which is what the plugin follows, so
    /// re-encoding them decodes on the CPU, several times slower than the GPU encodes.
    /// </summary>
    /// <param name="hardware">Jellyfin's hardware acceleration setting, with none when hardware encoding is off.</param>
    /// <param name="decodingCodecs">The codecs Jellyfin is set to decode on the GPU.</param>
    /// <param name="tenBitHevc">Jellyfin's setting for decoding 10-bit HEVC on the GPU.</param>
    /// <param name="libraryCodecs">How many files use each video codec, from the last scan, or null if there hasn't been one.</param>
    /// <param name="likelyTenBitHevc">How many HEVC files are HDR or 4K, which are nearly always 10-bit, or 0.</param>
    /// <returns>What's off, or null when nothing is, or when the GPU doesn't decode here at all.</returns>
    public static DecodingProblem? DecodeProblem(MediaBrowser.Model.Entities.HardwareAccelerationType hardware, IReadOnlyCollection<string> decodingCodecs, bool tenBitHevc, IReadOnlyDictionary<string, int>? libraryCodecs, int likelyTenBitHevc)
    {
        ArgumentNullException.ThrowIfNull(decodingCodecs);

        // NVIDIA, Intel and VA-API (AMD on Linux) decode on the GPU in this plugin; the others decode on the CPU whatever is set.
        if (hardware is not (MediaBrowser.Model.Entities.HardwareAccelerationType.nvenc or MediaBrowser.Model.Entities.HardwareAccelerationType.qsv or MediaBrowser.Model.Entities.HardwareAccelerationType.vaapi))
        {
            return null;
        }

        string[] decodable = ["h264", "hevc", "mpeg2video", "vc1", "vp8", "vp9", "av1"];
        var counts = (libraryCodecs ?? new Dictionary<string, int>())
            .GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(p => p.Value), StringComparer.OrdinalIgnoreCase);
        var candidates = counts.Count > 0 ? decodable.Where(counts.ContainsKey).ToList() : ["h264", "hevc"];
        var off = candidates.Where(c => !decodingCodecs.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
        var hevcOn = decodingCodecs.Contains("hevc", StringComparer.OrdinalIgnoreCase);
        var tenBitOff = hevcOn && !tenBitHevc && (counts.Count == 0 || likelyTenBitHevc > 0);
        if (off.Count == 0 && !tenBitOff)
        {
            return null;
        }

        string Name(string codec) => codec switch
        {
            "h264" => "H.264",
            "hevc" => "HEVC",
            "mpeg2video" => "MPEG-2",
            "vc1" => "VC-1",
            "vp8" => "VP8",
            "vp9" => "VP9",
            "av1" => "AV1",
            _ => codec
        };
        var names = off.Select(Name).ToList();
        if (tenBitOff)
        {
            names.Add("10-bit HEVC");
        }

        var list = names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
        var files = off.Sum(c => counts.GetValueOrDefault(c)) + (tenBitOff ? likelyTenBitHevc : 0);
        var message = "Jellyfin isn't set to decode " + list + " on the GPU"
            + (counts.Count > 0 && files > 0 ? string.Create(CultureInfo.InvariantCulture, $", which {files} of your files use") : string.Empty)
            + ". Re-encoding them decodes on the CPU, which can be several times slower than the GPU encodes. Turn hardware decoding on in Jellyfin, under Dashboard, Playback, Transcoding.";
        return new DecodingProblem(off, tenBitOff, message);
    }

    /// <summary>
    /// Takes the middle run of each number at once, as frames a second in total.
    /// </summary>
    /// <param name="totals">The total frames a second of each run: first the runs of one at a time, then of two, and so on.</param>
    /// <returns>The middle run of each.</returns>
    public static IReadOnlyList<double> Medians(IReadOnlyList<IReadOnlyList<double>> totals)
    {
        ArgumentNullException.ThrowIfNull(totals);
        return totals.Where(runs => runs.Count > 0).Select(Middle).ToList();
    }

    /// <summary>
    /// Says what holds the speed back, by comparing the encoder on its own with the encoder fed from the film.
    /// </summary>
    /// <param name="encoderOnly">Total frames a second with 1, 2, 3 and so on at once, encoding a generated picture.</param>
    /// <param name="film">The same, encoding a scene of the film.</param>
    /// <returns>A sentence, or null when either wasn't measured.</returns>
    public static string? Diagnose(IReadOnlyList<double> encoderOnly, IReadOnlyList<double> film)
    {
        ArgumentNullException.ThrowIfNull(encoderOnly);
        ArgumentNullException.ThrowIfNull(film);

        if (encoderOnly.Count == 0 || film.Count == 0 || encoderOnly.Max() <= 0)
        {
            return null;
        }

        var encoderBest = encoderOnly.Max();
        var filmBest = film.Max();
        var share = filmBest / encoderBest;
        var numbers = string.Create(CultureInfo.InvariantCulture, $"{Math.Round(filmBest):0} against {Math.Round(encoderBest):0} frames a second at best");

        // Decoding the film always costs something, so a film that gets most of the way to the encoder's speed is the encoder's limit.
        return share >= 0.75
            ? "Encoding your film keeps up with the encoder alone (" + numbers + "), so the GPU's encoders are the limit, not reading or decoding the film."
            : string.Create(CultureInfo.InvariantCulture, $"Encoding your film reaches only {Math.Round(share * 100):0}% of what the encoder alone does ({numbers}), so reading or decoding the film is holding it back. That points to a slow drive or network share, or decoding on the CPU.");
    }

    /// <summary>
    /// Says whether keeping decoded frames on the GPU was worth it, from the same scene run both ways.
    /// </summary>
    /// <param name="film">Total frames a second with 1, 2, 3 and so on at once, with frames copied to system memory.</param>
    /// <param name="onGpu">The same with frames kept on the GPU, or none when it failed.</param>
    /// <returns>A sentence.</returns>
    public static string CompareFramesOnGpu(IReadOnlyList<double> film, IReadOnlyList<double> onGpu)
    {
        ArgumentNullException.ThrowIfNull(film);
        ArgumentNullException.ThrowIfNull(onGpu);

        if (onGpu.Count == 0 || film.Count == 0)
        {
            return "Keeping frames on the GPU didn't work with your card and this film, so turning it on would only make encodes fall back to the usual way.";
        }

        var change = (onGpu.Max() / film.Max()) - 1;
        var numbers = string.Create(CultureInfo.InvariantCulture, $"{Math.Round(onGpu.Max()):0} against {Math.Round(film.Max()):0} frames a second at best");
        return change >= 0.10
            ? string.Create(CultureInfo.InvariantCulture, $"Keeping frames on the GPU was about {Math.Round(change * 100):0}% faster ({numbers}). It's worth turning on below.")
            : change <= -0.05
                ? string.Create(CultureInfo.InvariantCulture, $"Keeping frames on the GPU was about {Math.Round(-change * 100):0}% slower ({numbers}), so leave it off.")
                : "Keeping frames on the GPU made little difference (" + numbers + "), so there's no need to turn it on.";
    }

    private static double Middle(IReadOnlyList<double> runs)
    {
        var sorted = runs.Order().ToList();
        return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2;
    }

    /// <summary>
    /// Writes test speeds the way the settings keep them.
    /// </summary>
    /// <param name="speeds">The speeds.</param>
    /// <returns>Comma separated, such as 1.0,1.9,2.0.</returns>
    public static string FormatSpeeds(IEnumerable<double> speeds)
    {
        ArgumentNullException.ThrowIfNull(speeds);
        return string.Join(',', speeds.Select(s => s.ToString("0.0#", CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// Reads test speeds from the settings.
    /// </summary>
    /// <param name="text">Comma separated speeds.</param>
    /// <returns>The speeds, or none if the text isn't speeds.</returns>
    public static IReadOnlyList<double> ParseSpeeds(string? text)
    {
        var speeds = new List<double>();
        foreach (var part in (text ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed) || speed <= 0 || speeds.Count >= MostAtOnce)
            {
                return [];
            }

            speeds.Add(speed);
        }

        return speeds;
    }

    [GeneratedRegex("\"([^\"]*)\"")]
    private static partial Regex QuotedField();

    [GeneratedRegex(@"\s*\[[0-9a-fA-F]{4}\]\s*$")]
    private static partial Regex TrailingId();

    [GeneratedRegex(@"\[([^\]]+)\]")]
    private static partial Regex Bracketed();
}
