using System.Runtime.InteropServices;

namespace nVielLauncher.Services;

public readonly record struct SystemSample(double CpuPercent, long UsedMemoryMb, long TotalMemoryMb);

/// <summary>
/// Tiny dependency-free system monitor: CPU load via GetSystemTimes and RAM via GlobalMemoryStatusEx.
/// </summary>
public sealed class SystemMonitor
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
        public readonly ulong Value => ((ulong)High << 32) | Low;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [StructLayout(LayoutKind.Sequential)]
    private sealed class MemoryStatus
    {
        public uint Length = (uint)Marshal.SizeOf<MemoryStatus>();
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatus buffer);

    private ulong _prevIdle;
    private ulong _prevKernel;
    private ulong _prevUser;
    private bool _hasPrevious;

    public SystemSample Sample()
    {
        var cpu = SampleCpu();

        long totalMb = 0;
        long usedMb = 0;

        try
        {
            var memory = new MemoryStatus();
            if (GlobalMemoryStatusEx(memory) && memory.TotalPhys > 0)
            {
                totalMb = (long)(memory.TotalPhys / 1024 / 1024);
                usedMb = (long)((memory.TotalPhys - memory.AvailPhys) / 1024 / 1024);
            }
        }
        catch
        {
            // ignored — fall back below
        }

        if (totalMb <= 0)
        {
            try
            {
                totalMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024;
                usedMb = GC.GetTotalMemory(false) / 1024 / 1024;
            }
            catch
            {
                totalMb = 0;
            }
        }

        return new SystemSample(cpu, usedMb, totalMb);
    }

    public static long TotalPhysicalMemoryMb
    {
        get
        {
            try
            {
                var memory = new MemoryStatus();
                if (GlobalMemoryStatusEx(memory) && memory.TotalPhys > 0)
                    return (long)(memory.TotalPhys / 1024 / 1024);
            }
            catch
            {
                // ignored
            }

            try
            {
                return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024;
            }
            catch
            {
                return 0;
            }
        }
    }

    private double SampleCpu()
    {
        try
        {
            if (!GetSystemTimes(out var idle, out var kernel, out var user))
                return 0;

            var idleValue = idle.Value;
            var kernelValue = kernel.Value;
            var userValue = user.Value;

            if (!_hasPrevious)
            {
                _prevIdle = idleValue;
                _prevKernel = kernelValue;
                _prevUser = userValue;
                _hasPrevious = true;
                return 0;
            }

            var idleDelta = idleValue - _prevIdle;
            var totalDelta = (kernelValue - _prevKernel) + (userValue - _prevUser);

            _prevIdle = idleValue;
            _prevKernel = kernelValue;
            _prevUser = userValue;

            if (totalDelta == 0)
                return 0;

            return Math.Clamp((totalDelta - idleDelta) * 100.0 / totalDelta, 0, 100);
        }
        catch
        {
            return 0;
        }
    }
}
