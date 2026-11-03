using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Forge.ExtensionHost;

/// <summary>
/// The operating-system containment of one worker's process tree: on Windows a private job object holding the worker
/// and everything it starts, on Linux and macOS the worker's own process group. The worker is bound to it before
/// <c>worker.load</c> is sent and the tree is torn down through it before <see cref="Process.Kill(bool)"/> is used as a
/// fallback, so a descendant outlives neither a worker that exits on its own nor a host that is killed. All P/Invoke of
/// the host lives in this class; nothing here is unsafe, and no method is called on a platform it does not serve.
/// </summary>
internal sealed class WorkerProcessOwnership : IDisposable
{
#if DEBUG
    /// <summary>Set to <c>1</c> to make <see cref="Assign"/> fail; read only by this class, read only by a Debug build, and
    /// used by the integration tests to exercise the fail-closed start path.</summary>
    internal const string FailAssignmentEnvironmentVariable = "FORGE_HOST_TEST_FAIL_JOB_ASSIGN";
#endif

    /// <summary><c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>: every process in the job dies when the last handle to it
    /// closes, which includes the host process ending for any reason.</summary>
    private const uint JobObjectLimitKillOnJobClose = 0x0000_2000;

    /// <summary><c>JobObjectExtendedLimitInformation</c>.</summary>
    private const int JobObjectExtendedLimitInformationClass = 9;

    private const uint JobTerminateExitCode = 1;

    private const int SignalKill = 9;

    private readonly SafeJobHandle? job;

    private int processId;

    private int disposed;

    private WorkerProcessOwnership(SafeJobHandle? job) => this.job = job;

    /// <summary>Creates the containment for one worker. On Windows that is a new job object with kill-on-close and
    /// without any breakaway flag; other platforms need nothing before the worker starts. Throws
    /// <see cref="HostOperationException"/> with code <c>worker_start_failed</c> when the job cannot be prepared, so a
    /// caller never starts a worker it cannot contain.</summary>
    internal static WorkerProcessOwnership Create()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new WorkerProcessOwnership(null);
        }

        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            var createError = Marshal.GetLastPInvokeError();
            job.Dispose();
            throw new HostOperationException(
                "worker_start_failed",
                $"The plugin worker could not be given a job object: CreateJobObject failed with error {createError}.");
        }

        var limits = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose },
        };
        if (!SetInformationJobObject(
                job,
                JobObjectExtendedLimitInformationClass,
                ref limits,
                Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            var limitError = Marshal.GetLastPInvokeError();
            job.Dispose();
            throw new HostOperationException(
                "worker_start_failed",
                $"The plugin worker's job object could not be limited to kill-on-close: SetInformationJobObject failed with error {limitError}.");
        }

        return new WorkerProcessOwnership(job);
    }

    /// <summary>Binds a freshly started worker to this containment and records the process id used on platforms
    /// without job objects. Throws <see cref="HostOperationException"/> with code <c>worker_start_failed</c> when the
    /// binding does not hold.</summary>
    internal void Assign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

#if DEBUG
        if (string.Equals(
                Environment.GetEnvironmentVariable(FailAssignmentEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            throw new HostOperationException(
                "worker_start_failed",
                $"The plugin worker could not be assigned to its containment ({FailAssignmentEnvironmentVariable}=1).");
        }
#endif

        processId = process.Id;
        if (job is null)
        {
            return;
        }

        if (AssignProcessToJobObject(job, process.SafeHandle))
        {
            return;
        }

        var assignmentError = Marshal.GetLastPInvokeError();
        throw new HostOperationException(
            "worker_start_failed",
            $"The plugin worker could not be assigned to its job object: AssignProcessToJobObject failed with error {assignmentError}.");
    }

    /// <summary>Kills the worker and every process it started: the job object on Windows, the worker's process group on
    /// Linux and macOS. Best effort — the caller still falls back to <see cref="Process.Kill(bool)"/>.</summary>
    internal void Kill()
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        if (job is not null)
        {
            TerminateJobObject(job, JobTerminateExitCode);
            return;
        }

        if (OperatingSystem.IsWindows() || processId <= 0)
        {
            return;
        }

        kill(-processId, SignalKill);
    }

    /// <summary>Closes the job handle, which is itself a kill of anything still in the job.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        job?.Dispose();
    }

#pragma warning disable SYSLIB1054
    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeJobHandle CreateJobObjectW(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeJobHandle job,
        int informationClass,
        ref JobObjectExtendedLimitInformation information,
        int length);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeJobHandle job, uint exitCode);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("libc", ExactSpelling = true, SetLastError = true)]
    private static extern int kill(int processId, int signal);
#pragma warning restore SYSLIB1054

    private sealed class SafeJobHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectIoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public JobObjectIoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }
}
