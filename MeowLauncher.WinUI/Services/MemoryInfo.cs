using System.Runtime.InteropServices;

namespace MeowLauncher.WinUI.Services;

/// <summary>本机物理内存：总量与当前可用量（GlobalMemoryStatusEx），给内存条形图用。</summary>
internal static class HostMemory
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private sealed class MemoryStatusEx
    {
        public uint dwLength = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx buffer);

    /// <summary>读不到时返回 (0, 0)，调用方据此隐藏条形图。单位 MB。</summary>
    public static (double TotalMb, double AvailableMb) Read()
    {
        try
        {
            var status = new MemoryStatusEx();
            if (!GlobalMemoryStatusEx(status)) return (0, 0);
            return (status.ullTotalPhys / 1048576d, status.ullAvailPhys / 1048576d);
        }
        catch
        {
            return (0, 0);
        }
    }
}
