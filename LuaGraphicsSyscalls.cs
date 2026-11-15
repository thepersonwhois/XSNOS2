using System;
using System.Drawing;
using Cosmos.Executable.Lua;
using Cosmos.Kernel.System.Graphics;
using Cosmos.Kernel.System.Graphics.Fonts;
using Cosmos.Kernel.System.Input;
using System.Threading;

namespace XSNOS;

internal static class LuaGraphicsSyscalls
{
    private static readonly object Gate = new();
    private static Canvas? _canvas;

    public static int Size(ILuaState lua)
    {
        Canvas canvas = GetCanvas();
        lua.PushInteger(canvas.Width);
        lua.PushInteger(canvas.Height);
        return 2;
    }

    public static int Clear(ILuaState lua)
    {
        lock (Gate)
        {
            GetCanvas().Clear(ReadColor(lua, 1));
        }
        return 0;
    }

    public static int Pixel(ILuaState lua)
    {
        lock (Gate)
        {
            GetCanvas().DrawPoint(ReadColor(lua, 3), (int)lua.ToInteger(1), (int)lua.ToInteger(2));
        }
        return 0;
    }

    public static int Line(ILuaState lua)
    {
        lock (Gate)
        {
            GetCanvas().DrawLine(
                ReadColor(lua, 5),
                (int)lua.ToInteger(1),
                (int)lua.ToInteger(2),
                (int)lua.ToInteger(3),
                (int)lua.ToInteger(4));
        }
        return 0;
    }

    public static int Rectangle(ILuaState lua)
    {
        int x = (int)lua.ToInteger(1);
        int y = (int)lua.ToInteger(2);
        int width = (int)lua.ToInteger(3);
        int height = (int)lua.ToInteger(4);
        Color color = ReadColor(lua, 5);
        lock (Gate)
        {
            if (lua.ToBoolean(8))
            {
                GetCanvas().DrawFilledRectangle(color, x, y, width, height);
            }
            else
            {
                GetCanvas().DrawRectangle(color, x, y, width, height);
            }
        }
        return 0;
    }

    public static int Text(ILuaState lua)
    {
        string text = LuaText.Decode(lua.ToString(3) ?? string.Empty);
        lock (Gate)
        {
            GetCanvas().DrawString(text, PCScreenFont.DefaultFont, ReadColor(lua, 4),
                (int)lua.ToInteger(1), (int)lua.ToInteger(2));
        }
        return 0;
    }

    public static int Present(ILuaState lua)
    {
        lock (Gate)
        {
            GetCanvas().Display();
        }
        return 0;
    }

    public static int Wait(ILuaState lua)
    {
        int milliseconds = (int)Math.Clamp(lua.ToInteger(1), 0, 1000);
        Thread.Sleep(milliseconds);
        return 0;
    }

    public static int ReadKey(ILuaState lua)
    {
        if (!KeyboardManager.TryReadKey(out KeyEvent? key) || key is null)
        {
            lua.PushNil();
            return 1;
        }

        lua.PushString(key.Key.ToString());
        lua.PushString(key.KeyChar == '\0' || char.IsControl(key.KeyChar)
            ? string.Empty
            : LuaText.Encode(key.KeyChar.ToString()));
        lua.PushBoolean(key.Modifiers.HasFlag(ConsoleModifiers.Control));
        return 3;
    }

    public static int ReadMouse(ILuaState lua)
    {
        Canvas canvas = GetCanvas();
        MouseManager.SetScreenSize(canvas.Width, canvas.Height);
        lua.PushInteger(MouseManager.X);
        lua.PushInteger(MouseManager.Y);
        lua.PushBoolean(MouseManager.LeftButton);
        lua.PushBoolean(MouseManager.MiddleButton);
        lua.PushBoolean(MouseManager.RightButton);
        return 5;
    }

    private static Canvas GetCanvas()
    {
        lock (Gate)
        {
            _canvas ??= Canvas.GetFullScreen();
            return _canvas;
        }
    }

    private static Color ReadColor(ILuaState lua, int index)
    {
        int red = (int)Math.Clamp(lua.ToInteger(index), 0, 255);
        int green = (int)Math.Clamp(lua.ToInteger(index + 1), 0, 255);
        int blue = (int)Math.Clamp(lua.ToInteger(index + 2), 0, 255);
        return Color.FromArgb(red, green, blue);
    }
}
