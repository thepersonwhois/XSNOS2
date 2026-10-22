using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Sys = Cosmos.Kernel.System;
using Cosmos.Kernel.HAL.Devices.Storage;
using Cosmos.Kernel.System.FileSystem;
using Cosmos.Kernel.System.FileSystem.Fat;
using Cosmos.Kernel.System.Storage;
using Cosmos.Executable.Lua;

namespace XSNOS;

public class Kernel : Sys.Kernel
{
    public static string ver = "26.10";

    protected override void BeforeRun()
    {
        Console.WriteLine("XSNOS{0} OK",ver);
        FatFileSystemType fat = new();

        if (!VfsManager.RegisterFileSystem("fat", fat))
        {
            Console.WriteLine("The name \"fat\" is already registered.");
            return;
        }

        if (StorageManager.Partitions.Count == 0)
        {
            Console.WriteLine("no (mountable) partitions found.");
            return;
        }

        var partitions = StorageManager.Partitions.ToList();
        Partition boot = FindInstallerMedia(partitions) ?? partitions[0];

        if (VfsManager.TryMount("fat", boot, MountFlags.None, "/", out VfsMount? mount))
        {
            Directory.SetCurrentDirectory(mount.MountPoint);
            Console.WriteLine($"mounted {boot.Name} at {mount.MountPoint}");
        }
        else
        {
            Console.WriteLine($"failed to mount {boot.Name} at /.");
        }
    }

    private static Partition? FindInstallerMedia(List<Partition> partitions)
    {
        foreach (Partition partition in partitions)
        {
            VfsMount? probe = null;
            try
            {
                if (!VfsManager.TryMount("fat", partition, MountFlags.None, "/", out probe) || probe is null)
                {
                    continue;
                }

                if (File.Exists("/sys/installer.flag"))
                {
                    VfsManager.TryUnmount("/");
                    return partition;
                }
            }
            catch
            {
            }
            finally
            {
                if (VfsManager.Mounts.Any(mount => mount.MountPoint == "/"))
                {
                    VfsManager.TryUnmount("/");
                }
            }
        }

        return null;
    }

    protected override void Run()
    {
        Console.Write(Directory.GetCurrentDirectory() + "> ");
        var input = Console.ReadLine();

        if (string.IsNullOrEmpty(input))
            return;

        string trimmed = input.TrimStart();
        int spaceIdx = trimmed.IndexOf(' ');
        string commandToken = spaceIdx < 0 ? trimmed : trimmed.Substring(0, spaceIdx);
        string cmd = commandToken.ToLowerInvariant();
        string argsString = spaceIdx < 0 ? string.Empty : trimmed.Substring(spaceIdx + 1).Trim();

        switch (cmd)
        {
            case "clear":
                Console.Clear();
                break;

            case "hltpc":
                Console.WriteLine("flushing disks...");
                ShutdownStorage();
                Console.WriteLine("halting pc...");
                Stop();
                break;

            case "luash":
                CmdLuaShell(argsString);
                break;

            default:
                CmdExeDefault(commandToken, argsString);
                break;
        }
    }

    private void ShutdownStorage()
    {
        foreach (VfsMount m in VfsManager.Mounts.ToList())
        {
            VfsManager.TryUnmount(m.MountPoint);
        }

        foreach (var device in StorageManager.Devices.ToList())
        {
            try
            {
                device.Flush();
            }
            catch
            {
            }
        }
    }

    private void CmdLuaShell(string arg)
    {
        LuaInterpreter lua = new()
        {
            WorkingDirectory = Directory.GetCurrentDirectory(),
        };

        if (string.IsNullOrWhiteSpace(arg))
        {
            Console.WriteLine("lua 5.5 - call os.exit() to leave");
            try
            {
                lua.RunPrompt();
            }
            catch (LuaException e)
            {
                Console.WriteLine("lua: " + e.Message);
            }
            return;
        }

        string real = arg.Trim();
        if (!File.Exists(real))
        {
            Console.WriteLine("luash: no such file");
            return;
        }

        try
        {
            lua.DoFile(real);
        }
        catch (LuaException e)
        {
            Console.WriteLine("lua: " + e.Message);
        }
    }

    private void CmdExeDefault(string commandToken, string argsString)
    {
        LuaInterpreter lua = new()
        {
            WorkingDirectory = Directory.GetCurrentDirectory(),
        };
        void add(ILuaState inter, CSharpFunctionDelegate function, string name)
        {
            inter.PushCSharpFunction(function);
            inter.SetGlobal(name);
        }
        int screenHeight(ILuaState luaSTS)
        {
            luaSTS.PushInteger(Console.WindowHeight);
            return 1;
        }
        int screenWidth(ILuaState luaSTS)
        {
            luaSTS.PushInteger(Console.WindowWidth);
            return 1;
        }
        int green(ILuaState luaSTS)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            return 0;
        }
        int notgreen(ILuaState luaSTS)
        {
            Console.ResetColor();
            return 0;
        }
        int getversion(ILuaState luaSTS)
        {
            luaSTS.PushString(ver);
            return 1;
        }
        int getCurrentDirectory(ILuaState luaSTS)
        {
            luaSTS.PushString(Directory.GetCurrentDirectory());
            return 1;
        }
        int listDevices(ILuaState luaSTS)
        {
            var devices = StorageManager.Devices.ToList();
            luaSTS.CreateTable(devices.Count, 0);
            for (int i = 0; i < devices.Count; i++)
            {
                luaSTS.PushString(devices[i].Name);
                luaSTS.RawSetI(-2, i + 1);
            }
            return 1;
        }
        int readKey(ILuaState luaSTS)
        {
            ConsoleKeyInfo key = Console.ReadKey(true);
            luaSTS.PushString(key.Key.ToString());
            luaSTS.PushString(key.KeyChar == '\0' || char.IsControl(key.KeyChar)
                ? string.Empty
                : LuaText.Encode(key.KeyChar.ToString()));
            luaSTS.PushBoolean(key.Modifiers.HasFlag(ConsoleModifiers.Control));
            return 3;
        }
        int setCursorPosition(ILuaState luaSTS)
        {
            int width = Math.Max(Console.WindowWidth, 1);
            int height = Math.Max(Console.WindowHeight, 1);
            int x = (int)Math.Clamp(luaSTS.ToInteger(1), 0, width - 1);
            int y = (int)Math.Clamp(luaSTS.ToInteger(2), 0, height - 1);
            Console.SetCursorPosition(x, y);
            return 0;
        }
        int clearConsole(ILuaState luaSTS)
        {
            Console.Clear();
            return 0;
        }
        int setCursorVisible(ILuaState luaSTS)
        {
            Console.CursorVisible = luaSTS.ToBoolean(1);
            return 0;
        }
        int writeAtAndMoveCursor(ILuaState luaSTS)
        {
            int width = Math.Max(Console.WindowWidth, 1);
            int height = Math.Max(Console.WindowHeight, 1);
            int x = (int)Math.Clamp(luaSTS.ToInteger(1), 0, width - 1);
            int y = (int)Math.Clamp(luaSTS.ToInteger(2), 0, height - 1);
            string text = LuaText.Decode(luaSTS.ToString(3) ?? string.Empty);
            int cursorX = (int)Math.Clamp(luaSTS.ToInteger(4), 0, width - 1);
            int cursorY = (int)Math.Clamp(luaSTS.ToInteger(5), 0, height - 1);

            Console.SetCursorPosition(x, y);
            Console.Write(text);
            Console.SetCursorPosition(cursorX, cursorY);
            return 0;
        }
        int getPartitionInfo(ILuaState luaSTS)
        {
            IBlockDevice? disk = StorageManager.PrimaryDevice;
            if (disk is null)
            {
                luaSTS.PushNil();
                return 1;
            }

            string scheme = Gpt.IsGpt(disk) ? "gpt" : Mbr.IsMbr(disk) ? "mbr" : "raw";
            luaSTS.CreateTable(0, 4);
            luaSTS.PushString(disk.Name);
            luaSTS.SetField(-2, "name");
            luaSTS.PushInteger((long)disk.BlockCount);
            luaSTS.SetField(-2, "block_count");
            luaSTS.PushInteger((long)disk.BlockSize);
            luaSTS.SetField(-2, "block_size");
            luaSTS.PushString(scheme);
            luaSTS.SetField(-2, "scheme");
            return 1;
        }
        int setPartitionScheme(ILuaState luaSTS)
        {
            IBlockDevice? disk = StorageManager.PrimaryDevice;
            int choice = (int)luaSTS.ToInteger(1);
            if (disk is null)
            {
                luaSTS.PushBoolean(false);
                luaSTS.PushString("no primary disk found to partition");
                return 2;
            }

            try
            {
                string message = choice switch
                {
                    1 => CreateGpt(disk),
                    2 => CreateMbr(disk),
                    3 => "keeping current partition scheme",
                    _ => throw new ArgumentOutOfRangeException(nameof(choice), "invalid layout choice"),
                };
                luaSTS.PushBoolean(true);
                luaSTS.PushString(message);
            }
            catch (Exception e)
            {
                luaSTS.PushBoolean(false);
                luaSTS.PushString(e.Message);
            }
            return 2;
        }
        int createPartition(ILuaState luaSTS)
        {
            IBlockDevice? disk = StorageManager.PrimaryDevice;
            long startValue = luaSTS.ToInteger(1);
            long countValue = luaSTS.ToInteger(2);
            if (disk is null)
            {
                luaSTS.PushBoolean(false);
                luaSTS.PushString("no primary disk found to partition");
                return 2;
            }
            if (startValue < 0 || countValue <= 0)
            {
                luaSTS.PushBoolean(false);
                luaSTS.PushString("start sector must be nonnegative and sector count must be positive");
                return 2;
            }

            ulong startSector = (ulong)startValue;
            ulong sectorCount = (ulong)countValue;
            if (startSector >= disk.BlockCount)
            {
                luaSTS.PushBoolean(false);
                luaSTS.PushString($"start sector {startSector} is past the end of the disk ({disk.BlockCount} sectors total)");
                return 2;
            }

            ulong available = disk.BlockCount - startSector;
            if (sectorCount > available)
            {
                ulong diskMiB = disk.BlockCount * disk.BlockSize / (1024 * 1024);
                luaSTS.PushBoolean(false);
                luaSTS.PushString($"that's {sectorCount} sectors, but only {available} are free after sector {startSector} on a {disk.BlockCount}-sector ({diskMiB} MiB) disk");
                return 2;
            }

            try
            {
                bool success = PartitionManager.Create(
                    disk,
                    startSector: startSector,
                    sectorCount: sectorCount,
                    mbrSystemId: 0x0C,
                    gptType: Gpt.BasicDataPartitionType);
                if (!success)
                {
                    luaSTS.PushBoolean(false);
                    luaSTS.PushString("failed to create the partition entry");
                    return 2;
                }

                StorageManager.RescanPartitions(disk);
                luaSTS.PushBoolean(true);
                luaSTS.PushString("partition created. run mkfat to format it.");
            }
            catch (Exception e)
            {
                luaSTS.PushBoolean(false);
                luaSTS.PushString(e.Message);
            }
            return 2;
        }
        string CreateGpt(IBlockDevice disk)
        {
            Gpt.Create(disk);
            return "created GUID partition table";
        }
        string CreateMbr(IBlockDevice disk)
        {
            Mbr.Create(disk);
            return "created master boot record";
        }
        if (string.IsNullOrWhiteSpace(commandToken))
        {
            Console.WriteLine("E: you typed nothing. no file or command.");
            return;
        }

        string real = ResolveLuaCommand(commandToken.Trim());
        if (!File.Exists(real))
        {
            Console.WriteLine("E: no such file or command found.");
            return;
        }

        try
        {
            var state = lua.State;
            add(state,screenHeight,"sys_scrh");
            add(state,screenWidth,"sys_scrw");
            add(state,green,"sys_green");
            add(state,notgreen,"sys_white");
            add(state,getversion,"sys_ver");
            add(state,listDevices,"sys_lsdev");
            add(state,getCurrentDirectory,"sys_cwd");
            add(state,LuaCommandSyscalls.ListDirectory,"sys_listdir");
            add(state,LuaCommandSyscalls.MakeDirectory,"sys_mkdir");
            add(state,LuaCommandSyscalls.ChangeDirectory,"sys_chdir");
            add(state,LuaCommandSyscalls.PathKind,"sys_pathkind");
            add(state,LuaCommandSyscalls.RemovePath,"sys_remove");
            add(state,LuaCommandSyscalls.CopyPath,"sys_copy");
            add(state,LuaCommandSyscalls.ListPartitions,"sys_partitions");
            add(state,LuaCommandSyscalls.FormatFat,"sys_mkfat");
            add(state,readKey,"sys_readkey");
            add(state,setCursorPosition,"sys_goto");
            add(state,clearConsole,"sys_clear");
            add(state,setCursorVisible,"sys_cursor");
            add(state,writeAtAndMoveCursor,"sys_putat");
            add(state,getPartitionInfo,"sys_prtinfo");
            add(state,setPartitionScheme,"sys_prtscheme");
            add(state,createPartition,"sys_partition");
            add(state,KernelInstaller.ListDisks,"sys_installer_disks");
            add(state,KernelInstaller.Install,"sys_install");
            add(state,LuaCommandSyscalls.MountDrive,"sys_mount");
            lua.DoFile(real, SplitArguments(argsString));
        }
        catch (LuaException e)
        {
            Console.WriteLine("lua: " + e.Message);
        }
    }

    private static string ResolveLuaCommand(string commandToken)
    {
        string path = commandToken.StartsWith('/') ? commandToken : "/uprog/" + commandToken;
        if (File.Exists(path))
        {
            return path;
        }
        return Path.HasExtension(path) ? path : path + ".lua";
    }

    private static string[] SplitArguments(string arguments)
    {
        List<string> result = [];
        StringBuilder current = new();
        bool quoted = false;
        bool escaping = false;

        foreach (char character in arguments)
        {
            if (escaping)
            {
                current.Append(character);
                escaping = false;
            }
            else if (character == '\\' && quoted)
            {
                escaping = true;
            }
            else if (character == '"')
            {
                quoted = !quoted;
            }
            else if (char.IsWhiteSpace(character) && !quoted)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(character);
            }
        }

        if (escaping)
        {
            current.Append('\\');
        }
        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }
        return result.ToArray();
    }

}
