using Forge.App.Services;

namespace Forge.App.Tests.Services;

/// <summary>
/// A one-shot process as the applier sees it: it either confirms (the test writes the marker), exits early, or does nothing
/// until the applier's watchdog fires. <see cref="Kill"/> is recorded, because "the unconfirmed version is stopped before the
/// swap is undone" is a behaviour worth pinning.
/// </summary>
internal sealed class FakeApplierProcess(string path, int id) : IUpdateApplierProcess
{
    public string Path { get; } = path;

    public int Id { get; } = id;

    public bool HasExited { get; private set; }

    public int ExitCode { get; private set; }

    public int Kills { get; private set; }

    public bool Disposed { get; private set; }

    /// <summary>Played by <see cref="Kill"/>, so a test can make the version confirm at the very last moment.</summary>
    public Action? OnKill { get; set; }

    /// <summary>The process ends on its own, with <paramref name="code"/>.</summary>
    public void Exit(int code)
    {
        ExitCode = code;
        HasExited = true;
    }

    public void Kill()
    {
        Kills++;
        ExitCode = -1;
        HasExited = true;
        OnKill?.Invoke();
    }

    public void Dispose() => Disposed = true;
}

/// <summary>
/// The applier's two operating-system interactions without an operating system: the exit wait answers from
/// <see cref="OldProcessExits"/>, and every start is recorded and then handed to <see cref="OnStart"/> so a test can play the
/// new version (write the confirmation, exit early, hang).
/// </summary>
internal sealed class FakeUpdateApplyProcesses : IUpdateApplyProcesses
{
    private readonly Dictionary<int, FakeApplierProcess> started = [];

    private int nextId = 1000;

    /// <summary>What the exit wait answers for process ids this fake did not start (the instance being replaced).</summary>
    public bool OldProcessExits { get; set; } = true;

    /// <summary>When false, every start fails the way a locked executable would.</summary>
    public bool StartSucceeds { get; set; } = true;

    public List<int> Waited { get; } = [];

    public List<(string Path, IReadOnlyList<string> Arguments)> Starts { get; } = [];

    public List<FakeApplierProcess> Processes { get; } = [];

    /// <summary>Played for every start, after the process is recorded.</summary>
    public Action<FakeApplierProcess, IReadOnlyList<string>>? OnStart { get; set; }

    public Task<bool> WaitForExitAsync(int processId, TimeSpan timeout, CancellationToken ct)
    {
        Waited.Add(processId);
        return Task.FromResult(started.TryGetValue(processId, out var process) ? process.HasExited : OldProcessExits);
    }

    public IUpdateApplierProcess? Start(string executablePath, IReadOnlyList<string> arguments)
    {
        if (!StartSucceeds)
        {
            return null;
        }

        var process = new FakeApplierProcess(executablePath, nextId++);
        started[process.Id] = process;
        Starts.Add((executablePath, arguments));
        Processes.Add(process);
        OnStart?.Invoke(process, arguments);
        return process;
    }
}

/// <summary>
/// One arranged install: a throw-away data root with the <c>updates</c> directory, an install directory holding the published
/// executable (the two are siblings, exactly as a real-data-on-another-volume layout), a staged tree, and the runner wired to
/// the fake process seam with short timeouts.
/// </summary>
internal sealed class UpdateApplySandbox : IDisposable
{
    private UpdateApplySandbox(
        string root,
        string dataRoot,
        string install,
        string staging,
        FakeUpdateApplyProcesses processes,
        UpdateApplyOptions options,
        List<string> log)
    {
        Root = root;
        DataRoot = dataRoot;
        Install = install;
        Staging = staging;
        Processes = processes;
        Options = options;
        Log = log;
        Runner = new UpdateApplyRunner(dataRoot, options, processes, log.Add);
        Request = new UpdateApplyRequest(4242, install, staging, Version);
    }

    public const string Version = "1.2.0";

    public string Root { get; }

    public string DataRoot { get; }

    public string Install { get; }

    public string Staging { get; }

    public FakeUpdateApplyProcesses Processes { get; }

    public UpdateApplyOptions Options { get; }

    public List<string> Log { get; }

    public UpdateApplyRunner Runner { get; }

    public UpdateApplyRequest Request { get; private set; }

    public string MarkerPath => UpdateApplyPaths.Marker(DataRoot, Version);

    public string OldDirectory => UpdateApplyPaths.OldDirectory(Install, Version);

    public string FailedDirectory => UpdateApplyPaths.FailedDirectory(Install, Version);

    public string Executable => UpdateApplyPaths.AppExecutable(Install);

    /// <summary>What is in the install directory now: a marker file per tree, so a swap is visible at a glance.</summary>
    public string InstallMarker => Path.Combine(Install, "tree.txt");

    public void UseRecovery() => Request = Request with { Recovery = true };

    public void UseStaging(string staging) => Request = Request with { StagingDirectory = staging };

    public static UpdateApplySandbox Create(
        TimeSpan? confirmationTimeout = null,
        TimeSpan? processExitTimeout = null)
    {
        var root = TestTempFiles.NewDirectory("forge-apply-");
        var dataRoot = Path.Combine(root, "data");
        var install = Path.Combine(root, "install");
        var staging = Path.Combine(dataRoot, "updates", Version, UpdateApplyPaths.PayloadDirectoryName);
        Directory.CreateDirectory(Path.Combine(dataRoot, "updates"));
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(staging);

        WriteTree(install, "old");
        WriteTree(staging, "new");

        var options = new UpdateApplyOptions
        {
            ProcessExitTimeout = processExitTimeout ?? TimeSpan.FromSeconds(2),
            ConfirmationTimeout = confirmationTimeout ?? TimeSpan.FromMilliseconds(150),
            ConfirmationPollInterval = TimeSpan.FromMilliseconds(5),
            KillGrace = TimeSpan.FromSeconds(2),
        };

        return new UpdateApplySandbox(root, dataRoot, install, staging, new FakeUpdateApplyProcesses(), options, []);
    }

    /// <summary>The confirmation of the new version: what the app does once its main window is up.</summary>
    public void Confirm()
    {
        File.WriteAllText(MarkerPath, "confirmed");
    }

    /// <summary>A tree with its executable and a file that says which side of the swap it is.</summary>
    public static void WriteTree(string directory, string name)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "tree.txt"), name);
        File.WriteAllText(UpdateApplyPaths.AppExecutable(directory), "#!/bin/sh\nexit 0\n");
    }

    public void Dispose() => TestTempFiles.DeleteTree(Root);
}
