using System.IO.Compression;
using System.Net;
using System.Text;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Services;
using Forge.App.Settings;
using Xunit;

namespace Forge.App.Tests.Services;

/// <summary>
/// The install entry and the settings section's install command: the readable failure of an unpack or an unusable version
/// token, the mutual exclusion of a running install, and the confirmation → exit seam the view supplies. The service side
/// already refuses a missing package and a start that fails; this file pins the branches the view reaches and the state the
/// section exposes while an install is in flight.
/// </summary>
public sealed class UpdateApplyInstallFlowTests
{
    // ---------------------------------------------------------------- the service's failure branches

    [Fact]
    public async Task An_unpackable_package_is_reported_as_an_unpack_failure()
    {
        using var sandbox = UpdateTestService.Build(currentVersion: "1.0.0");
        UpdateTestService.SetStableOffer(sandbox.Handler, "1.2.0", "win-x64", out _);
        Assert.Equal(UpdateCheckStatus.UpdateAvailable, (await sandbox.Service.CheckForUpdateAsync(manual: true, CancellationToken.None)).Status);

        var outcome = await sandbox.Service.InstallAndRestartAsync(CancellationToken.None);

        Assert.False(outcome.Started);
        Assert.Equal(UpdatesStrings.InstallErrorUnpackKey, outcome.Message.Key);
    }

    [Fact]
    public async Task An_offer_whose_version_is_not_a_file_name_token_is_refused_before_unpacking()
    {
        // The download accepts any parseable numeric prefix plus a long prerelease suffix; the install protocol requires a
        // file-name token of at most UpdateApplyPaths.MaxVersionLength, which is stricter.
        using var sandbox = UpdateTestService.Build(currentVersion: "1.0.0");
        var version = "1.2.3-" + new string('a', 60);
        UpdateTestService.SetStableOffer(sandbox.Handler, version, "win-x64", out _);
        Assert.Equal(UpdateCheckStatus.UpdateAvailable, (await sandbox.Service.CheckForUpdateAsync(manual: true, CancellationToken.None)).Status);

        var outcome = await sandbox.Service.InstallAndRestartAsync(CancellationToken.None);

        Assert.False(outcome.Started);
        Assert.Equal(UpdatesStrings.InstallErrorVersionKey, outcome.Message.Key);
    }

    // ---------------------------------------------------------------- the settings section

    [Fact]
    public async Task An_install_turns_off_the_command_while_it_runs_and_recovers_afterwards()
    {
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        using var sandbox = InstallFlowSandbox.Create(start: _ =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return true;
        });
        await sandbox.WithStagedOfferAsync();
        using var section = new UpdatesSettingsSectionViewModel(new AppPreferences(), sandbox.Service);
        section.ConfirmInstallAsync = _ => Task.FromResult(true);
        var exits = 0;
        section.RequestExit = () => exits++;

        var installTask = section.InstallAndRestartAsync();

        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "the applier must be reached");
        Assert.True(section.IsInstalling);
        Assert.False(section.CanInstallAndRestart);
        Assert.False(section.InstallAndRestartCommand.CanExecute(null));

        release.Set();
        await installTask;

        Assert.False(section.IsInstalling);
        Assert.Equal(1, exits);
        Assert.True(section.HasInstallResult);
    }

    [Fact]
    public async Task A_declined_confirmation_leaves_the_app_open_and_starts_nothing()
    {
        using var sandbox = InstallFlowSandbox.Create();
        await sandbox.WithStagedOfferAsync();
        using var section = new UpdatesSettingsSectionViewModel(new AppPreferences(), sandbox.Service);
        var events = new List<string>();
        section.ConfirmInstallAsync = _ =>
        {
            events.Add("confirm");
            return Task.FromResult(false);
        };
        section.RequestExit = () => events.Add("exit");

        await section.InstallAndRestartAsync();

        Assert.Equal(["confirm"], events);
        Assert.Null(sandbox.Started);
        Assert.False(section.IsInstalling);
        Assert.False(section.HasInstallResult);
    }

    [Fact]
    public async Task The_confirmation_runs_before_the_install_and_the_exit_happens_only_after_the_applier_started()
    {
        using var sandbox = InstallFlowSandbox.Create();
        await sandbox.WithStagedOfferAsync();
        using var section = new UpdatesSettingsSectionViewModel(new AppPreferences(), sandbox.Service);
        var events = new List<string>();
        section.ConfirmInstallAsync = _ =>
        {
            events.Add("confirm");
            return Task.FromResult(true);
        };
        section.RequestExit = () => events.Add("exit");

        await section.InstallAndRestartAsync();

        Assert.Equal(["confirm", "exit"], events);
        Assert.NotNull(sandbox.Started);
        Assert.True(section.HasInstallResult);
    }

    [Fact]
    public async Task The_install_is_not_offered_while_a_check_is_running()
    {
        var root = TestTempFiles.NewDirectory("forge-install-matrix-");
        try
        {
            var handler = new BlockingManifestHandler();
            var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            var service = new UpdateService(
                http,
                Path.Combine(root, "updates"),
                currentVersion: "1.0.0",
                manifestUrl: () => UpdateTestManifest.ManifestUrl,
                channel: () => "stable",
                checkFrequency: () => UpdateSchedule.Never,
                uiState: null,
                rid: "win-x64",
                time: null,
                openFolder: _ => { });
            using var section = new UpdatesSettingsSectionViewModel(new AppPreferences(), service);

            Assert.False(section.CanInstallAndRestart);
            Assert.False(section.InstallAndRestartCommand.CanExecute(null));

            var checkTask = Task.Run(() => section.CheckNowAsync());
            try
            {
                Assert.True(handler.Entered.Wait(TimeSpan.FromSeconds(10)), "the manifest fetch must be reached");
                Assert.True(section.IsChecking);
                Assert.False(section.CanInstallAndRestart);
                Assert.False(section.InstallAndRestartCommand.CanExecute(null));
            }
            finally
            {
                handler.Release.Set();
                await checkTask;
            }

            Assert.False(section.IsChecking);
            Assert.False(section.CanInstallAndRestart);
            service.Dispose();
        }
        finally
        {
            TestTempFiles.DeleteTree(root);
        }
    }

    // ---------------------------------------------------------------- the arranged install

    private sealed class InstallFlowSandbox : IDisposable
    {
        private readonly Func<UpdateApplyRequest?> readStarted;

        private InstallFlowSandbox(
            string root,
            string dataRoot,
            string install,
            UpdateService service,
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

        public UpdateService Service { get; }

        public ScriptedUpdateHandler Handler { get; }

        public UpdateApplyRequest? Started => readStarted();

        public static InstallFlowSandbox Create(Func<UpdateApplyRequest, bool>? start = null)
        {
            var root = TestTempFiles.NewDirectory("forge-installflow-");
            var dataRoot = Path.Combine(root, "data");
            var install = Path.Combine(root, "install");
            Directory.CreateDirectory(dataRoot);
            UpdateApplySandbox.WriteTree(install, "current");

            var handler = new ScriptedUpdateHandler();
            var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            var launcher = start ?? (_ => true);
            UpdateApplyRequest? started = null;
            var service = new UpdateService(
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
            return new InstallFlowSandbox(root, dataRoot, install, service, handler, () => started);
        }

        public async Task<InstallFlowSandbox> WithStagedOfferAsync(string version = "1.2.0")
        {
            var payload = Zip(version);
            var packageUrl = "https://example.test/package/Forge-" + version + "-win-x64.zip";
            Handler.Manifest(UpdateTestManifest.ManifestUrl, UpdateTestManifest.Stable(version, "win-x64", packageUrl, payload));
            Handler.Package(packageUrl, payload);
            var outcome = await Service.CheckForUpdateAsync(manual: true, CancellationToken.None);
            Assert.Equal(UpdateCheckStatus.UpdateAvailable, outcome.Status);
            return this;
        }

        private static byte[] Zip(string version)
        {
            using var stream = new MemoryStream();
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                Write(archive, UpdateApplyPaths.AppExecutableName, "#!/bin/sh\nexit 0\n");
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

    /// <summary>A manifest handler that parks the fetch so a test can observe the checking state.</summary>
    private sealed class BlockingManifestHandler : HttpMessageHandler
    {
        public ManualResetEventSlim Entered { get; } = new(false);

        public ManualResetEventSlim Release { get; } = new(false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Entered.Set();
            Release.Wait(TimeSpan.FromSeconds(10));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }
}
