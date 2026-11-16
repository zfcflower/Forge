using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Logging;
using ZLogger;

namespace Forge.App.Services;

/// <summary>Bytes downloaded so far and, when the source declared a length, the total; null total means unknown.</summary>
internal readonly record struct UpdateDownloadProgress(long BytesRead, long? TotalBytes);

/// <summary>What a check concluded.</summary>
internal enum UpdateCheckStatus
{
    /// <summary>The offered version is not newer than the running one.</summary>
    UpToDate,

    /// <summary>A newer version was downloaded, verified and staged.</summary>
    UpdateAvailable,

    /// <summary>The running build is a CI development build and the automatic schedule may not check it.</summary>
    SkippedDevBuild,

    /// <summary>The check could not complete; <see cref="UpdateCheckOutcome.Message"/> says why.</summary>
    Failed,
}

/// <summary>A staged update ready for the user to install by hand, or to install and restart into.</summary>
internal sealed record UpdateOffer(string Version, string ReleasedAt, string Notes, string PackagePath, long SizeBytes);

/// <summary>
/// The readable result of one install attempt. <see cref="Started"/> is true when the applier process is running and the
/// caller should close the app; <see cref="Message"/> is always populated and localized.
/// </summary>
internal sealed record UpdateInstallOutcome(bool Started, LocText Message)
{
    public static UpdateInstallOutcome Begun(LocText message) => new(true, message);

    public static UpdateInstallOutcome Failed(LocText message) => new(false, message);
}

/// <summary>
/// The readable result of one check. <see cref="Message"/> is always populated and is localized; the offer is present
/// only when the status is <see cref="UpdateCheckStatus.UpdateAvailable"/>.
/// </summary>
internal sealed record UpdateCheckOutcome(UpdateCheckStatus Status, UpdateOffer? Offer, LocText Message)
{
    public static UpdateCheckOutcome UpToDate(LocText message) => new(UpdateCheckStatus.UpToDate, null, message);

    public static UpdateCheckOutcome Available(UpdateOffer offer, LocText message) =>
        new(UpdateCheckStatus.UpdateAvailable, offer, message);

    public static UpdateCheckOutcome DevBuild(LocText message) => new(UpdateCheckStatus.SkippedDevBuild, null, message);

    public static UpdateCheckOutcome Failed(LocText message) => new(UpdateCheckStatus.Failed, null, message);
}

/// <summary>
/// Wave W10b's automatic-update stage A: check the manifest on the chosen channel, compare versions, download the
/// current RID's package with progress and cancellation, verify its SHA-256, stage it under
/// <c>&lt;data&gt;/updates/&lt;version&gt;/</c> and let the user open that location.
/// <para>
/// Wave W10c adds the install entry: once a package is staged and verified, <see cref="InstallAndRestartAsync"/> unpacks it
/// inside the same version directory and starts the staged new executable as the applier (<c>--apply-update</c>) that owns
/// the directory swap and its rollback. The service itself never renames or replaces anything — that state machine lives in
/// <see cref="UpdateApplyRunner"/>, deliberately in another process.
/// </para>
/// <para>
/// The service is a background worker: the automatic pump checks once immediately and then every period, consulting the live
/// frequency and the last-check record (time and outcome) in <c>ui_state</c>, and every network operation runs on the caller's
/// thread (the VM calls from the UI thread but only ever awaits). One check runs at a time; a second caller is turned away with
/// a readable result instead of being queued behind the download. <see cref="Start"/> launches the pump;
/// <see cref="Dispose"/> stops it.
/// </para>
/// </summary>
internal sealed class UpdateService : IDisposable
{
    private const string LastCheckStateKey = "updates.lastCheck";
    private const int LastCheckStateVersion = 1;
    private const string StatusUpToDate = "up-to-date";
    private const string StatusAvailable = "available";
    private const string StatusDevBuild = "dev-build";

    /// <summary>How often the pump wakes to ask "is it due?"; the due-ness itself comes from <see cref="UpdateSchedule"/>.</summary>
    private static readonly TimeSpan DefaultAutoCheckPeriod = TimeSpan.FromHours(1);

    private static readonly TimeSpan ManifestTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(30);

    /// <summary>Even a declared-unknown size is bounded: a hostile manifest must not fill the disk.</summary>
    private const long MaxDownloadBytes = 2L * 1024 * 1024 * 1024;

    private const int BufferSize = 64 * 1024;

    private readonly HttpClient http;
    private readonly IUiStateStore? uiState;
    private readonly string updatesRoot;
    private readonly string currentVersion;
    private readonly Func<string> manifestUrl;
    private readonly Func<string> channel;
    private readonly Func<string> checkFrequency;
    private readonly string rid;
    private readonly TimeProvider time;
    private readonly TimeSpan autoCheckPeriod;

    private readonly Lock gate = new();
    private string? stagedPackagePath;
    private UpdateOffer? stagedOffer;
    private CancellationTokenSource? autoCts;
    private Task? autoLoop;
    private int started;
    private int disposed;

    /// <summary>
    /// One check at a time. Both entrances (the pump and the check-now button) stage into the same version directory, so two
    /// overlapping checks would download over each other. Not disposed: <see cref="SemaphoreSlim"/> only owns an OS handle
    /// once <see cref="SemaphoreSlim.AvailableWaitHandle"/> is touched, and disposing it under an in-flight check would turn a
    /// shutdown race into an unobserved <see cref="ObjectDisposedException"/>.
    /// </summary>
    private readonly SemaphoreSlim checkGate = new(1, 1);

    private readonly Action<string> openFolder;

    /// <summary>
    /// The directory the applier may replace: the directory this process runs from. It is never taken from a manifest, a
    /// preference or an argument — the one value the install protocol trusts is the place the executable actually lives.
    /// </summary>
    private readonly string installDirectory;

    /// <summary>Starts the staged executable as the applier; settable so the applier can be substituted without spawning a process.</summary>
    private readonly Func<UpdateApplyRequest, bool> startApplier;

    public UpdateService(
        HttpClient http,
        string updatesRootDirectory,
        string currentVersion,
        Func<string> manifestUrl,
        Func<string> channel,
        Func<string> checkFrequency,
        IUiStateStore? uiState = null,
        string? rid = null,
        TimeProvider? time = null,
        Action<string>? openFolder = null,
        string? installDirectory = null,
        Func<UpdateApplyRequest, bool>? startApplier = null,
        TimeSpan? autoCheckPeriod = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(updatesRootDirectory);
        ArgumentNullException.ThrowIfNull(currentVersion);
        ArgumentNullException.ThrowIfNull(manifestUrl);
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(checkFrequency);

        this.http = http;
        this.updatesRoot = Path.GetFullPath(updatesRootDirectory);
        this.currentVersion = currentVersion;
        this.manifestUrl = manifestUrl;
        this.channel = channel;
        this.checkFrequency = checkFrequency;
        this.uiState = uiState;
        this.rid = string.IsNullOrWhiteSpace(rid) ? RuntimeInformation.RuntimeIdentifier : rid;
        this.time = time ?? TimeProvider.System;
        this.autoCheckPeriod = autoCheckPeriod is { } period && period > TimeSpan.Zero ? period : DefaultAutoCheckPeriod;
        this.openFolder = openFolder ?? OpenFolderWithShell;
        this.installDirectory = UpdateApplyPaths.TrimSeparator(
            string.IsNullOrWhiteSpace(installDirectory) ? AppContext.BaseDirectory : installDirectory);
        this.startApplier = startApplier ?? StartApplierProcess;
    }

    public string CurrentVersion => currentVersion;

    /// <summary>The staged package of the last successful download, or null.</summary>
    public string? StagedPackagePath
    {
        get
        {
            lock (gate)
            {
                return stagedPackagePath;
            }
        }
    }

    /// <summary>
    /// The offer whose package was downloaded and passed the SHA-256 check, or null. This is the only state in which an
    /// install is offered: an unverified payload never becomes installable.
    /// </summary>
    public UpdateOffer? StagedOffer
    {
        get
        {
            lock (gate)
            {
                return stagedOffer;
            }
        }
    }

    /// <summary>
    /// Starts the automatic-check pump; idempotent, and a no-op once disposed. The first tick runs immediately: waiting a
    /// whole period first means an app that is started and closed within the period never checks at all.
    /// </summary>
    public void Start()
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        if (Interlocked.Exchange(ref started, 1) != 0)
        {
            return;
        }

        autoCts = new CancellationTokenSource();
        autoLoop = Task.Run(() => AutoCheckLoopAsync(autoCts.Token));
    }

    /// <summary>
    /// Runs one check. Manual checks always proceed; automatic checks first refuse development builds and the
    /// <c>never</c> frequency. The result is also persisted to <c>ui_state</c> (best effort — a failed write does not
    /// mask a successful check). A user cancellation (<paramref name="ct"/>) surfaces as
    /// <see cref="OperationCanceledException"/>; every other failure comes back as <see cref="UpdateCheckOutcome"/>.
    /// </summary>
    public async Task<UpdateCheckOutcome> CheckForUpdateAsync(
        bool manual,
        CancellationToken ct,
        Action<UpdateDownloadProgress>? progress = null)
    {
        if (!await checkGate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            // A readable answer, not a second download into the same staging directory; nothing is recorded, so the next
            // attempt is judged on the check that is actually running.
            return UpdateCheckOutcome.Failed(UpdatesStrings.ErrorCheckBusy);
        }

        try
        {
            return await CheckAndRecordAsync(manual, ct, progress).ConfigureAwait(false);
        }
        finally
        {
            checkGate.Release();
        }
    }

    private async Task<UpdateCheckOutcome> CheckAndRecordAsync(
        bool manual,
        CancellationToken ct,
        Action<UpdateDownloadProgress>? progress)
    {
        if (!manual && UpdateVersion.IsDevBuild(currentVersion))
        {
            return UpdateCheckOutcome.DevBuild(UpdatesStrings.ResultDevBuild(currentVersion));
        }

        var url = manifestUrl().Trim();
        if (url.Length == 0)
        {
            return UpdateCheckOutcome.Failed(UpdatesStrings.ErrorManifestUrlEmpty);
        }

        if (!IsUsableManifestUrl(url))
        {
            return UpdateCheckOutcome.Failed(UpdatesStrings.ErrorManifestUrlInvalid(url));
        }

        UpdateCheckOutcome outcome;
        try
        {
            outcome = await CheckAsync(url, channel(), currentVersion, ct, progress).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsUnusableAddress(exception))
        {
            // Belt and braces behind the pre-check: the URL is free text that reaches HttpClient, and a disposed transport
            // must not escape this method — a thrown check leaves the section with no result at all.
            outcome = UpdateCheckOutcome.Failed(UpdatesStrings.ErrorManifestUrlInvalid(url));
        }

        await RecordCheckAsync(outcome, ct).ConfigureAwait(false);
        return outcome;
    }

    /// <summary>An absolute http(s) URL is the only shape the manifest transport can be asked for.</summary>
    private static bool IsUsableManifestUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// The exception types a bad address or a torn-down transport surfaces as: a relative or malformed URL throws
    /// <see cref="InvalidOperationException"/>/<see cref="UriFormatException"/> out of <see cref="HttpRequestMessage"/>, an
    /// unsupported scheme <see cref="NotSupportedException"/>, and a disposed <see cref="HttpClient"/> an
    /// <see cref="ObjectDisposedException"/> (which is an <see cref="InvalidOperationException"/>).
    /// </summary>
    private static bool IsUnusableAddress(Exception exception) =>
        exception is InvalidOperationException or UriFormatException or NotSupportedException;

    /// <summary>
    /// The automatic pump's entry: checks the live frequency and the persisted last-check time, then runs the check only
    /// when it is due. Concurrent pumps are folded into one.
    /// </summary>
    public async Task RunAutoCheckIfDueAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        if (!await checkGate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            // A check is already running (a manual one, or the previous tick): skipping this tick is the point of the gate.
            return;
        }

        try
        {
            var lastCheck = await ReadLastCheckAsync(ct).ConfigureAwait(false);
            if (!UpdateSchedule.ShouldCheck(checkFrequency(), lastCheck?.CheckedAt, lastCheck?.Status, time.GetUtcNow()))
            {
                return;
            }

            await CheckAndRecordAsync(manual: false, ct, progress: null).ConfigureAwait(false);
        }
        finally
        {
            checkGate.Release();
        }
    }

    /// <summary>Opens the folder holding the staged package (or the staging root when nothing has been staged yet).</summary>
    public void OpenDownloadLocation()
    {
        string? path;
        lock (gate)
        {
            path = stagedPackagePath;
        }

        openFolder(string.IsNullOrEmpty(path) ? updatesRoot : path);
    }

    /// <summary>The persisted last-check time, or null when there has never been a check or the entry cannot be read.</summary>
    public async Task<DateTimeOffset?> GetLastCheckAsync(CancellationToken ct) =>
        (await ReadLastCheckAsync(ct).ConfigureAwait(false))?.CheckedAt;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        autoCts?.Cancel();
        autoCts?.Dispose();
        autoCts = null;
        autoLoop = null;
        http.Dispose();
    }

    // ---------------------------------------------------------------- the check itself

    private async Task<UpdateCheckOutcome> CheckAsync(
        string url,
        string channelToken,
        string current,
        CancellationToken ct,
        Action<UpdateDownloadProgress>? progress)
    {
        UpdateManifest? manifest;
        using (var request = new HttpRequestMessage(HttpMethod.Get, url))
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            deadline.CancelAfter(ManifestTimeout);
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return UpdateCheckOutcome.Failed(UpdatesStrings.ErrorManifestTimeout);
            }
            catch (HttpRequestException exception)
            {
                return UpdateCheckOutcome.Failed(UpdatesStrings.ErrorNetwork(exception.Message));
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    return UpdateCheckOutcome.Failed(UpdatesStrings.ErrorManifestHttp((int)response.StatusCode));
                }

                byte[]? payload;
                try
                {
                    payload = await ReadBoundedAsync(response.Content, UpdateManifestParser.MaxBytes, deadline.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return UpdateCheckOutcome.Failed(UpdatesStrings.ErrorManifestTimeout);
                }
                catch (Exception exception) when (exception is IOException or HttpRequestException)
                {
                    return UpdateCheckOutcome.Failed(UpdatesStrings.ErrorNetwork(exception.Message));
                }

                if (payload is null)
                {
                    return UpdateCheckOutcome.Failed(UpdatesStrings.ErrorManifestTooLarge);
                }

                if (!UpdateManifestParser.TryParse(payload, out manifest, out var parseError))
                {
                    return UpdateCheckOutcome.Failed(UpdatesStrings.ErrorBadManifest(parseError ?? string.Empty));
                }
            }
        }

        var selected = manifest.Channel(channelToken);
        if (selected is null)
        {
            // A release that published only the other channel is a normal state, not a failure: the user's channel simply
            // has nothing newer. Reported as up-to-date with its own wording so a stable user on a beta-only manifest is
            // not told the check broke.
            return UpdateCheckOutcome.UpToDate(UpdatesStrings.ResultChannelMissing(channelToken));
        }

        var comparison = UpdateVersion.Compare(current, selected.Version);
        if (comparison is null)
        {
            return UpdateCheckOutcome.Failed(UpdatesStrings.ErrorBadVersion(selected.Version));
        }

        if (comparison.Value <= 0)
        {
            return UpdateCheckOutcome.UpToDate(UpdatesStrings.ResultUpToDate(current));
        }

        if (selected.Package(rid) is not { } package)
        {
            return UpdateCheckOutcome.Failed(UpdatesStrings.ErrorNoPackage(selected.Version, rid));
        }

        try
        {
            var offer = await DownloadAndStageAsync(selected, package, ct, progress).ConfigureAwait(false);
            return UpdateCheckOutcome.Available(offer, UpdatesStrings.ResultAvailable(offer.Version, offer.SizeBytes));
        }
        catch (UpdateCheckFailureException failure)
        {
            return UpdateCheckOutcome.Failed(failure.Text);
        }
    }

    private async Task<UpdateOffer> DownloadAndStageAsync(
        UpdateChannel channel,
        UpdatePackage package,
        CancellationToken ct,
        Action<UpdateDownloadProgress>? progress)
    {
        var versionDirectory = VersionDirectory(channel.Version);
        var targetDirectory = Path.Combine(updatesRoot, versionDirectory);
        var fileName = FileNameFromUrl(package.Url, channel.Version, rid);
        var targetPath = Path.Combine(targetDirectory, fileName);
        var tempPath = targetPath + ".tmp";

        using var request = new HttpRequestMessage(HttpMethod.Get, package.Url);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(DownloadTimeout);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw Failure(UpdatesStrings.ErrorDownloadTimeout);
        }
        catch (HttpRequestException exception)
        {
            throw Failure(UpdatesStrings.ErrorNetwork(exception.Message));
        }

        var downloadedBytes = 0L;
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw Failure(UpdatesStrings.ErrorDownloadHttp((int)response.StatusCode));
            }

            var declared = response.Content.Headers.ContentLength;
            if (declared is { } length && package.SizeBytes > 0 && length > package.SizeBytes)
            {
                throw Failure(UpdatesStrings.ErrorDownloadTooLarge(package.SizeBytes));
            }

            Directory.CreateDirectory(targetDirectory);
            ClearStaleTempFiles(targetDirectory);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            try
            {
                await using var source = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                await using var destination = new FileStream(
                    tempPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    BufferSize,
                    useAsync: true);
                var buffer = new byte[BufferSize];
                while (true)
                {
                    var read = await source.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    downloadedBytes += read;
                    if (downloadedBytes > MaxDownloadBytes)
                    {
                        throw Failure(UpdatesStrings.ErrorDownloadTooLarge(MaxDownloadBytes));
                    }

                    if (package.SizeBytes > 0 && downloadedBytes > package.SizeBytes)
                    {
                        throw Failure(UpdatesStrings.ErrorDownloadTooLarge(package.SizeBytes));
                    }

                    hash.AppendData(buffer.AsSpan(0, read));
                    await destination.WriteAsync(buffer.AsMemory(0, read), deadline.Token).ConfigureAwait(false);
                    progress?.Invoke(new UpdateDownloadProgress(
                        downloadedBytes,
                        declared ?? (package.SizeBytes > 0 ? package.SizeBytes : null)));
                }

                await destination.FlushAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                TryDelete(tempPath);
                throw Failure(UpdatesStrings.ErrorDownloadTimeout);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or HttpRequestException
                                              or InvalidOperationException or NotSupportedException)
            {
                // A torn-down or misconfigured transport surfaces as an InvalidOperationException (a disposed HttpClient is
                // an ObjectDisposedException, which derives from it); the download is a failure with a reason, not a throw.
                TryDelete(tempPath);
                throw Failure(UpdatesStrings.ErrorDownloadFailed(exception.Message));
            }
            catch
            {
                TryDelete(tempPath);
                throw;
            }

            if (package.SizeBytes > 0 && downloadedBytes != package.SizeBytes)
            {
                TryDelete(tempPath);
                throw Failure(UpdatesStrings.ErrorSizeMismatch(downloadedBytes, package.SizeBytes));
            }

            if (downloadedBytes == 0)
            {
                // An empty body is never a release package. When the manifest declares no size (0 = unknown) the size check
                // above cannot catch it, and the empty body would pass SHA-256 against the hash of the empty string.
                TryDelete(tempPath);
                throw Failure(UpdatesStrings.ErrorEmptyPackage);
            }

            var actualHash = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!string.Equals(actualHash, package.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(tempPath);
                throw Failure(UpdatesStrings.ErrorHashMismatch);
            }

            ClearStagingExcept(targetDirectory);
            File.Move(tempPath, targetPath, overwrite: true);
        }

        var offer = new UpdateOffer(channel.Version, channel.ReleasedAt, channel.Notes, targetPath, downloadedBytes);
        lock (gate)
        {
            stagedPackagePath = targetPath;
            stagedOffer = offer;
        }

        return offer;
    }

    /// <summary>
    /// Unpacks the verified package and starts the applier, which waits for this process to exit, swaps the install directory
    /// and restarts the app. The caller closes the app when the outcome says <see cref="UpdateInstallOutcome.Started"/>.
    /// The unpacking runs on the thread pool: an app directory is hundreds of files, and the caller is the UI thread.
    /// </summary>
    public async Task<UpdateInstallOutcome> InstallAndRestartAsync(CancellationToken ct)
    {
        var offer = StagedOffer;
        if (offer is null)
        {
            return UpdateInstallOutcome.Failed(UpdatesStrings.InstallErrorNothingStaged);
        }

        if (!UpdateApplyPaths.TryVersionToken(offer.Version, out var version))
        {
            return UpdateInstallOutcome.Failed(UpdatesStrings.InstallErrorVersion(offer.Version));
        }

        // Hold the check gate so a background download cannot clear the staging tree mid-install (ClearStagingExcept
        // deletes every version directory but the one being downloaded). A check that does not finish promptly yields a
        // readable retry instead of blocking the user behind a long download; the gate is released before the app exits.
        if (!await checkGate.WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false))
        {
            return UpdateInstallOutcome.Failed(UpdatesStrings.InstallErrorBusy);
        }

        try
        {
            // The offer may have been replaced while the gate was held by a finishing check: re-read it.
            offer = StagedOffer;
            if (offer is null || !string.Equals(offer.Version, version, StringComparison.Ordinal))
            {
                return UpdateInstallOutcome.Failed(UpdatesStrings.InstallErrorNothingStaged);
            }

            if (!File.Exists(offer.PackagePath))
            {
                return UpdateInstallOutcome.Failed(UpdatesStrings.InstallErrorPackageMissing(offer.PackagePath));
            }

            // The payload lives next to the package wave W10b staged, inside the same version directory; deriving it from the
            // package path (rather than from the manifest version) keeps the two in step even when the staged name differs.
            var payload = UpdateApplyPaths.PayloadDirectory(Path.GetDirectoryName(offer.PackagePath)!);
            try
            {
                await Task.Run(() => Unpack(offer.PackagePath, payload), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                AppLogs.Update.ZLogWarning($"unpacking '{offer.PackagePath}' failed: {exception.Message}");
                return UpdateInstallOutcome.Failed(UpdatesStrings.InstallErrorUnpack(exception.Message));
            }

            var executable = UpdateApplyPaths.AppExecutable(payload);
            if (!File.Exists(executable))
            {
                return UpdateInstallOutcome.Failed(UpdatesStrings.InstallErrorNoExecutable(UpdateApplyPaths.AppExecutableName));
            }

            var request = new UpdateApplyRequest(Environment.ProcessId, installDirectory, payload, version);
            // The applier would refuse a request it considers unsafe, and a process that refuses silently is the worst failure
            // mode: validate with the same parser here so the section can say why nothing happened.
            if (!UpdateApplyRequest.TryParse(request.ToArguments(), DataRoot, out var validated, out var refusal))
            {
                return UpdateInstallOutcome.Failed(UpdatesStrings.InstallErrorRefused(refusal ?? string.Empty));
            }

            return startApplier(validated!)
                ? UpdateInstallOutcome.Begun(UpdatesStrings.InstallStarted(version))
                : UpdateInstallOutcome.Failed(UpdatesStrings.InstallErrorStart(executable));
        }
        finally
        {
            checkGate.Release();
        }
    }

    /// <summary>The data root the applier validates staging paths against: the parent of the updates root.</summary>
    private string DataRoot => Path.GetDirectoryName(updatesRoot) ?? updatesRoot;

    private static void Unpack(string packagePath, string payload)
    {
        if (Directory.Exists(payload))
        {
            Directory.Delete(payload, recursive: true);
        }

        UpdatePackages.Extract(packagePath, payload);
    }

    /// <summary>The real applier launch: the staged executable, detached, with the private install command line.</summary>
    /// <summary>
    /// The real applier launch: the private install command line, run from a neutral copy of the staged tree (see
    /// <see cref="UpdateApplierHost"/>) — the staged tree itself is what the applier renames, and Windows refuses to move a
    /// directory that holds the image of a running process.
    /// </summary>
    private bool StartApplierProcess(UpdateApplyRequest request)
    {
        var start = UpdateApplierHost.CreateStartInfo(DataRoot, request.StagingDirectory, request.ToArguments(), out var error);
        if (start is null)
        {
            AppLogs.Update.ZLogWarning($"preparing the update applier from '{request.StagingDirectory}' failed: {error}");
            return false;
        }

        try
        {
            using var process = Process.Start(start);
            AppLogs.Update.ZLogInformation($"started the update applier '{start.FileName}' (pid {process?.Id})");
            return process is not null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            AppLogs.Update.ZLogWarning($"starting the update applier '{start.FileName}' failed: {exception.Message}");
            return false;
        }
    }

    // ---------------------------------------------------------------- staging housekeeping

    private void ClearStagingExcept(string keepDirectory)
    {
        Directory.CreateDirectory(updatesRoot);
        foreach (var child in Directory.GetDirectories(updatesRoot))
        {
            if (string.Equals(child, keepDirectory, StringComparison.Ordinal))
            {
                continue;
            }

            // The neutral roots the appliers ran from are not staged versions: they belong to the startup housekeeping (which
            // is the only place that knows an applier is done), and a running applier's directory is none of a download's
            // business.
            if (Path.GetFileName(child).StartsWith(UpdateApplyPaths.ApplierPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            TryDeleteDirectory(child);
        }
    }

    /// <summary>Removes a half-written <c>.tmp</c> left by a previous interrupted download of the same version.</summary>
    private static void ClearStaleTempFiles(string directory)
    {
        foreach (var file in Directory.GetFiles(directory))
        {
            if (file.EndsWith(".tmp", StringComparison.Ordinal))
            {
                TryDelete(file);
            }
        }
    }

    private static string VersionDirectory(string version)
    {
        // The version already passed UpdateVersion.Compare's numeric parse, so it is a numeric prefix plus an optional
        // -prerelease / +metadata suffix — none of which can contain a path separator. The staging path is built from it,
        // so the invariant is asserted once more rather than trusted.
        if (version.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw Failure(UpdatesStrings.ErrorBadVersion(version));
        }

        return version;
    }

    private static string FileNameFromUrl(string url, string version, string rid)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var name = Path.GetFileName(uri.AbsolutePath);
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        return $"Forge-{version}-{rid}";
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLogs.Update.ZLogWarning($"staging cleanup failed for '{path}': {exception.Message}");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLogs.Update.ZLogWarning($"staging cleanup failed for '{path}': {exception.Message}");
        }
    }

    private static void OpenFolderWithShell(string path)
    {
        var directory = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo { FileName = "explorer.exe", Arguments = "\"" + directory + "\"", UseShellExecute = true }
            : OperatingSystem.IsMacOS()
                ? new ProcessStartInfo { FileName = "open", Arguments = "\"" + directory + "\"", UseShellExecute = true }
                : new ProcessStartInfo { FileName = "xdg-open", Arguments = "\"" + directory + "\"", UseShellExecute = true };

        try
        {
            using var process = Process.Start(start);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            AppLogs.Update.ZLogWarning($"opening the download location failed: {exception.Message}");
        }
    }

    // ---------------------------------------------------------------- ui_state (best effort)

    private async Task RecordCheckAsync(UpdateCheckOutcome outcome, CancellationToken ct)
    {
        if (uiState is null || outcome.Status == UpdateCheckStatus.SkippedDevBuild)
        {
            return;
        }

        try
        {
            var json = "{\"checkedAt\":\"" + time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture) +
                       "\",\"status\":\"" + StatusToken(outcome.Status) + "\",\"offeredVersion\":"
                       + (outcome.Offer is { } offer ? "\"" + JsonEscape(offer.Version) + "\"" : "null") + "}";
            await uiState.PutAsync(LastCheckStateKey, LastCheckStateVersion, json, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            AppLogs.Update.ZLogWarning($"recording the last update check failed: {exception.Message}");
        }
    }

    /// <summary>What one recorded check left behind: when it ran and how it ended (null status for an old or hand-written entry).</summary>
    private readonly record struct LastCheck(DateTimeOffset CheckedAt, string? Status);

    private async Task<LastCheck?> ReadLastCheckAsync(CancellationToken ct)
    {
        if (uiState is null)
        {
            return null;
        }

        try
        {
            var entry = await uiState.GetAsync(LastCheckStateKey, ct).ConfigureAwait(false);
            if (entry is null)
            {
                return null;
            }

            using var document = System.Text.Json.JsonDocument.Parse(entry.Json);
            if (!document.RootElement.TryGetProperty("checkedAt", out var checkedAt) ||
                checkedAt.ValueKind != System.Text.Json.JsonValueKind.String ||
                !DateTimeOffset.TryParse(
                    checkedAt.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var parsed))
            {
                return null;
            }

            var status = document.RootElement.TryGetProperty("status", out var statusElement) &&
                         statusElement.ValueKind == System.Text.Json.JsonValueKind.String
                ? statusElement.GetString()
                : null;
            return new LastCheck(parsed, status);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            AppLogs.Update.ZLogWarning($"reading the last update check failed: {exception.Message}");
            return null;
        }
    }

    private static string StatusToken(UpdateCheckStatus status) => status switch
    {
        UpdateCheckStatus.UpToDate => StatusUpToDate,
        UpdateCheckStatus.UpdateAvailable => StatusAvailable,
        UpdateCheckStatus.SkippedDevBuild => StatusDevBuild,
        _ => UpdateSchedule.FailedStatus,
    };

    private static string JsonEscape(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                default:
                    // Control characters (a hostile manifest's version can carry them) must become \uXXXX, or the record
                    // is invalid JSON, reads back as null, and the frequency gate silently re-downloads every tick.
                    if (c < 0x20)
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        return builder.ToString();
    }

    private async Task AutoCheckLoopAsync(CancellationToken ct)
    {
        // The first tick is immediate: an app started and closed within one period must still have checked once.
        if (!await PumpTickAsync(ct).ConfigureAwait(false))
        {
            return;
        }

        using var timer = new PeriodicTimer(autoCheckPeriod);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (!await PumpTickAsync(ct).ConfigureAwait(false))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed: the periodic pump is done.
        }
    }

    /// <summary>
    /// One pump tick. An unexpected exception is logged and swallowed: the pump's value is that it is still there tomorrow,
    /// so one faulty tick (a clock, the file system, a manifest) must not end the loop and silently stop every future check.
    /// Returns false when the pump was cancelled.
    /// </summary>
    private async Task<bool> PumpTickAsync(CancellationToken ct)
    {
        try
        {
            await RunAutoCheckIfDueAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception)
        {
            AppLogs.Update.ZLogWarning($"the automatic update check failed: {exception.Message}");
            return true;
        }
    }

    private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, long maxBytes, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[BufferSize];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
            {
                return output.ToArray();
            }

            if (output.Length + read > maxBytes)
            {
                return null;
            }

            output.Write(buffer, 0, read);
        }
    }

    private sealed class UpdateCheckFailureException(LocText text) : Exception
    {
        public LocText Text { get; } = text;
    }

    private static UpdateCheckFailureException Failure(LocText text) => new(text);
}
