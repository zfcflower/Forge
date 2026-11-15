using Forge.Approval;
using Forge.Plugin.Sdk;

namespace Forge.ExtensionHost;

/// <summary>
/// <c>extensions.install</c> and the origin plumbing behind it (wave W10a). The main program has already downloaded the
/// package, verified its SHA-256 against the repository index and extracted it into a directory under the agreed
/// installation temp root; the host re-validates the manifest, swaps the directory in with the same trash rollback as
/// <c>extensions.commit</c>, and records the approval entry with the caller's origin. The installed version starts
/// <see cref="PluginStatus.Draft"/>: installation never approves.
/// </summary>
public sealed partial class ExtensionHost
{
    /// <summary>The root inside <see cref="HostOptions.DataDirectory"/> the main program extracts packages into. The name
    /// comes from <see cref="ExtensionInstallPaths"/>, the one place both processes read it from; the host refuses a
    /// <c>sourceDir</c> outside this root, so install can never move an arbitrary directory into the plugins root.</summary>
    private string InstallTempRoot => Path.Combine(options.DataDirectory, ExtensionInstallPaths.TempDirectoryName);

    /// <summary>
    /// Copies plugins shipped next to the app into the plugins directory on first run (origin Bundled). A plugin that
    /// already exists is never overwritten, so a user's later changes win over the bundled copy; the copied ids are
    /// remembered so their first approval record is Bundled. The mechanism is in place while the bundled directory is
    /// empty.
    /// </summary>
    private void CopyBundledPlugins()
    {
        if (options.BundledPluginsDirectory is not { Length: > 0 } bundled || !Directory.Exists(bundled))
        {
            return;
        }

        Directory.CreateDirectory(options.PluginsDirectory);
        string[] sources;
        try
        {
            sources = Directory.GetDirectories(bundled);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(
                $"forge-extension-host: the bundled plugins directory '{bundled}' could not be read: {exception.Message}");
            return;
        }

        Array.Sort(sources, StringComparer.Ordinal);
        foreach (var source in sources)
        {
            var id = Path.GetFileName(source);
            if (!PluginIds.IsSafeSegment(id))
            {
                Console.Error.WriteLine(
                    $"forge-extension-host: '{source}' is not a usable bundled plugin id; it was skipped.");
                continue;
            }

            var target = staging.LiveDirectory(id);
            if (Directory.Exists(target) || File.Exists(target))
            {
                continue;
            }

            // A dot-prefixed temporary name is skipped by discovery, so a crash mid-copy cannot leave a half-written
            // plugin that a later run tries to load.
            var temporary = Path.Combine(
                options.PluginsDirectory,
                ".bundled-tmp-" + id + "-" + System.Security.Cryptography.RandomNumberGenerator.GetHexString(8));
            try
            {
                CopyDirectory(source, temporary);
                Directory.Move(temporary, target);
                bundledIds.Add(id);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                ExtensionStagingArea.DeleteTrash(temporary);
                Console.Error.WriteLine(
                    $"forge-extension-host: the bundled plugin '{id}' could not be copied: {exception.Message}");
            }
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            File.Copy(file, target, overwrite: false);
        }
    }

    /// <summary>The origin a discovered plugin should carry: BuiltIn when it came from the read-only built-in root,
    /// Bundled when this run copied it from the bundled root, Installed otherwise. Persisted origins of earlier runs are
    /// preserved by <see cref="SyncRecord"/>; this decides the origin of a newly created record.</summary>
    private PluginOrigin OriginOf(PluginEntry entry)
    {
        if (options.BuiltInPluginsDirectory is { Length: > 0 } builtIn &&
            IsStrictSubdirectory(builtIn, entry.Directory))
        {
            return PluginOrigin.BuiltIn;
        }

        return bundledIds.Contains(entry.Id) ? PluginOrigin.Bundled : PluginOrigin.Installed;
    }

    /// <summary>Built-in plugins are part of the app payload: the host enforces that they can neither be disabled nor
    /// deleted, so the settings surface must not offer those actions for them.</summary>
    private void RequireNotBuiltIn(PluginEntry entry)
    {
        if (OriginOf(entry) == PluginOrigin.BuiltIn)
        {
            throw new HostOperationException(
                "builtin_blocked",
                $"Plugin '{entry.Id}' is built in and cannot be disabled or deleted.");
        }
    }

    private static PluginOrigin ParseOrigin(string? origin) => origin switch
    {
        ExtensionOrigins.Installed => PluginOrigin.Installed,
        ExtensionOrigins.Bundled => PluginOrigin.Bundled,
        ExtensionOrigins.BuiltIn => PluginOrigin.BuiltIn,
        _ => throw new HostOperationException(
            "invalid_request",
            $"'{origin ?? "<null>"}' is not a usable plugin origin; use '{ExtensionOrigins.Installed}', "
            + $"'{ExtensionOrigins.Bundled}' or '{ExtensionOrigins.BuiltIn}'."),
    };

    private string RequireInstallSourceDir(string? sourceDir)
    {
        if (string.IsNullOrWhiteSpace(sourceDir))
        {
            throw new HostOperationException("invalid_request", "sourceDir is required.");
        }

        if (!Path.IsPathFullyQualified(sourceDir))
        {
            throw new HostOperationException("invalid_request", $"sourceDir '{sourceDir}' must be an absolute path.");
        }

        var full = Path.GetFullPath(sourceDir);
        if (!IsStrictSubdirectory(InstallTempRoot, full))
        {
            throw new HostOperationException(
                "invalid_request",
                $"sourceDir '{sourceDir}' is outside the installation temp root '{InstallTempRoot}'.");
        }

        if (!Directory.Exists(full))
        {
            throw new HostOperationException("invalid_request", $"sourceDir '{sourceDir}' does not exist.");
        }

        return full;
    }

    private static bool IsStrictSubdirectory(string root, string candidate)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(rootWithSeparator, comparison);
    }

    /// <summary>
    /// Installs an extracted package. Under the refresh gate and then the plugin's gate it re-validates the manifest,
    /// moves the previous live directory aside, moves the source into place, re-prepares the live bytes and writes a
    /// <see cref="PluginStatus.Draft"/> record with the caller's origin. Any failure puts the previous directory and record
    /// back. The plugin does not run until the user approves the computed hashes.
    /// </summary>
    private async Task<ExtensionInstallResult> InstallExtensionAsync(ExtensionInstallRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var origin = ParseOrigin(request.Origin);
        var sourceDir = RequireInstallSourceDir(request.SourceDir);

        // The manifest is read once (outside the gates) to learn the plugin id this install targets; the gate is per id.
        if (!PluginManifestReader.TryRead(sourceDir, out var manifest, out var kind, out var error))
        {
            throw new HostOperationException("invalid_extension", $"The package could not be installed: {error}");
        }

        var pluginId = manifest.Id;
        RequireNotBlocked(pluginId, manifest.Version);
        await refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = StateOf(pluginId);
            await state.EnterAsync(ct).ConfigureAwait(false);
            try
            {
                return await InstallCoreAsync(pluginId, sourceDir, origin, state, ct).ConfigureAwait(false);
            }
            finally
            {
                state.Exit();
            }
        }
        finally
        {
            refreshGate.Release();
        }
    }

    private async Task<ExtensionInstallResult> InstallCoreAsync(
        string pluginId,
        string sourceDir,
        PluginOrigin origin,
        PluginState state,
        CancellationToken ct)
    {
        PluginEntry? previous;
        lock (sync)
        {
            plugins.TryGetValue(pluginId, out previous);
        }

        var previousVersion = previous?.Version;
        var previousRecord = previousVersion is null ? null : approvals.Get(pluginId, previousVersion);
        var trash = staging.MoveLiveAside(pluginId);
        try
        {
            Directory.Move(sourceDir, staging.LiveDirectory(pluginId));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            RestoreLiveDirectory(pluginId, trash);
            throw new HostOperationException(
                "install_failed",
                $"The package of '{pluginId}' could not be moved into place: {exception.Message}");
        }

        PluginEntry entry;
        try
        {
            // The directory now carries the plugin id, so this runs the ordinary name-checked preparation path.
            var live = await catalog.PreparePackageAsync(staging.LiveDirectory(pluginId), ct).ConfigureAwait(false);
            if (live.Entry is not { } prepared || live.Issues.Count > 0)
            {
                throw new HostOperationException(
                    "invalid_extension",
                    $"The package of '{pluginId}' could not be prepared after it was moved into place: {JoinIssues(live.Issues)}");
            }

            entry = prepared;
            RequireNotBlocked(pluginId, entry.Version);

            lock (sync)
            {
                plugins[pluginId] = entry;
            }

            // The bytes that just went live must not keep running in the worker that served the bytes they replaced.
            await StopPluginAsync(entry, state, ct).ConfigureAwait(false);

            InstallRecord(entry, origin);

            // A Draft plugin is unregistered and stopped; nothing runs without an approval the user has not given.
            await ApplyCoreAsync(entry, ct).ConfigureAwait(false);
        }
        catch
        {
            await RollBackInstallAsync(pluginId, previous, previousVersion, previousRecord, trash)
                .ConfigureAwait(false);
            throw;
        }

        ExtensionStagingArea.DeleteTrash(trash);
        if (previousVersion is not null && !string.Equals(previousVersion, entry.Version, StringComparison.Ordinal))
        {
            try
            {
                approvals.Revoke(pluginId, previousVersion);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    $"forge-extension-host: the replaced version '{pluginId}' {previousVersion} could not be revoked: {exception.Message}");
            }
        }

        return new ExtensionInstallResult(entry.Id, entry.Version, BuildStatus(entry), []);
    }

    /// <summary>Writes the installed version as Draft with the caller's origin. Installing never carries an old approval
    /// over: the user still has to approve the hashes of exactly the bytes that went live.</summary>
    private void InstallRecord(PluginEntry entry, PluginOrigin origin)
    {
        if (!entry.HasContent)
        {
            return;
        }

        approvals.Upsert(new PluginRecord
        {
            PluginId = entry.Id,
            Version = entry.Version,
            SourceHash = entry.SourceHash,
            DependencyHash = entry.DependencyHash,
            SdkVersion = entry.SdkVersion,
            Status = PluginStatus.Draft,
            Origin = origin,
        });
    }

    /// <summary>
    /// Undoes a failed install: the previous directory comes back and its record is restored exactly; a fresh install
    /// leaves only a harmless Draft record for a version that is no longer on disk. Runs with
    /// <see cref="CancellationToken.None"/> so a cancelled install still restores the previous state; a rollback failure is
    /// reported, never thrown.
    /// </summary>
    private async Task RollBackInstallAsync(
        string pluginId,
        PluginEntry? previous,
        string? previousVersion,
        PluginRecord? previousRecord,
        string? trash)
    {
        try
        {
            RestoreLiveDirectory(pluginId, trash);
            lock (sync)
            {
                if (previous is null)
                {
                    plugins.Remove(pluginId);
                }
                else
                {
                    plugins[pluginId] = previous;
                }
            }

            if (previousRecord is not null)
            {
                approvals.Upsert(previousRecord);
            }

            if (previous is not null)
            {
                await ApplyCoreAsync(previous, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await UnregisterAllAsync(pluginId, CancellationToken.None).ConfigureAwait(false);
                await StopWorkerAsync(pluginId).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"forge-extension-host: the failed install of '{pluginId}' could not be rolled back completely: {exception.Message}");
        }
    }
}
