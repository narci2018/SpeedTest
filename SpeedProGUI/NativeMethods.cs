using System.Runtime.InteropServices;
internal static class NativeMethods
{
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
