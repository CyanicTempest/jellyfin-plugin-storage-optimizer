// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.DataManager.Services;
using Xunit;

namespace Jellyfin.Plugin.DataManager.Tests;

public sealed class DriveListTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "so-drives-" + Guid.NewGuid().ToString("N"));

    public DriveListTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, true);

    // A Jellyfin container: its own overlay root, the config folder and two libraries bind-mounted from one host
    // disk, another library on a second disk, a network share and two datasets of one ZFS pool.
    private static readonly string[] _mountInfo =
    [
        "100 90 0:52 / / rw,relatime master:1 - overlay overlay rw,lowerdir=/a,upperdir=/b,workdir=/c",
        "101 100 8:1 /docker/jellyfin/config /config rw,relatime - ext4 /dev/sda1 rw",
        "102 100 8:1 /media/movies /media/movies rw,relatime - ext4 /dev/sda1 rw",
        "103 100 8:1 /media/shows /media/shows rw,relatime - ext4 /dev/sda1 rw",
        "104 100 8:17 / /media/big\\040films rw,relatime - xfs /dev/sdb1 rw",
        "105 100 0:60 / /media/nas rw,relatime - cifs //homelab/Movies rw",
        "106 100 0:70 / /media/tank/films rw - zfs tank/films rw",
        "107 100 0:71 / /media/tank/shows rw - zfs tank/shows rw"
    ];

    private DriveList Fixture(Dictionary<string, (long Total, long Free)> space)
    {
        var proc = Directory.CreateDirectory(Path.Combine(_root, "proc/self")).FullName;
        File.WriteAllLines(Path.Combine(proc, "mountinfo"), _mountInfo);
        Disk("sda", "8:1", "Samsung SSD 870 EVO 1TB", "0", 1_953_525_168, "S5Y1NJ0R123456");
        Disk("sdb", "8:17", "WDC WD40EFRX-68N32N0", "1", 7_814_037_168, null);
        return new DriveList(_root, folder => space.TryGetValue(folder, out var s) ? s : null);
    }

    private void Disk(string name, string device, string model, string rotational, long sectors, string? serial)
    {
        var disk = Directory.CreateDirectory(Path.Combine(_root, "sys/devices/pci0000:00/ata1/block", name)).FullName;
        Directory.CreateDirectory(Path.Combine(disk, "device"));
        Directory.CreateDirectory(Path.Combine(disk, "queue"));
        File.WriteAllText(Path.Combine(disk, "device/model"), model + "\n");
        File.WriteAllText(Path.Combine(disk, "device/vendor"), "ATA     \n");
        File.WriteAllText(Path.Combine(disk, "queue/rotational"), rotational + "\n");
        File.WriteAllText(Path.Combine(disk, "size"), sectors + "\n");
        if (serial is not null)
        {
            File.WriteAllText(Path.Combine(disk, "device/serial"), serial);
        }

        var partition = Directory.CreateDirectory(Path.Combine(disk, name + "1")).FullName;
        File.WriteAllText(Path.Combine(partition, "partition"), "1");
        Directory.CreateDirectory(Path.Combine(_root, "sys/dev/block"));
        Directory.CreateSymbolicLink(Path.Combine(_root, "sys/dev/block", device), partition);
    }

    [Fact]
    public void ParseMountInfo_reads_mount_points_types_and_sources()
    {
        var mounts = DriveList.ParseMountInfo(_mountInfo);
        Assert.Equal(8, mounts.Count);
        Assert.Equal(new Mount("8:17", "/media/big films", "xfs", "/dev/sdb1"), mounts[4]);
        Assert.Equal("//homelab/Movies", mounts[5].Source);
    }

    [Theory]
    [InlineData("/media/movies/Film (2008)", "/media/movies")]
    [InlineData("/media/moviesextra", "/")]
    [InlineData("/media/big films/A", "/media/big films")]
    [InlineData("/config/data/collections", "/config")]
    public void FindMount_picks_the_deepest_mount_holding_the_path(string path, string expected)
        => Assert.Equal(expected, DriveList.FindMount(DriveList.ParseMountInfo(_mountInfo), path)!.MountPoint);

    [Theory]
    [InlineData(1_000_204_886_016, "1 TB")]
    [InlineData(4_000_787_030_016, "4 TB")]
    [InlineData(500_107_862_016, "500 GB")]
    [InlineData(2_500_000_000_000, "2.5 TB")]
    [InlineData(0, "")]
    public void MarketingSize_uses_decimal_units(long bytes, string expected)
        => Assert.Equal(expected, DriveList.MarketingSize(bytes));

    [Theory]
    [InlineData("WDC WD40EFRX-68N32N0", "ATA", "Western Digital")]
    [InlineData("ST4000VN008-2DR166", "ATA", "Seagate")]
    [InlineData("Samsung SSD 870 EVO 1TB", "ATA", "Samsung")]
    [InlineData("CT1000MX500SSD1", "ATA", "Crucial")]
    [InlineData("Elements 25A1", "WD", "Western Digital")]
    [InlineData("Mystery Disk 9000", "ATA", "Mystery Disk 9000")]
    [InlineData("", "", null)]
    public void MakerName_maps_model_codes_to_makers(string model, string vendor, string? expected)
        => Assert.Equal(expected, DriveList.MakerName(model, vendor));

    [Fact]
    public void Map_gives_one_drive_per_disk_with_its_own_space()
    {
        var list = Fixture(new Dictionary<string, (long, long)>
        {
            ["/media/movies/A"] = (1_000_000_000_000, 400_000_000_000),
            ["/media/big films/B"] = (4_000_000_000_000, 1_000_000_000_000),
            ["/media/nas/C"] = (8_000_000_000_000, 3_000_000_000_000),
            ["/media/tank/films/D"] = (5_000_000_000_000, 2_000_000_000_000),
            ["/media/tank/shows/E"] = (3_500_000_000_000, 2_000_000_000_000)
        });

        var map = list.Map(["/media/movies/A", "/media/shows/X", "/media/big films/B", "/media/nas/C", "/media/tank/films/D", "/media/tank/shows/E"]);

        Assert.Equal(4, map.Drives.Count);
        var samsung = map.DriveOf("/media/shows/X")!;
        Assert.Same(samsung, map.DriveOf("/media/movies/A"));
        Assert.Equal("Samsung", samsung.Name);
        Assert.Equal("1 TB", samsung.Size);
        Assert.Equal("SSD", samsung.Kind);
        Assert.Equal("disk:S5Y1NJ0R123456", samsung.Id);
        Assert.Equal(["/media/movies", "/media/shows"], samsung.MountPoints);
        Assert.Equal(400_000_000_000, samsung.FreeBytes);

        var wd = map.DriveOf("/media/big films/B")!;
        Assert.Equal(("Western Digital", "4 TB", "HDD"), (wd.Name, wd.Size, wd.Kind));

        var share = map.DriveOf("/media/nas/C")!;
        Assert.Equal(("Network share on homelab", "Network share", "8 TB"), (share.Name, share.Kind, share.Size));

        // The pool's datasets share one free space; what each holds is added to it.
        var pool = map.DriveOf("/media/tank/shows/E")!;
        Assert.Equal("tank pool", pool.Name);
        Assert.Equal(2_000_000_000_000, pool.FreeBytes);
        Assert.Equal(2_000_000_000_000 + 3_000_000_000_000 + 1_500_000_000_000, pool.TotalBytes);
    }

    [Fact]
    public void Map_calls_a_container_root_the_system_drive()
    {
        var list = Fixture(new Dictionary<string, (long, long)> { ["/srv/films"] = (100_000_000_000, 10_000_000_000) });
        var drive = list.Map(["/srv/films"]).Drives.Single();
        Assert.Equal(("system", "System drive"), (drive.Id, drive.Name));
    }

    [Theory]
    [InlineData("boxsets", "/srv/collections", false)]
    [InlineData("playlists", "/srv/playlists", false)]
    [InlineData("movies", "/config/data/collections", false)]
    [InlineData("movies", "/media/movies", true)]
    [InlineData(null, "/config/database", true)]
    public void IsLibraryFolder_leaves_out_collections_and_playlists(string? type, string location, bool expected)
        => Assert.Equal(expected, SummaryBuilder.IsLibraryFolder(type, location, "/config/data"));
}
