using System.Runtime.InteropServices;

namespace SCPSL.ModLaunch;

/// <summary>Give parameter-driven launches a console while keeping the GUI apphost's prerequisite dialog.</summary>
internal static class NativeConsole
{
    internal static void Initialize()
    {
        int[] ids = [-10, -11, -12];
        var inherited = ids.Select(id => (Id: id, Handle: GetStdHandle(id))).ToArray();
        var redirected = inherited.Where(item => item.Handle != IntPtr.Zero && item.Handle != new IntPtr(-1) &&
            GetFileType(item.Handle) != 0 && !GetConsoleMode(item.Handle, out _)).ToArray();
        if (GetConsoleWindow() == IntPtr.Zero && !AttachConsole(unchecked((uint)-1)) && redirected.Length == 0)
            _ = AllocConsole();
        // Attaching a console must not replace a caller's redirected log/input streams.
        foreach (var item in redirected) _ = SetStdHandle(item.Id, item.Handle);
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetStdHandle(int id);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetFileType(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(IntPtr handle, out uint mode);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetStdHandle(int id, IntPtr handle);
}
