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
    public static string ver = "2.1.0";
    private readonly LuaProcessManager _processManager = new();

    protected override void BeforeRun()
    {
        Console.WriteLine("xsnkrnl {0} OK",ver);
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
        CmdExeDefault("/sys/xsnis.lua",string.Empty);
    }

    private string? ShutdownStorage()
    {
        List<string> errors = [];
        foreach (VfsMount m in VfsManager.Mounts.ToList())
        {
            try
            {
                if (!VfsManager.TryUnmount(m.MountPoint))
                {
                    errors.Add($"failed to unmount {m.MountPoint}");
                }
            }
            catch (Exception error)
            {
                errors.Add($"failed to unmount {m.MountPoint}: {error.Message}");
            }
        }

        foreach (var device in StorageManager.Devices.ToList())
        {
            try
            {
                device.Flush();
            }
            catch (Exception error)
            {
                errors.Add($"failed to flush {device.Name}: {error.Message}");
            }
        }

        return errors.Count == 0 ? null : string.Join("; ", errors);
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

    private LuaInterpreter CreateLuaInterpreter(string? workingDirectory = null)
    {
        LuaInterpreter lua = new()
        {
            WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory(),
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
            luaSTS.PushString(lua.WorkingDirectory);
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
        int spawnProcess(ILuaState luaSTS)
        {
            return _processManager.Spawn(luaSTS, lua.WorkingDirectory, CreateLuaInterpreter);
        }
        int listProcesses(ILuaState luaSTS)
        {
            return _processManager.List(luaSTS);
        }
        int waitProcess(ILuaState luaSTS)
        {
            return _processManager.Wait(luaSTS);
        }
        int shutdown(ILuaState luaSTS)
        {
            string? storageError = ShutdownStorage();
            if (storageError is not null)
            {
                luaSTS.PushBoolean(false);
                luaSTS.PushString(storageError);
                return 2;
            }

            if (!Sys.Power.IsSupported)
            {
                luaSTS.PushBoolean(false);
                luaSTS.PushString("platform does not support power-off");
                return 2;
            }

            Sys.Power.Shutdown();
            return 0;
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
        var state = lua.State;
        add(state,screenHeight,"sys_scrh");
        add(state,screenWidth,"sys_scrw");
        add(state,green,"sys_green");
        add(state,notgreen,"sys_white");
        add(state,getversion,"sys_ver");
        add(state,listDevices,"sys_lsdev");
        add(state,getCurrentDirectory,"sys_cwd");
        add(state,sts => LuaCommandSyscalls.ListDirectory(sts, lua),"sys_listdir");
        add(state,sts => LuaCommandSyscalls.MakeDirectory(sts, lua),"sys_mkdir");
        add(state,sts => LuaCommandSyscalls.ChangeDirectory(sts, lua),"sys_chdir");
        add(state,sts => LuaCommandSyscalls.PathKind(sts, lua),"sys_pathkind");
        add(state,sts => LuaCommandSyscalls.RemovePath(sts, lua),"sys_remove");
        add(state,sts => LuaCommandSyscalls.CopyPath(sts, lua),"sys_copy");
        add(state,LuaCommandSyscalls.ListPartitions,"sys_partitions");
        add(state,sts => LuaCommandSyscalls.FormatFat(sts, lua),"sys_mkfat");
        add(state,readKey,"sys_readkey");
        add(state,setCursorPosition,"sys_goto");
        add(state,clearConsole,"sys_clear");
        add(state,setCursorVisible,"sys_cursor");
        add(state,writeAtAndMoveCursor,"sys_putat");
        add(state,getPartitionInfo,"sys_prtinfo");
        add(state,setPartitionScheme,"sys_prtscheme");
        add(state,createPartition,"sys_partition");
        add(state,sts => LuaCommandSyscalls.MountDrive(sts, lua),"sys_mount");
        add(state,spawnProcess,"sys_spawn");
        add(state,listProcesses,"sys_processes");
        add(state,waitProcess,"sys_wait");
        add(state,shutdown,"sys_shutdown");
        add(state,LuaGraphicsSyscalls.Size,"sys_gui_size");
        add(state,LuaGraphicsSyscalls.Clear,"sys_gui_clear");
        add(state,LuaGraphicsSyscalls.Pixel,"sys_gui_pixel");
        add(state,LuaGraphicsSyscalls.Line,"sys_gui_line");
        add(state,LuaGraphicsSyscalls.Rectangle,"sys_gui_rect");
        add(state,LuaGraphicsSyscalls.Text,"sys_gui_text");
        add(state,LuaGraphicsSyscalls.Present,"sys_gui_present");
        add(state,LuaGraphicsSyscalls.Wait,"sys_gui_wait");
        add(state,LuaGraphicsSyscalls.ReadKey,"sys_gui_key");
        add(state,LuaGraphicsSyscalls.ReadMouse,"sys_gui_mouse");
        return lua;
    }

    private void CmdExeDefault(string commandToken, string argsString)
    {
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

        using LuaInterpreter lua = CreateLuaInterpreter();
        try
        {
            lua.DoFile(real, SplitArguments(argsString));
        }
        catch (LuaException e)
        {
            Console.WriteLine("lua: " + e.Message);
        }
    }

    private static string ResolveLuaCommand(string commandToken)
    {
        return commandToken;
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
