// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

namespace Jellyfin.Plugin.DataManager.Encoding;

/// <summary>
/// A running ffmpeg that can be frozen, so a viewer's stream has the GPU, and thawed to carry on from the same frame.
/// </summary>
/// <remarks>
/// Freezing doesn't cancel anything: ffmpeg keeps its place and its file, and the GPU is idle until it's thawed.
/// </remarks>
internal sealed class EncodeProcess
{
    private readonly Process _process;
    private readonly Lock _lock = new();
    private bool _exited;
    private bool _frozen;

    /// <summary>
    /// Initializes a new instance of the <see cref="EncodeProcess"/> class.
    /// </summary>
    /// <param name="process">The started ffmpeg.</param>
    public EncodeProcess(Process process)
    {
        _process = process;
    }

    /// <summary>
    /// Gets a value indicating whether ffmpeg is frozen now.
    /// </summary>
    public bool IsFrozen
    {
        get
        {
            lock (_lock)
            {
                return _frozen;
            }
        }
    }

    /// <summary>
    /// Freezes ffmpeg where it is.
    /// </summary>
    /// <returns>True if it was frozen, false if it had already ended, was already frozen, or this system couldn't.</returns>
    public bool Freeze()
    {
        lock (_lock)
        {
            if (_exited || _frozen || !ProcessSuspender.Suspend(_process))
            {
                return false;
            }

            _frozen = true;
            return true;
        }
    }

    /// <summary>
    /// Lets a frozen ffmpeg carry on.
    /// </summary>
    public void Thaw()
    {
        lock (_lock)
        {
            if (!_frozen)
            {
                return;
            }

            _frozen = false;
            if (!_exited)
            {
                ProcessSuspender.Resume(_process);
            }
        }
    }

    /// <summary>
    /// Notes that ffmpeg has ended, so it can't be frozen after that.
    /// </summary>
    public void MarkExited()
    {
        lock (_lock)
        {
            _exited = true;
        }
    }
}

/// <summary>
/// Freezes and thaws a process: SIGSTOP and SIGCONT on Linux and macOS, NtSuspendProcess on Windows.
/// </summary>
internal static partial class ProcessSuspender
{
    /// <summary>
    /// Freezes a process.
    /// </summary>
    /// <param name="process">The process.</param>
    /// <returns>True if it was frozen.</returns>
    public static bool Suspend(Process process) => Signal(process, "STOP", true);

    /// <summary>
    /// Thaws a frozen process.
    /// </summary>
    /// <param name="process">The process.</param>
    /// <returns>True if it was thawed.</returns>
    public static bool Resume(Process process) => Signal(process, "CONT", false);

    [LibraryImport("ntdll.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int NtSuspendProcess(IntPtr processHandle);

    [LibraryImport("ntdll.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int NtResumeProcess(IntPtr processHandle);

    private static bool Signal(Process process, string signal, bool suspend)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var status = suspend ? NtSuspendProcess(process.Handle) : NtResumeProcess(process.Handle);
                return status == 0;
            }

            // The shell's own kill is always there, unlike a kill program in a slim container. The id is the process's own number.
            using var kill = Process.Start(new ProcessStartInfo("/bin/sh")
            {
                ArgumentList = { "-c", string.Create(CultureInfo.InvariantCulture, $"kill -{signal} {process.Id}") },
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (kill is null)
            {
                return false;
            }

            kill.WaitForExit(5000);
            return kill.HasExited && kill.ExitCode == 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or PlatformNotSupportedException or DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }
}
