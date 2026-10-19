using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sys = Cosmos.Kernel.System;
using Cosmos.Kernel.HAL.Devices.Storage;
using Cosmos.Kernel.System.FileSystem;
using Cosmos.Kernel.System.FileSystem.Fat;
using Cosmos.Kernel.System.Storage;
using Cosmos.Executable.Lua;

namespace XSNOS;

public class Kernel : Sys.Kernel
{
    public static string ver = "2.0.1b";
    private string _diskLabel = "disk";

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

        var boot = StorageManager.Partitions[0];
        _diskLabel = boot.Name;

        if (VfsManager.TryMount("fat", boot, MountFlags.None, "/" + _diskLabel, out VfsMount? mount))
        {
            Directory.SetCurrentDirectory(mount.MountPoint);
            Console.WriteLine($"mounted {_diskLabel}:data at {mount.MountPoint}");
        }
        else
        {
            Console.WriteLine($"failed to mount {_diskLabel}. staying at the virtual root.");
        }
    }

    protected override void Run()
    {
        Console.Write(ToVirtualPath(Directory.GetCurrentDirectory()) + "> ");
        var input = Console.ReadLine();

        if (string.IsNullOrEmpty(input))
            return;

        string trimmed = input.TrimStart();
        int spaceIdx = trimmed.IndexOf(' ');
        string cmd = (spaceIdx < 0 ? trimmed : trimmed.Substring(0, spaceIdx)).ToLower();
        string argsString = spaceIdx < 0 ? string.Empty : trimmed.Substring(spaceIdx + 1).Trim();

        switch (cmd)
        {
            case "hlpcm":
                Console.WriteLine("commands:");
                Console.WriteLine("  hlpcm     - what, why on earth do you need help with this?");
                Console.WriteLine("  clear     - clears the screen");
                Console.WriteLine("  hltpc     - halts the processor");
                Console.WriteLine("  lsdev     - lists block devices.");
                Console.WriteLine("  lsdir     - lists a directory. lsdir [path]");
                Console.WriteLine("  mkprt     - partitions a disk");
                Console.WriteLine("  mkfat     - formats a partition as FAT and (re)mounts it");
                Console.WriteLine("  mkdir     - makes a directory. mkdir <path>");
                Console.WriteLine("  chdir     - changes directory. chdir [path]");
                Console.WriteLine("  rmdir     - removes a file or directory. rmdir <path>");
                Console.WriteLine("  xcopy     - copies a file or directory. xcopy <src> <dst>");
                Console.WriteLine("  txted     - text editor. txted <path>");
                Console.WriteLine("  luash     - lua 5.5 repl. luash");
                Console.WriteLine();
                Console.WriteLine("paths look like: " + _diskLabel + ":data\\some\\dir\\file.txt");
                break;

            case "clear":
                Console.Clear();
                break;

            case "hltpc":
                Console.WriteLine("flushing disks...");
                ShutdownStorage();
                Console.WriteLine("halting pc...");
                Stop();
                break;

            case "lsdev":
                Console.WriteLine("available block devices:");
                var devices = StorageManager.Devices.ToList();
                for (int i = 0; i < devices.Count; i++)
                {
                    Console.WriteLine($"[{i}] {devices[i].Name}");
                }
                break;

            case "lsdir":
                CmdLsDir(argsString);
                break;

            case "mkprt":
                InteractivePartition();
                break;

            case "mkfat":
                InteractiveFormat();
                break;

            case "mkdir":
                CmdMkDir(argsString);
                break;

            case "chdir":
                CmdChDir(argsString);
                break;

            case "rmdir":
                CmdRmDir(argsString);
                break;

            case "xcopy":
                CmdXCopy(argsString);
                break;

            case "txted":
                CmdTextEdit(argsString);
                break;

            case "luash":
                CmdLuaShell(argsString);
                break;

            default:
                CmdExeDefault(cmd);
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

    private string ToRealPath(string virtualPath)
    {
        if (string.IsNullOrWhiteSpace(virtualPath))
            return Directory.GetCurrentDirectory();

        string path = virtualPath.Trim();
        int colon = path.IndexOf(':');

        if (colon < 0)
        {
            return path.Replace('\\', '/');
        }

        string label = path.Substring(0, colon);
        string rest = path.Substring(colon + 1);

        if (rest.StartsWith("data", StringComparison.OrdinalIgnoreCase))
            rest = rest.Substring(4);

        rest = rest.Replace('\\', '/').TrimStart('/');

        string root = "/" + label;
        return rest.Length == 0 ? root : root + "/" + rest;
    }

    private string ToVirtualPath(string realPath)
    {
        string path = realPath.Replace('\\', '/');
        if (!path.StartsWith("/"))
            path = "/" + path;

        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0)
            return "(root)";

        string label = parts[0];
        string rest = string.Join("\\", parts, 1, parts.Length - 1);
        return rest.Length == 0 ? $"{label}:data" : $"{label}:data\\{rest}";
    }

    private static string CombineReal(string baseDir, string name) => baseDir.TrimEnd('/') + "/" + name;

    private void CmdMkDir(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            Console.WriteLine($"usage: mkdir <{_diskLabel}:data\\path>");
            return;
        }

        string real = ToRealPath(arg);
        try
        {
            Directory.CreateDirectory(real);
            Console.WriteLine("created " + ToVirtualPath(real));
        }
        catch (Exception e)
        {
            Console.WriteLine("mkdir: " + e.Message);
        }
    }

    private void CmdLsDir(string arg)
    {
        string real = ToRealPath(arg);

        try
        {
            if (!Directory.Exists(real))
            {
                Console.WriteLine("lsdir: no such directory");
                return;
            }

            string[] directories = Directory.GetDirectories(real);
            string[] files = Directory.GetFiles(real);

            Console.WriteLine(ToVirtualPath(real));

            foreach (string dir in directories.OrderBy(d => d))
            {
                Console.WriteLine($"  <dir>  {Path.GetFileName(dir)}");
            }

            foreach (string file in files.OrderBy(f => f))
            {
                long size = 0;
                try { size = new FileInfo(file).Length; } catch { Console.WriteLine("You saw this???"); }
                Console.WriteLine($"  {size,8}  {Path.GetFileName(file)}");
            }

            if (directories.Length == 0 && files.Length == 0)
            {
                Console.WriteLine("  (empty)");
            }
        }
        catch (Exception e)
        {
            Console.WriteLine("lsdir: " + e.Message);
        }
    }

    private void CmdChDir(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            Console.WriteLine(ToVirtualPath(Directory.GetCurrentDirectory()));
            return;
        }

        string real = ToRealPath(arg);
        try
        {
            if (!Directory.Exists(real))
            {
                Console.WriteLine("chdir: no such directory");
                return;
            }

            Directory.SetCurrentDirectory(real);
        }
        catch (Exception e)
        {
            Console.WriteLine("chdir: " + e.Message);
        }
    }

    private void CmdRmDir(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            Console.WriteLine($"usage: rmdir <{_diskLabel}:data\\path>");
            return;
        }

        string real = ToRealPath(arg);

        try
        {
            if (Directory.Exists(real))
            {
                Console.Write($"remove directory {ToVirtualPath(real)} and everything in it? (y/n) ");
                if ((Console.ReadLine() ?? "").Trim().ToLower() != "y")
                {
                    Console.WriteLine("cancelled.");
                    return;
                }

                Directory.Delete(real, recursive: true);
                Console.WriteLine("removed directory " + ToVirtualPath(real));
            }
            else if (File.Exists(real))
            {
                File.Delete(real);
                Console.WriteLine("removed file " + ToVirtualPath(real));
            }
            else
            {
                Console.WriteLine("rmdir: not found");
            }
        }
        catch (Exception e)
        {
            Console.WriteLine("rmdir: " + e.Message);
        }
    }

    private void CmdXCopy(string argsString)
    {
        string[] parts = argsString.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            Console.WriteLine($"usage: xcopy <src> <dst>   e.g. xcopy {_diskLabel}:data\\a.txt {_diskLabel}:data\\backup\\a.txt");
            return;
        }

        string src = ToRealPath(parts[0]);
        string dst = ToRealPath(parts[1]);

        try
        {
            if (Directory.Exists(src))
            {
                CopyDirectoryRecursive(src, dst);
                Console.WriteLine("copied directory to " + ToVirtualPath(dst));
            }
            else if (File.Exists(src))
            {
                File.Copy(src, dst, overwrite: true);
                Console.WriteLine("copied file to " + ToVirtualPath(dst));
            }
            else
            {
                Console.WriteLine("xcopy: source not found");
            }
        }
        catch (Exception e)
        {
            Console.WriteLine("xcopy: " + e.Message);
        }
    }

    private static void CopyDirectoryRecursive(string srcDir, string dstDir)
    {
        Directory.CreateDirectory(dstDir);

        foreach (string file in Directory.GetFiles(srcDir))
        {
            string name = Path.GetFileName(file);
            File.Copy(file, CombineReal(dstDir, name), overwrite: true);
        }

        foreach (string dir in Directory.GetDirectories(srcDir))
        {
            string name = Path.GetFileName(dir);
            CopyDirectoryRecursive(dir, CombineReal(dstDir, name));
        }
    }

    private void InteractiveFormat()
    {
        var parts = StorageManager.Partitions;
        if (parts.Count == 0)
        {
            Console.WriteLine("mkfat: no partitions found. run mkprt first.");
            return;
        }

        Console.WriteLine("available partitions:");
        for (int i = 0; i < parts.Count; i++)
        {
            Console.WriteLine($"[{i}] {parts[i].Name}");
        }

        Console.Write("partition to format: ");
        if (!int.TryParse(Console.ReadLine(), out int idx) || idx < 0 || idx >= parts.Count)
        {
            Console.WriteLine("mkfat: invalid selection.");
            return;
        }

        Console.Write("volume label (max 11 chars, blank for \"COSMOS\"): ");
        string label = Console.ReadLine() ?? "";
        if (string.IsNullOrWhiteSpace(label)) label = "COSMOS";
        label = label.Length > 11 ? label.Substring(0, 11) : label.PadRight(11);

        Console.Write("fat type [12/16/32, default 32]: ");
        string typeStr = (Console.ReadLine() ?? "").Trim();
        FatType type = typeStr switch
        {
            "12" => FatType.Fat12,
            "16" => FatType.Fat16,
            _ => FatType.Fat32,
        };

        var target = parts[idx];

        VfsMount? existing = VfsManager.Mounts.FirstOrDefault(m => m.Partition == target);
        if (existing != null)
        {
            VfsManager.TryUnmount(existing.MountPoint);
        }

        FatFormatOptions options = new() { Type = type, VolumeLabel = label };

        Console.Write("formatting (this destroys everything on the partition)... ");
        if (!VfsManager.TryFormat("fat", target, options))
        {
            Console.WriteLine("failed.");
            return;
        }
        Console.WriteLine("done.");

        string mountLabel = target.Name;
        if (VfsManager.TryMount("fat", target, MountFlags.None, "/" + mountLabel, out VfsMount? mount))
        {
            _diskLabel = mountLabel;
            Directory.SetCurrentDirectory(mount.MountPoint);
            Console.WriteLine("remounted at " + ToVirtualPath(mount.MountPoint));
        }
        else
        {
            Console.WriteLine($"mkfat: formatted {mountLabel}, but mounting it at /{mountLabel} failed.");
            Console.WriteLine("it's formatted and on disk, but unreachable until you can mount it - check for a name collision with an existing mount.");
        }
    }

    private void CmdTextEdit(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            Console.WriteLine($"usage: txted <{_diskLabel}:data\\path>");
            return;
        }

        string real = ToRealPath(arg);
        List<string> lines;

        try
        {
            lines = File.Exists(real)
                ? new List<string>(File.ReadAllLines(real))
                : new List<string>();
        }
        catch (Exception e)
        {
            Console.WriteLine("txted: " + e.Message);
            return;
        }

        if (lines.Count == 0)
            lines.Add(string.Empty);

        int row = 0, col = 0, topRow = 0;
        bool dirty = false;
        bool running = true;

        int screenRows = Math.Max(Console.WindowHeight - 2, 5);
        int screenCols = Math.Max(Console.WindowWidth, 20);

        Console.CursorVisible = true;

        void Redraw()
        {
            Console.Clear();
            Console.SetCursorPosition(0, 0);
            Console.Write($"txted  {ToVirtualPath(real)}{(dirty ? " *" : "")}   ^S save   ^Q quit");

            for (int i = 0; i < screenRows; i++)
            {
                int lineIdx = topRow + i;
                Console.SetCursorPosition(0, i + 1);

                if (lineIdx < lines.Count)
                {
                    string text = lines[lineIdx];
                    if (text.Length > screenCols) text = text.Substring(0, screenCols);
                    Console.Write(text.PadRight(screenCols));
                }
                else
                {
                    Console.Write(new string(' ', screenCols));
                }
            }

            Console.SetCursorPosition(Math.Min(col, screenCols - 1), row - topRow + 1);
        }

        void Save()
        {
            try
            {
                File.WriteAllLines(real, lines);
                dirty = false;
            }
            catch (Exception e)
            {
                Console.SetCursorPosition(0, screenRows + 1);
                Console.Write("save failed: " + e.Message);
                Console.ReadKey(true);
            }
        }

        Redraw();

        while (running)
        {
            ConsoleKeyInfo key = Console.ReadKey(true);
            bool ctrl = key.Modifiers.HasFlag(ConsoleModifiers.Control);

            if (ctrl && key.Key == ConsoleKey.S)
            {
                Save();
                Redraw();
                continue;
            }

            if (ctrl && key.Key == ConsoleKey.Q)
            {
                running = false;
                continue;
            }

            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                    if (row > 0) { row--; col = Math.Min(col, lines[row].Length); }
                    break;

                case ConsoleKey.DownArrow:
                    if (row < lines.Count - 1) { row++; col = Math.Min(col, lines[row].Length); }
                    break;

                case ConsoleKey.LeftArrow:
                    if (col > 0) col--;
                    else if (row > 0) { row--; col = lines[row].Length; }
                    break;

                case ConsoleKey.RightArrow:
                    if (col < lines[row].Length) col++;
                    else if (row < lines.Count - 1) { row++; col = 0; }
                    break;

                case ConsoleKey.Home:
                    col = 0;
                    break;

                case ConsoleKey.End:
                    col = lines[row].Length;
                    break;

                case ConsoleKey.Enter:
                    {
                        string tail = lines[row].Substring(col);
                        lines[row] = lines[row].Substring(0, col);
                        lines.Insert(row + 1, tail);
                        row++;
                        col = 0;
                        dirty = true;
                    }
                    break;

                case ConsoleKey.Backspace:
                    if (col > 0)
                    {
                        lines[row] = lines[row].Remove(col - 1, 1);
                        col--;
                        dirty = true;
                    }
                    else if (row > 0)
                    {
                        int prevLen = lines[row - 1].Length;
                        lines[row - 1] += lines[row];
                        lines.RemoveAt(row);
                        row--;
                        col = prevLen;
                        dirty = true;
                    }
                    break;

                case ConsoleKey.Delete:
                    if (col < lines[row].Length)
                    {
                        lines[row] = lines[row].Remove(col, 1);
                        dirty = true;
                    }
                    else if (row < lines.Count - 1)
                    {
                        lines[row] += lines[row + 1];
                        lines.RemoveAt(row + 1);
                        dirty = true;
                    }
                    break;

                default:
                    if (!char.IsControl(key.KeyChar))
                    {
                        lines[row] = lines[row].Insert(col, key.KeyChar.ToString());
                        col++;
                        dirty = true;
                    }
                    break;
            }

            if (row < topRow) topRow = row;
            if (row >= topRow + screenRows) topRow = row - screenRows + 1;

            Redraw();
        }

        Console.Clear();
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

        string real = ToRealPath(arg);
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

    private void CmdExeDefault(string arg)
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
        if (string.IsNullOrWhiteSpace(arg))
        {
            Console.WriteLine("E: you typed nothing. no file or command.");
            return;
        }

        string real = ToRealPath(arg);
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
            lua.DoFile(real);
        }
        catch (LuaException e)
        {
            Console.WriteLine("lua: " + e.Message);
        }
    }

    private void InteractivePartition()
    {
        IBlockDevice? disk = StorageManager.PrimaryDevice;
        if (disk is null)
        {
            Console.WriteLine("error: no primary disk found to partition.");
            return;
        }

        Console.WriteLine($"found Primary Disk: {disk.Name}");
        Console.Write("checking for partition scheme");

        if (Gpt.IsGpt(disk))
        {
            Console.WriteLine("found guid partition table");
        }
        else if (Mbr.IsMbr(disk))
        {
            Console.WriteLine("found good ol' mbr.");
        }
        else
        {
            Console.WriteLine("no partition table (raw).");
        }

        Console.WriteLine("\n[1] create guid");
        Console.WriteLine("[2] create the master boot record");
        Console.WriteLine("[3] keep current scheme and just nuke the partitions");
        Console.Write("choose an option: ");
        string layoutChoice = Console.ReadLine();

        if (layoutChoice == "1")
            Gpt.Create(disk);
        else if (layoutChoice == "2")
            Mbr.Create(disk);
        else if (layoutChoice != "3")
        {
            Console.WriteLine("invalid option.");
            return;
        }

        Console.WriteLine("\n--- partition sizing ---");
        Console.Write("enter start sector (standard default is 2048 for some reason): ");
        if (!ulong.TryParse(Console.ReadLine(), out ulong startSector))
        {
            Console.WriteLine("invalid number.");
            return;
        }

        Console.Write("enter sector count (example: figure it out yourself): ");
        if (!ulong.TryParse(Console.ReadLine(), out ulong sectorCount))
        {
            Console.WriteLine("invalid number.");
            return;
        }

        if (startSector >= disk.BlockCount)
        {
            Console.WriteLine($"start sector {startSector} is past the end of the disk ({disk.BlockCount} sectors total).");
            return;
        }

        ulong available = disk.BlockCount - startSector;
        if (sectorCount > available)
        {
            ulong diskMiB = disk.BlockCount * disk.BlockSize / (1024 * 1024);
            Console.WriteLine($"that's {sectorCount} sectors, but only {available} are free after sector {startSector} on a {disk.BlockCount}-sector ({diskMiB} MiB) disk.");
            Console.WriteLine("pick a smaller sector count, or make a bigger disk.img.");
            return;
        }

        Console.WriteLine("\nwriting partition layout...");

        bool success = PartitionManager.Create(
            disk,
            startSector: startSector,
            sectorCount: sectorCount,
            mbrSystemId: 0x0C,
            gptType: Gpt.BasicDataPartitionType
        );

        if (!success)
        {
            Console.WriteLine("failed to create the partition entry.");
            return;
        }

        Console.Write("rescanning partition tables to alert kernel... ");
        StorageManager.RescanPartitions(disk);
        Console.WriteLine("done.");
        Console.WriteLine("partition created. run mkfat to format it.");
    }
}