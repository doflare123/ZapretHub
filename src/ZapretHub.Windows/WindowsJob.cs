using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ZapretHub.Windows;

public sealed class WindowsJob : IDisposable
{
    private readonly SafeFileHandle handle;

    private WindowsJob(SafeFileHandle handle) => this.handle = handle;

    public bool IsRunning
    {
        get
        {
            if (!QueryInformationJobObject(handle, 1, out var accounting, Marshal.SizeOf<Accounting>(), IntPtr.Zero))
                throw new Win32Exception();
            return accounting.ActiveProcesses != 0;
        }
    }

    public static WindowsJob Start(string executable, string arguments, string directory)
    {
        SafeFileHandle job = CreateJobObjectW(IntPtr.Zero, null);
        try
        {
            if (job.IsInvalid)
                throw new Win32Exception();
            var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } }; // Kill on close.
            if (!SetInformationJobObject(job, 9, in limits, Marshal.SizeOf<ExtendedLimits>()))
                throw new Win32Exception();

            var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
            var command = new StringBuilder($"\"{executable}\" {arguments}");
            const uint createSuspendedAndHidden = 0x08000004;
            if (!CreateProcessW(executable, command, IntPtr.Zero, IntPtr.Zero, false, createSuspendedAndHidden, IntPtr.Zero,
                directory, ref startup, out var process))
                throw new Win32Exception();

            using var processHandle = new SafeFileHandle(process.Process, ownsHandle: true);
            using var threadHandle = new SafeFileHandle(process.Thread, ownsHandle: true);
            if (!AssignProcessToJobObject(job, processHandle) || ResumeThread(threadHandle) == uint.MaxValue)
            {
                var error = new Win32Exception();
                TerminateProcess(processHandle, 1);
                throw error;
            }
            return new WindowsJob(job);
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    public bool Contains(SafeProcessHandle process) => IsProcessInJob(process, handle, out bool belongs) && belongs;

    public async Task StopAsync(CancellationToken token)
    {
        if (!IsRunning)
            return;
        if (!TerminateJobObject(handle, 1))
            throw new Win32Exception();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, token);
        try
        {
            while (IsRunning)
                await Task.Delay(25, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("Процессы Zapret не завершились вовремя.");
        }
    }

    public void Dispose() => handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public UIntPtr WorkingSetMin, WorkingSetMax;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint Priority, Scheduling;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Accounting
    {
        public long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime;
        public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCount, YCount, Fill, Flags;
        public short ShowWindow, ReservedSize;
        public IntPtr ReservedData, StdIn, StdOut, StdErr;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public uint ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int type, in ExtendedLimits data, int length);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int type, out Accounting data, int length, IntPtr returnLength);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeFileHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(SafeFileHandle process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessInJob(SafeProcessHandle process, SafeFileHandle job, out bool result);
}
