// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DataManager.Encoding;

/// <summary>
/// Uses the GPU Jellyfin is set up for: picks its encoders, decodes the original on it when Jellyfin's
/// hardware decoding settings cover the file's codec, and decodes on the CPU instead if that fails.
/// </summary>
public sealed class HardwareAcceleration
{
    // Decodes that failed on the GPU but worked on the CPU, as "hwaccel|codec|bits". They stay on the CPU until
    // Jellyfin restarts, so each encode doesn't fail once first.
    private readonly ConcurrentDictionary<string, bool> _failedDecodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly IConfigurationManager _configurationManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<HardwareAcceleration> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="HardwareAcceleration"/> class.
    /// </summary>
    /// <param name="configurationManager">The configuration manager, for Jellyfin's transcoding settings.</param>
    /// <param name="mediaEncoder">Jellyfin's media encoder, which knows what its ffmpeg supports.</param>
    /// <param name="logger">The logger.</param>
    public HardwareAcceleration(IConfigurationManager configurationManager, IMediaEncoder mediaEncoder, ILogger<HardwareAcceleration> logger)
    {
        _configurationManager = configurationManager;
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    /// <summary>
    /// Picks the encoder for a codec: the GPU's when Jellyfin has hardware encoding on and its ffmpeg has it,
    /// otherwise the CPU's.
    /// </summary>
    /// <param name="codec">The codec.</param>
    /// <param name="isHardware">Whether the encoder runs on the GPU.</param>
    /// <returns>The encoder name, or null if this ffmpeg can't encode the codec at all.</returns>
    public string? PickEncoder(TargetCodec codec, out bool isHardware)
    {
        var options = _configurationManager.GetEncodingOptions();
        if (options.EnableHardwareEncoding)
        {
            var hardware = EncoderSelector.HardwareEncoder(codec, options.HardwareAccelerationType);
            if (hardware is not null && _mediaEncoder.SupportsEncoder(hardware))
            {
                isHardware = true;
                return hardware;
            }
        }

        isHardware = false;
        var software = EncoderSelector.SoftwareEncoder(codec);
        return _mediaEncoder.SupportsEncoder(software) ? software : null;
    }

    /// <summary>
    /// Works out the ffmpeg arguments that set up the GPU for one encode, which go before the input.
    /// </summary>
    /// <param name="encoder">The encoder the encode uses.</param>
    /// <param name="source">What is known about the source video.</param>
    /// <param name="allowDecode">Whether the GPU may decode the original.</param>
    /// <returns>The setup.</returns>
    public HardwareSetup Plan(string encoder, SourceVideo source, bool allowDecode = true)
    {
        ArgumentNullException.ThrowIfNull(source);

        var options = _configurationManager.GetEncodingOptions();
        var hwaccel = allowDecode ? DecodeWith(options, source) : null;
        var args = EncoderSelector.HardwareArguments(options.HardwareAccelerationType, encoder, hwaccel, DevicePath(options), OperatingSystem.IsWindows());
        return new HardwareSetup(args, hwaccel);
    }

    /// <summary>
    /// Runs an ffmpeg job with the GPU decoding when it can, and runs it again decoding on the CPU if it fails.
    /// </summary>
    /// <typeparam name="T">What the job returns.</typeparam>
    /// <param name="encoder">The encoder the job uses.</param>
    /// <param name="source">What is known about the source video.</param>
    /// <param name="run">Runs ffmpeg with the setup's arguments before the input. Throws <see cref="InvalidOperationException"/> when ffmpeg fails.</param>
    /// <param name="onFallback">Called before the run on the CPU, if there is one.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What the job returned.</returns>
    public async Task<T> RunAsync<T>(string encoder, SourceVideo source, Func<HardwareSetup, Task<T>> run, Action? onFallback, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        var setup = Plan(encoder, source);
        if (setup.Decoder is null)
        {
            return await run(setup).ConfigureAwait(false);
        }

        try
        {
            return await run(setup).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Storage Optimizer could not decode {Codec} with {Hwaccel}, so it is trying again decoding on the CPU: {Error}", source.Codec, setup.Decoder, ex.Message);
            onFallback?.Invoke();
            var result = await run(Plan(encoder, source, false)).ConfigureAwait(false);
            DecodeFailed(setup, source);
            return result;
        }
    }

    /// <summary>
    /// Notes that the GPU couldn't decode a file the CPU could, so files like it decode on the CPU from now on.
    /// </summary>
    /// <param name="setup">The setup that failed.</param>
    /// <param name="source">The source video.</param>
    public void DecodeFailed(HardwareSetup setup, SourceVideo source)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(source);

        if (setup.Decoder is not null && _failedDecodes.TryAdd(FailureKey(setup.Decoder, source), true))
        {
            _logger.LogWarning("Storage Optimizer will decode {Bits}-bit {Codec} on the CPU until Jellyfin restarts, because {Hwaccel} failed on it", source.IsTenBit ? 10 : 8, source.Codec, setup.Decoder);
        }
    }

    private static string FailureKey(string hwaccel, SourceVideo source) => hwaccel + "|" + source.Codec + "|" + (source.IsTenBit ? "10" : "8");

    // Jellyfin's QSV device is the render node on Linux; without one, its VA-API device; without that, the first GPU.
    private static string DevicePath(EncodingOptions options)
        => options.HardwareAccelerationType == HardwareAccelerationType.vaapi
            ? (!string.IsNullOrWhiteSpace(options.VaapiDevice) ? options.VaapiDevice
                : !string.IsNullOrWhiteSpace(options.QsvDevice) ? options.QsvDevice
                : "/dev/dri/renderD128")
            : !string.IsNullOrWhiteSpace(options.QsvDevice) ? options.QsvDevice
            : !string.IsNullOrWhiteSpace(options.VaapiDevice) ? options.VaapiDevice
            : "/dev/dri/renderD128";

    private string? DecodeWith(EncodingOptions options, SourceVideo source)
    {
        var hwaccel = EncoderSelector.DecodeHwaccel(
            options.HardwareAccelerationType,
            options.HardwareDecodingCodecs ?? [],
            source.Codec,
            source.IsTenBit,
            options.EnableDecodingColorDepth10Hevc,
            options.EnableDecodingColorDepth10Vp9,
            OperatingSystem.IsWindows());
        if (hwaccel is null || _failedDecodes.ContainsKey(FailureKey(hwaccel, source)))
        {
            return null;
        }

        return _mediaEncoder.SupportsHwaccel(hwaccel) ? hwaccel : null;
    }
}

/// <summary>
/// How one encode uses the GPU.
/// </summary>
/// <param name="Arguments">The ffmpeg arguments that set up the GPU, which go before the input.</param>
/// <param name="Decoder">The ffmpeg hwaccel that decodes the original, such as cuda, or null when the CPU decodes it.</param>
public sealed record HardwareSetup(IReadOnlyList<string> Arguments, string? Decoder)
{
    /// <summary>Gets a setup that leaves everything to the CPU.</summary>
    public static HardwareSetup None { get; } = new([], null);
}
