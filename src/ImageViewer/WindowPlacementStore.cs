using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;

namespace ImageViewer;

internal sealed record WindowPlacement(double Left, double Top, double Width, double Height, bool Maximized)
{
    public bool IsValid => double.IsFinite(Left) && double.IsFinite(Top) && double.IsFinite(Width) &&
        double.IsFinite(Height) && Width >= 460 && Height >= 300 && Width < 30000 && Height < 30000 &&
        Math.Abs(Left) < 100000 && Math.Abs(Top) < 100000;
}

internal static class WindowPlacementStore
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LightImageViewer", "window.json");
    public static WindowPlacement? Load(string path)
    {
        try
        {
            var value = JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(path));
            return value?.IsValid == true ? value : null;
        }
        catch { return null; }
    }
    public static void Save(string path, WindowPlacement value)
    {
        if (!value.IsValid) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value));
            File.Move(temp, path, true);
        }
        catch (Exception error) { ErrorLog.Write(error); }
    }
}

internal static class NativeWindow
{
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref Rect rect, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

    public static void CoverMonitor(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (GetMonitorInfo(MonitorFromWindow(handle, 2), ref info))
            SetWindowPos(handle, IntPtr.Zero, info.Monitor.Left, info.Monitor.Top,
                info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top, 0x24);
    }

    public static WindowPlacement KeepVisible(WindowPlacement value)
    {
        var rect = new Rect { Left = (int)value.Left, Top = (int)value.Top,
            Right = (int)(value.Left + value.Width), Bottom = (int)(value.Top + value.Height) };
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromRect(ref rect, 2), ref info)) return value;
        var area = info.Work;
        var width = Math.Min(value.Width, area.Right - area.Left);
        var height = Math.Min(value.Height, area.Bottom - area.Top);
        return value with { Left = Math.Clamp(value.Left, area.Left, area.Right - width),
            Top = Math.Clamp(value.Top, area.Top, area.Bottom - height), Width = width, Height = height };
    }
}
