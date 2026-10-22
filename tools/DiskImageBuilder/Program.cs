using System.Buffers.Binary;
using OutWit.Common.Fat;
using OutWit.Common.Fat.Devices;
using OutWit.Common.Fat.Model;

internal static class Program
{
    private const long ImageSize = 2L * 1024 * 1024 * 1024;
    private const int SectorSize = 512;
    private const string DefaultLfnSuffix = "__xsnos_lfn";

    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 7)
        {
            Console.Error.WriteLine("usage: DiskImageBuilder <image> <kernel.elf> <limine-dir> <lua-dir> <sysnk-dir> <limine.conf> <x64|arm64>");
            return 2;
        }

        string imagePath = Path.GetFullPath(args[0]);
        string kernelPath = Path.GetFullPath(args[1]);
        string limineDirectory = Path.GetFullPath(args[2]);
        string luaDirectory = Path.GetFullPath(args[3]);
        string sysnkDirectory = Path.GetFullPath(args[4]);
        string limineConfigPath = Path.GetFullPath(args[5]);
        string architecture = args[6];

        if (architecture is not ("x64" or "arm64"))
        {
            Console.Error.WriteLine($"unsupported architecture: {architecture}");
            return 2;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
        string lfnSuffix = ChooseLfnSuffix(luaDirectory);
        await CreateImageAsync(imagePath, kernelPath, limineDirectory, luaDirectory, sysnkDirectory, limineConfigPath, architecture, lfnSuffix);
        return 0;
    }

    private static async Task CreateImageAsync(
        string imagePath,
        string kernelPath,
        string limineDirectory,
        string luaDirectory,
        string sysnkDirectory,
        string limineConfigPath,
        string architecture,
        string lfnSuffix)
    {
        const long sectorCount = ImageSize / SectorSize;
        await using var disk = BlockDeviceFile.Create(imagePath, sectorCount, SectorSize);
        await FatFormatter.FormatDiskAsync(disk, new FatFormatOptions
        {
            Kind = FatKind.Fat32,
            ClusterSize = 4096,
            Label = "XSNOS",
        });

        byte[] mbr = new byte[SectorSize];
        await disk.ReadAsync(0, mbr);
        mbr[446] = 0x80;
        mbr[450] = 0xEF;
        await disk.WriteAsync(0, mbr);
        await disk.FlushAsync();

        string efiFile = architecture == "x64" ? "BOOTX64.EFI" : "BOOTAA64.EFI";
        await using (FatVolume volume = await FatVolume.MountDiskAsync(disk, new FatVolumeOptions { LeaveOpen = true }))
        {
            await volume.CreateDirectoryAsync(ToStagedPath("/sys", lfnSuffix));
            await volume.CreateDirectoryAsync(ToStagedPath("/prog", lfnSuffix));
            await volume.CreateDirectoryAsync(ToStagedPath("/prog/sys", lfnSuffix));
            await volume.CreateDirectoryAsync(ToStagedPath("/prog/user", lfnSuffix));
            await volume.CreateDirectoryAsync(ToStagedPath("/user", lfnSuffix));
            await volume.CreateDirectoryAsync(ToStagedPath("/boot/limine", lfnSuffix));
            await volume.CreateDirectoryAsync(ToStagedPath("/EFI/BOOT", lfnSuffix));

            await CopyFileAsync(volume, kernelPath, ToStagedPath("/sys/xsnkrnl", lfnSuffix));
            await CreateEmptyFileAsync(volume, ToStagedPath("/sys/installer.flag", lfnSuffix));
            await CopyFileAsync(volume, limineConfigPath, ToStagedPath("/boot/limine/limine.conf", lfnSuffix));
            await CopyFileAsync(volume, Path.Combine(limineDirectory, "limine-bios.sys"), ToStagedPath("/boot/limine/limine-bios.sys", lfnSuffix));
            await CopyFileAsync(volume, Path.Combine(limineDirectory, efiFile), ToStagedPath($"/EFI/BOOT/{efiFile}", lfnSuffix));

            await CopySysnkFilesAsync(volume, sysnkDirectory, lfnSuffix);
            await CopyLuaFilesAsync(volume, luaDirectory, lfnSuffix);
            await volume.FlushAsync();
        }

        await RewriteStagedLongNamesAsync(disk, lfnSuffix);
        await disk.FlushAsync();

        await using FatVolume verifiedVolume = await FatVolume.MountDiskAsync(
            disk,
            new FatVolumeOptions { LeaveOpen = true });
        await VerifyImageAsync(verifiedVolume, kernelPath, limineDirectory, limineConfigPath, luaDirectory, sysnkDirectory, efiFile);
    }

    private static string ChooseLfnSuffix(string luaDirectory)
    {
        string suffix = DefaultLfnSuffix;
        int attempt = 0;

        while (Directory.Exists(luaDirectory) && Directory.EnumerateFileSystemEntries(luaDirectory, "*", SearchOption.AllDirectories)
            .Any(path => Path.GetFileName(path).EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
        {
            suffix = DefaultLfnSuffix + (++attempt).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return suffix;
    }

    private static string ToStagedPath(string path, string lfnSuffix)
    {
        string[] components = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < components.Length; i++)
        {
            if (NeedsCasePreservingLongName(components[i]))
            {
                components[i] += lfnSuffix;
            }
        }

        return "/" + string.Join('/', components);
    }

    private static bool NeedsCasePreservingLongName(string name)
    {
        if (!name.Any(character => character is >= 'a' and <= 'z'))
        {
            return false;
        }

        int dot = name.LastIndexOf('.');
        if (dot != name.IndexOf('.') || dot == 0)
        {
            return false;
        }

        int baseLength = dot < 0 ? name.Length : dot;
        int extensionLength = dot < 0 ? 0 : name.Length - dot - 1;
        if (baseLength is < 1 or > 8 || extensionLength > 3)
        {
            return false;
        }

        foreach (char character in name)
        {
            if (character == '.')
            {
                continue;
            }

            bool allowed = character is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9'
                or '$' or '%' or '\'' or '-' or '_' or '@' or '~' or '`' or '!'
                or '(' or ')' or '{' or '}' or '^' or '#' or '&';
            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }

    private static async Task RewriteStagedLongNamesAsync(BlockDeviceFile disk, string suffix)
    {
        byte[] mbr = new byte[disk.SectorSize];
        await disk.ReadAsync(0, mbr);
        long partitionStart = BinaryPrimitives.ReadUInt32LittleEndian(mbr.AsSpan(446 + 8, 4));
        if (partitionStart <= 0)
        {
            throw new InvalidDataException("The disk image has no FAT partition.");
        }

        byte[] bootSector = new byte[disk.SectorSize];
        await disk.ReadAsync(partitionStart, bootSector);
        int bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(bootSector.AsSpan(11, 2));
        int sectorsPerCluster = bootSector[13];
        int reservedSectors = BinaryPrimitives.ReadUInt16LittleEndian(bootSector.AsSpan(14, 2));
        int fatCount = bootSector[16];
        uint sectorsPerFat = BinaryPrimitives.ReadUInt32LittleEndian(bootSector.AsSpan(36, 4));
        uint rootCluster = BinaryPrimitives.ReadUInt32LittleEndian(bootSector.AsSpan(44, 4));

        if (bytesPerSector != disk.SectorSize || sectorsPerCluster == 0 || fatCount == 0 || sectorsPerFat == 0 || rootCluster < 2)
        {
            throw new InvalidDataException("The image does not contain a valid FAT32 volume.");
        }

        long dataStart = partitionStart + reservedSectors + (long)fatCount * sectorsPerFat;
        int clusterSize = bytesPerSector * sectorsPerCluster;
        HashSet<uint> visitedDirectories = [];
        await RewriteDirectoryLongNamesAsync(
            disk,
            partitionStart,
            reservedSectors,
            sectorsPerFat,
            dataStart,
            bytesPerSector,
            sectorsPerCluster,
            clusterSize,
            rootCluster,
            suffix,
            visitedDirectories);
    }

    private static async Task RewriteDirectoryLongNamesAsync(
        BlockDeviceFile disk,
        long partitionStart,
        int reservedSectors,
        uint sectorsPerFat,
        long dataStart,
        int bytesPerSector,
        int sectorsPerCluster,
        int clusterSize,
        uint firstCluster,
        string suffix,
        HashSet<uint> visitedDirectories)
    {
        if (!visitedDirectories.Add(firstCluster))
        {
            return;
        }

        using MemoryStream directoryStream = new();
        List<long> clusterLbas = [];
        HashSet<uint> visitedClusters = [];
        uint cluster = firstCluster;

        while (cluster >= 2 && cluster < 0x0FFFFFF8 && visitedClusters.Add(cluster))
        {
            long clusterLba = dataStart + (long)(cluster - 2) * sectorsPerCluster;
            byte[] clusterBytes = new byte[clusterSize];
            await disk.ReadAsync(clusterLba, clusterBytes);
            directoryStream.Write(clusterBytes);
            clusterLbas.Add(clusterLba);
            cluster = await ReadNextClusterAsync(disk, partitionStart, reservedSectors, bytesPerSector, cluster);
        }

        byte[] directoryData = directoryStream.ToArray();
        List<int> lfnOffsets = [];
        List<uint> childDirectories = [];
        bool changed = false;

        for (int offset = 0; offset + 32 <= directoryData.Length; offset += 32)
        {
            byte firstByte = directoryData[offset];
            if (firstByte == 0)
            {
                break;
            }
            if (firstByte == 0xE5)
            {
                lfnOffsets.Clear();
                continue;
            }

            byte attributes = directoryData[offset + 11];
            if (attributes == 0x0F)
            {
                lfnOffsets.Add(offset);
                continue;
            }

            if (lfnOffsets.Count > 0
                && TryReadLongName(directoryData, lfnOffsets, offset, out string longName)
                && longName.EndsWith(suffix, StringComparison.Ordinal))
            {
                string originalName = longName[..^suffix.Length];
                if (originalName.Length == 0)
                {
                    throw new InvalidDataException("A staged FAT name became empty.");
                }

                WriteLongName(directoryData, lfnOffsets, originalName);
                changed = true;
            }

            bool isDirectory = (attributes & 0x10) != 0;
            bool isDotEntry = firstByte == '.' && (directoryData[offset + 1] == ' ' || directoryData[offset + 1] == '.');
            if (isDirectory && !isDotEntry)
            {
                uint highCluster = BinaryPrimitives.ReadUInt16LittleEndian(directoryData.AsSpan(offset + 20, 2));
                uint lowCluster = BinaryPrimitives.ReadUInt16LittleEndian(directoryData.AsSpan(offset + 26, 2));
                uint childCluster = (highCluster << 16) | lowCluster;
                if (childCluster >= 2)
                {
                    childDirectories.Add(childCluster);
                }
            }

            lfnOffsets.Clear();
        }

        if (changed)
        {
            for (int i = 0; i < clusterLbas.Count; i++)
            {
                await disk.WriteAsync(clusterLbas[i], directoryData.AsMemory(i * clusterSize, clusterSize));
            }
        }

        foreach (uint childCluster in childDirectories)
        {
            await RewriteDirectoryLongNamesAsync(
                disk,
                partitionStart,
                reservedSectors,
                sectorsPerFat,
                dataStart,
                bytesPerSector,
                sectorsPerCluster,
                clusterSize,
                childCluster,
                suffix,
                visitedDirectories);
        }
    }

    private static async Task<uint> ReadNextClusterAsync(
        BlockDeviceFile disk,
        long partitionStart,
        int reservedSectors,
        int bytesPerSector,
        uint cluster)
    {
        ulong fatOffset = (ulong)cluster * 4;
        long fatSector = partitionStart + reservedSectors + (long)(fatOffset / (uint)bytesPerSector);
        int offsetInSector = (int)(fatOffset % (uint)bytesPerSector);
        byte[] fatSectorBytes = new byte[bytesPerSector];
        await disk.ReadAsync(fatSector, fatSectorBytes);
        return BinaryPrimitives.ReadUInt32LittleEndian(fatSectorBytes.AsSpan(offsetInSector, 4)) & 0x0FFFFFFF;
    }

    private static bool TryReadLongName(byte[] directoryData, List<int> lfnOffsets, int shortEntryOffset, out string name)
    {
        name = string.Empty;
        byte checksum = ComputeShortNameChecksum(directoryData.AsSpan(shortEntryOffset, 11));
        int entryCount = 0;
        foreach (int offset in lfnOffsets)
        {
            byte sequence = directoryData[offset];
            int ordinal = sequence & 0x1F;
            if (ordinal == 0 || directoryData[offset + 13] != checksum)
            {
                return false;
            }
            entryCount = Math.Max(entryCount, ordinal);
        }

        if (entryCount != lfnOffsets.Count)
        {
            return false;
        }

        char[] chars = new char[entryCount * 13];
        foreach (int offset in lfnOffsets)
        {
            int start = ((directoryData[offset] & 0x1F) - 1) * 13;
            int[] nameOffsets = { 1, 14, 28 };
            int[] lengths = { 5, 6, 2 };
            int destination = start;
            for (int region = 0; region < nameOffsets.Length; region++)
            {
                for (int i = 0; i < lengths[region]; i++)
                {
                    chars[destination++] = (char)BinaryPrimitives.ReadUInt16LittleEndian(
                        directoryData.AsSpan(offset + nameOffsets[region] + i * 2, 2));
                }
            }
        }

        int nameLength = Array.FindIndex(chars, character => character is '\0' or '\uFFFF');
        if (nameLength < 0)
        {
            nameLength = chars.Length;
        }
        name = new string(chars, 0, nameLength);
        return true;
    }

    private static void WriteLongName(byte[] directoryData, List<int> lfnOffsets, string name)
    {
        foreach (int offset in lfnOffsets)
        {
            int ordinal = directoryData[offset] & 0x1F;
            int source = (ordinal - 1) * 13;
            int[] nameOffsets = { 1, 14, 28 };
            int[] lengths = { 5, 6, 2 };
            for (int region = 0; region < nameOffsets.Length; region++)
            {
                for (int i = 0; i < lengths[region]; i++)
                {
                    int sourceIndex = source++;
                    ushort value = sourceIndex < name.Length
                        ? name[sourceIndex]
                        : sourceIndex == name.Length ? (ushort)0 : (ushort)0xFFFF;
                    BinaryPrimitives.WriteUInt16LittleEndian(
                        directoryData.AsSpan(offset + nameOffsets[region] + i * 2, 2),
                        value);
                }
            }
        }
    }

    private static byte ComputeShortNameChecksum(ReadOnlySpan<byte> shortName)
    {
        byte checksum = 0;
        foreach (byte value in shortName)
        {
            checksum = (byte)(((checksum & 1) != 0 ? 0x80 : 0) + (checksum >> 1) + value);
        }
        return checksum;
    }

    private static async Task VerifyImageAsync(
        FatVolume volume,
        string kernelPath,
        string limineDirectory,
        string limineConfigPath,
        string luaDirectory,
        string sysnkDirectory,
        string efiFile)
    {
        string[] directories = { "/sys", "/prog", "/prog/sys", "/prog/user", "/user", "/boot/limine", "/EFI/BOOT" };
        foreach (string directory in directories)
        {
            if (await volume.GetEntryAsync(directory) is null)
            {
                throw new InvalidDataException($"Required image directory is missing: {directory}");
            }
        }

        var files = new List<(string Source, string Destination)>
        {
            (kernelPath, "/sys/xsnkrnl"),
            (limineConfigPath, "/boot/limine/limine.conf"),
            (Path.Combine(limineDirectory, "limine-bios.sys"), "/boot/limine/limine-bios.sys"),
            (Path.Combine(limineDirectory, efiFile), $"/EFI/BOOT/{efiFile}"),
        };

        foreach (string sourcePath in Directory.Exists(sysnkDirectory)
            ? Directory.EnumerateFiles(sysnkDirectory, "*", SearchOption.AllDirectories)
            : Array.Empty<string>())
        {
            string relativePath = Path.GetRelativePath(sysnkDirectory, sourcePath).Replace(Path.DirectorySeparatorChar, '/');
            files.Add((sourcePath, $"/sys/{relativePath}"));
        }

        foreach (string sourcePath in Directory.Exists(luaDirectory)
            ? Directory.EnumerateFiles(luaDirectory, "*", SearchOption.AllDirectories)
            : Array.Empty<string>())
        {
            if (Path.GetFileName(sourcePath) == ".gitkeep")
            {
                continue;
            }

            string relativePath = Path.GetRelativePath(luaDirectory, sourcePath).Replace(Path.DirectorySeparatorChar, '/');
            files.Add((sourcePath, $"/prog/sys/{relativePath}"));
        }

        foreach ((string source, string destination) in files)
        {
            var entry = await volume.GetEntryAsync(destination);
            if (entry is null || entry.Length != new FileInfo(source).Length)
            {
                throw new InvalidDataException($"Image file is missing or incomplete: {destination}");
            }
        }

        if (await volume.GetEntryAsync("/sys/installer.flag") is null)
        {
            throw new InvalidDataException("Installer media marker is missing.");
        }
    }

    private static async Task CopySysnkFilesAsync(FatVolume volume, string sysnkDirectory, string lfnSuffix)
    {
        if (!Directory.Exists(sysnkDirectory))
        {
            return;
        }

        foreach (string directory in Directory.EnumerateDirectories(sysnkDirectory, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(sysnkDirectory, directory).Replace(Path.DirectorySeparatorChar, '/');
            await volume.CreateDirectoryAsync(ToStagedPath($"/sys/{relativePath}", lfnSuffix));
        }

        foreach (string sourcePath in Directory.EnumerateFiles(sysnkDirectory, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(sysnkDirectory, sourcePath).Replace(Path.DirectorySeparatorChar, '/');
            await CopyFileAsync(volume, sourcePath, ToStagedPath($"/sys/{relativePath}", lfnSuffix));
        }
    }

    private static async Task CopyLuaFilesAsync(FatVolume volume, string luaDirectory, string lfnSuffix)
    {
        if (!Directory.Exists(luaDirectory))
        {
            return;
        }

        foreach (string directory in Directory.EnumerateDirectories(luaDirectory, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(luaDirectory, directory).Replace(Path.DirectorySeparatorChar, '/');
            await volume.CreateDirectoryAsync(ToStagedPath($"/prog/sys/{relativePath}", lfnSuffix));
        }

        foreach (string sourcePath in Directory.EnumerateFiles(luaDirectory, "*", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(sourcePath) == ".gitkeep")
            {
                continue;
            }

            string relativePath = Path.GetRelativePath(luaDirectory, sourcePath).Replace(Path.DirectorySeparatorChar, '/');
            await CopyFileAsync(volume, sourcePath, ToStagedPath($"/prog/sys/{relativePath}", lfnSuffix));
        }
    }

    private static async Task CopyFileAsync(FatVolume volume, string sourcePath, string destinationPath)
    {
        await using FileStream source = File.OpenRead(sourcePath);
        await using Stream destination = await volume.OpenAsync(destinationPath, FileMode.Create, FileAccess.Write);
        await source.CopyToAsync(destination);
    }

    private static async Task CreateEmptyFileAsync(FatVolume volume, string path)
    {
        await using Stream _ = await volume.OpenAsync(path, FileMode.Create, FileAccess.Write);
    }
}