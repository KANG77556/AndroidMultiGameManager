using System.Runtime.InteropServices;

namespace AndroidMultiGameManager;

public static class SystemMetricsService
{
    private static ulong _prevIdle;
    private static ulong _prevKernel;
    private static ulong _prevUser;
    private static bool _hasPrevious;

    public static SystemMetrics Read()
    {
        var cpu = ReadCpuPercent();
        var mem = new MEMORYSTATUSEX();
        mem.dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
        if (!GlobalMemoryStatusEx(ref mem))
            throw new InvalidOperationException("메모리 정보를 읽지 못했습니다.");

        var total = mem.ullTotalPhys / 1024d / 1024d / 1024d;
        var available = mem.ullAvailPhys / 1024d / 1024d / 1024d;
        var used = total - available;

        return new SystemMetrics(cpu, mem.dwMemoryLoad, used, total);
    }

    private static double ReadCpuPercent()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
            return 0;

        var i = ToUInt64(idle);
        var k = ToUInt64(kernel);
        var u = ToUInt64(user);

        if (!_hasPrevious)
        {
            _prevIdle = i;
            _prevKernel = k;
            _prevUser = u;
            _hasPrevious = true;
            return 0;
        }

        var idleDiff = i - _prevIdle;
        var kernelDiff = k - _prevKernel;
        var userDiff = u - _prevUser;

        _prevIdle = i;
        _prevKernel = k;
        _prevUser = u;

        var total = kernelDiff + userDiff;
        if (total == 0) return 0;
        return Math.Clamp((total - idleDiff) * 100d / total, 0, 100);
    }

    private static ulong ToUInt64(FILETIME time) =>
        ((ulong)time.dwHighDateTime << 32) | time.dwLowDateTime;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }
}

public sealed record SystemMetrics(double CpuPercent, double MemoryPercent, double UsedMemoryGb, double TotalMemoryGb);