using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Forge.ExtensionHost;

/// <summary>
/// The pseudo-terminal half of the host's only native class: creating a shell on a ConPTY (Windows) or on a
/// <c>posix_openpt</c> master (Linux, macOS), sizing it, asking whether it has a foreground child, and tearing its whole
/// process tree down. It lives in this partial class - another file of the class the architecture confines native process
/// ownership to - so no P/Invoke has to move out of <see cref="WorkerProcessOwnership"/>; nothing here is unsafe, and no
/// method is called on a platform it does not serve.
/// <para>
/// Containment is the same idea as for a worker. On Windows the shell is created suspended, bound to a private
/// kill-on-close job object, and resumed only afterwards, so it cannot execute an instruction outside the job; closing the
/// job kills whatever the shell left behind. On POSIX <c>POSIX_SPAWN_SETSID</c> makes the shell the leader of a new
/// session and process group inside the spawn call itself, so <c>kill(-pid, SIGKILL)</c> can never signal the host's own
/// group. A session leader that opens a terminal slave gets it as its controlling terminal (Linux).
/// </para>
/// <para>
/// CI-to-validate on macOS: the <c>POSIX_SPAWN_SETSID</c> value, <c>TIOCSWINSZ</c>, <c>ptsname</c> (Darwin has no
/// <c>ptsname_r</c>), <c>posix_spawn_file_actions_addchdir_np</c> (macOS 10.15+) and whether a session leader gets the
/// controlling terminal without an explicit <c>TIOCSCTTY</c> are all unverified here - this file has only ever been
/// executed on Windows and Linux. macOS is therefore not claimed as supported.
/// </para>
/// </summary>
internal sealed partial class WorkerProcessOwnership
{
    /// <summary>Size of the host's read buffer: one PTY read becomes exactly one <c>terminal.output</c> frame, with no
    /// extra timer that would merge or split reads.</summary>
    internal const int PtyReadChunkBytes = 64 * 1024;

    /// <summary>How long the host waits for a killed tree to disappear before it reports the tree as unverified.</summary>
    internal static readonly TimeSpan PtyExitWindow = TimeSpan.FromSeconds(5);

    /// <summary>How long one terminal session sweep may keep looking for survivors before it reports them as
    /// unverified. SIGKILL is immediate, so this only covers a process that is mid-exit.</summary>
    private static readonly TimeSpan PtySessionSweepWindow = TimeSpan.FromSeconds(2);

    /// <summary><c>PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE</c>: the pseudo console a new process is attached to.</summary>
    private const int ProcThreadAttributePseudoConsole = 0x0002_0016;

    private const uint ExtendedStartupInfoPresent = 0x0008_0000;

    private const uint CreateSuspended = 0x0000_0004;

    private const uint CreateUnicodeEnvironment = 0x0000_0400;

    /// <summary><c>STARTF_USESTDHANDLES</c> with all three handles left null: the child must take the standard handles of
    /// the pseudo console it is attached to, not the host's. The host is normally started by the main program with its
    /// stdout and stderr redirected (its diagnostics are captured), so without this flag a shell inherits those pipes and
    /// writes its terminal stream into the host's log instead of into the pseudoconsole - silently, with no error from any
    /// call, and the terminal looks like a shell that started and printed nothing.</summary>
    private const int StartupStandardHandles = 0x0000_0100;

    /// <summary><c>JobObjectBasicAccountingInformation</c>: the class that reports how many processes of a job are alive.</summary>
    private const int JobObjectBasicAccountingInformationClass = 1;

    /// <summary><c>ERROR_INSUFFICIENT_BUFFER</c>, what the first, sizing call to
    /// <c>InitializeProcThreadAttributeList</c> is expected to report.</summary>
    private const int ErrorInsufficientBuffer = 122;

    /// <summary>The buffer hint for the two ConPTY pipes. Larger than the default 4 KiB so a program that bursts output
    /// does not pay a context switch per kilobyte; the 1 MiB acknowledgement window still bounds what the host holds.</summary>
    private const int PtyPipeBufferBytes = 64 * 1024;

    /// <summary><c>O_RDWR</c>, the only flag the POSIX master is opened with. <c>O_NOCTTY</c> is deliberately absent: it
    /// governs opening a terminal <em>slave</em> as a controlling terminal and has no meaning for the master side, while
    /// its value differs between Linux and Darwin.</summary>
    private const int OpenReadWrite = 2;

    /// <summary><c>O_CLOEXEC</c>: the master must not survive an <c>exec</c> in any other child the host spawns later (a
    /// worker, another terminal's shell), or that child keeps this terminal alive and could read its output. Passed at open
    /// time, which is atomic; <c>fcntl(F_SETFD)</c> afterwards would leave a window and is variadic, which P/Invoke cannot
    /// call correctly on Apple arm64. Linux value 0x80000; Darwin 0x1000000 (CI-to-validate: never executed on macOS).</summary>
    private static readonly int MasterCloseOnExec = OperatingSystem.IsMacOS() ? 0x0100_0000 : 0x0008_0000;

    /// <summary><c>TIOCSWINSZ</c> / <c>TIOCGWINSZ</c>'s argument type: rows and columns first, pixels after.</summary>
    private const int SetTerminalWindowSize = 0x5414;

    private const int SetTerminalWindowSizeDarwin = unchecked((int)0x8008_7467);

    /// <summary><c>POSIX_SPAWN_SETSID</c>: the child calls <c>setsid()</c> in the spawn call, so it is a session leader -
    /// and therefore the leader of its own process group - before it runs its first instruction. glibc needs 2.26, Darwin
    /// spells it differently. CI-to-validate: the Darwin value has never been executed by the tests behind this file.</summary>
    private static readonly short PosixSpawnSetSid = OperatingSystem.IsMacOS() ? (short)0x0400 : (short)0x80;

    /// <summary>Offset of <c>si_status</c> inside a <c>siginfo_t</c>: it follows <c>si_pid</c> and <c>si_uid</c>, whose
    /// offset this class already knows per platform.</summary>
    private static readonly int ReportedStatusOffset = ReportedChildOffset + 8;

    /// <summary>Offset of <c>si_code</c>, which says whether the child exited (<c>CLD_EXITED</c>) or was killed
    /// (<c>CLD_KILLED</c>/<c>CLD_DUMPED</c>). The three leading ints have the same position in glibc and Darwin.</summary>
    private const int ReportedCodeOffset = 8;

    private const int ChildExitedNormally = 1;

    private const int SignalTerminationBase = 128;

    /// <summary>
    /// One running shell on a pseudo-terminal. Everything the host does with it goes through here: read the master, write
    /// the master, resize it, ask whether a child has the terminal, kill its tree, and - only after the tree is gone -
    /// release the console so the reader can reach end of file.
    /// </summary>
    internal abstract class PtyProcess : IDisposable
    {
        /// <summary>The shell's process id (Windows) or the id of the session leader (POSIX).</summary>
        internal abstract int ProcessId { get; }

        /// <summary>The host's read end: the raw VT stream the shell writes.</summary>
        internal abstract Stream OutputStream { get; }

        /// <summary>The host's write end: keystrokes for the shell.</summary>
        internal abstract Stream InputStream { get; }

        /// <summary>Completes once the shell process is gone, with its exit code (128 + signal when POSIX killed it).</summary>
        internal abstract Task<int> ExitCode { get; }

        /// <summary>Sizes the terminal; the kernel sends <c>SIGWINCH</c> to the foreground group on POSIX.</summary>
        internal abstract bool TryResize(int columns, int rows, out string? problem);

        /// <summary>True while a process other than the shell itself is alive under it - an editor, an agent CLI, a build.</summary>
        internal abstract bool HasForegroundChild();

        /// <summary>Kills the whole process tree. Returns a description when the native call itself reported a failure;
        /// that is not proof of a survivor, so the caller still verifies the process is gone.</summary>
        internal abstract string? KillTree();

        /// <summary>
        /// Releases the console once the shell is gone. On Windows that is <c>ClosePseudoConsole</c>, which the caller must
        /// run while the output pipe is still being drained - conhost flushes into that pipe and blocks when nobody reads,
        /// which is the documented way to hang an old Windows. On POSIX there is nothing to close: the slave ends at process
        /// exit and the master reports end of file by itself.
        /// </summary>
        internal abstract void CloseConsole();

        public abstract void Dispose();
    }

    /// <summary>
    /// Starts one shell on a fresh pseudo-terminal. Throws <see cref="HostOperationException"/> with code
    /// <c>terminal_start_failed</c> when the terminal cannot be created; nothing is left running in that case.
    /// </summary>
    internal static PtyProcess StartTerminal(
        string shell,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        int columns,
        int rows) =>
        OperatingSystem.IsWindows()
            ? WindowsPtyProcess.Start(shell, arguments, workingDirectory, environment, columns, rows)
            : PosixPtyProcess.Start(shell, arguments, workingDirectory, environment, columns, rows);

    /// <summary>The environment a child inherits as the <c>name=value</c> strings <c>envp</c>/a CreateProcess block needs.</summary>
    private static string[] EnvironmentVectorEntries(IReadOnlyDictionary<string, string> environment)
    {
        var entries = new string[environment.Count];
        var index = 0;
        foreach (var pair in environment)
        {
            entries[index++] = $"{pair.Key}={pair.Value}";
        }

        return entries;
    }

    private static HostOperationException PtyStartFailed(string what, int error) =>
        new(
            "terminal_start_failed",
            $"The terminal could not be started: {what} failed with error {error} ({Marshal.GetPInvokeErrorMessage(error)}).");

    /// <summary>
    /// One ConPTY. The pseudo console, the job object that contains the shell, the two remaining pipe ends and the process
    /// handle all die together in <see cref="Dispose"/>; killing the job is what removes the tree, closing the pseudo
    /// console is what ends the output stream.
    /// </summary>
    private sealed class WindowsPtyProcess : PtyProcess
    {
        private readonly WorkerProcessOwnership containment;
        private readonly Process client;
        private readonly SafeFileHandle inputWrite;
        private readonly SafeFileHandle outputRead;
        private readonly Task<int> exitCode;
        private IntPtr pseudoConsole;
        private int consoleClosed;
        private int disposed;

        private WindowsPtyProcess(
            WorkerProcessOwnership containment,
            Process client,
            SafeFileHandle inputWrite,
            SafeFileHandle outputRead,
            IntPtr pseudoConsole)
        {
            this.containment = containment;
            this.client = client;
            this.inputWrite = inputWrite;
            this.outputRead = outputRead;
            this.pseudoConsole = pseudoConsole;
            InputStream = new FileStream(inputWrite, FileAccess.Write, bufferSize: 0, isAsync: false);
            OutputStream = new FileStream(outputRead, FileAccess.Read, bufferSize: 0, isAsync: false);
            exitCode = WaitForExitAsync();
        }

        internal override int ProcessId
        {
            get
            {
                try
                {
                    return client.Id;
                }
                catch (InvalidOperationException)
                {
                    return 0;
                }
            }
        }

        internal override Stream OutputStream { get; }

        internal override Stream InputStream { get; }

        internal override Task<int> ExitCode => exitCode;

        private async Task<int> WaitForExitAsync()
        {
            try
            {
                await client.WaitForExitAsync().ConfigureAwait(false);
                return client.ExitCode;
            }
            catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException or System.ComponentModel.Win32Exception)
            {
                // The handle is gone with the process object, so the code is no longer observable; a terminal that cannot
                // report a code reports a failure exit instead of inventing one.
                return -1;
            }
        }

        internal static WindowsPtyProcess Start(
            string shell,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            IReadOnlyDictionary<string, string> environment,
            int columns,
            int rows)
        {
            SafeFileHandle? inputRead = null;
            SafeFileHandle? inputWrite = null;
            SafeFileHandle? outputRead = null;
            SafeFileHandle? outputWrite = null;
            var pseudoConsole = IntPtr.Zero;
            var attributeList = IntPtr.Zero;
            var thread = IntPtr.Zero;
            var processHandle = IntPtr.Zero;
            Process? client = null;
            WorkerProcessOwnership? containment = null;
            var clientStarted = false;
            try
            {
                if (!CreatePipe(out inputRead, out inputWrite, IntPtr.Zero, PtyPipeBufferBytes) ||
                    !CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, PtyPipeBufferBytes))
                {
                    throw PtyStartFailed("CreatePipe", Marshal.GetLastPInvokeError());
                }

                var size = new ConsoleCoordinate { X = (short)columns, Y = (short)rows };
                var consoleResult = CreatePseudoConsole(size, inputRead!, outputWrite!, 0, out pseudoConsole);
                if (consoleResult != 0)
                {
                    throw PtyStartFailed("CreatePseudoConsole", consoleResult);
                }

                // The pseudo console holds its own references to these two ends from here on; the caller's copies must be
                // closed, or the host keeps the console's write end open and the reader never reaches end of file.
                inputRead!.Dispose();
                inputRead = null;
                outputWrite!.Dispose();
                outputWrite = null;

                var listSize = IntPtr.Zero;
                if (!InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref listSize))
                {
                    var sizeError = Marshal.GetLastPInvokeError();
                    if (sizeError != ErrorInsufficientBuffer || listSize == IntPtr.Zero)
                    {
                        throw PtyStartFailed("InitializeProcThreadAttributeList", sizeError);
                    }
                }

                attributeList = Marshal.AllocHGlobal(listSize);
                if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref listSize))
                {
                    throw PtyStartFailed("InitializeProcThreadAttributeList", Marshal.GetLastPInvokeError());
                }

                if (!UpdateProcThreadAttribute(
                        attributeList,
                        0,
                        ProcThreadAttributePseudoConsole,
                        pseudoConsole,
                        IntPtr.Size,
                        IntPtr.Zero,
                        IntPtr.Zero))
                {
                    throw PtyStartFailed("UpdateProcThreadAttribute", Marshal.GetLastPInvokeError());
                }

                var startup = new StartupInfoEx
                {
                    StartupInfo = new StartupInfo
                    {
                        cb = Marshal.SizeOf<StartupInfoEx>(),
                        dwFlags = StartupStandardHandles,
                    },
                    lpAttributeList = attributeList,
                };

                var commandLine = BuildCommandLine(shell, arguments);
                var environmentBlock = Marshal.StringToHGlobalUni(
                    string.Join('\0', EnvironmentVectorEntries(environment)) + "\0");
                try
                {
                    if (!CreateProcessW(
                            shell,
                            commandLine,
                            IntPtr.Zero,
                            IntPtr.Zero,
                            false,
                            ExtendedStartupInfoPresent | CreateSuspended | CreateUnicodeEnvironment,
                            environmentBlock,
                            workingDirectory,
                            ref startup,
                            out var process))
                    {
                        var creationError = Marshal.GetLastPInvokeError();
                        throw new HostOperationException(
                            "terminal_start_failed",
                            $"The terminal could not be started: CreateProcessW('{shell}', working directory "
                            + $"'{workingDirectory}') failed with error {creationError} "
                            + $"({Marshal.GetPInvokeErrorMessage(creationError)}).");
                    }

                    clientStarted = true;
                    thread = process.Thread;
                    processHandle = process.Process;
                    client = Process.GetProcessById((int)process.ProcessId);

                    // Opening the handle now pins it for the process object's lifetime, so the exit code stays observable
                    // after the shell is gone.
                    _ = client.SafeHandle;
                }
                finally
                {
                    Marshal.FreeHGlobal(environmentBlock);
                }

                // The process is suspended, so it cannot have started a child outside the job when it is assigned; the
                // ResumeThread below is the first instruction it ever runs.
                containment = Create();
                try
                {
                    containment.Assign(client);
                }
                catch (HostOperationException exception)
                {
                    throw new HostOperationException(
                        "terminal_start_failed",
                        $"The terminal's shell could not be contained before it ran: {exception.Message}");
                }

                if (ResumeThread(thread) == uint.MaxValue)
                {
                    throw PtyStartFailed("ResumeThread", Marshal.GetLastPInvokeError());
                }

                _ = CloseHandle(thread);
                thread = IntPtr.Zero;

                var started = new WindowsPtyProcess(containment, client, inputWrite!, outputRead!, pseudoConsole);
                inputWrite = null;
                outputRead = null;
                client = null;
                containment = null;
                pseudoConsole = IntPtr.Zero;
                processHandle = IntPtr.Zero;
                return started;
            }
            catch (Exception exception) when (exception is not HostOperationException)
            {
                throw new HostOperationException("terminal_start_failed", $"The terminal could not be started: {exception.Message}");
            }
            finally
            {
                // Fail-closed: every path out of here that did not transfer ownership leaves nothing running.
                if (containment is not null)
                {
                    _ = containment.Kill(out _);
                    containment.Dispose();
                }
                else if (clientStarted && processHandle != IntPtr.Zero)
                {
                    _ = TerminateProcess(processHandle, 1);
                }

                client?.Dispose();
                if (thread != IntPtr.Zero)
                {
                    _ = CloseHandle(thread);
                }

                if (processHandle != IntPtr.Zero)
                {
                    _ = CloseHandle(processHandle);
                }

                inputRead?.Dispose();
                inputWrite?.Dispose();
                outputRead?.Dispose();
                outputWrite?.Dispose();
                if (pseudoConsole != IntPtr.Zero)
                {
                    ClosePseudoConsole(pseudoConsole);
                }

                if (attributeList != IntPtr.Zero)
                {
                    DeleteProcThreadAttributeList(attributeList);
                    Marshal.FreeHGlobal(attributeList);
                }
            }
        }

        internal override bool TryResize(int columns, int rows, out string? problem)
        {
            problem = null;
            if (Volatile.Read(ref consoleClosed) != 0)
            {
                problem = "the pseudo console was already closed";
                return false;
            }

            var result = ResizePseudoConsole(pseudoConsole, new ConsoleCoordinate { X = (short)columns, Y = (short)rows });
            if (result == 0)
            {
                return true;
            }

            problem = $"ResizePseudoConsole failed with 0x{result:X8}";
            return false;
        }

        /// <summary>
        /// The job's accounting information counts the shell itself, so more than one active process means the shell has a
        /// live child - exactly what "closing this terminal would kill work the user started" means. A job that cannot be
        /// queried reports no foreground child rather than inventing one.
        /// </summary>
        internal override bool HasForegroundChild()
        {
            if (containment.job is not { } job || Volatile.Read(ref consoleClosed) != 0)
            {
                return false;
            }

            return QueryInformationJobObject(
                    job,
                    JobObjectBasicAccountingInformationClass,
                    out var information,
                    Marshal.SizeOf<JobObjectBasicAccountingInformation>(),
                    out _) &&
                information.ActiveProcesses > 1;
        }

        internal override string? KillTree() => containment.Kill(out var problem) ? null : problem;

        internal override void CloseConsole()
        {
            if (Interlocked.Exchange(ref consoleClosed, 1) != 0)
            {
                return;
            }

            ClosePseudoConsole(pseudoConsole);
        }

        public override void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            CloseConsole();
            OutputStream.Dispose();
            InputStream.Dispose();
            client.Dispose();
            containment.Dispose();
        }

        /// <summary>
        /// Quotes one argument the way <c>CommandLineToArgvW</c> and the C runtime parse it: backslashes are literal unless
        /// they precede a double quote, where they are doubled. Getting this wrong is how a shell argument containing a
        /// space silently becomes two arguments.
        /// </summary>
        private static string QuoteArgument(string argument)
        {
            if (argument.Length > 0 && argument.AsSpan().IndexOfAny(" \t\n\"") < 0)
            {
                return argument;
            }

            var builder = new StringBuilder(argument.Length + 2);
            builder.Append('"');
            var backslashes = 0;
            foreach (var character in argument)
            {
                if (character == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (character == '"')
                {
                    builder.Append('\\', (backslashes * 2) + 1);
                    backslashes = 0;
                    builder.Append('"');
                    continue;
                }

                builder.Append('\\', backslashes);
                backslashes = 0;
                builder.Append(character);
            }

            builder.Append('\\', backslashes * 2);
            builder.Append('"');
            return builder.ToString();
        }

        private static string BuildCommandLine(string shell, IReadOnlyList<string> arguments)
        {
            var builder = new StringBuilder(QuoteArgument(shell));
            foreach (var argument in arguments)
            {
                builder.Append(' ').Append(QuoteArgument(argument));
            }

            return builder.ToString();
        }
    }

    /// <summary>
    /// One <c>posix_openpt</c> terminal. The master descriptor is the only handle: it is read by the session's reader and
    /// written by its input writer, and closing it ends the terminal. The shell is spawned with <c>POSIX_SPAWN_SETSID</c>
    /// and its own reaping loop, which records the exit status the shared worker reaper deliberately discards.
    /// </summary>
    private sealed class PosixPtyProcess : PtyProcess
    {
        private readonly int processId;
        private readonly SafeFileHandle master;
        private readonly Task<int> exitCode;
        private int groupClosed;
        private string? reapProblem;
        private int disposed;

        private PosixPtyProcess(int processId, SafeFileHandle master)
        {
            this.processId = processId;
            this.master = master;

            // Two streams over the one descriptor, both unbuffered: the reader thread blocks in read(2) while the input
            // writer issues write(2), and a buffer in either direction would split one logical read into several frames.
            OutputStream = new FileStream(master, FileAccess.Read, bufferSize: 0, isAsync: false);
            InputStream = new FileStream(master, FileAccess.Write, bufferSize: 0, isAsync: false);

            // The reaper is the only source of this terminal's exit code: the shared worker reaper deliberately discards the
            // status the kernel reports, and this one keeps it.
            exitCode = ReapAsync(processId);
        }

        internal override int ProcessId => processId;

        internal override Stream OutputStream { get; }

        internal override Stream InputStream { get; }

        internal override Task<int> ExitCode => exitCode;

        internal static PosixPtyProcess Start(
            string shell,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            IReadOnlyDictionary<string, string> environment,
            int columns,
            int rows)
        {
            var masterDescriptor = posix_openpt(OpenReadWrite | MasterCloseOnExec);
            if (masterDescriptor < 0)
            {
                throw PtyStartFailed("posix_openpt", Marshal.GetLastPInvokeError());
            }

            var master = new SafeFileHandle((IntPtr)masterDescriptor, ownsHandle: true);
            var processId = 0;
            var actions = Marshal.AllocHGlobal(PosixSpawnOpaqueSize);
            var attributes = Marshal.AllocHGlobal(PosixSpawnOpaqueSize);
            var actionsInitialised = false;
            var attributesInitialised = false;
            var groupEstablished = false;
            var handed = false;
            try
            {
                if (grantpt(masterDescriptor) != 0)
                {
                    throw PtyStartFailed("grantpt", Marshal.GetLastPInvokeError());
                }

                if (unlockpt(masterDescriptor) != 0)
                {
                    throw PtyStartFailed("unlockpt", Marshal.GetLastPInvokeError());
                }

                var slavePath = SlaveName(masterDescriptor);

                // The size is set before the shell starts, so the first program that asks for the window size already sees
                // what the terminal was opened with instead of the kernel's 80x24 default.
                var size = new TerminalWindowSize
                {
                    Rows = (ushort)rows,
                    Columns = (ushort)columns,
                };
                CheckPtyCall("ioctl(TIOCSWINSZ)", ioctl(masterDescriptor, WindowSizeRequest(), ref size));

                CheckPtyCall("posix_spawn_file_actions_init", posix_spawn_file_actions_init(actions));
                actionsInitialised = true;
                CheckPtyCall("posix_spawnattr_init", posix_spawnattr_init(attributes));
                attributesInitialised = true;

                // The child gets the slave on 0, 1 and 2. Opening the slave is what makes it the controlling terminal of
                // the new session; opening it as descriptor 0 first keeps that single open as the one that counts.
                CheckPtyCall(
                    "posix_spawn_file_actions_addopen",
                    posix_spawn_file_actions_addopen(actions, 0, slavePath, OpenReadWrite, 0));
                CheckPtyCall("posix_spawn_file_actions_adddup2", posix_spawn_file_actions_adddup2(actions, 0, 1));
                CheckPtyCall("posix_spawn_file_actions_adddup2", posix_spawn_file_actions_adddup2(actions, 0, 2));

                // The master has no business in the child: an inherited master would keep the terminal alive and let the
                // child compete with the host for its output.
                CheckPtyCall("posix_spawn_file_actions_addclose", posix_spawn_file_actions_addclose(actions, masterDescriptor));
                CheckPtyCall(
                    "posix_spawn_file_actions_addchdir_np",
                    posix_spawn_file_actions_addchdir_np(actions, workingDirectory));

                CheckPtyCall("posix_spawnattr_setflags", posix_spawnattr_setflags(attributes, PosixSpawnSetSid));

                var commandLine = new List<string>(arguments.Count + 1) { shell };
                foreach (var argument in arguments)
                {
                    commandLine.Add(argument);
                }

                var argv = AllocateVector(commandLine);
                var envp = AllocateVector(EnvironmentVectorEntries(environment));
                try
                {
                    var spawnError = posix_spawnp(out processId, shell, actions, attributes, argv, envp);
                    if (spawnError != 0)
                    {
                        throw PtyStartFailed("posix_spawnp", spawnError);
                    }
                }
                finally
                {
                    ReleaseVector(argv);
                    ReleaseVector(envp);
                }

                // Fail-closed, the same check the worker containment makes: a shell that is not the leader of its own group
                // would make kill(-pgid) signal the host's group. SETSID makes this hold, so a mismatch means the spawn did
                // not establish what this class depends on.
                var groupId = getpgid(processId);
                if (groupId != processId)
                {
                    throw new HostOperationException(
                        "terminal_start_failed",
                        groupId < 0
                            ? $"The terminal's shell (pid {processId}) has no process group of its own: getpgid failed with error {Marshal.GetLastPInvokeError()}."
                            : $"The terminal's shell (pid {processId}) was placed in process group {groupId} instead of one of its own.");
                }

                groupEstablished = true;
                var started = new PosixPtyProcess(processId, master);
                handed = true;
                return started;
            }
            catch (Exception exception) when (exception is not HostOperationException)
            {
                throw new HostOperationException("terminal_start_failed", $"The terminal could not be started: {exception.Message}");
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

                Marshal.FreeHGlobal(actions);
                Marshal.FreeHGlobal(attributes);
                if (!handed)
                {
                    master.Dispose();
                }

                if (processId > 0 && !handed)
                {
                    // Nothing may survive a failed start. The group is only signalled when this method verified that the
                    // shell leads it; otherwise only the pid this method just created is killed, because an unrelated
                    // group could own that number.
                    _ = groupEstablished ? kill(-processId, SignalKill) : kill(processId, SignalKill);
                }
            }
        }

        /// <summary>The slave's device path. Linux has the reentrant <c>ptsname_r</c>; Darwin only has <c>ptsname</c>.</summary>
        private static string SlaveName(int masterDescriptor)
        {
            if (!OperatingSystem.IsMacOS())
            {
                var buffer = Marshal.AllocHGlobal(128);
                try
                {
                    CheckPtyCall("ptsname_r", ptsname_r(masterDescriptor, buffer, 128));
                    return Marshal.PtrToStringUTF8(buffer) ??
                        throw new HostOperationException("terminal_start_failed", "The terminal's slave name was empty.");
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }

            var name = ptsname(masterDescriptor);
            if (name == IntPtr.Zero)
            {
                throw PtyStartFailed("ptsname", Marshal.GetLastPInvokeError());
            }

            return Marshal.PtrToStringUTF8(name) ??
                throw new HostOperationException("terminal_start_failed", "The terminal's slave name was empty.");
        }

        private static int WindowSizeRequest() =>
            OperatingSystem.IsMacOS() ? SetTerminalWindowSizeDarwin : SetTerminalWindowSize;

        private static void CheckPtyCall(string name, int result)
        {
            if (result == 0)
            {
                return;
            }

            throw PtyStartFailed(name, Marshal.GetLastPInvokeError());
        }

        /// <summary>
        /// Reaps the shell and reports its exit code. The order is the one the worker reaper uses, because it is the only
        /// one that keeps the process group id from being reused before it is signalled: <c>waitid(WNOWAIT)</c> observes the
        /// exit while the dead child still holds its pid, the group is swept, and only then does <c>waitpid</c> release the
        /// pid. The exit status read in between is what makes "the shell was killed by a signal" reportable as 128 + signal
        /// instead of a fabricated code.
        /// </summary>
        private async Task<int> ReapAsync(int childProcessId)
        {
            var information = Marshal.AllocHGlobal(SignalInformationSize);
            try
            {
                var delay = 1;
                while (true)
                {
                    if (TryReap(childProcessId, information, out var exitCode))
                    {
                        return exitCode;
                    }

                    await Task.Delay(delay).ConfigureAwait(false);
                    delay = Math.Min(delay * 2, 100);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(information);
            }
        }

        /// <summary>
        /// Kills every process that is still in <paramref name="sessionId"/>'s session, and returns the first problem
        /// that left a survivor behind - <c>null</c> once the session is empty.
        /// <para>
        /// The shell's process group is not the whole tree. An interactive shell with job control puts each job into a
        /// process group of its own (that is exactly what makes <c>tcgetpgrp</c> answer the foreground question), and
        /// those groups stay in the shell's session. A session id is a pid and the shell holds it for as long as this
        /// runs - alive before it is reaped, a zombie afterwards - so no unrelated process can carry it: every member
        /// found here was started by this terminal.
        /// </para>
        /// <para>
        /// Linux answers membership from <c>/proc</c>. macOS has no <c>/proc</c>, so only the shell's own group is
        /// signalled there and a job-control child can outlive the shell - one more reason macOS remains unverified.
        /// </para>
        /// </summary>
        private static string? SweepSession(int sessionId)
        {
            string? problem = null;
            if (kill(-sessionId, SignalKill) != 0)
            {
                var groupError = Marshal.GetLastPInvokeError();
                if (groupError is not (NoSuchProcess or 0))
                {
                    problem = $"kill(-{sessionId}, SIGKILL) failed with error {groupError} while the terminal was being swept";
                }
            }

            if (OperatingSystem.IsMacOS())
            {
                return problem;
            }

            var deadline = Environment.TickCount64 + (long)PtySessionSweepWindow.TotalMilliseconds;
            while (true)
            {
                var survivors = SessionMembers(sessionId);
                if (survivors.Count == 0)
                {
                    return problem;
                }

                foreach (var member in survivors)
                {
                    if (kill(member, SignalKill) != 0)
                    {
                        var memberError = Marshal.GetLastPInvokeError();
                        if (memberError is not (NoSuchProcess or 0))
                        {
                            problem ??=
                                $"kill({member}, SIGKILL) failed with error {memberError} while the terminal's session was being swept";
                        }
                    }
                }

                if (Environment.TickCount64 >= deadline)
                {
                    return problem
                        ?? $"{survivors.Count} process(es) of the terminal's session {sessionId} were still alive "
                        + $"{PtySessionSweepWindow.TotalSeconds:0.###} s after they were killed";
                }

                Thread.Sleep(1);
            }
        }

        /// <summary>
        /// The live processes whose session is <paramref name="sessionId"/>, from <c>/proc</c>. Zombies are left out:
        /// they are already dead and only wait for a parent to collect them, so counting them would never terminate.
        /// </summary>
        private static List<int> SessionMembers(int sessionId)
        {
            var members = new List<int>();
            string[] entries;
            try
            {
                entries = Directory.GetDirectories("/proc");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return members;
            }

            foreach (var entry in entries)
            {
                if (!int.TryParse(Path.GetFileName(entry), out var process) || process == sessionId)
                {
                    continue;
                }

                string stat;
                try
                {
                    stat = File.ReadAllText(Path.Combine(entry, "stat"));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                var close = stat.LastIndexOf(')');
                if (close < 0)
                {
                    continue;
                }

                // Fields after "comm)": state, ppid, pgrp, session, tty_nr, ...
                var fields = stat[(close + 2)..].Split(' ');
                if (fields.Length < 4
                    || fields[0] is ['Z', ..] or ['X', ..]
                    || !int.TryParse(fields[3], out var session)
                    || session != sessionId)
                {
                    continue;
                }

                members.Add(process);
            }

            return members;
        }

        private bool TryReap(int childProcessId, IntPtr information, out int exitCode)
        {
            exitCode = -1;
            Marshal.Copy(ZeroedSignalInformation, 0, information, SignalInformationSize);
            var observed = waitid(
                WaitForProcessId,
                childProcessId,
                information,
                WaitReportExit | WaitNoHang | WaitLeaveChild);
            if (observed != 0)
            {
                // EINTR learned nothing. ECHILD/ESRCH mean the pid is out of the process table already, so the group id is
                // free as well and this loop closes the group without signalling it again.
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

            // The child is a zombie: its pid is reserved, so the session it led can only contain processes it started.
            // Sweep all of them before the waitpid below makes those ids reusable. The session, not the process group,
            // is the unit that has to die: an interactive shell with job control gives every job a process group of its
            // own, so `kill(-pgid)` alone leaves editors, builds and agent CLIs running - and holding the only slave
            // descriptors, which keeps the master from ever reporting end of file.
            Volatile.Write(ref reapProblem, SweepSession(childProcessId));
            Volatile.Write(ref groupClosed, 1);
            exitCode = ExitCodeOf(information);

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

        /// <summary>128 + signal for a process that was killed, the shell's own code when it exited, and a failure code
        /// when the kernel reported neither.</summary>
        private static int ExitCodeOf(IntPtr information)
        {
            var status = Marshal.ReadInt32(information, ReportedStatusOffset);
            var code = Marshal.ReadInt32(information, ReportedCodeOffset);
            return code == ChildExitedNormally ? status : SignalTerminationBase + status;
        }

        internal override bool TryResize(int columns, int rows, out string? problem)
        {
            problem = null;
            if (Volatile.Read(ref disposed) != 0)
            {
                problem = "the terminal was already closed";
                return false;
            }

            var size = new TerminalWindowSize { Rows = (ushort)rows, Columns = (ushort)columns };
            if (ioctl((int)master.DangerousGetHandle(), WindowSizeRequest(), ref size) == 0)
            {
                return true;
            }

            problem = $"ioctl(TIOCSWINSZ) failed with error {Marshal.GetLastPInvokeError()}";
            return false;
        }

        /// <summary>The terminal's foreground process group is the shell's own only while the shell holds the terminal.</summary>
        internal override bool HasForegroundChild()
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                return false;
            }

            var foreground = tcgetpgrp((int)master.DangerousGetHandle());
            return foreground >= 0 && foreground != processId;
        }

        internal override string? KillTree()
        {
            if (Volatile.Read(ref groupClosed) != 0)
            {
                // The reaper closed the group at the only moment its id was provably still the shell's; signalling it again
                // could hit a group an unrelated process owns now that the pid has been released.
                return Volatile.Read(ref reapProblem);
            }

            // The shell is still alive (or at least not reaped), so its session id is reserved and the sweep can only
            // find this terminal's processes. It kills the shell's own group first and then every job-control group the
            // shell created, which `kill(-pgid)` cannot reach.
            return SweepSession(processId);
        }

        internal override void CloseConsole()
        {
            // Nothing to release: once the shell and its group are gone, every slave descriptor is closed and the master
            // read reports end of file by itself.
        }

        public override void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            OutputStream.Dispose();
            InputStream.Dispose();
            master.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ConsoleCoordinate
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TerminalWindowSize
    {
        public ushort Rows;
        public ushort Columns;
        public ushort PixelWidth;
        public ushort PixelHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicAccountingInformation
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

#pragma warning disable SYSLIB1054
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(
        out SafeFileHandle? readPipe,
        out SafeFileHandle? writePipe,
        IntPtr pipeAttributes,
        int size);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int CreatePseudoConsole(
        ConsoleCoordinate size,
        SafeFileHandle input,
        SafeFileHandle output,
        uint flags,
        out IntPtr pseudoConsole);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr pseudoConsole, ConsoleCoordinate size);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern void ClosePseudoConsole(IntPtr pseudoConsole);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr attributeList,
        int attributeCount,
        int flags,
        ref IntPtr size);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr attributeList,
        uint flags,
        IntPtr attribute,
        IntPtr value,
        IntPtr size,
        IntPtr previousValue,
        IntPtr returnSize);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string? applicationName,
        string commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(
        SafeJobHandle job,
        int informationClass,
        out JobObjectBasicAccountingInformation information,
        int length,
        out int returnLength);

    [DllImport("libc", ExactSpelling = true, SetLastError = true)]
    private static extern int posix_openpt(int flags);

    [DllImport("libc", ExactSpelling = true, SetLastError = true)]
    private static extern int grantpt(int descriptor);

    [DllImport("libc", ExactSpelling = true, SetLastError = true)]
    private static extern int unlockpt(int descriptor);

    [DllImport("libc", ExactSpelling = true, SetLastError = true)]
    private static extern int ptsname_r(int descriptor, IntPtr buffer, nuint length);

    [DllImport("libc", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr ptsname(int descriptor);

    [DllImport("libc", ExactSpelling = true, SetLastError = true)]
    private static extern int ioctl(int descriptor, int request, ref TerminalWindowSize size);

    [DllImport("libc", ExactSpelling = true, SetLastError = true)]
    private static extern int tcgetpgrp(int descriptor);

    [DllImport("libc", ExactSpelling = true, SetLastError = true)]
    private static extern int posix_spawn_file_actions_addopen(
        IntPtr actions,
        int descriptor,
        string path,
        int flags,
        uint mode);

    [DllImport("libc", ExactSpelling = true, SetLastError = true)]
    private static extern int posix_spawn_file_actions_addchdir_np(IntPtr actions, string path);
#pragma warning restore SYSLIB1054
}
