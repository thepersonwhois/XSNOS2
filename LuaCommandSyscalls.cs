using System;
using System.IO;
using System.Linq;
using Cosmos.Executable.Lua;
using Cosmos.Kernel.System.FileSystem;
using Cosmos.Kernel.System.FileSystem.Fat;
using Cosmos.Kernel.System.Storage;

namespace XSNOS;

internal static class LuaCommandSyscalls
{
        public static int MountDrive(ILuaState lua)
    {
        string deviceName = LuaText.Decode(lua.ToString(1) ?? string.Empty);
        string mountPath = ReadPath(lua, 2);

        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return PushResult(lua, false, "device name cannot be empty");
        }

        if (string.IsNullOrWhiteSpace(mountPath))
        {
            return PushResult(lua, false, "invalid target mount path");
        }

        var partitions = StorageManager.Partitions;
        Partition? target = partitions.FirstOrDefault(p => string.Equals(p.Name, deviceName, StringComparison.OrdinalIgnoreCase));

        if (target is null)
        {
            return PushResult(lua, false, $"block device '{deviceName}' not found");
        }

        try
        {
            // 3. Safely clean up collisions on that target mount path or partition
            foreach (VfsMount existingMount in VfsManager.Mounts.ToList())
            {
                if (existingMount.MountPoint == mountPath || ReferenceEquals(existingMount.Partition, target))
                {
                    VfsManager.TryUnmount(existingMount.MountPoint);
                }
            }

            // 4. Try mounting using the Gen 3 VfsManager matching your filesystem context
            if (!VfsManager.TryMount("fat", target, MountFlags.None, mountPath, out VfsMount? mountedVolume) || mountedVolume is null)
            {
                return PushResult(lua, false, $"failed to mount block device {deviceName} at {mountPath}");
            }

            return PushResult(lua, true, $"successfully mounted at {mountedVolume.MountPoint}");
        }
        catch (Exception error)
        {
            return PushResult(lua, false, error.Message);
        }
    }
    public static int ListDirectory(ILuaState lua)
    {
        string path = ReadPath(lua, 1);
        try
        {
            if (!Directory.Exists(path))
            {
                lua.PushNil();
                lua.PushString("no such directory");
                return 2;
            }

            string[] directories = Directory.GetDirectories(path).OrderBy(item => item).ToArray();
            string[] files = Directory.GetFiles(path).OrderBy(item => item).ToArray();
            lua.CreateTable(directories.Length + files.Length, 0);
            int index = 1;
            foreach (string directory in directories)
            {
                lua.CreateTable(0, 3);
                lua.PushString(Path.GetFileName(directory));
                lua.SetField(-2, "name");
                lua.PushString("dir");
                lua.SetField(-2, "kind");
                lua.PushInteger(0);
                lua.SetField(-2, "size");
                lua.RawSetI(-2, index++);
            }
            foreach (string file in files)
            {
                lua.CreateTable(0, 3);
                lua.PushString(Path.GetFileName(file));
                lua.SetField(-2, "name");
                lua.PushString("file");
                lua.SetField(-2, "kind");
                lua.PushInteger(new FileInfo(file).Length);
                lua.SetField(-2, "size");
                lua.RawSetI(-2, index++);
            }
            return 1;
        }
        catch (Exception error)
        {
            lua.PushNil();
            lua.PushString(error.Message);
            return 2;
        }
    }

    public static int MakeDirectory(ILuaState lua)
    {
        try
        {
            Directory.CreateDirectory(ReadPath(lua, 1));
            return PushResult(lua, true, "created");
        }
        catch (Exception error)
        {
            return PushResult(lua, false, error.Message);
        }
    }

    public static int ChangeDirectory(ILuaState lua)
    {
        try
        {
            string path = ReadPath(lua, 1);
            if (!Directory.Exists(path))
            {
                return PushResult(lua, false, "no such directory");
            }
            Directory.SetCurrentDirectory(path);
            return PushResult(lua, true, Directory.GetCurrentDirectory());
        }
        catch (Exception error)
        {
            return PushResult(lua, false, error.Message);
        }
    }

    public static int PathKind(ILuaState lua)
    {
        string path = ReadPath(lua, 1);
        if (Directory.Exists(path))
        {
            lua.PushString("dir");
        }
        else if (File.Exists(path))
        {
            lua.PushString("file");
        }
        else
        {
            lua.PushNil();
        }
        return 1;
    }

    public static int RemovePath(ILuaState lua)
    {
        try
        {
            string path = ReadPath(lua, 1);
            bool recursive = lua.ToBoolean(2);
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive);
                return PushResult(lua, true, "removed directory");
            }
            if (File.Exists(path))
            {
                File.Delete(path);
                return PushResult(lua, true, "removed file");
            }
            return PushResult(lua, false, "not found");
        }
        catch (Exception error)
        {
            return PushResult(lua, false, error.Message);
        }
    }

    public static int CopyPath(ILuaState lua)
    {
        string source = ReadPath(lua, 1);
        string destination = ReadPath(lua, 2);
        try
        {
            if (Directory.Exists(source))
            {
                CopyDirectoryRecursive(source, destination);
                return PushResult(lua, true, "copied directory");
            }
            if (File.Exists(source))
            {
                File.Copy(source, destination, overwrite: true);
                return PushResult(lua, true, "copied file");
            }
            return PushResult(lua, false, "source not found");
        }
        catch (Exception error)
        {
            return PushResult(lua, false, error.Message);
        }
    }

    public static int ListPartitions(ILuaState lua)
    {
        var partitions = StorageManager.Partitions.ToList();
        lua.CreateTable(partitions.Count, 0);
        for (int index = 0; index < partitions.Count; index++)
        {
            var partition = partitions[index];
            lua.CreateTable(0, 5);
            lua.PushInteger(index);
            lua.SetField(-2, "index");
            lua.PushString(partition.Name);
            lua.SetField(-2, "name");
            lua.PushInteger((long)partition.BlockCount);
            lua.SetField(-2, "blocks");
            lua.PushInteger((long)partition.BlockSize);
            lua.SetField(-2, "block_size");
            lua.PushString(partition.Host.Name);
            lua.SetField(-2, "host");
            lua.RawSetI(-2, index + 1);
        }
        return 1;
    }

    public static int FormatFat(ILuaState lua)
    {
        long indexValue = lua.ToInteger(1);
        string label = LuaText.Decode(lua.ToString(2) ?? string.Empty);
        long typeValue = lua.ToInteger(3);
        var partitions = StorageManager.Partitions;
        if (indexValue < 0 || indexValue >= partitions.Count)
        {
            return PushResult(lua, false, "invalid partition selection");
        }

        if (string.IsNullOrWhiteSpace(label))
        {
            label = "XSNOS";
        }
        label = label.Length > 11 ? label[..11] : label.PadRight(11);
        FatType type = typeValue switch
        {
            12 => FatType.Fat12,
            16 => FatType.Fat16,
            _ => FatType.Fat32,
        };

        Partition target = partitions[(int)indexValue];
        try
        {
            foreach (VfsMount existingMount in VfsManager.Mounts.ToList())
            {
                if (existingMount.MountPoint == "/" || ReferenceEquals(existingMount.Partition, target))
                {
                    VfsManager.TryUnmount(existingMount.MountPoint);
                }
            }

            if (!VfsManager.TryFormat("fat", target, new FatFormatOptions { Type = type, VolumeLabel = label }))
            {
                return PushResult(lua, false, "format failed");
            }

            if (!VfsManager.TryMount("fat", target, MountFlags.None, "/", out VfsMount? mountedVolume) || mountedVolume is null)
            {
                return PushResult(lua, false, "formatted, but could not mount at /");
            }

            Directory.SetCurrentDirectory(mountedVolume.MountPoint);
            return PushResult(lua, true, "formatted and mounted at /");
        }
        catch (Exception error)
        {
            return PushResult(lua, false, error.Message);
        }
    }

    private static string ReadPath(ILuaState lua, int index)
    {
        return LuaText.Decode(lua.ToString(index) ?? string.Empty);
    }

    private static int PushResult(ILuaState lua, bool success, string message)
    {
        lua.PushBoolean(success);
        lua.PushString(message);
        return 2;
    }

    private static void CopyDirectoryRecursive(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (string file in Directory.GetFiles(sourceDirectory))
        {
            string name = Path.GetFileName(file);
            File.Copy(file, destinationDirectory.TrimEnd('/', '\\') + "/" + name, overwrite: true);
        }
        foreach (string directory in Directory.GetDirectories(sourceDirectory))
        {
            string name = Path.GetFileName(directory);
            CopyDirectoryRecursive(directory, destinationDirectory.TrimEnd('/', '\\') + "/" + name);
        }
    }
}
