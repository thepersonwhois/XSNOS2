using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cosmos.Executable.Lua;

namespace XSNOS;

internal sealed class LuaProcessManager
{
    private readonly object _gate = new();
    private readonly Dictionary<int, LuaProcess> _processes = new();
    private int _nextProcessId = 1;

    public int Spawn(ILuaState lua, string workingDirectory, Func<string, LuaInterpreter> createInterpreter)
    {
        string path;
        try
        {
            path = ResolvePath(workingDirectory, LuaText.Decode(lua.ToString(1) ?? string.Empty));
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                lua.PushNil();
                lua.PushString("no such Lua program");
                return 2;
            }
        }
        catch (Exception error)
        {
            lua.PushNil();
            lua.PushString(error.Message);
            return 2;
        }

        LuaProcess process;
        lock (_gate)
        {
            int processId = _nextProcessId++;
            process = new LuaProcess(processId, path);
            _processes.Add(processId, process);
            process.Thread = new Thread(() => Run(process, workingDirectory, createInterpreter));
            try
            {
                process.Thread.Start();
            }
            catch (Exception error)
            {
                _processes.Remove(processId);
                lua.PushNil();
                lua.PushString(error.Message);
                return 2;
            }
        }

        lua.PushInteger(process.Id);
        return 1;
    }

    public int List(ILuaState lua)
    {
        LuaProcess[] processes;
        lock (_gate)
        {
            processes = new LuaProcess[_processes.Count];
            _processes.Values.CopyTo(processes, 0);
        }

        lua.CreateTable(processes.Length, 0);
        for (int index = 0; index < processes.Length; index++)
        {
            LuaProcess process = processes[index];
            lock (process.Gate)
            {
                lua.CreateTable(0, 4);
                lua.PushInteger(process.Id);
                lua.SetField(-2, "pid");
                lua.PushString(process.Path);
                lua.SetField(-2, "path");
                lua.PushString(process.State);
                lua.SetField(-2, "state");
                if (process.Error is not null)
                {
                    lua.PushString(process.Error);
                    lua.SetField(-2, "error");
                }
                lua.RawSetI(-2, index + 1);
            }
        }

        return 1;
    }

    public int Wait(ILuaState lua)
    {
        long processValue = lua.ToInteger(1);
        if (processValue < 1 || processValue > int.MaxValue)
        {
            lua.PushBoolean(false);
            lua.PushString("no such process");
            return 2;
        }

        int processId = (int)processValue;
        LuaProcess? process;
        lock (_gate)
        {
            _processes.TryGetValue(processId, out process);
        }

        if (process is null)
        {
            lua.PushBoolean(false);
            lua.PushString("no such process");
            return 2;
        }

        Thread? thread = process.Thread;
        if (thread is null)
        {
            lua.PushBoolean(false);
            lua.PushString("process has not started");
            return 2;
        }

        thread.Join();
        lock (_gate)
        {
            _processes.Remove(processId);
        }

        lock (process.Gate)
        {
            if (process.Error is not null)
            {
                lua.PushBoolean(false);
                lua.PushString(process.Error);
                return 2;
            }

            lua.PushBoolean(true);
            lua.PushInteger(process.ExitCode);
            return 2;
        }
    }

    private static void Run(LuaProcess process, string workingDirectory, Func<string, LuaInterpreter> createInterpreter)
    {
        try
        {
            using LuaInterpreter interpreter = createInterpreter(workingDirectory);
            interpreter.DoFile(process.Path);
            lock (process.Gate)
            {
                process.State = "exited";
            }
        }
        catch (LuaExitException exit)
        {
            lock (process.Gate)
            {
                process.ExitCode = exit.ExitCode;
                process.State = "exited";
            }
        }
        catch (Exception error)
        {
            lock (process.Gate)
            {
                process.Error = error.Message;
                process.State = "faulted";
            }
        }
    }

    private static string ResolvePath(string workingDirectory, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            return path;
        }

        return Path.GetFullPath(Path.Combine(workingDirectory, path));
    }

    private sealed class LuaProcess(int id, string path)
    {
        public readonly object Gate = new();
        public int Id { get; } = id;
        public string Path { get; } = path;
        public Thread? Thread { get; set; }
        public string State { get; set; } = "running";
        public int ExitCode { get; set; }
        public string? Error { get; set; }
    }
}
