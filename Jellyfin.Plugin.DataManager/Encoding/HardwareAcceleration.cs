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
    private readonly ConcurrentDictionary<string, bool> _failedKeepFrames = new(StringComparer.OrdinalIgnoreCase);
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
    /// Finds the formats in a library that Jellyfin isn't set to decode on the GPU.
    /// </summary>
    /// <param name="libraryCodecs">How many files use each video codec, or null if there hasn't been a scan.</param>
    /// <param name="likelyTenBitHevc">How many HEVC files are HDR or 4K.</param>
    /// <returns>What's off, or null.</returns>
    public DecodingProblem? DecodingProblem(IReadOnlyDictionary<string, int>? libraryCodecs, int likelyTenBitHevc)
    {
        var options = _configurationManager.GetEncodingOptions();
        return options.EnableHardwareEncoding
            ? GpuParsing.DecodeProblem(options.HardwareAccelerationType, options.HardwareDecodingCodecs ?? [], options.EnableDecodingColorDepth10Hevc, libraryCodecs, likelyTenBitHevc)
            : null;
    }

    /// <summary>
    /// Works out the ffmpeg arguments that set up the GPU for one encode, which go before the input.
    /// </summary>
    /// <param name="encoder">The encoder the encode uses.</param>
    /// <param name="source">What is known about the source video.</param>
    /// <param name="allowDecode">Whether the GPU may decode the original.</param>
    /// <param name="keepFrames">Whether decoded frames should stay on the GPU for the encoder. This only happens with NVIDIA's decoder and encoder, and not for a file that failed that way before.</param>
    /// <returns>The setup.</returns>
    public HardwareSetup Plan(string encoder, SourceVideo source, bool allowDecode = true, bool keepFrames = false)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        ArgumentNullException.ThrowIfNull(source);

        var options = _configurationManager.GetEncodingOptions();
        var hwaccel = allowDecode ? DecodeWith(options, source) : null;
        var keep = keepFrames
            && hwaccel is not null
            && options.HardwareAccelerationType == HardwareAccelerationType.nvenc
            && encoder.EndsWith("_nvenc", StringComparison.Ordinal)
            && !_failedKeepFrames.ContainsKey(FailureKey("frames-on-gpu", source));
        var args = EncoderSelector.HardwareArguments(options.HardwareAccelerationType, encoder, hwaccel, DevicePath(options), OperatingSystem.IsWindows(), keep);
        return new HardwareSetup(args, hwaccel, keep);
    }

    /// <summary>
    /// Runs an ffmpeg job the best way the GPU allows, and the next way down if that fails: frames kept on the GPU (when
    /// asked for), then decoding on the GPU with frames copied to system memory, then decoding on the CPU.
    /// </summary>
    /// <typeparam name="T">What the job returns.</typeparam>
    /// <param name="encoder">The encoder the job uses.</param>
    /// <param name="source">What is known about the source video.</param>
    /// <param name="run">Runs ffmpeg with the setup's arguments before the input. Throws <see cref="InvalidOperationException"/> when ffmpeg fails.</param>
    /// <param name="onFallback">Called with the setup about to be tried, before each run after the first.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="keepFrames">Whether to try keeping decoded frames on the GPU first. Only for NVIDIA, and only for a job that doesn't scale or tone map the picture.</param>
    /// <returns>What the job returned.</returns>
    public async Task<T> RunAsync<T>(string encoder, SourceVideo source, Func<HardwareSetup, Task<T>> run, Action<HardwareSetup>? onFallback, CancellationToken cancellationToken, bool keepFrames = false)
    {
        ArgumentNullException.ThrowIfNull(run);

        var plans = new List<HardwareSetup>();
        if (keepFrames && Plan(encoder, source, true, true) is { FramesOnGpu: true } kept)
        {
            plans.Add(kept);
        }

        var gpu = Plan(encoder, source);
        plans.Add(gpu);
        if (gpu.Decoder is not null)
        {
            plans.Add(Plan(encoder, source, false));
        }

        var (result, failed) = await RunAsync(
            plans,
            run,
            (plan, next, ex) =>
            {
                _logger.LogWarning(
                    "Storage Optimizer could not use {Way} for {Codec}, so it is trying again {Next}: {Error}",
                    plan.FramesOnGpu ? "frames kept on the GPU" : "decoding on the " + plan.Decoder,
                    source.Codec,
                    next.Decoder is null ? "decoding on the CPU" : "copying frames to system memory",
                    ex.Message);
                onFallback?.Invoke(next);
            },
            cancellationToken).ConfigureAwait(false);

        // Only now is it clear the way that failed was the problem, and not the file or the encode itself.
        foreach (var plan in failed)
        {
            if (plan.FramesOnGpu)
            {
                KeepFramesFailed(source);
            }
            else
            {
                DecodeFailed(plan, source);
            }
        }

        return result;
    }

    /// <summary>
    /// Tries each setup in turn, moving on to the next when ffmpeg fails with one.
    /// </summary>
    /// <typeparam name="T">What the job returns.</typeparam>
    /// <param name="plans">The setups, best first. There is at least one.</param>
    /// <param name="run">Runs the job with a setup. Throws <see cref="InvalidOperationException"/> when ffmpeg fails.</param>
    /// <param name="onFallback">Called with the setup that failed, the one about to be tried and what went wrong.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What the job returned, and the setups that failed before the one that worked.</returns>
    internal static async Task<(T Result, IReadOnlyList<HardwareSetup> Failed)> RunAsync<T>(IReadOnlyList<HardwareSetup> plans, Func<HardwareSetup, Task<T>> run, Action<HardwareSetup, HardwareSetup, InvalidOperationException>? onFallback, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(run);

        var failed = new List<HardwareSetup>();
        for (var i = 0; ; i++)
        {
            try
            {
                return (await run(plans[i]).ConfigureAwait(false), failed);
            }
            catch (InvalidOperationException ex) when (!cancellationToken.IsCancellationRequested && i < plans.Count - 1)
            {
                failed.Add(plans[i]);
                onFallback?.Invoke(plans[i], plans[i + 1], ex);
            }
        }
    }

    /// <summary>
    /// Notes that keeping frames on the GPU failed for a file the usual way could do, so files like it use the usual way from now on.
    /// </summary>
    /// <param name="source">The source video.</param>
    public void KeepFramesFailed(SourceVideo source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (_failedKeepFrames.TryAdd(FailureKey("frames-on-gpu", source), true))
        {
            _logger.LogWarning("Storage Optimizer will copy frames to system memory for {Bits}-bit {Codec} until Jellyfin restarts, because keeping them on the GPU failed", source.IsTenBit ? 10 : 8, source.Codec);
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
/// <param name="FramesOnGpu">Whether decoded frames stay on the GPU for the encoder, which the arguments that follow must allow for.</param>
public sealed record HardwareSetup(IReadOnlyList<string> Arguments, string? Decoder, bool FramesOnGpu = false)
{
    /// <summary>Gets a setup that leaves everything to the CPU.</summary>
    public static HardwareSetup None { get; } = new([], null);
}
