using System.Text.Json;
using Forge.Approval;
using Forge.Plugin.Sdk;
using Forge.Scripting;

namespace Forge.ExtensionHost;

/// <summary>
/// Agent-authored extensions over IPC: staging, listing and committing drafts, and the lifecycle operations the settings
/// surface drives (disable, enable, delete).
/// <para>
/// Staging only writes <c>&lt;plugins&gt;/.pending/&lt;id&gt;/</c>, which discovery skips, and computes the hashes the
/// approval card shows; it never writes an approval record, registers a tool or starts a worker. A commit is the only way
/// a draft becomes live, it must echo the hashes the user saw, and it reruns every approval check the existing
/// <c>plugin.approve</c> path runs - under the refresh gate and then the plugin's gate, in that order.
/// </para>
/// </summary>
public sealed partial class ExtensionHost
{
    private async Task<JsonElement> StageExtensionAsync(ExtensionStageRequest request, CancellationToken ct)
    {
        var draft = ExtensionStaging.Validate(request);
        RequireNotBlocked(draft.PluginId, draft.Version);

        staging.Write(draft);

        var prepared = await catalog.PreparePackageAsync(staging.PendingDirectory(draft.PluginId), ct).ConfigureAwait(false);
        var staged = BuildStagedExtension(
            draft.PluginId,
            draft.Version,
            draft.Kind,
            draft.Files,
            draft.Permissions,
            draft.Dependencies,
            draft.Tool,
            prepared,
            DateTimeOffset.UtcNow);
        if (draft.Tool is { } declared && FindToolCollision(draft.PluginId, [declared.Name]) is { } collision)
        {
            // A diagnostic blocks the commit and shows on the card; C# plugins declare their tools only after the worker
            // loaded them, so theirs is refused at commit instead.
            staged = staged with { Diagnostics = [.. staged.Diagnostics, collision] };
        }

        return HostJson.ToElement(staged);
    }

    /// <summary>
    /// Re-derives every staged draft from the bytes on disk, so the pending approvals of a previous run can be restored.
    /// One broken draft reports its own diagnostics instead of failing the response.
    /// </summary>
    private async Task<JsonElement> PendingExtensionsAsync(CancellationToken ct)
    {
        var items = new List<StagedExtension>();
        foreach (var directory in staging.PendingDirectories())
        {
            ct.ThrowIfCancellationRequested();
            var pluginId = Path.GetFileName(directory);
            if (!PluginIds.IsSafeSegment(pluginId))
            {
                continue;
            }

            PreparedPackage prepared;
            try
            {
                prepared = await catalog.PreparePackageAsync(directory, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"forge-extension-host: the staged draft '{pluginId}' could not be prepared: {exception.Message}");
                continue;
            }

            // A directory whose manifest does not declare its own name is not a draft: it is the leftover of a stage that
            // was interrupted between writing its files and replacing the previous draft.
            if (prepared.Entry is not { } entry)
            {
                Console.Error.WriteLine(
                    $"forge-extension-host: '{directory}' is not a usable staged draft and was ignored: {JoinIssues(prepared.Issues)}");
                continue;
            }

            // A draft the host did not write in its own layout (planted file, extra directory, edited manifest) is reported
            // with diagnostics only: no hashes, no files, so it can never be approved from the card.
            if (ExtensionStaging.VerifyLayout(directory, pluginId) is { } layoutError)
            {
                items.Add(BuildStagedExtension(
                    pluginId,
                    entry.Version,
                    entry.Manifest.Kind,
                    [],
                    entry.Manifest.Permissions,
                    entry.Manifest.Dependencies,
                    entry.Manifest.Tool,
                    new PreparedPackage(null, [new PluginIssue(pluginId, PluginIssueKind.InvalidPackage, layoutError, directory)]),
                    new DateTimeOffset(Directory.GetLastWriteTimeUtc(directory))));
                continue;
            }

            var files = new List<StagedText>(4);
            foreach (var path in ListAuthorFiles(directory))
            {
                files.Add(new StagedText(path, ReadFileText(directory, path)));
            }

            items.Add(BuildStagedExtension(
                pluginId,
                entry.Version,
                entry.Manifest.Kind,
                files,
                entry.Manifest.Permissions,
                entry.Manifest.Dependencies,
                entry.Manifest.Tool,
                prepared,
                new DateTimeOffset(Directory.GetLastWriteTimeUtc(directory))));
        }

        return HostJson.ToElement(new StagedExtensionList(items));
    }

    private async Task<JsonElement> DiscardExtensionAsync(ExtensionIdRequest request, CancellationToken ct)
    {
        var pluginId = RequirePluginId(request.PluginId);
        await refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = StateOf(pluginId);
            await state.EnterAsync(ct).ConfigureAwait(false);
            try
            {
                staging.Discard(pluginId);
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

        return HostJson.EmptyObject;
    }

    /// <summary>
    /// Turns the staged draft into the live version. Under the refresh gate and then the plugin's gate it: recomputes the
    /// three hashes from the staged bytes and refuses a mismatch, moves the live directory to <c>.trash</c> and the staged
    /// one into place, re-prepares the plugin from its new directory, and then runs the ordinary approval path with the
    /// approved hashes. Any failure puts the old directory back and restores the previous entry and record.
    /// </summary>
    private async Task<JsonElement> CommitExtensionAsync(ExtensionCommitRequest request, CancellationToken ct)
    {
        var pluginId = RequirePluginId(request.PluginId);
        if (string.IsNullOrWhiteSpace(request.SourceHash) ||
            string.IsNullOrWhiteSpace(request.DependencyHash) ||
            string.IsNullOrWhiteSpace(request.SdkVersion))
        {
            throw new HostOperationException(
                "invalid_extension",
                "extensions.commit requires the sourceHash, dependencyHash and sdkVersion the approval showed.");
        }

        await refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = StateOf(pluginId);
            await state.EnterAsync(ct).ConfigureAwait(false);
            try
            {
                return await CommitCoreAsync(pluginId, request, state, ct).ConfigureAwait(false);
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

    private async Task<JsonElement> CommitCoreAsync(
        string pluginId,
        ExtensionCommitRequest request,
        PluginState state,
        CancellationToken ct)
    {
        var pending = staging.PendingDirectory(pluginId);
        if (!Directory.Exists(pending))
        {
            throw new HostOperationException(
                "invalid_extension",
                $"There is no staged draft for '{pluginId}'; stage it before committing.");
        }

        // The layout is re-asserted here because .pending is plain disk: whatever sits in it now is what would go live.
        if (ExtensionStaging.VerifyLayout(pending, pluginId) is { } layoutError)
        {
            throw new HostOperationException(
                "invalid_extension",
                $"The staged draft of '{pluginId}' cannot be committed: {layoutError}");
        }

        // (a) The staged bytes decide, not the card: recompute all three hashes and refuse a mismatch before touching
        // anything, so a draft edited after the user looked at it can never ride an old approval.
        var staged = await catalog.PreparePackageAsync(pending, ct).ConfigureAwait(false);
        if (staged.Entry is not { } candidate || staged.Issues.Count > 0)
        {
            throw new HostOperationException(
                "invalid_extension",
                $"The staged draft of '{pluginId}' cannot be committed: {JoinIssues(staged.Issues)}");
        }

        if (!string.Equals(candidate.SourceHash, request.SourceHash, StringComparison.Ordinal) ||
            !string.Equals(candidate.DependencyHash, request.DependencyHash, StringComparison.Ordinal) ||
            !string.Equals(candidate.SdkVersion, request.SdkVersion, StringComparison.Ordinal))
        {
            throw new HostOperationException(
                "approval_mismatch",
                $"The staged bytes of '{pluginId}' no longer match the approval: staged source {candidate.SourceHash}, "
                + $"dependencies {candidate.DependencyHash}, SDK {candidate.SdkVersion}. Nothing was changed; stage the "
                + "extension again and approve the new hashes.");
        }

        RequireNotBlocked(pluginId, candidate.Version);

        // A workflow's tool is known from its manifest, so a clash with another plugin is refused before anything moves.
        if (candidate.Manifest.Tool is { } workflowTool &&
            FindToolCollision(pluginId, [workflowTool.Name]) is { } workflowCollision)
        {
            throw new HostOperationException("invalid_extension", workflowCollision);
        }

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
            Directory.Move(pending, staging.LiveDirectory(pluginId));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            RestoreLiveDirectory(pluginId, trash);
            throw new HostOperationException(
                "commit_failed",
                $"The staged draft of '{pluginId}' could not be moved into place: {exception.Message}");
        }

        PluginEntry entry;
        try
        {
            var live = await catalog.PreparePackageAsync(staging.LiveDirectory(pluginId), ct).ConfigureAwait(false);
            if (live.Entry is not { } prepared || live.Issues.Count > 0)
            {
                throw new HostOperationException(
                    "invalid_extension",
                    $"The staged draft of '{pluginId}' could not be prepared after it was moved into place: {JoinIssues(live.Issues)}");
            }

            entry = prepared;
            lock (sync)
            {
                plugins[pluginId] = entry;
            }

            // (b) The bytes that just went live must not keep running in a worker that was started for the bytes they
            // replaced. A same-version commit leaves the version string untouched, so only the image separates the two;
            // stopping here - with the same steps as plugin.revoke: unregister the tools, kill the process tree, then wait
            // until nothing this plugin dispatched is still in flight - means the approval path below loads the new image
            // in a fresh worker instead of reusing a live one. A commit that fails afterwards rolls back with the same
            // stop, so a rollback can never leave a worker that runs the new code while the disk holds the old one. The
            // plugin gate is held throughout, so no execution can observe the swap in between.
            await StopPluginAsync(entry, state, ct).ConfigureAwait(false);

            // (c) Only this plugin is refreshed, and it goes through the existing approval path: re-hash under the gate,
            // compare with what the user approved, SetApproved, apply, publish. A tool-name clash with another plugin is
            // refused inside ApplyCoreAsync before anything is published (the names exist only once the worker loaded), and
            // the catch below rolls the whole commit back, so the colliding tools are never registered.
            await ApproveCoreAsync(entry, entry.Version, request.SourceHash, request.DependencyHash, request.SdkVersion, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            await RollBackCommitAsync(pluginId, previous, previousVersion, previousRecord, candidate.Version, trash)
                .ConfigureAwait(false);
            throw;
        }

        // (d) The commit is live now. Everything below is cleanup: it must not turn a successful swap into a failure.
        ExtensionStagingArea.DeleteTrash(trash);
        if (previousVersion is not null && !string.Equals(previousVersion, entry.Version, StringComparison.Ordinal))
        {
            try
            {
                // The replaced version is not on disk any more; revoking it keeps a restart from re-approving it if the
                // old directory ever comes back. A Disabled/Uninstalled record stays blocked (Revoke never lifts a block).
                approvals.Revoke(pluginId, previousVersion);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    $"forge-extension-host: the replaced version '{pluginId}' {previousVersion} could not be revoked: {exception.Message}");
            }
        }

        return HostJson.ToElement(new ExtensionCommitResult(BuildStatus(entry), [.. entry.Tools.Select(static tool => tool.Name)]));
    }

    /// <summary>
    /// Undoes a failed commit: the new directory leaves the live path and the previous one comes back, the discovery entry
    /// is restored, the previous record is written back, and the previous plugin is applied again (its worker and tools
    /// come back if its approval still covers the restored bytes). Runs with <see cref="CancellationToken.None"/> because a
    /// cancelled commit must still restore the state the user had. A rollback failure is reported, never thrown: the caller
    /// gets the original error.
    /// </summary>
    private async Task RollBackCommitAsync(
        string pluginId,
        PluginEntry? previous,
        string? previousVersion,
        PluginRecord? previousRecord,
        string stagedVersion,
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

            // Drop the approval this commit wrote for the version that is no longer live, then put the previous version's
            // record back exactly: a same-version commit overwrote it with the new hashes, and leaving it that way would
            // describe content that was just rolled back.
            if (!string.Equals(stagedVersion, previousVersion, StringComparison.Ordinal))
            {
                approvals.Revoke(pluginId, stagedVersion);
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
                $"forge-extension-host: the failed commit of '{pluginId}' could not be rolled back completely: {exception.Message}");
        }
    }

    /// <summary>Puts the previous live directory back and removes the one this commit moved in. Best effort per step.</summary>
    private void RestoreLiveDirectory(string pluginId, string? trash)
    {
        var live = staging.LiveDirectory(pluginId);
        try
        {
            if (Directory.Exists(live))
            {
                var removed = staging.MoveLiveAside(pluginId);
                ExtensionStagingArea.DeleteTrash(removed);
            }

            if (trash is not null && Directory.Exists(trash))
            {
                Directory.Move(trash, live);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(
                $"forge-extension-host: the previous directory of '{pluginId}' could not be restored: {exception.Message}");
        }
    }

    /// <summary>Marks the version <see cref="PluginStatus.Disabled"/>, unregisters its tools and stops its worker tree.</summary>
    private async Task<JsonElement> DisablePluginAsync(PluginVersionRequest request, CancellationToken ct)
    {
        var (captured, state) = await EnterPluginGateAsync(request.PluginId, ct).ConfigureAwait(false);
        try
        {
            var entry = Current(captured);
            var version = RequireVersion(entry, request.Version);
            SyncRecord(entry);
            approvals.Disable(entry.Id, version);
            await StopPluginAsync(entry, state, ct).ConfigureAwait(false);
            await ApplyCoreAsync(entry, ct).ConfigureAwait(false);
        }
        finally
        {
            state.Exit();
        }

        return HostJson.EmptyObject;
    }

    /// <summary>
    /// Lifts <see cref="PluginStatus.Disabled"/>. The plugin runs again only when its recorded approval still covers the
    /// bytes on disk; otherwise it stays installed and not running, which is exactly what the approval engine decides.
    /// </summary>
    private async Task<JsonElement> EnablePluginAsync(PluginVersionRequest request, CancellationToken ct)
    {
        var (captured, state) = await EnterPluginGateAsync(request.PluginId, ct).ConfigureAwait(false);
        try
        {
            var entry = Current(captured);
            var version = RequireVersion(entry, request.Version);
            if (approvals.Get(entry.Id, version) is null)
            {
                throw new HostOperationException(
                    "unknown_plugin",
                    $"No approval record for '{entry.Id}' {version}; the version has to be discovered and approved before it can be enabled.");
            }

            approvals.Enable(entry.Id, version);
            await ApplyCoreAsync(entry, ct).ConfigureAwait(false);
        }
        finally
        {
            state.Exit();
        }

        return HostJson.EmptyObject;
    }

    /// <summary>
    /// Uninstalls one version for good: it stops like a disable, the record becomes
    /// <see cref="PluginStatus.Uninstalled"/> (which no approval can lift), the package and any staged draft are removed,
    /// and the plugin leaves discovery. A later stage of the same id and version is refused with
    /// <c>extension_blocked</c>; the agent has to pick a new version number.
    /// </summary>
    private async Task<JsonElement> DeletePluginAsync(PluginVersionRequest request, CancellationToken ct)
    {
        var (captured, state) = await EnterPluginGateAsync(request.PluginId, ct).ConfigureAwait(false);
        try
        {
            var entry = Current(captured);
            var version = RequireVersion(entry, request.Version);
            await StopPluginAsync(entry, state, ct).ConfigureAwait(false);

            var record = approvals.Get(entry.Id, version);
            if (record is null)
            {
                SyncRecord(entry);
                record = approvals.Get(entry.Id, version);
            }

            approvals.Upsert((record ?? new PluginRecord
            {
                PluginId = entry.Id,
                Version = version,
                SourceHash = entry.SourceHash,
                DependencyHash = entry.DependencyHash,
                SdkVersion = entry.SdkVersion,
            }) with
            {
                // The approval is dropped with the content it covered; the status alone keeps the version blocked.
                Status = PluginStatus.Uninstalled,
                ApprovedHash = null,
            });

            lock (sync)
            {
                plugins.Remove(entry.Id);
            }

            staging.Discard(entry.Id);
            try
            {
                if (Directory.Exists(entry.Directory))
                {
                    Directory.Delete(entry.Directory, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                await UnregisterAllAsync(entry.Id, CancellationToken.None).ConfigureAwait(false);
                await StopWorkerAsync(entry.Id).ConfigureAwait(false);
                throw new HostOperationException(
                    "delete_failed",
                    $"'{entry.Id}' {version} is marked uninstalled and blocked, but its directory could not be removed: "
                    + $"{exception.Message}");
            }

            await UnregisterAllAsync(entry.Id, CancellationToken.None).ConfigureAwait(false);
            await StopWorkerAsync(entry.Id).ConfigureAwait(false);
        }
        finally
        {
            state.Exit();
        }

        return HostJson.EmptyObject;
    }

    /// <summary>
    /// The stop half of a lifecycle change, identical to <c>plugin.revoke</c>: unregister first, then kill the worker tree
    /// (which ends whatever is in flight), then wait until the in-flight count reached zero. The caller holds the gate.
    /// </summary>
    private async Task StopPluginAsync(PluginEntry entry, PluginState state, CancellationToken ct)
    {
        await UnregisterAllAsync(entry.Id, CancellationToken.None).ConfigureAwait(false);
        await StopWorkerAsync(entry.Id).ConfigureAwait(false);
        await state.WaitForIdleAsync(RevokeDrainWindow, ct).ConfigureAwait(false);
    }

    /// <summary>Takes the plugin's gate for a lifecycle change; the plugin has to be discovered first.</summary>
    private async Task<(PluginEntry Captured, PluginState State)> EnterPluginGateAsync(string? pluginId, CancellationToken ct)
    {
        var id = RequirePluginId(pluginId);
        var captured = Find(id) ??
            throw new HostOperationException("unknown_plugin", $"No plugin '{id}' was discovered.");
        var state = StateOf(captured.Id);
        await state.EnterAsync(ct).ConfigureAwait(false);
        return (captured, state);
    }

    private static string RequirePluginId(string? pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            throw new HostOperationException("invalid_request", "pluginId is required.");
        }

        if (!PluginIds.IsSafeSegment(pluginId))
        {
            throw new HostOperationException("invalid_request", $"'{pluginId}' is not a usable plugin id.");
        }

        return pluginId;
    }

    /// <summary>The version a lifecycle request names has to be the installed one.</summary>
    private static string RequireVersion(PluginEntry entry, string? requested)
    {
        var version = string.IsNullOrWhiteSpace(requested) ? entry.Version : requested;
        if (!string.Equals(version, entry.Version, StringComparison.Ordinal))
        {
            throw new HostOperationException(
                "version_mismatch",
                $"Plugin '{entry.Id}' is installed at version '{entry.Version}', not '{version}'.");
        }

        return version;
    }

    /// <summary>
    /// Refuses a draft for a version that is disabled or uninstalled. Removing a version blocks it for good: re-staging
    /// the same id and version would otherwise be a way around a delete, and no approval may lift the block.
    /// </summary>
    private void RequireNotBlocked(string pluginId, string version)
    {
        if (approvals.Get(pluginId, version) is not { Status: PluginStatus.Disabled or PluginStatus.Uninstalled } record)
        {
            return;
        }

        throw new HostOperationException(
            "extension_blocked",
            $"'{pluginId}' {version} is {record.Status.ToString().ToLowerInvariant()} and stays blocked; a disabled or "
            + "uninstalled version cannot be re-staged. Use a new version number.");
    }

    private StagedExtension BuildStagedExtension(
        string pluginId,
        string version,
        string kind,
        IReadOnlyList<StagedText> files,
        IReadOnlyList<Permission> permissions,
        IReadOnlyList<Dependency> dependencies,
        ExtensionToolSpec? tool,
        PreparedPackage prepared,
        DateTimeOffset stagedAt)
    {
        var stagedFiles = new List<StagedFile>(files.Count);
        foreach (var file in files)
        {
            stagedFiles.Add(new StagedFile(file.Path, file.Text, ReadLiveText(pluginId, file.Path)));
        }

        var extensionDependencies = new List<ExtensionDependency>(dependencies.Count);
        foreach (var dependency in dependencies)
        {
            extensionDependencies.Add(new ExtensionDependency(dependency.Name, dependency.Version));
        }

        var diagnostics = new List<string>(prepared.Issues.Count);
        foreach (var issue in prepared.Issues)
        {
            diagnostics.Add(issue.Message);
        }

        return new StagedExtension(
            pluginId,
            version,
            kind,
            stagedFiles,
            [.. permissions],
            extensionDependencies,
            prepared.Entry?.SourceHash ?? string.Empty,
            prepared.Entry?.DependencyHash ?? string.Empty,
            prepared.Entry?.SdkVersion ?? DefaultSdkVersion(kind),
            diagnostics,
            LiveManifestVersion(pluginId),
            tool,
            stagedAt);
    }

    /// <summary>
    /// The error message when another plugin (a different id) already publishes one of <paramref name="names"/>; null when
    /// there is no clash. Without this check the last registration would silently shadow the other tool.
    /// </summary>
    private string? FindToolCollision(string pluginId, IReadOnlyList<string> names)
    {
        if (names.Count == 0)
        {
            return null;
        }

        lock (sync)
        {
            foreach (var other in plugins.Values)
            {
                if (string.Equals(other.Id, pluginId, StringComparison.Ordinal))
                {
                    continue;
                }

                var owned = other.Tools.Count > 0 ? other.Tools : other.IsRunnable ? other.WorkflowTools : [];
                foreach (var tool in owned)
                {
                    foreach (var name in names)
                    {
                        if (string.Equals(tool.Name, name, StringComparison.Ordinal))
                        {
                            return $"the tool name '{name}' is already provided by the extension '{other.Id}'; "
                                + "choose a different tool name.";
                        }
                    }
                }
            }
        }

        return null;
    }

    private static string DefaultSdkVersion(string kind) =>
        string.Equals(kind, ExtensionKinds.Workflow, StringComparison.Ordinal)
            ? PluginCatalog.WorkflowContractSdkVersion
            : ScriptCompiler.ContractVersion;

    /// <summary>The version of the package currently installed under this id, or null when there is none.</summary>
    private string? LiveManifestVersion(string pluginId) =>
        PluginManifestReader.TryRead(staging.LiveDirectory(pluginId), out var manifest, out _, out _)
            ? manifest.Version
            : null;

    /// <summary>Text of the live file at the same relative path, or null when it does not exist.</summary>
    private string? ReadLiveText(string pluginId, string relativePath) =>
        TryReadText(staging.LiveDirectory(pluginId), relativePath);

    /// <summary>Text of a file of a staged package; an unreadable file is reported as empty rather than failing the list.</summary>
    private static string ReadFileText(string directory, string relativePath) =>
        TryReadText(directory, relativePath) ?? string.Empty;

    private static string? TryReadText(string directory, string relativePath)
    {
        var path = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The author files of a staged package: everything except the generated manifest and the compiler's artifacts, whose
    /// extensions are outside the authoring whitelist.
    /// </summary>
    private static List<string> ListAuthorFiles(string directory)
    {
        var root = Path.GetFullPath(directory);
        var files = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (string.Equals(relative, ExtensionStaging.ManifestFileName, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!ExtensionStaging.IsAllowedExtension(Path.GetExtension(relative)))
                {
                    continue;
                }

                files.Add(relative);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"forge-extension-host: '{root}' could not be listed: {exception.Message}");
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }

    private static string JoinIssues(IReadOnlyList<PluginIssue> issues)
    {
        if (issues.Count == 0)
        {
            return "no further detail was reported";
        }

        var messages = new List<string>(issues.Count);
        foreach (var issue in issues)
        {
            messages.Add(issue.Message);
        }

        return string.Join("; ", messages);
    }
}
