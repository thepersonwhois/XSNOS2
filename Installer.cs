using System.Buffers.Binary;
using System.Linq;
using Cosmos.Executable.Lua;
using Cosmos.Kernel.HAL.Devices.Storage;
using Cosmos.Kernel.System.FileSystem;
using Cosmos.Kernel.System.FileSystem.Fat;
using Cosmos.Kernel.System.Storage;

namespace XSNOS;

internal static class KernelInstaller
{
    private const ulong PartitionAlignment = 2048;
    private const ulong MaximumFat32Sectors = uint.MaxValue;
    private const ulong MinimumFat32Sectors = 70_000;
    private const int WipeBufferBytes = 1024 * 1024;
    private const int GptHeaderSize = 92;
    private const int GptHeaderCrcOffset = 16;
    private const int GptCurrentLbaOffset = 24;
    private const int GptBackupLbaOffset = 32;
    private const int GptEntryArrayLbaOffset = 72;
    private const int GptEntryArrayCrcOffset = 88;
    private const int GptEntryCount = 128;
    private const int GptEntrySize = 128;
    private const ulong GptPrimaryEntryArrayLba = 2;
    private const string TargetMountPoint = "/__xsnos_install_target";

    private static readonly Guid EfiSystemPartitionType = new(
        0xC12A7328, 0xF81F, 0x11D2, 0xBA, 0x4B, 0x00, 0xA0, 0xC9, 0x3E, 0xC9, 0x3B);

    public static int ListDisks(ILuaState lua)
    {
        IBlockDevice? bootDisk = GetBootDisk();
        if (bootDisk is null)
        {
            lua.PushNil();
            return 1;
        }

        var disks = StorageManager.Devices.ToList();
        lua.CreateTable(disks.Count, 0);
        for (int index = 0; index < disks.Count; index++)
        {
            IBlockDevice disk = disks[index];
            lua.CreateTable(0, 4);
            lua.PushInteger(index + 1);
            lua.SetField(-2, "index");
            lua.PushString(disk.Name);
            lua.SetField(-2, "name");
            lua.PushNumber((double)disk.BlockCount * disk.BlockSize / (1024.0 * 1024 * 1024));
            lua.SetField(-2, "size_gib");
            lua.PushBoolean(ReferenceEquals(disk, bootDisk));
            lua.SetField(-2, "is_boot");
            lua.RawSetI(-2, index + 1);
        }
        return 1;
    }

    public static int Install(ILuaState lua)
    {
        string requestedName = LuaText.Decode(lua.ToString(1) ?? string.Empty);
        (bool success, string message) = InstallToDisk(requestedName);
        lua.PushBoolean(success);
        lua.PushString(message);
        return 2;
    }

    private static (bool Success, string Message) InstallToDisk(string requestedName)
    {
        IBlockDevice? bootDisk = GetBootDisk();
        if (bootDisk is null)
        {
            return (false, "cannot identify the running system disk; refusing to install");
        }

        IBlockDevice? target = StorageManager.Devices.FirstOrDefault(
            device => string.Equals(device.Name, requestedName, StringComparison.Ordinal));
        if (target is null)
        {
            return (false, "selected disk is no longer available");
        }
        if (ReferenceEquals(target, bootDisk))
        {
            return (false, "refusing to erase the disk the installer booted from");
        }
        if (target.BlockSize != 512)
        {
            return (false, $"unsupported block size: {target.BlockSize}; GPT support currently requires 512-byte sectors");
        }

        List<(ulong Start, ulong Count)> partitions;
        List<string> sourceDirectories = [];
        List<string> sourceFiles = [];
        try
        {
            partitions = PlanPartitions(target.BlockCount);
            CollectSourceTree("/", sourceDirectories, sourceFiles);
        }
        catch (Exception e)
        {
            return (false, "install preflight failed: " + e.Message);
        }

        try
        {
            foreach (VfsMount mount in VfsManager.Mounts
                .Where(mount => ReferenceEquals(mount.Partition?.Host, target))
                .ToList())
            {
                if (!VfsManager.TryUnmount(mount.MountPoint))
                {
                    throw new IOException("could not unmount " + mount.MountPoint);
                }
            }

            Console.WriteLine($"Wiping all {target.BlockCount} sectors on {target.Name}...");
            WipeDisk(target);

            Console.WriteLine("Writing GPT partition table...");
            Gpt.Create(target);
            for (int index = 0; index < partitions.Count; index++)
            {
                (ulong start, ulong count) = partitions[index];
                Guid type = index == 0 ? EfiSystemPartitionType : Gpt.BasicDataPartitionType;
                if (!Gpt.AddPartition(target, start, count, type))
                {
                    throw new IOException($"could not add GPT partition {index + 1}");
                }
            }
            WriteBackupGpt(target);
            target.Flush();
            StorageManager.RescanPartitions(target);

            IReadOnlyList<Partition> targetPartitions = StorageManager.GetPartitions(target);
            if (targetPartitions.Count != partitions.Count)
            {
                throw new IOException($"GPT scan found {targetPartitions.Count} partitions; expected {partitions.Count}");
            }

            Console.WriteLine($"Formatting {partitions.Count} FAT32 partition(s)...");
            Partition? systemPartition = null;
            for (int index = 0; index < partitions.Count; index++)
            {
                (ulong start, ulong count) = partitions[index];
                Partition partition = targetPartitions[index];
                if (partition.StartSector != start || partition.BlockCount != count)
                {
                    throw new IOException($"GPT scan returned unexpected geometry for partition {index + 1}");
                }
                string label = index == 0 ? "XSNOS" : "DATA" + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (label.Length > 11)
                {
                    label = label[..11];
                }

                FatFileSystemType fat = new(partition);
                FatFormatOptions options = new() { Type = FatType.Fat32, VolumeLabel = label.PadRight(11) };
                if (!fat.TryFormat(ReadOnlySpan<char>.Empty, options))
                {
                    throw new IOException($"FAT32 format failed for partition {index + 1} ({count} sectors)");
                }
                if (index == 0)
                {
                    systemPartition = partition;
                }
            }

            if (systemPartition is null)
            {
                throw new InvalidOperationException("partition plan did not include an EFI system partition");
            }

            Console.WriteLine("Copying system and userspace files to the EFI system partition...");
            if (!VfsManager.TryMount("fat", systemPartition, MountFlags.None, TargetMountPoint, out VfsMount? targetMount)
                || targetMount is null)
            {
                throw new IOException("could not mount the new EFI system partition");
            }

            try
            {
                CopyInstallTree(targetMount.MountPoint, sourceDirectories, sourceFiles);
            }
            finally
            {
                if (!VfsManager.TryUnmount(TargetMountPoint))
                {
                    throw new IOException("could not unmount the installed EFI system partition");
                }
            }
            return (true, $"installation complete on {target.Name}; first partition is the EFI system partition");
        }
        catch (Exception e)
        {
            return (false, "installation failed after modifying the disk: " + e.Message);
        }
    }

    private static IBlockDevice? GetBootDisk()
    {
        if (!File.Exists("/sys/installer.flag"))
        {
            return null;
        }

        VfsMount? rootMount = VfsManager.Mounts.FirstOrDefault(mount => mount.MountPoint == "/");
        return rootMount?.Partition?.Host;
    }

    private static List<(ulong Start, ulong Count)> PlanPartitions(ulong diskSectors)
    {
        const ulong entryArraySectors = (GptEntryCount * GptEntrySize + 511) / 512;
        const ulong firstPartitionStart = 2048;
        if (diskSectors <= entryArraySectors + 1 + firstPartitionStart)
        {
            throw new ArgumentException("disk is too small for GPT and an aligned FAT32 partition");
        }

        ulong usableEndExclusive = diskSectors - entryArraySectors - 1;
        ulong usableSectors = usableEndExclusive - firstPartitionStart;
        if (usableSectors < MinimumFat32Sectors)
        {
            throw new ArgumentException("disk is too small for a valid FAT32 partition");
        }

        if (usableSectors <= MaximumFat32Sectors)
        {
            return [(firstPartitionStart, usableSectors)];
        }

        ulong lastStart = (usableEndExclusive - MinimumFat32Sectors) / PartitionAlignment * PartitionAlignment;
        if (lastStart <= firstPartitionStart)
        {
            throw new ArgumentException("disk is too small to split into FAT32 partitions");
        }

        ulong alignedMaximum = MaximumFat32Sectors / PartitionAlignment * PartitionAlignment;
        List<(ulong Start, ulong Count)> result = [];
        ulong start = firstPartitionStart;
        ulong remaining = lastStart - start;
        while (remaining > MaximumFat32Sectors)
        {
            ulong count = alignedMaximum;
            if (remaining - count < MinimumFat32Sectors)
            {
                count = (remaining - MinimumFat32Sectors) / PartitionAlignment * PartitionAlignment;
            }
            if (count < MinimumFat32Sectors || count > MaximumFat32Sectors)
            {
                throw new ArgumentException("disk cannot be split into supported FAT32 partition sizes");
            }

            result.Add((start, count));
            start += count;
            remaining = lastStart - start;
        }

        if (remaining < MinimumFat32Sectors)
        {
            throw new ArgumentException("disk cannot be split into supported FAT32 partition sizes");
        }
        result.Add((start, remaining));
        result.Add((lastStart, usableEndExclusive - lastStart));
        if (result.Count > 127)
        {
            throw new ArgumentException("disk requires more GPT partitions than this installer supports");
        }
        return result;
    }

    private static void WipeDisk(IBlockDevice disk)
    {
        const int reportStepPercent = 5;
        ulong blocksPerBatch = Math.Max(1UL, (ulong)WipeBufferBytes / disk.BlockSize);
        byte[] zeroes = new byte[checked((int)(blocksPerBatch * disk.BlockSize))];
        ulong completed = 0;
        int nextReport = reportStepPercent;

        while (completed < disk.BlockCount)
        {
            ulong count = Math.Min(blocksPerBatch, disk.BlockCount - completed);
            disk.WriteBlock(completed, count, zeroes.AsSpan(0, checked((int)(count * disk.BlockSize))));
            completed += count;
            int percent = (int)((double)completed * 100 / disk.BlockCount);
            if (percent >= nextReport)
            {
                Console.WriteLine($"  {percent}%");
                nextReport = percent + reportStepPercent;
            }
        }
        disk.Flush();
    }

    private static void WriteBackupGpt(IBlockDevice disk)
    {
        int blockSize = checked((int)disk.BlockSize);
        int arrayBytes = GptEntryCount * GptEntrySize;
        int arraySectors = (arrayBytes + blockSize - 1) / blockSize;
        ulong backupArrayLba = disk.BlockCount - (ulong)arraySectors - 1;
        byte[] entries = new byte[arrayBytes];
        for (int sector = 0; sector < arraySectors; sector++)
        {
            disk.ReadBlock(GptPrimaryEntryArrayLba + (ulong)sector, 1, entries.AsSpan(sector * blockSize, blockSize));
        }

        uint entriesCrc = ComputeCrc32(entries);
        byte[] primaryHeader = new byte[blockSize];
        disk.ReadBlock(Gpt.PrimaryHeaderLba, 1, primaryHeader);
        BinaryPrimitives.WriteUInt32LittleEndian(primaryHeader.AsSpan(GptEntryArrayCrcOffset, 4), entriesCrc);
        BinaryPrimitives.WriteUInt32LittleEndian(primaryHeader.AsSpan(GptHeaderCrcOffset, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(
            primaryHeader.AsSpan(GptHeaderCrcOffset, 4),
            ComputeCrc32(primaryHeader.AsSpan(0, GptHeaderSize)));
        disk.WriteBlock(Gpt.PrimaryHeaderLba, 1, primaryHeader);

        for (int sector = 0; sector < arraySectors; sector++)
        {
            disk.WriteBlock(backupArrayLba + (ulong)sector, 1, entries.AsSpan(sector * blockSize, blockSize));
        }

        byte[] backupHeader = (byte[])primaryHeader.Clone();
        BinaryPrimitives.WriteUInt64LittleEndian(backupHeader.AsSpan(GptCurrentLbaOffset, 8), disk.BlockCount - 1);
        BinaryPrimitives.WriteUInt64LittleEndian(backupHeader.AsSpan(GptBackupLbaOffset, 8), Gpt.PrimaryHeaderLba);
        BinaryPrimitives.WriteUInt64LittleEndian(backupHeader.AsSpan(GptEntryArrayLbaOffset, 8), backupArrayLba);
        BinaryPrimitives.WriteUInt32LittleEndian(backupHeader.AsSpan(GptHeaderCrcOffset, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(
            backupHeader.AsSpan(GptHeaderCrcOffset, 4),
            ComputeCrc32(backupHeader.AsSpan(0, GptHeaderSize)));
        disk.WriteBlock(disk.BlockCount - 1, 1, backupHeader);
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in data)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }
        return ~crc;
    }

    private static void CollectSourceTree(string path, List<string> directories, List<string> files)
    {
        foreach (string directory in Directory.GetDirectories(path))
        {
            directories.Add(directory);
            CollectSourceTree(directory, directories, files);
        }
        files.AddRange(Directory.GetFiles(path));
    }

    private static void CopyInstallTree(string targetRoot, List<string> sourceDirectories, List<string> sourceFiles)
    {
        foreach (string sourceDirectory in sourceDirectories)
        {
            string relative = sourceDirectory.TrimStart('/', '\\').Replace('\\', '/');
            Directory.CreateDirectory(targetRoot.TrimEnd('/') + "/" + relative);
        }

        foreach (string sourceFile in sourceFiles)
        {
            string relative = sourceFile.TrimStart('/', '\\').Replace('\\', '/');
            if (string.Equals(relative, "sys/installer.flag", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string destination = targetRoot.TrimEnd('/') + "/" + relative;
            string? destinationDirectory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }
            File.Copy(sourceFile, destination, overwrite: false);
        }
    }
}