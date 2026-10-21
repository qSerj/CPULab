using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CpuLab.Core;

// The host waits for START until containment has been established.
public sealed class ProcessContainment : IDisposable
{
    private readonly SafeFileHandle? job;
    private int groupId;

    public ProcessContainment()
    {
        if (OperatingSystem.IsWindows())
        {
            job = CreateJobObjectW(IntPtr.Zero, null);
            if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } }; // KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
            {
                int error = Marshal.GetLastWin32Error();
                job.Dispose();
                throw new Win32Exception(error);
            }
        }
        else if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Process supervision supports Windows and Linux.");
    }

    public void AttachReadyHost(Process host)
    {
        if (job is not null)
        {
            if (!AssignProcessToJobObject(job, host.Handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        else
        {
            if (getpgid(host.Id) != host.Id) throw new InvalidOperationException("Host did not establish its own process group.");
            groupId = host.Id;
        }
    }

    public async Task<string> StopAsync()
    {
        if (job is not null)
        {
            if (!TerminateJobObject(job, 137)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var elapsed = Stopwatch.StartNew();
            do
            {
                if (!QueryInformationJobObject(job, 1, out var accounting, (uint)Marshal.SizeOf<Accounting>(), IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (accounting.ActiveProcesses == 0) return "windows-job-empty";
                await Task.Delay(20);
            } while (elapsed.Elapsed < TimeSpan.FromSeconds(5));
            throw new TimeoutException("Windows job still contains active processes after termination.");
        }
        if (groupId > 0) KillLinuxGroup(groupId);
        return "linux-group-kill-requested"; // POSIX kill success is not a wait/reap guarantee.
    }

    public static void PrepareHost()
    {
        if (OperatingSystem.IsLinux() && setsid() == -1) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Unsupported process host platform.");
    }

    public static void KillLinuxGroup(int id)
    {
        if (!OperatingSystem.IsLinux() || id <= 1) throw new InvalidOperationException("Invalid owned Linux group.");
        if (kill(-id, 9) != 0 && Marshal.GetLastWin32Error() != 3) // ESRCH: already gone
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public void Dispose() => job?.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public nuint MinWorkingSet, MaxWorkingSet;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Accounting
    {
        public long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime;
        public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref ExtendedLimits data, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int kind, out Accounting data, uint size, IntPtr returned);
    [DllImport("libc", SetLastError = true)] private static extern int setsid();
    [DllImport("libc", SetLastError = true)] private static extern int getpgid(int pid);
    [DllImport("libc", SetLastError = true)] private static extern int kill(int pid, int signal);
}
