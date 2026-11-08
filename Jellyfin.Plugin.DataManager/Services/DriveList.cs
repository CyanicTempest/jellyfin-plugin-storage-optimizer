// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.DataManager.Services;

/// <summary>
/// One line of the mount table: a folder tree and the device it lives on.
/// </summary>
/// <param name="Device">The device number, such as "8:1". Bind mounts of one disk share it.</param>
/// <param name="MountPoint">Where it is mounted.</param>
/// <param name="FileSystem">The file system type, such as ext4, zfs or cifs.</param>
/// <param name="Source">What is mounted: a device such as /dev/sda1, a share such as //nas/films, or a ZFS dataset.</param>
public sealed record Mount(string Device, string MountPoint, string FileSystem, string Source);

/// <summary>
/// A disk that holds library folders, as the overview and the settings show it.
/// </summary>
public sealed class StorageDrive
{
    /// <summary>Gets or sets an id that stays the same across restarts, used to hide the drive.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the drive's name, such as "Samsung", "Network share on homelab" or "System drive".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the drive's size as it's sold, in decimal units, such as "1 TB", or empty if unknown.</summary>
    public string Size { get; set; } = string.Empty;

    /// <summary>Gets or sets what kind of drive it is: HDD, SSD, Network share, ZFS pool, System drive or Drive.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets the file system, such as ext4, or empty if unknown.</summary>
    public string FileSystem { get; set; } = string.Empty;

    /// <summary>Gets the places it is mounted that hold library folders.</summary>
    public IList<string> MountPoints { get; init; } = [];

    /// <summary>Gets or sets the space the file system has in bytes, or -1 if it can't be read.</summary>
    public long TotalBytes { get; set; } = -1;

    /// <summary>Gets or sets the free space in bytes, or -1 if it can't be read.</summary>
    public long FreeBytes { get; set; } = -1;

    /// <summary>Gets or sets how much of it is library video, in bytes.</summary>
    public long VideoBytes { get; set; }

    /// <summary>Gets or sets how many library videos are on it.</summary>
    public int FileCount { get; set; }

    /// <summary>Gets or sets a value indicating whether the overview leaves it out. Encodes still check its free space.</summary>
    public bool Hidden { get; set; }
}

/// <summary>
/// Finds the disk each folder is on, from the mount table, so a disk shared by several libraries is counted once and
/// folders on different disks aren't mixed up.
/// </summary>
public sealed class DriveList
{
    private static readonly HashSet<string> _networkFileSystems = new(StringComparer.OrdinalIgnoreCase)
    {
        "cifs", "smb3", "smbfs", "nfs", "nfs4", "fuse.sshfs", "afpfs", "webdav", "davfs", "fuse.rclone"
    };

    // Model names start with a short maker code more often than the maker's name.
    private static readonly (Regex Pattern, string Maker)[] _makers =
    [
        (MakerRegex("^WDC\\b|^WD\\b|^WD[0-9A-Z]{4,}|^Western ?Digital"), "Western Digital"),
        (MakerRegex("^ST[0-9]|^Seagate"), "Seagate"),
        (MakerRegex("^Samsung"), "Samsung"),
        (MakerRegex("^CT[0-9]|^Crucial"), "Crucial"),
        (MakerRegex("^Micron|^MTFD"), "Micron"),
        (MakerRegex("^TOSHIBA|^KIOXIA"), "Toshiba"),
        (MakerRegex("^HGST|^Hitachi|^HUS|^HUH"), "HGST"),
        (MakerRegex("^INTEL|^SSDPE"), "Intel"),
        (MakerRegex("^KINGSTON|^SA400|^SKC|^SNV"), "Kingston"),
        (MakerRegex("^SanDisk|^SDSSD"), "SanDisk"),
        (MakerRegex("^SK ?hynix|^HFS"), "SK hynix"),
        (MakerRegex("^Sabrent"), "Sabrent"),
        (MakerRegex("^ADATA"), "ADATA"),
        (MakerRegex("^Lexar"), "Lexar"),
        (MakerRegex("^PNY"), "PNY"),
        (MakerRegex("^TEAM|^T-FORCE"), "Teamgroup"),
        (MakerRegex("^APPLE"), "Apple"),
        (MakerRegex("^LaCie"), "LaCie"),
        (MakerRegex("^Corsair"), "Corsair")
    ];

    private readonly string _root;
    private readonly Func<string, (long Total, long Free)?> _space;

    /// <summary>
    /// Initializes a new instance of the <see cref="DriveList"/> class that reads this machine.
    /// </summary>
    public DriveList()
        : this("/", ReadSpace)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DriveList"/> class that reads /proc and /sys under another folder.
    /// </summary>
    /// <param name="root">The folder that stands for /.</param>
    /// <param name="space">Reads a folder's total and free bytes.</param>
    internal DriveList(string root, Func<string, (long Total, long Free)?> space)
    {
        _root = root;
        _space = space;
    }

    /// <summary>
    /// Reads /proc/self/mountinfo lines.
    /// </summary>
    /// <param name="lines">The lines.</param>
    /// <returns>The mounts, in the order the kernel lists them.</returns>
    public static IReadOnlyList<Mount> ParseMountInfo(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var mounts = new List<Mount>();
        foreach (var line in lines)
        {
            // id parent major:minor root mount-point options [optional...] - type source super-options
            var parts = line.Split(' ');
            var dash = Array.IndexOf(parts, "-");
            if (parts.Length < 5 || dash < 6 || dash + 2 >= parts.Length)
            {
                continue;
            }

            mounts.Add(new Mount(parts[2], Unescape(parts[4]), parts[dash + 1], Unescape(parts[dash + 2])));
        }

        return mounts;
    }

    /// <summary>
    /// Finds the mount a path is on: the deepest mount point that holds it, and the last one mounted if several are
    /// mounted at the same place.
    /// </summary>
    /// <param name="mounts">The mount table.</param>
    /// <param name="path">The path.</param>
    /// <returns>The mount, or null if none holds it.</returns>
    public static Mount? FindMount(IReadOnlyList<Mount> mounts, string path)
    {
        ArgumentNullException.ThrowIfNull(mounts);
        ArgumentNullException.ThrowIfNull(path);
        Mount? best = null;
        foreach (var mount in mounts)
        {
            var point = mount.MountPoint.Length > 1 ? mount.MountPoint.TrimEnd('/') : mount.MountPoint;
            var holds = point == "/"
                || path.Equals(point, StringComparison.Ordinal)
                || (path.StartsWith(point, StringComparison.Ordinal) && path.Length > point.Length && path[point.Length] == '/');
            if (holds && (best is null || point.Length >= TrimmedLength(best.MountPoint)))
            {
                best = mount;
            }
        }

        return best;
    }

    /// <summary>
    /// Writes a size the way drives are sold: decimal units, whole gigabytes below a terabyte.
    /// </summary>
    /// <param name="bytes">The size in bytes.</param>
    /// <returns>The size, such as "500 GB" or "1 TB", or empty if it isn't known.</returns>
    public static string MarketingSize(long bytes)
    {
        if (bytes <= 0)
        {
            return string.Empty;
        }

        var gb = bytes / 1e9;
        if (gb < 999.5)
        {
            return gb < 1 ? Math.Round(bytes / 1e6).ToString(CultureInfo.InvariantCulture) + " MB" : Math.Round(gb).ToString(CultureInfo.InvariantCulture) + " GB";
        }

        var tb = Math.Round(bytes / 1e12, 1);
        return tb.ToString("0.#", CultureInfo.InvariantCulture) + " TB";
    }

    /// <summary>
    /// Names a drive's maker from its model, such as "Western Digital" for "WDC WD40EFRX-68N32N0".
    /// </summary>
    /// <param name="model">The model from /sys.</param>
    /// <param name="vendor">The vendor from /sys, which USB enclosures fill in and SATA drives set to "ATA".</param>
    /// <returns>The maker, the model itself if the maker isn't known, or null if there is neither.</returns>
    public static string? MakerName(string? model, string? vendor)
    {
        model = model?.Trim();
        vendor = vendor?.Trim();
        foreach (var text in new[] { model, vendor })
        {
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            foreach (var (pattern, maker) in _makers)
            {
                if (pattern.IsMatch(text))
                {
                    return maker;
                }
            }
        }

        if (!string.IsNullOrEmpty(vendor) && !vendor.Equals("ATA", StringComparison.OrdinalIgnoreCase) && !vendor.Equals("NVMe", StringComparison.OrdinalIgnoreCase))
        {
            return vendor;
        }

        return string.IsNullOrEmpty(model) ? null : model;
    }

    /// <summary>
    /// Finds the drives the folders are on.
    /// </summary>
    /// <param name="folders">Library and video folders.</param>
    /// <returns>The drives, and which drive holds each folder.</returns>
    public DriveMap Map(IEnumerable<string> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        var mounts = ReadMounts();
        var drives = new Dictionary<string, (StorageDrive Drive, Dictionary<string, string> Devices, bool Pool)>(StringComparer.Ordinal);
        var byFolder = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in folders)
        {
            if (string.IsNullOrEmpty(raw))
            {
                continue;
            }

            var folder = Path.TrimEndingDirectorySeparator(raw);
            if (folder.Length == 0)
            {
                folder = raw;
            }

            if (byFolder.ContainsKey(folder))
            {
                continue;
            }

            var found = mounts.Count > 0 ? Describe(FindMount(mounts, folder)) : DescribeWithoutMounts(folder);
            if (found is not { } d)
            {
                continue;
            }

            byFolder[folder] = d.Drive.Id;
            if (!drives.TryGetValue(d.Drive.Id, out var entry))
            {
                entry = (d.Drive, new Dictionary<string, string>(StringComparer.Ordinal), d.Pool);
                drives[d.Drive.Id] = entry;
            }

            if (!entry.Drive.MountPoints.Contains(d.MountPoint))
            {
                entry.Drive.MountPoints.Add(d.MountPoint);
            }

            // A folder on each device is enough to read its space.
            entry.Devices.TryAdd(d.Device, folder);
        }

        foreach (var (drive, devices, pool) in drives.Values)
        {
            var spaces = devices.Values.Select(_space).OfType<(long Total, long Free)>().ToList();
            if (spaces.Count == 0)
            {
                continue;
            }

            // A ZFS pool's datasets share its free space, so it's counted once. Partitions of one disk are added up.
            if (pool)
            {
                drive.FreeBytes = spaces.Max(s => s.Free);
                drive.TotalBytes = drive.FreeBytes + spaces.Sum(s => Math.Max(s.Total - s.Free, 0));
            }
            else
            {
                drive.FreeBytes = spaces.Sum(s => s.Free);
                drive.TotalBytes = spaces.Sum(s => s.Total);
            }

            if (string.IsNullOrEmpty(drive.Size))
            {
                drive.Size = MarketingSize(drive.TotalBytes);
            }
        }

        return new DriveMap(drives.Values.Select(d => d.Drive).ToList(), byFolder);
    }

    private static int TrimmedLength(string point) => point.Length > 1 ? point.TrimEnd('/').Length : point.Length;

    private static (long Total, long Free)? ReadSpace(string folder)
    {
        try
        {
            var drive = new DriveInfo(folder);
            return (drive.TotalSize, drive.AvailableFreeSpace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    // The kernel writes space, tab, newline and backslash as \040, \011, \012 and \134.
    private static string Unescape(string text)
    {
        if (!text.Contains('\\', StringComparison.Ordinal))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && IsOctal(text, i + 1))
            {
                builder.Append((char)Convert.ToInt32(text.Substring(i + 1, 3), 8));
                i += 3;
            }
            else
            {
                builder.Append(text[i]);
            }
        }

        return builder.ToString();
    }

    private static bool IsOctal(string text, int start)
        => start + 3 <= text.Length && text.Skip(start).Take(3).All(c => c is >= '0' and <= '7');

    private static Regex MakerRegex(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private IReadOnlyList<Mount> ReadMounts()
    {
        try
        {
            var path = Path.Combine(_root, "proc/self/mountinfo");
            return File.Exists(path) ? ParseMountInfo(File.ReadAllLines(path)) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string? ReadSys(string folder, string name)
    {
        try
        {
            var path = Path.Combine(folder, name);
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private (StorageDrive Drive, string Device, string MountPoint, bool Pool)? Describe(Mount? mount)
    {
        if (mount is null)
        {
            return null;
        }

        var drive = new StorageDrive { FileSystem = mount.FileSystem };
        var pool = false;
        if (_networkFileSystems.Contains(mount.FileSystem))
        {
            var host = ShareHost(mount.Source);
            drive.Id = "share:" + mount.Source.ToUpperInvariant();
            drive.Kind = "Network share";
            drive.Name = host is null ? "Network share" : "Network share on " + host;
        }
        else if (mount.FileSystem.Equals("overlay", StringComparison.OrdinalIgnoreCase))
        {
            // A Docker container's own files: whatever disk the host keeps its containers on.
            drive.Id = "system";
            drive.Kind = "System drive";
            drive.Name = "System drive";
        }
        else if (mount.FileSystem.Equals("zfs", StringComparison.OrdinalIgnoreCase))
        {
            var name = mount.Source.Split('/')[0];
            drive.Id = "zfs:" + name;
            drive.Kind = "ZFS pool";
            drive.Name = name + " pool";
            pool = true;
        }
        else if (DiskFolder(mount.Device) is { } disk)
        {
            var model = ReadSys(disk, "device/model");
            var vendor = ReadSys(disk, "device/vendor");
            var serial = ReadSys(disk, "wwid") ?? ReadSys(disk, "device/wwid") ?? ReadSys(disk, "device/serial");
            var sectors = long.TryParse(ReadSys(disk, "size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 0;
            var rotational = ReadSys(disk, "queue/rotational");
            drive.Name = MakerName(model, vendor) ?? "Drive";
            drive.Size = MarketingSize(sectors * 512);
            drive.Kind = rotational == "1" ? "HDD" : rotational == "0" ? "SSD" : "Drive";
            drive.Id = "disk:" + (string.IsNullOrEmpty(serial)
                ? (model ?? Path.GetFileName(disk)) + "|" + sectors.ToString(CultureInfo.InvariantCulture)
                : serial);
        }
        else
        {
            // Folders Docker Desktop or OrbStack share from the computer it runs on (virtiofs, 9p) land here too.
            drive.Id = mount.Source.StartsWith('/') ? "dev:" + mount.Source : "fs:" + mount.FileSystem + ":" + mount.Source;
            drive.Kind = "Drive";
            drive.Name = "Drive";
        }

        return (drive, mount.Device, mount.MountPoint, pool);
    }

    // /sys/dev/block/8:1 links to .../block/sda/sda1. A partition's disk is the folder above it.
    private string? DiskFolder(string device)
    {
        try
        {
            var link = Path.Combine(_root, "sys/dev/block", device);
            if (!Directory.Exists(link))
            {
                return null;
            }

            var folder = new DirectoryInfo(link).ResolveLinkTarget(true)?.FullName ?? link;
            if (File.Exists(Path.Combine(folder, "partition")))
            {
                folder = Path.GetDirectoryName(folder) ?? folder;
            }

            // Device mapper and software RAID have no model of their own.
            return File.Exists(Path.Combine(folder, "device/model")) || File.Exists(Path.Combine(folder, "size")) ? folder : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // "//homelab/Movies" and "homelab:/mnt/films" are both on homelab.
    private static string? ShareHost(string source)
    {
        if (source.StartsWith("//", StringComparison.Ordinal))
        {
            var host = source[2..].Split('/')[0];
            return host.Length > 0 ? host : null;
        }

        var colon = source.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 && !source.StartsWith('/') ? source[..colon] : null;
    }

    // Windows and macOS have no mount table to read, so each drive letter or volume is a drive.
    private static (StorageDrive Drive, string Device, string MountPoint, bool Pool)? DescribeWithoutMounts(string folder)
    {
        try
        {
            var info = new DriveInfo(folder);
            var root = info.RootDirectory.FullName;
            var network = info.DriveType == DriveType.Network;
            var label = network ? "Network share" : string.IsNullOrWhiteSpace(info.VolumeLabel) ? "Drive" : info.VolumeLabel;
            var drive = new StorageDrive
            {
                Id = "root:" + root,
                Name = label,
                Kind = network ? "Network share" : "Drive",
                FileSystem = info.DriveFormat
            };
            return (drive, root, root, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>
/// The drives some folders are on.
/// </summary>
public sealed class DriveMap
{
    private readonly Dictionary<string, string> _byFolder;

    /// <summary>
    /// Initializes a new instance of the <see cref="DriveMap"/> class.
    /// </summary>
    /// <param name="drives">The drives.</param>
    /// <param name="byFolder">The drive id of each folder.</param>
    public DriveMap(IReadOnlyList<StorageDrive> drives, Dictionary<string, string> byFolder)
    {
        Drives = drives;
        _byFolder = byFolder;
    }

    /// <summary>Gets the drives.</summary>
    public IReadOnlyList<StorageDrive> Drives { get; }

    /// <summary>
    /// Gets the drive a folder is on.
    /// </summary>
    /// <param name="folder">A folder that was mapped.</param>
    /// <returns>The drive, or null if it isn't known.</returns>
    public StorageDrive? DriveOf(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var key = Path.TrimEndingDirectorySeparator(folder);
        return _byFolder.TryGetValue(key.Length == 0 ? folder : key, out var id) ? Drives.FirstOrDefault(d => d.Id == id) : null;
    }
}
