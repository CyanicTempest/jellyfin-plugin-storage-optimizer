// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DataManager.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DataManager.Encoding;

/// <summary>
/// Finds out which GPU Jellyfin encodes on and how many encodes it can run at once.
/// </summary>
public sealed class GpuDetector : IDisposable
{
    private static readonly TimeSpan _cacheFor = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan _toolTimeout = TimeSpan.FromSeconds(8);

    private readonly IConfigurationManager _configurationManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<GpuDetector> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private GpuInfo? _cached;
    private string _cachedFor = string.Empty;
    private DateTime _cachedAt;

    /// <summary>
    /// Initializes a new instance of the <see cref="GpuDetector"/> class.
    /// </summary>
    /// <param name="configurationManager">The configuration manager, for Jellyfin's transcoding settings.</param>
    /// <param name="mediaEncoder">Jellyfin's media encoder, which knows what its ffmpeg supports.</param>
    /// <param name="logger">The logger.</param>
    public GpuDetector(IConfigurationManager configurationManager, IMediaEncoder mediaEncoder, ILogger<GpuDetector> logger)
    {
        _configurationManager = configurationManager;
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    /// <summary>
    /// Finds the GPU Jellyfin is set up to encode on. The answer is kept for a few minutes.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What is known about the GPU.</returns>
    public async Task<GpuInfo> DetectAsync(CancellationToken cancellationToken)
    {
        var options = _configurationManager.GetEncodingOptions();
        var setting = options.EnableHardwareEncoding + "|" + options.HardwareAccelerationType + "|" + options.VaapiDevice + "|" + options.QsvDevice;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cached is not null && _cachedFor == setting && DateTime.UtcNow - _cachedAt < _cacheFor)
            {
                return _cached;
            }

            var info = await ReadAsync(options.EnableHardwareEncoding ? options.HardwareAccelerationType : HardwareAccelerationType.none, options.VaapiDevice, options.QsvDevice, cancellationToken).ConfigureAwait(false);
            _cached = info;
            _cachedFor = setting;
            _cachedAt = DateTime.UtcNow;
            return info;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<GpuInfo> ReadAsync(HardwareAccelerationType accel, string? vaapiDevice, string? qsvDevice, CancellationToken cancellationToken)
    {
        if (accel == HardwareAccelerationType.none)
        {
            return CpuOnly("Hardware encoding is off in Jellyfin.");
        }

        if (EncoderSelector.HardwareEncoder(TargetCodec.Hevc, accel) is null)
        {
            return CpuOnly("Jellyfin is set to " + accel + ", which this plugin can't encode with yet, so it uses the CPU.");
        }

        var codecs = Codecs(accel);
        try
        {
            return accel switch
            {
                HardwareAccelerationType.nvenc => await NvidiaAsync(codecs, cancellationToken).ConfigureAwait(false),
                HardwareAccelerationType.videotoolbox => Apple(codecs),
                _ => await OtherAsync(accel, vaapiDevice, qsvDevice, codecs, cancellationToken).ConfigureAwait(false)
            };
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or TimeoutException or IOException or UnauthorizedAccessException)
        {
            _logger.LogInformation("Storage Optimizer could not read the GPU: {Error}", ex.Message);
            return Unreadable(accel, codecs, ex is Win32Exception ? NoTool(accel) : "Jellyfin couldn't read it.");
        }
    }

    private static string NoTool(HardwareAccelerationType accel) => accel == HardwareAccelerationType.nvenc ? "nvidia-smi is not available to Jellyfin" : "The system's graphics list isn't available to Jellyfin";

    // The codecs this server's ffmpeg can encode on the GPU, as a person writes them.
    private List<string> Codecs(HardwareAccelerationType accel)
    {
        var codecs = new List<string>();
        foreach (var (codec, label) in new[] { (TargetCodec.H264, "H.264"), (TargetCodec.Hevc, "HEVC"), (TargetCodec.Av1, "AV1") })
        {
            var encoder = EncoderSelector.HardwareEncoder(codec, accel);
            if (encoder is not null && _mediaEncoder.SupportsEncoder(encoder))
            {
                codecs.Add(label);
            }
        }

        return codecs;
    }

    private static GpuInfo CpuOnly(string why) => new(
        "CpuOnly",
        "None",
        "CPU",
        "CPU encoding",
        null,
        null,
        null,
        0,
        why,
        ["libx265", "libsvtav1", "libx264"],
        false,
        false,
        1,
        "One CPU encode already uses every core.",
        "Turn on hardware encoding in Jellyfin's Playback settings to use a GPU here.",
        null);

    private static GpuInfo Unreadable(HardwareAccelerationType accel, List<string> codecs, string reason)
    {
        var label = AccelerationLabel(accel);
        return new GpuInfo(
            "Unreadable",
            accel == HardwareAccelerationType.nvenc ? "Nvidia" : "Unknown",
            label,
            "Couldn't read the GPU",
            null,
            null,
            null,
            0,
            reason,
            codecs,
            true,
            accel == HardwareAccelerationType.vaapi,
            1,
            "Run the test to find out how many your GPU handles.",
            "The test still works, because it only needs ffmpeg.",
            accel == HardwareAccelerationType.nvenc ? "In Docker, the container needs the NVIDIA runtime for Jellyfin to see the card. The test above does not depend on it." : null);
    }

    private static string AccelerationLabel(HardwareAccelerationType accel) => accel switch
    {
        HardwareAccelerationType.nvenc => "NVENC",
        HardwareAccelerationType.qsv => "QSV",
        HardwareAccelerationType.amf => "AMF",
        HardwareAccelerationType.vaapi => "VAAPI",
        HardwareAccelerationType.videotoolbox => "VideoToolbox",
        _ => accel.ToString()
    };

    private static async Task<GpuInfo> NvidiaAsync(List<string> codecs, CancellationToken cancellationToken)
    {
        var (output, _) = await FfmpegRunner.RunAsync("nvidia-smi", ["--query-gpu=name,memory.total,driver_version", "--format=csv,noheader,nounits"], _toolTimeout, "read the GPU", cancellationToken).ConfigureAwait(false);
        var card = GpuParsing.ParseNvidiaSmi(output);
        if (card?.Name is null)
        {
            return Unreadable(HardwareAccelerationType.nvenc, codecs, "nvidia-smi didn't list a card");
        }

        var engines = GpuParsing.NvencEngines(card.Name);
        return new GpuInfo(
            "Found",
            "Nvidia",
            "NVENC",
            card.Name,
            card.VideoMemoryBytes is { } bytes ? GpuParsing.MemoryLabel(bytes) : null,
            card.Driver,
            "Dedicated",
            engines,
            engines + (engines == 1 ? " NVENC engine" : " NVENC engines"),
            codecs,
            true,
            false,
            Math.Clamp(engines, 1, GpuParsing.MostAtOnce),
            engines == 1 ? "It has 1 encode engine. A second encode would only share it." : "It has " + engines + " encode engines, so " + engines + " at once keeps each busy.",
            "Not tested yet. The test checks the recommendation on your actual GPU.",
            null);
    }

    private static GpuInfo Apple(List<string> codecs) => new(
        "Found",
        "Apple",
        "VideoToolbox",
        "Apple VideoToolbox",
        "Shared memory",
        null,
        "Integrated",
        1,
        "VideoToolbox",
        codecs,
        true,
        false,
        1,
        "Apple's media engine handles one encode well. The test checks yours.",
        "Not tested yet. The test checks the recommendation on your actual GPU.",
        null);

    // Intel with QSV or VAAPI, and AMD with AMF or VAAPI: the card comes from the system's own list of graphics devices.
    private static async Task<GpuInfo> OtherAsync(HardwareAccelerationType accel, string? vaapiDevice, string? qsvDevice, List<string> codecs, CancellationToken cancellationToken)
    {
        IReadOnlyList<GpuDevice> devices;
        if (OperatingSystem.IsWindows())
        {
            var (output, _) = await FfmpegRunner.RunAsync(
                "powershell",
                ["-NoProfile", "-NonInteractive", "-Command", "Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion | ConvertTo-Json -Compress"],
                _toolTimeout,
                "list the graphics cards",
                cancellationToken).ConfigureAwait(false);
            devices = GpuParsing.ParseWindowsControllers(output);
        }
        else
        {
            devices = await LinuxDevicesAsync(cancellationToken).ConfigureAwait(false);
        }

        var wanted = accel == HardwareAccelerationType.qsv ? "Intel" : accel == HardwareAccelerationType.amf ? "Amd" : null;
        var device = PickDevice(devices, wanted, accel == HardwareAccelerationType.qsv ? qsvDevice : vaapiDevice);
        if (device is null)
        {
            return Unreadable(accel, codecs, "No graphics card is listed for " + AccelerationLabel(accel));
        }

        return Describe(accel, device, codecs);
    }

    private static GpuDevice? PickDevice(IReadOnlyList<GpuDevice> devices, string? vendor, string? renderNode)
    {
        if (devices.Count == 0)
        {
            return null;
        }

        // A render node, such as /dev/dri/renderD128, sits on one card.
        if (!string.IsNullOrWhiteSpace(renderNode))
        {
            var address = PciAddressOfRenderNode(renderNode);
            var onNode = address is null ? null : devices.FirstOrDefault(d => string.Equals(d.PciAddress, address, StringComparison.OrdinalIgnoreCase));
            if (onNode is not null)
            {
                return onNode;
            }
        }

        return (vendor is null ? null : devices.FirstOrDefault(d => d.Vendor == vendor))
            ?? devices.FirstOrDefault(d => d.Vendor is "Amd" or "Intel")
            ?? devices[0];
    }

    private static string? PciAddressOfRenderNode(string renderNode)
    {
        try
        {
            var link = new DirectoryInfo(Path.Combine("/sys/class/drm", Path.GetFileName(renderNode), "device")).ResolveLinkTarget(true);
            return link?.Name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static async Task<IReadOnlyList<GpuDevice>> LinuxDevicesAsync(CancellationToken cancellationToken)
    {
        var devices = new List<GpuDevice>();
        const string Root = "/sys/class/drm";
        if (!Directory.Exists(Root))
        {
            return devices;
        }

        foreach (var directory in Directory.EnumerateDirectories(Root, "card*").OrderBy(d => d, StringComparer.Ordinal))
        {
            // card0-HDMI-A-1 and the like are connectors, not cards.
            if (Path.GetFileName(directory).Contains('-', StringComparison.Ordinal))
            {
                continue;
            }

            var device = Path.Combine(directory, "device");
            var vendorId = ReadText(Path.Combine(device, "vendor"));
            if (vendorId is null)
            {
                continue;
            }

            var address = new DirectoryInfo(device).ResolveLinkTarget(true)?.Name ?? string.Empty;
            var deviceId = (ReadText(Path.Combine(device, "device")) ?? string.Empty).Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase);
            long? memory = long.TryParse(ReadText(Path.Combine(device, "mem_info_vram_total")), NumberStyles.Integer, CultureInfo.InvariantCulture, out var vram) && vram > 0 ? vram : null;
            var vendor = GpuParsing.VendorOf(vendorId);
            var name = address.Length > 0 ? await LspciNameAsync(address, cancellationToken).ConfigureAwait(false) : null;
            devices.Add(new GpuDevice(vendor, address, deviceId, name, memory, null));
        }

        return devices;
    }

    private static string? ReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // lspci is often missing in a container, and then the card is only known by its vendor.
    private static async Task<string?> LspciNameAsync(string address, CancellationToken cancellationToken)
    {
        try
        {
            var (output, _) = await FfmpegRunner.RunAsync("lspci", ["-mm", "-nn", "-s", address], _toolTimeout, "name the graphics card", cancellationToken).ConfigureAwait(false);
            return GpuParsing.ParseLspciName(output);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or TimeoutException)
        {
            return null;
        }
    }

    private static GpuInfo Describe(HardwareAccelerationType accel, GpuDevice device, List<string> codecs)
    {
        var vendor = device.Vendor == "Unknown" ? (accel == HardwareAccelerationType.qsv ? "Intel" : "Amd") : device.Vendor;
        var maker = vendor == "Intel" ? "Intel" : "AMD";
        var raw = device.Name;
        var name = string.IsNullOrWhiteSpace(raw)
            ? maker + " graphics" + (device.DeviceId.Length > 0 ? " (device " + device.DeviceId + ")" : string.Empty)
            : raw.Contains(maker, StringComparison.OrdinalIgnoreCase) || (vendor == "Amd" && raw.Contains("Radeon", StringComparison.OrdinalIgnoreCase)) ? raw : maker + " " + raw;
        var placement = GpuParsing.PlacementOf(vendor, device.Name, device.VideoMemoryBytes);
        var label = AccelerationLabel(accel);
        var experimental = accel == HardwareAccelerationType.vaapi;

        int engines;
        string engineLabel;
        string why;
        string hint = "Not tested yet. The test checks the recommendation on your actual GPU.";
        if (vendor == "Intel")
        {
            var arc = name.Contains("Arc", StringComparison.OrdinalIgnoreCase);
            engines = arc ? 2 : 1;
            engineLabel = engines + (engines == 1 ? " media engine" : " media engines");
            why = engines == 1 ? "It has 1 encode engine. A second encode would only share it." : "It has 2 encode engines, so 2 at once keeps each busy.";
        }
        else
        {
            engines = 1;
            engineLabel = label;
            why = placement == "Integrated"
                ? "Integrated graphics share memory with the CPU, so one at a time is safest."
                : "AMD cards usually have 1 encode engine. The test checks yours.";
            if (accel == HardwareAccelerationType.amf)
            {
                hint = "Not tested yet. Decoding the original stays on the CPU with AMF.";
            }
            else if (accel == HardwareAccelerationType.vaapi)
            {
                hint = "Not tested yet. The test shows straight away whether this card can encode.";
            }
        }

        string? memory = placement == "Integrated" ? "Shares system memory" : device.VideoMemoryBytes is { } bytes ? GpuParsing.MemoryLabel(bytes) : null;
        return new GpuInfo(
            "Found",
            vendor,
            label,
            name,
            memory,
            device.Driver,
            placement,
            engines,
            engineLabel,
            codecs,
            true,
            experimental,
            engines,
            why,
            hint,
            experimental ? "Encoding with VAAPI is new in this version and has not been tried on every card, so run the test before you queue a long batch." : null);
    }
}
