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
