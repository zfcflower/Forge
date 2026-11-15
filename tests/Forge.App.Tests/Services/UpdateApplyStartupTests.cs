using System.IO.Compression;
using System.Text;
using Forge.App.Services;
using Xunit;

namespace Forge.App.Tests.Services;

/// <summary>
/// Wave W10c's app side and package handling: the confirmation marker (and the check that it names this process's own
/// install directory), the startup housekeeping that makes a confirmed swap permanent, the half-swap repair that hands an
/// unconfirmed install back to the previous version, and the install entry that unpacks a verified package and starts the
/// applier with the directory this process runs from.
/// </summary>
public sealed class UpdateApplyStartupTests
{
    // ---------------------------------------------------------------- the confirmation marker

    [Fact]
    public void The_confirmation_is_written_only_for_the_directory_this_process_runs_from()
    {
        using var root = new TempTree("forge-confirm-");
        var dataRoot = root.Combine("data");
        var install = root.Combine("install");
        UpdateApplySandbox.WriteTree(install, "new");

        Assert.True(UpdateApplyStartup.TryConfirm(dataRoot, install, "1.2.0", install, out var written), written);
        Assert.True(File.Exists(UpdateApplyPaths.Marker(dataRoot, "1.2.0")));

        var elsewhere = root.Combine("elsewhere");
        UpdateApplySandbox.WriteTree(elsewhere, "elsewhere");
        Assert.False(UpdateApplyStartup.TryConfirm(dataRoot, elsewhere, "1.3.0", install, out var refused));
        Assert.Contains("runs from", refused, StringComparison.Ordinal);
        Assert.False(File.Exists(UpdateApplyPaths.Marker(dataRoot, "1.3.0")));
    }

    [Fact]
    public void A_confirmation_with_an_unusable_version_writes_nothing()
    {
        using var root = new TempTree("forge-confirm-");
        var dataRoot = root.Combine("data");
        var install = root.Combine("install");
        UpdateApplySandbox.WriteTree(install, "new");

        Assert.False(UpdateApplyStartup.TryConfirm(dataRoot, install, "../1.2.0", install, out var reason));
        Assert.Contains("file-name token", reason, StringComparison.Ordinal);
        Assert.False(Directory.Exists(UpdateApplyPaths.UpdatesRoot(dataRoot)));
    }

    [Fact]
    public void The_confirmation_launch_is_read_from_the_command_line()
    {
        using var root = new TempTree("forge-confirm-");
        var install = root.Combine("install");

        Assert.True(UpdateApplyStartup.TryReadConfirmation(
            ["Forge.App", UpdateApplyRequest.ConfirmedSwitch, "1.2.0", UpdateApplyRequest.InstallSwitch, install],
            out var version,
            out var read));
        Assert.Equal("1.2.0", version);
        Assert.Equal(install, read);

        Assert.False(UpdateApplyStartup.TryReadConfirmation(["Forge.App"], out _, out _));
        Assert.False(UpdateApplyStartup.TryReadConfirmation(
            ["Forge.App", UpdateApplyRequest.ConfirmedSwitch, "1.2.0"],
            out _,
            out _));
        Assert.False(UpdateApplyStartup.TryReadConfirmation(
            ["Forge.App", UpdateApplyRequest.ConfirmedSwitch, "1.2.0", UpdateApplyRequest.InstallSwitch, "relative"],
            out _,
            out _));
    }

    // ---------------------------------------------------------------- startup housekeeping

    [Fact]
    public void The_backup_of_a_confirmed_swap_and_the_trees_of_failed_installs_are_removed()
    {
        using var root = new TempTree("forge-maintain-");
        var dataRoot = root.Combine("data");
        var install = root.Combine("install");
        UpdateApplySandbox.WriteTree(install, "current");
        var old = UpdateApplyPaths.OldDirectory(install, "1.2.0");
        UpdateApplySandbox.WriteTree(old, "previous");
        var failed = UpdateApplyPaths.FailedDirectory(install, "1.3.0");
        UpdateApplySandbox.WriteTree(failed, "broken");

        Directory.CreateDirectory(UpdateApplyPaths.UpdatesRoot(dataRoot));
        File.WriteAllText(UpdateApplyPaths.Marker(dataRoot, "1.2.0"), "confirmed");
        File.WriteAllText(UpdateApplyPaths.Marker(dataRoot, "9.9.9"), "stale");

        var maintenance = UpdateApplyStartup.Maintain(dataRoot, install);

        Assert.Contains(old, maintenance.Removed);
        Assert.Contains(failed, maintenance.Removed);
        Assert.False(Directory.Exists(old));
        Assert.False(Directory.Exists(failed));
        Assert.False(File.Exists(UpdateApplyPaths.Marker(dataRoot, "1.2.0")));
        Assert.False(File.Exists(UpdateApplyPaths.Marker(dataRoot, "9.9.9")));
        Assert.Empty(maintenance.LeftBehind);
    }

    [Fact]
    public void An_unconfirmed_backup_is_left_in_place_for_the_startup_repair()
    {
        using var root = new TempTree("forge-maintain-");
        var dataRoot = root.Combine("data");
        var install = root.Combine("install");
        UpdateApplySandbox.WriteTree(install, "unconfirmed");
        var old = UpdateApplyPaths.OldDirectory(install, "1.2.0");
        UpdateApplySandbox.WriteTree(old, "previous");

        var maintenance = UpdateApplyStartup.Maintain(dataRoot, install);

        Assert.True(Directory.Exists(old));
        Assert.Contains(old, maintenance.LeftBehind);
        Assert.False(File.Exists(UpdateApplyPaths.Marker(dataRoot, "1.2.0")));
    }

    [Fact]
    public void The_backup_of_the_run_that_just_confirmed_survives_until_the_next_start()
    {
        using var root = new TempTree("forge-maintain-");
        var dataRoot = root.Combine("data");
        var install = root.Combine("install");
        UpdateApplySandbox.WriteTree(install, "new");
        var old = UpdateApplyPaths.OldDirectory(install, "1.2.0");
        UpdateApplySandbox.WriteTree(old, "previous");
        Directory.CreateDirectory(UpdateApplyPaths.UpdatesRoot(dataRoot));
        File.WriteAllText(UpdateApplyPaths.Marker(dataRoot, "1.2.0"), "confirmed");

        UpdateApplyStartup.Maintain(dataRoot, install, preserveOldVersion: "1.2.0");
        Assert.True(Directory.Exists(old));

        UpdateApplyStartup.Maintain(dataRoot, install);
        Assert.False(Directory.Exists(old));
    }

    // ---------------------------------------------------------------- the half-swap repair

    [Fact]
    public void The_half_swap_is_composed_as_a_rollback_of_this_install_directory()
    {
        using var root = new TempTree("forge-repair-");
        var dataRoot = root.Combine("data");
        var install = root.Combine("install");
        UpdateApplySandbox.WriteTree(install, "unconfirmed");
        var old = UpdateApplyPaths.OldDirectory(install, "1.2.0");
        UpdateApplySandbox.WriteTree(old, "previous");

        Assert.True(UpdateApplyStartup.TryComposeRepair(dataRoot, install, "1.2.0", 777, out var request, out var reason), reason);
        Assert.NotNull(request);
        Assert.True(request!.Recovery);
        Assert.Equal(777, request.OldProcessId);
        Assert.Equal(install, request.InstallDirectory);
        Assert.Equal(old, request.StagingDirectory);

        // The request the repair composes is one the applier accepts.
        Assert.True(UpdateApplyRequest.TryParse(request.ToArguments(), dataRoot, out _, out var parseError), parseError);
    }

    [Fact]
    public void A_confirmed_or_missing_backup_is_not_a_half_swap()
    {
        using var root = new TempTree("forge-repair-");
        var dataRoot = root.Combine("data");
        var install = root.Combine("install");
        UpdateApplySandbox.WriteTree(install, "current");
        Directory.CreateDirectory(UpdateApplyPaths.UpdatesRoot(dataRoot));

        Assert.False(UpdateApplyStartup.TryComposeRepair(dataRoot, install, "1.2.0", 777, out _, out _));

        UpdateApplySandbox.WriteTree(UpdateApplyPaths.OldDirectory(install, "1.2.0"), "previous");
        File.WriteAllText(UpdateApplyPaths.Marker(dataRoot, "1.2.0"), "confirmed");
        Assert.False(UpdateApplyStartup.TryComposeRepair(dataRoot, install, "1.2.0", 777, out _, out _));

        File.Delete(UpdateApplyPaths.Marker(dataRoot, "1.2.0"));
        File.Delete(UpdateApplyPaths.AppExecutable(UpdateApplyPaths.OldDirectory(install, "1.2.0")));
        Assert.False(UpdateApplyStartup.TryComposeRepair(dataRoot, install, "1.2.0", 777, out _, out var reason));
        Assert.Contains("does not hold", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_startup_repair_starts_the_previous_version_and_lets_the_caller_exit()
    {
        using var root = new TempTree("forge-repair-");
        var dataRoot = root.Combine("data");
        var install = root.Combine("install");
        UpdateApplySandbox.WriteTree(install, "unconfirmed");
        var old = UpdateApplyPaths.OldDirectory(install, "1.2.0");
        UpdateApplySandbox.WriteTree(old, "previous");

        UpdateApplyRequest? started = null;
        var exitCode = UpdateApplyStartup.RepairInterruptedSwap(
            dataRoot,
            args: ["Forge.App"],
            install: install,
            ownVersion: "1.2.0",
            oldProcessId: 777,
            start: request =>
            {
                started = request;
                return true;
            });

        Assert.Equal(0, exitCode);
        Assert.NotNull(started);
        Assert.True(started!.Recovery);
        Assert.Equal(old, started.StagingDirectory);

        // The confirming launch is the applier's, not a repair.
        UpdateApplyRequest? notStarted = null;
        Assert.Null(UpdateApplyStartup.RepairInterruptedSwap(
            dataRoot,
            [UpdateApplyRequest.ConfirmedSwitch, "1.2.0", UpdateApplyRequest.InstallSwitch, install],
            install,
            "1.2.0",
            777,
            request =>
            {
                notStarted = request;
                return true;
            }));
        Assert.Null(notStarted);

        // A repair that cannot be started leaves the app starting (a broken launch would be worse).
        Assert.Null(UpdateApplyStartup.RepairInterruptedSwap(
            dataRoot,
            ["Forge.App"],
            install,
            "1.2.0",
            777,
            _ => false));
    }

    [Fact]
    public void The_start_window_then_the_next_start_remove_the_backup()
    {
        using var root = new TempTree("forge-shown-");
        var dataRoot = root.Combine("data");
        var install = root.Combine("install");
        UpdateApplySandbox.WriteTree(install, "new");
        var old = UpdateApplyPaths.OldDirectory(install, "1.2.0");
        UpdateApplySandbox.WriteTree(old, "previous");
        var lines = new List<string>();

        UpdateApplyStartup.RunAfterMainWindowShown(
            dataRoot,
            ["Forge.App", UpdateApplyRequest.ConfirmedSwitch, "1.2.0", UpdateApplyRequest.InstallSwitch, install],
            install,
            ownVersion: "1.2.0",
            lines.Add);

        Assert.True(File.Exists(UpdateApplyPaths.Marker(dataRoot, "1.2.0")));
        Assert.True(Directory.Exists(old));
        Assert.Contains(lines, line => line.Contains("confirmed the install of 1.2.0", StringComparison.Ordinal));

        // The next start has no confirmation arguments; the backup of the confirmed swap goes away.
        UpdateApplyStartup.RunAfterMainWindowShown(dataRoot, ["Forge.App"], install, ownVersion: "1.2.0", lines.Add);

        Assert.False(Directory.Exists(old));
        Assert.False(File.Exists(UpdateApplyPaths.Marker(dataRoot, "1.2.0")));
    }

    [Fact]
    public void A_confirmation_for_another_directory_is_not_written()
    {
        using var root = new TempTree("forge-shown-");
        var dataRoot = root.Combine("data");
        var install = root.Combine("install");
        var elsewhere = root.Combine("elsewhere");
        UpdateApplySandbox.WriteTree(install, "new");
        UpdateApplySandbox.WriteTree(elsewhere, "elsewhere");
        var lines = new List<string>();

        UpdateApplyStartup.RunAfterMainWindowShown(
            dataRoot,
            ["Forge.App", UpdateApplyRequest.ConfirmedSwitch, "1.2.0", UpdateApplyRequest.InstallSwitch, elsewhere],
            install,
            ownVersion: "1.2.0",
            lines.Add);

        Assert.False(File.Exists(UpdateApplyPaths.Marker(dataRoot, "1.2.0")));
        Assert.Contains(lines, line => line.Contains("was not confirmed", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- the install entry

    [Fact]
    public async Task A_verified_package_is_unpacked_and_the_applier_starts_on_this_install_directory()
    {
        using var sandbox = await InstallSandbox.CreateAsync();

        var outcome = await sandbox.Service.InstallAndRestartAsync(CancellationToken.None);

        Assert.True(outcome.Started, outcome.Message.Resolve());
        Assert.NotNull(sandbox.Started);
        Assert.Equal(Environment.ProcessId, sandbox.Started!.OldProcessId);
        Assert.Equal(sandbox.Install, sandbox.Started.InstallDirectory);
        Assert.Equal("1.2.0", sandbox.Started.Version);
        Assert.False(sandbox.Started.Recovery);
        Assert.True(UpdateApplyPaths.IsStrictlyInside(sandbox.Started.StagingDirectory, UpdateApplyPaths.UpdatesRoot(sandbox.DataRoot)));
        Assert.True(File.Exists(UpdateApplyPaths.AppExecutable(sandbox.Started.StagingDirectory)));
        Assert.True(File.Exists(Path.Combine(sandbox.Started.StagingDirectory, "payload.txt")));
    }

    [Fact]
    public async Task An_install_without_a_verified_package_is_refused()
    {
        using var sandbox = InstallSandbox.Create();

        var outcome = await sandbox.Service.InstallAndRestartAsync(CancellationToken.None);

        Assert.False(outcome.Started);
        Assert.Null(sandbox.Started);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Message.Resolve()));
    }

    [Fact]
    public async Task A_package_that_disappeared_after_the_check_is_refused()
    {
        using var sandbox = await InstallSandbox.CreateAsync();
        File.Delete(sandbox.Service.StagedPackagePath!);

        var outcome = await sandbox.Service.InstallAndRestartAsync(CancellationToken.None);

        Assert.False(outcome.Started);
        Assert.Null(sandbox.Started);
        Assert.NotEqual(
            Forge.App.Localization.UpdatesStrings.InstallErrorNothingStaged.Resolve(),
            outcome.Message.Resolve());
        Assert.Contains("Forge-1.2.0", outcome.Message.Resolve(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_applier_that_cannot_be_started_is_reported()
    {
        using var sandbox = await InstallSandbox.CreateAsync(start: _ => false);

        var outcome = await sandbox.Service.InstallAndRestartAsync(CancellationToken.None);

        Assert.False(outcome.Started);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Message.Resolve()));
    }

    // ---------------------------------------------------------------- the settings section

    [Fact]
    public async Task The_install_command_needs_a_verified_package_a_confirmation_and_the_app_closing()
    {
        using var sandbox = InstallSandbox.Create();
        sandbox.ArrangeOffer();
        using var section = new Forge.App.Settings.UpdatesSettingsSectionViewModel(new Forge.App.Preferences.AppPreferences(), sandbox.Service);
        Assert.False(section.CanInstallAndRestart);
        Assert.False(section.InstallAndRestartCommand.CanExecute(null));

        await section.CheckNowAsync();
        Assert.True(section.CanInstallAndRestart);
        Assert.True(section.InstallAndRestartCommand.CanExecute(null));

        var confirmations = 0;
        section.ConfirmInstallAsync = _ =>
        {
            confirmations++;
            return Task.FromResult(false);
        };
        await section.InstallAndRestartAsync();

        Assert.Equal(1, confirmations);
        Assert.Null(sandbox.Started);
        Assert.False(section.HasInstallResult);

        var exits = 0;
        section.ConfirmInstallAsync = _ =>
        {
            confirmations++;
            return Task.FromResult(true);
        };
        section.RequestExit = () => exits++;
        await section.InstallAndRestartAsync();

        Assert.Equal(2, confirmations);
        Assert.NotNull(sandbox.Started);
        Assert.Equal(1, exits);
        Assert.True(section.HasInstallResult);
        Assert.Contains("1.2.0", section.InstallText, StringComparison.Ordinal);
    }

    /// <summary>
    /// One arranged install entry: a data root with the <c>updates</c> tree, a sibling install directory holding the
    /// published executable, a real zip package served over the scripted handler, and the service wired to a recording
    /// applier instead of a process.
    /// </summary>
    private sealed class InstallSandbox : IDisposable
    {
        private readonly Func<UpdateApplyRequest?> readStarted;

        private InstallSandbox(
            string root,
            string dataRoot,
            string install,
            Forge.App.Services.UpdateService service,
            ScriptedUpdateHandler handler,
            Func<UpdateApplyRequest?> readStarted)
        {
            Root = root;
            DataRoot = dataRoot;
            Install = install;
            Service = service;
            Handler = handler;
            this.readStarted = readStarted;
        }

        public string Root { get; }

        public string DataRoot { get; }

        public string Install { get; }

        public Forge.App.Services.UpdateService Service { get; }

        public ScriptedUpdateHandler Handler { get; }

        /// <summary>The request the service handed to the applier seam, if it got that far.</summary>
        public UpdateApplyRequest? Started => readStarted();

        public static InstallSandbox Create(Func<UpdateApplyRequest, bool>? start = null)
        {
            var root = TestTempFiles.NewDirectory("forge-install-");
            var dataRoot = Path.Combine(root, "data");
            var install = Path.Combine(root, "install");
            Directory.CreateDirectory(dataRoot);
            UpdateApplySandbox.WriteTree(install, "current");

            var handler = new ScriptedUpdateHandler();
            var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            var launcher = start ?? (_ => true);
            UpdateApplyRequest? started = null;
            var service = new Forge.App.Services.UpdateService(
                http,
                Path.Combine(dataRoot, "updates"),
                currentVersion: "1.0.0",
                manifestUrl: () => UpdateTestManifest.ManifestUrl,
                channel: () => "stable",
                checkFrequency: () => UpdateSchedule.Never,
                uiState: null,
                rid: "win-x64",
                time: null,
                openFolder: _ => { },
                installDirectory: install,
                startApplier: request =>
                {
                    started = request;
                    return launcher(request);
                });
            return new InstallSandbox(root, dataRoot, install, service, handler, () => started);
        }

        /// <summary>Runs one check so a package is downloaded, verified and staged — the precondition of an install.</summary>
        public static async Task<InstallSandbox> CreateAsync(Func<UpdateApplyRequest, bool>? start = null)
        {
            var sandbox = Create(start);
            sandbox.ArrangeOffer();
            var outcome = await sandbox.Service.CheckForUpdateAsync(manual: true, CancellationToken.None);
            Assert.Equal(UpdateCheckStatus.UpdateAvailable, outcome.Status);
            return sandbox;
        }

        /// <summary>Serves the next check a real zip package for version 1.2.0.</summary>
        public void ArrangeOffer()
        {
            var payload = Zip("1.2.0");
            var packageUrl = "https://example.test/package/Forge-1.2.0-win-x64.zip";
            Handler.Manifest(UpdateTestManifest.ManifestUrl, UpdateTestManifest.Stable("1.2.0", "win-x64", packageUrl, payload));
            Handler.Package(packageUrl, payload);
        }

        private static byte[] Zip(string version)
        {
            var appName = UpdateApplyPaths.AppExecutableName;
            using var stream = new MemoryStream();
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                Write(archive, appName, "#!/bin/sh\nexit 0\n");
                Write(archive, "payload.txt", "version " + version);
            }

            return stream.ToArray();

            static void Write(ZipArchive archive, string name, string content)
            {
                var entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write(content);
            }
        }

        public void Dispose()
        {
            Service.Dispose();
            TestTempFiles.DeleteTree(Root);
        }
    }

    /// <summary>A throw-away directory tree with path helpers and a best-effort teardown.</summary>
    private sealed class TempTree : IDisposable
    {
        public TempTree(string prefix) => Root = TestTempFiles.NewDirectory(prefix);

        public string Root { get; }

        public string Combine(string name) => Path.Combine(Root, name);

        public void Dispose() => TestTempFiles.DeleteTree(Root);
    }
}
