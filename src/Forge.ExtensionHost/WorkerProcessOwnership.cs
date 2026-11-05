using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Forge.ExtensionHost;

/// <summary>
/// The operating-system containment of one worker's process tree: on Windows a private job object holding the worker
/// and everything it starts, on Linux and macOS the worker's own process group - which this class also creates, because
/// only the spawn call can put a child in a group before it runs its first instruction. The worker is bound to it before
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

    /// <summary><c>POSIX_SPAWN_SETPGROUP</c>: the child is placed in the process group named by
    /// <c>posix_spawnattr_setpgroup</c> by the spawn call itself, so the group exists before the child runs its first
    /// instruction. The value is the same in glibc, musl and Darwin's <c>spawn.h</c>.</summary>
    private const short PosixSpawnSetPgroup = 0x02;

    /// <summary>A process group id of zero in <c>posix_spawnattr_setpgroup</c> means "the child's own pid": the child
    /// becomes the leader of a new group, which is the same state the worker used to establish itself with
    /// <c>setpgid(0, 0)</c>.</summary>
    private const int OwnProcessGroup = 0;

    private const int StandardOutputDescriptor = 1;

    private const int StandardErrorDescriptor = 2;

    /// <summary><c>O_CLOEXEC</c> for <c>pipe2</c> on Linux.</summary>
    private const int OpenCloseOnExec = 0x8_0000;

    /// <summary><c>F_SETFD</c> / <c>FD_CLOEXEC</c>, used for the pipes on platforms without <c>pipe2</c>.</summary>
    private const int SetDescriptorFlags = 2;

    private const int CloseOnExec = 1;

    /// <summary><c>WNOHANG</c> for <c>waitpid</c>: report, never block.</summary>
    private const int WaitNoHang = 1;

    /// <summary><c>EINTR</c>, which is 4 on both Linux and Darwin.</summary>
    private const int Interrupted = 4;

    /// <summary><c>ESRCH</c>, which is 3 on both Linux and Darwin: the process or process group does not exist.</summary>
    private const int NoSuchProcess = 3;

    /// <summary><c>P_PID</c> for <c>waitid</c>: the id argument names a process.</summary>
    private const int WaitForProcessId = 1;

    /// <summary><c>WEXITED</c> for <c>waitid</c>: report an exit rather than a stop or a continue. 4 on Linux and
    /// Darwin alike.</summary>
    private const int WaitReportExit = 4;

    /// <summary><c>WNOWAIT</c> for <c>waitid</c>: report the child's exit but leave it waitable, so its pid - and with it
    /// its process group id - is not released yet. Linux uses <c>0x0100_0000</c>, Darwin <c>0x20</c>.
    /// <para>CI-to-validate: the Darwin value is taken from <c>sys/wait.h</c> and has never been executed by the tests
    /// behind this file (they run on Linux and Windows). A macOS run may also reveal that the <c>waitid</c> declaration
    /// below needs a different entry point, in which case the reaper must fall back to the plain <c>waitpid</c> order.</para>
    /// </summary>
    private static readonly int WaitLeaveChild = OperatingSystem.IsMacOS() ? 0x20 : 0x0100_0000;

    /// <summary>Bytes reserved for one <c>siginfo_t</c>: 128 in glibc, 104 in Darwin's <c>__siginfo</c>.</summary>
    private const int SignalInformationSize = 128;

    /// <summary>Offset of <c>siginfo_t.si_pid</c>, the field <c>waitid</c> uses to name the child whose state changed.
    /// glibc places the signal's union of fields at 16 on both x86-64 and aarch64; Darwin's <c>__siginfo</c> places the pid
    /// at 12, right after signo/errno/code. CI-to-validate: the Darwin offset has never been executed by the tests behind
    /// this file, so it is the first thing a macOS run would have to confirm.</summary>
    private static readonly int ReportedChildOffset = OperatingSystem.IsMacOS() ? 12 : 16;

    /// <summary>A clean <c>siginfo_t</c>. <c>waitid</c> with <c>WNOHANG</c> reports "no child changed state" as a zero
    /// <c>si_pid</c>, which is only distinguishable from a real exit if the buffer started zeroed.</summary>
    private static readonly byte[] ZeroedSignalInformation = new byte[SignalInformationSize];

    /// <summary>Bytes reserved for one opaque <c>posix_spawnattr_t</c> or <c>posix_spawn_file_actions_t</c>. Neither
    /// type may be declared in C#: glibc and musl define them as structs of a few hundred bytes, Darwin defines them as
    /// a pointer. A caller-owned, over-sized buffer that the C library initialises through
    /// <c>posix_spawnattr_init</c>/<c>posix_spawn_file_actions_init</c> is the portable way to keep both layouts out of
    /// this file.</summary>
    private const int PosixSpawnOpaqueSize = 512;

    private readonly SafeJobHandle? job;

    private int processId;

    private int disposed;

    /// <summary>The reaping loop of a POSIX child, so its process-table entry is released instead of staying a zombie;
    /// <see langword="null"/> on Windows, where the job object is the containment, and cancelled by
    /// <see cref="Dispose"/>.</summary>
    private Task? reaper;

    private CancellationTokenSource? reapStop;

    /// <summary>Non-zero once the reaping loop has closed this worker's process group: it either swept the group while the
    /// pid was provably still the worker's and then released the pid, or found the pid already released. From that moment
    /// the group id may name an unrelated process, so <see cref="Kill"/> never signals it again.
    /// <see cref="reapProblem"/> carries the failure text of a sweep that could not be delivered, and stays
    /// <see langword="null"/> when the close was clean.</summary>
    private int groupClosed;

    private string? reapProblem;

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
        ThrowIfAssignmentMustFail();

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

    /// <summary>Binds a child that <see cref="Spawn"/> started on POSIX. There is nothing left to establish: the spawn
    /// already put the child in its own process group, so this only records the pid, verifies the guarantee it depends on
    /// and starts reaping. The verification is fail-closed - a child that is not the leader of its own group is never
    /// handed back as contained, because <see cref="Kill"/> would then signal the host's own group.</summary>
    internal void Assign(int childProcessId)
    {
        ThrowIfAssignmentMustFail();
        processId = childProcessId;
        var groupId = getpgid(childProcessId);
        if (groupId != childProcessId)
        {
            throw new HostOperationException(
                "worker_start_failed",
                groupId < 0
                    ? $"The plugin worker (pid {childProcessId}) was not in a process group of its own: getpgid failed with error {Marshal.GetLastPInvokeError()}."
                    : $"The plugin worker (pid {childProcessId}) was placed in process group {groupId} instead of one of its own.");
        }

        reapStop = new CancellationTokenSource();
        reaper = ReapAsync(processId, reapStop.Token);
    }

    /// <summary>The fail-closed hook both <see cref="Assign(Process)"/> and <see cref="Assign(int)"/> honour, so the
    /// integration tests can exercise "a worker that cannot be contained never runs" on every platform. Does nothing
    /// in a Release build.</summary>
    private static void ThrowIfAssignmentMustFail()
    {
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
    }

    /// <summary>True when starting a worker is this class's job rather than <see cref="Process.Start(ProcessStartInfo)"/>'s:
    /// <see cref="Process"/> exposes no window between <c>fork</c> and <c>exec</c>, and POSIX refuses a group change on a
    /// child that has already exec'd (<c>setpgid</c> returns <c>EACCES</c>), so a process group can only be established by
    /// the spawn call itself.</summary>
    internal static bool SpawnsChildNatively => !OperatingSystem.IsWindows();

    /// <summary>Starts one worker already in a process group of its own, with stdout and stderr redirected into pipes the
    /// host reads, and returns its process id and the readable ends of those pipes. The group exists before the child
    /// executes its first instruction (<c>POSIX_SPAWN_SETPGROUP</c> with a group of zero makes the child the leader of a
    /// new group), so there is no window in which a descendant could inherit the host's group. Everything else - the
    /// working directory, stdin, the environment - is inherited exactly as <see cref="Process.Start(ProcessStartInfo)"/>
    /// inherits it. Throws <see cref="HostOperationException"/> with code <c>worker_start_failed</c> when the child cannot
    /// be started; nothing is left running in that case.</summary>
    /// <remarks>
    /// <c>posix_spawn</c> reports a failure as an error number in its return value rather than through <c>errno</c>, and it
    /// does not return before the child either exec'd or failed to exec, so the marshalled argument and environment
    /// vectors are released as soon as it returns.
    /// </remarks>
    internal static SpawnedWorker Spawn(string fileName, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        ArgumentNullException.ThrowIfNull(arguments);

        var actions = Marshal.AllocHGlobal(PosixSpawnOpaqueSize);
        var attributes = Marshal.AllocHGlobal(PosixSpawnOpaqueSize);
        var outputPipe = Marshal.AllocHGlobal(2 * sizeof(int));
        var errorPipe = Marshal.AllocHGlobal(2 * sizeof(int));
        var commandLine = new List<string>(arguments.Count + 1) { fileName };
        foreach (var argument in arguments)
        {
            commandLine.Add(argument);
        }

        var argv = AllocateVector(commandLine);
        var envp = AllocateVector(EnvironmentEntries());
        var outputRead = -1;
        var outputWrite = -1;
        var errorRead = -1;
        var errorWrite = -1;
        var actionsInitialised = false;
        var attributesInitialised = false;
        try
        {
            CreateCloseOnExecPipe(outputPipe, "standard output", out outputRead, out outputWrite);
            CreateCloseOnExecPipe(errorPipe, "standard error", out errorRead, out errorWrite);

            CheckSpawnCall("posix_spawn_file_actions_init", posix_spawn_file_actions_init(actions));
            actionsInitialised = true;
            CheckSpawnCall("posix_spawnattr_init", posix_spawnattr_init(attributes));
            attributesInitialised = true;

            CheckSpawnCall(
                "posix_spawn_file_actions_adddup2",
                posix_spawn_file_actions_adddup2(actions, outputWrite, StandardOutputDescriptor));
            CheckSpawnCall(
                "posix_spawn_file_actions_adddup2",
                posix_spawn_file_actions_adddup2(actions, errorWrite, StandardErrorDescriptor));

            // The read ends have no business in the child; closing them through the file actions keeps them out of the
            // child from the moment the group is established, not only from the moment it execs.
            CheckSpawnCall("posix_spawn_file_actions_addclose", posix_spawn_file_actions_addclose(actions, outputRead));
            CheckSpawnCall("posix_spawn_file_actions_addclose", posix_spawn_file_actions_addclose(actions, errorRead));

            CheckSpawnCall("posix_spawnattr_setpgroup", posix_spawnattr_setpgroup(attributes, OwnProcessGroup));
            CheckSpawnCall("posix_spawnattr_setflags", posix_spawnattr_setflags(attributes, PosixSpawnSetPgroup));

            var spawnError = posix_spawnp(out var processId, fileName, actions, attributes, argv, envp);
            if (spawnError != 0)
            {
                throw new HostOperationException(
                    "worker_start_failed",
                    $"The plugin worker '{fileName}' could not be started: posix_spawnp failed with error {spawnError} ({Marshal.GetPInvokeErrorMessage(spawnError)}).");
            }

            // The child owns the write ends from here on; holding them open in the host would hide the end of its output.
            CloseDescriptor(ref outputWrite);
            CloseDescriptor(ref errorWrite);
            var spawned = new SpawnedWorker(processId, outputRead, errorRead);
            outputRead = -1;
            errorRead = -1;
            return spawned;
        }
        finally
        {
            if (attributesInitialised)
            {
                _ = posix_spawnattr_destroy(attributes);
            }

            if (actionsInitialised)
            {
                _ = posix_spawn_file_actions_destroy(actions);
            }

            CloseDescriptor(ref outputRead);
            CloseDescriptor(ref outputWrite);
            CloseDescriptor(ref errorRead);
            CloseDescriptor(ref errorWrite);
            ReleaseVector(argv);
            ReleaseVector(envp);
            Marshal.FreeHGlobal(outputPipe);
            Marshal.FreeHGlobal(errorPipe);
            Marshal.FreeHGlobal(actions);
            Marshal.FreeHGlobal(attributes);
        }
    }

    /// <summary>Reaps a POSIX child so its process table entry does not stay a zombie. A reaped child also stops answering
    /// <c>kill(pid, 0)</c>, which is how <see cref="Process.HasExited"/> decides for a process the BCL did not start:
    /// without this, an exited worker would be reported as still running and every "was the tree really torn down" check
    /// would fail on POSIX. Polling starts fast and backs off to 100 ms, the interval <see cref="Process"/> itself uses for
    /// processes it did not start.</summary>
    private async Task ReapAsync(int childProcessId, CancellationToken ct)
    {
        var information = Marshal.AllocHGlobal(SignalInformationSize);
        try
        {
            var delay = 1;
            while (true)
            {
                if (TryReapChild(childProcessId, information))
                {
                    return;
                }

                try
                {
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                delay = Math.Min(delay * 2, 100);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(information);
        }
    }

    /// <summary>Whether <paramref name="childProcessId"/> is gone from the process table. The exit is observed with
    /// <c>waitid(WNOWAIT)</c> before anything is released, because an exited child that has not been waited for is a
    /// zombie and a zombie keeps its pid: the worker's process group id therefore cannot have been handed to another
    /// process. Sweeping the group first and releasing the pid second is what keeps a plain <c>waitpid</c> from widening
    /// the pid-reuse window - reap first and the group id is free for an unrelated process to take before
    /// <see cref="Kill"/> signals it.</summary>
    private bool TryReapChild(int childProcessId, IntPtr information)
    {
        Marshal.Copy(ZeroedSignalInformation, 0, information, SignalInformationSize);
        var observed = waitid(
            WaitForProcessId,
            childProcessId,
            information,
            WaitReportExit | WaitNoHang | WaitLeaveChild);
        if (observed != 0)
        {
            // EINTR learned nothing, so ask again. ECHILD means another waiter already released the child and ESRCH that
            // the pid is gone: either way it is out of the process table, which is all this loop is for. Its group id is
            // already free as well, so the loop closes the group without a sweep and never signals it again.
            var observationError = Marshal.GetLastPInvokeError();
            if (observationError is Interrupted or 0)
            {
                return false;
            }

            Volatile.Write(ref groupClosed, 1);
            return true;
        }

        if (Marshal.ReadInt32(information, ReportedChildOffset) != childProcessId)
        {
            return false;
        }

        // The child is a zombie: its pid is reserved, so the group it led can only contain processes it started. Sweep
        // them now, before the waitpid below makes the group id reusable. A group this class already emptied answers
        // ESRCH, which is not a problem.
        var sweepError = 0;
        if (kill(-childProcessId, SignalKill) != 0)
        {
            sweepError = Marshal.GetLastPInvokeError();
        }

        Volatile.Write(
            ref reapProblem,
            sweepError is 0 or NoSuchProcess
                ? null
                : $"kill(-{childProcessId}, SIGKILL) failed with error {sweepError} while the worker was being reaped");
        Volatile.Write(ref groupClosed, 1);

        var reaped = waitpid(childProcessId, out _, WaitNoHang);
        if (reaped == childProcessId)
        {
            return true;
        }

        if (reaped >= 0)
        {
            return false;
        }

        var reapError = Marshal.GetLastPInvokeError();
        return reapError is not (Interrupted or 0);
    }

    private static void CreateCloseOnExecPipe(IntPtr fds, string name, out int readEnd, out int writeEnd)
    {
        readEnd = -1;
        writeEnd = -1;

        // pipe2 sets close-on-exec atomically on Linux; Darwin has no pipe2, so the flags are set separately there. Both
        // ends are close-on-exec in the host, so no child inherits a pipe another worker's reader is waiting on;
        // posix_spawn_file_actions_adddup2 then clears the flag on the copies the child is meant to keep.
        var result = OperatingSystem.IsLinux() ? pipe2(fds, OpenCloseOnExec) : pipe(fds);
        if (result != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new HostOperationException(
                "worker_start_failed",
                $"The plugin worker's {name} pipe could not be created: pipe failed with error {error} ({Marshal.GetPInvokeErrorMessage(error)}).");
        }

        readEnd = Marshal.ReadInt32(fds);
        writeEnd = Marshal.ReadInt32(fds, sizeof(int));
        if (OperatingSystem.IsLinux())
        {
            return;
        }

        SetCloseOnExec(readEnd);
        SetCloseOnExec(writeEnd);
    }

    private static void SetCloseOnExec(int descriptor)
    {
        if (fcntl(descriptor, SetDescriptorFlags, CloseOnExec) >= 0)
        {
            return;
        }

        var error = Marshal.GetLastPInvokeError();
        throw new HostOperationException(
            "worker_start_failed",
            $"A plugin worker pipe could not be marked close-on-exec: fcntl failed with error {error} ({Marshal.GetPInvokeErrorMessage(error)}).");
    }

    private static void CloseDescriptor(ref int descriptor)
    {
        if (descriptor < 0)
        {
            return;
        }

        _ = close(descriptor);
        descriptor = -1;
    }

    private static void CheckSpawnCall(string name, int error)
    {
        if (error == 0)
        {
            return;
        }

        throw new HostOperationException(
            "worker_start_failed",
            $"The plugin worker could not be prepared for its process group: {name} failed with error {error} ({Marshal.GetPInvokeErrorMessage(error)}).");
    }

    /// <summary>The environment the child inherits, in the <c>name=value</c> form <c>envp</c> expects: the host's own
    /// environment as <see cref="Environment"/> reports it, which is the copy of <c>environ</c> the runtime took at
    /// startup plus every <see cref="Environment.SetEnvironmentVariable(string, string)"/>. The one thing this would miss
    /// is a native <c>setenv</c> made by a library the host loads, which nothing in the host does. Reading the
    /// <c>environ</c> symbol instead would need a per-platform library and symbol name (<c>libc.so.6</c> against
    /// <c>libSystem</c>/<c>_environ</c>) whose behaviour could only be verified on the platform it names.</summary>
    private static string[] EnvironmentEntries()
    {
        var variables = Environment.GetEnvironmentVariables();
        var entries = new List<string>(variables.Count);
        foreach (System.Collections.DictionaryEntry variable in variables)
        {
            entries.Add($"{variable.Key}={variable.Value}");
        }

        return [.. entries];
    }

    /// <summary>Marshals a null-terminated UTF-8 vector for <c>argv</c>/<c>envp</c>; the trailing slot stays
    /// <see cref="IntPtr.Zero"/>, which is the terminator.</summary>
    private static IntPtr[] AllocateVector(IReadOnlyList<string> values)
    {
        var vector = new IntPtr[values.Count + 1];
        for (var index = 0; index < values.Count; index++)
        {
            vector[index] = Marshal.StringToCoTaskMemUTF8(values[index]);
        }

        return vector;
    }

    private static void ReleaseVector(IntPtr[] vector)
    {
        foreach (var value in vector)
        {
            if (value != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(value);
            }
        }
    }

    /// <summary>Kills the worker and every process it started: the job object on Windows, the worker's process group on
    /// Linux and macOS. Returns <c>false</c> with a description when the native call itself reported a problem; that is
    /// not proof of a survivor, so a caller still verifies the process is gone and falls back to
    /// <see cref="Process.Kill(bool)"/>. Nothing here throws for a normal "already gone" error.</summary>
    internal bool Kill(out string problem)
    {
        problem = string.Empty;
        if (Volatile.Read(ref disposed) != 0)
        {
            problem = "its containment was already released";
            return false;
        }

        if (job is not null)
        {
            if (TerminateJobObject(job, JobTerminateExitCode))
            {
                return true;
            }

            problem = $"TerminateJobObject failed with error {Marshal.GetLastPInvokeError()}";
            return false;
        }

        if (OperatingSystem.IsWindows() || processId <= 0)
        {
            problem = "no process group was recorded for the worker";
            return false;
        }

        if (Volatile.Read(ref groupClosed) != 0)
        {
            // The reaping loop closed this group at the only moment its id was provably still the worker's. Signalling it
            // again would risk signalling a group an unrelated process owns now that the pid has been released.
            var closedProblem = Volatile.Read(ref reapProblem);
            problem = closedProblem ?? string.Empty;
            return closedProblem is null;
        }

        if (kill(-processId, SignalKill) == 0)
        {
            return true;
        }

        var error = Marshal.GetLastPInvokeError();
        problem = $"kill(-{processId}, SIGKILL) failed with error {error}";
        return false;
    }

    /// <summary>Closes the job handle, which is itself a kill of anything still in the job.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        // Only cancels: a reaping loop that is already inside waitpid or a delay finishes on its own, and cancelling is
        // enough to make it stop. Its loop is observation only - it never holds a resource the disposal needs.
        reapStop?.Cancel();
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

    [DllImport("libc", ExactSpelling = true)]
    private static extern int posix_spawnp(
        out int processId,
        string file,
        IntPtr fileActions,
        IntPtr attributes,
        IntPtr[] argv,
        IntPtr[] envp);

    [DllImport("libc", ExactSpelling = true)]
    private static extern int posix_spawnattr_init(IntPtr attributes);

    [DllImport("libc", ExactSpelling = true)]
    private static extern int posix_spawnattr_destroy(IntPtr attributes);

    [DllImport("libc", ExactSpelling = true)]
    private static extern int posix_spawnattr_setflags(IntPtr attributes, short flags);

    [DllImport("libc", ExactSpelling = true)]
    private static extern int posix_spawnattr_setpgroup(IntPtr attributes, int processGroup);

    [DllImport("libc", ExactSpelling = true)]
    private static extern int posix_spawn_file_actions_init(IntPtr actions);

    [DllImport("libc", ExactSpelling = true)]
    private static extern int posix_spawn_file_actions_destroy(IntPtr actions);

    [DllImport("libc", ExactSpelling = true)]
    private static extern int posix_spawn_file_actions_adddup2(IntPtr actions, int descriptor, int newDescriptor);

    [DllImport("libc", ExactSpelling = true)]
    private static extern int posix_spawn_file_actions_addclose(IntPtr actions, int descriptor);

    [DllImport("libc", ExactSpelling = true, SetLastError = true)]
    private static extern int pipe(IntPtr fds);

    [DllImport("libc", ExactSpelling = true, SetLastError = true)]
    private static extern int pipe2(IntPtr fds, int flags);

    [DllImport("libc", ExactSpelling = true, SetLastError = true)]
    private static extern int fcntl(int descriptor, int command, int argument);

    [DllImport("libc", ExactSpelling = true, SetLastError = true)]
    private static extern int close(int descriptor);

    [DllImport("libc", ExactSpelling = true, SetLastError = true)]
    private static extern int getpgid(int processId);

    [DllImport("libc", ExactSpelling = true, SetLastError = true)]
    private static extern int waitid(int idType, int id, IntPtr information, int options);

    [DllImport("libc", ExactSpelling = true, SetLastError = true)]
    private static extern int waitpid(int processId, out int status, int options);
#pragma warning restore SYSLIB1054

    private sealed class SafeJobHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    /// <summary>One POSIX worker the host started: its process id and the readable ends of the pipes its stdout and stderr
    /// were redirected into. Closing it closes those pipes, which ends the host's line pumps for this worker.</summary>
    internal sealed class SpawnedWorker : IDisposable
    {
        internal SpawnedWorker(int processId, int outputDescriptor, int errorDescriptor)
        {
            ProcessId = processId;
            StandardOutput = OpenReader(outputDescriptor);
            StandardError = OpenReader(errorDescriptor);
        }

        internal int ProcessId { get; }

        internal Stream StandardOutput { get; }

        internal Stream StandardError { get; }

        public void Dispose()
        {
            StandardOutput.Dispose();
            StandardError.Dispose();
        }

        private static Stream OpenReader(int descriptor) => new FileStream(
            new SafeFileHandle((IntPtr)descriptor, ownsHandle: true),
            FileAccess.Read,
            bufferSize: 4096,
            isAsync: false);
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
