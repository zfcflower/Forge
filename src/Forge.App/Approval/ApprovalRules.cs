using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Forge.Abstractions;
using Forge.Core.Tools;
using Forge.IO;

namespace Forge.App.Approval;

/// <summary>Where a remembered "allow" applies. Session rules live in memory only; project and global rules are persisted.</summary>
internal enum ApprovalScope
{
    Session,
    Project,
    Global,
}

/// <summary>
/// Identity of one remembered rule: tool name plus side effect plus scope. <see cref="ScopeId"/> is the session id, the
/// project id, or empty for <see cref="ApprovalScope.Global"/>.
/// </summary>
internal readonly record struct ApprovalRuleKey(string ToolName, ToolSideEffect SideEffect, ApprovalScope Scope, string ScopeId)
{
    public static ApprovalRuleKey ForSession(string toolName, ToolSideEffect sideEffect, string sessionId) =>
        new(toolName, sideEffect, ApprovalScope.Session, sessionId);

    public static ApprovalRuleKey ForProject(string toolName, ToolSideEffect sideEffect, string projectId) =>
        new(toolName, sideEffect, ApprovalScope.Project, projectId);

    public static ApprovalRuleKey Global(string toolName, ToolSideEffect sideEffect) =>
        new(toolName, sideEffect, ApprovalScope.Global, string.Empty);

    /// <summary>A session or project rule needs its id; a global rule must not carry one.</summary>
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(ToolName) &&
        (Scope == ApprovalScope.Global || !string.IsNullOrWhiteSpace(ScopeId));

    public bool IsPersisted => Scope != ApprovalScope.Session;
}

/// <summary>One remembered allow, with the moment it was granted (shown in the settings section).</summary>
internal sealed record ApprovalRule(ApprovalRuleKey Key, DateTimeOffset CreatedAt);

/// <summary>
/// The remembered "allow" rules of the approval queue. Session rules are memory only; project and global rules are written
/// to <c>approval-rules.json</c> under the user data directory by this class (never to <c>AppPreferences</c>, which other
/// features own). Reads and mutations are safe from any thread; file IO always runs on the thread pool and a burst of
/// changes becomes one write after a short debounce.
/// <para>A corrupt file is treated as "nothing was remembered" (fail-closed) and is backed up once to
/// <c>approval-rules.json.bak</c> before the first write replaces it; the reason is kept in <see cref="LoadProblem"/>.
/// <c>extension_delete</c> can never be remembered, and approvals never bypass the plugin approval card.</para>
/// </summary>
internal sealed class ApprovalRules : IAsyncDisposable
{
    /// <summary>File name inside the user data directory.</summary>
    public const string FileName = "approval-rules.json";

    /// <summary>Suffix of the one-time copy of an unreadable rules file.</summary>
    public const string BackupSuffix = ".bak";

    private const int DocumentVersion = 1;
    private const int DebounceMilliseconds = 500;

    private readonly string filePath;
    private readonly int debounceMilliseconds;
    private readonly Lock gate = new();
    private readonly Lock writeGate = new();
    private readonly List<ApprovalRule> rules = [];
    private CancellationTokenSource? debounce;
    private Task? loadTask;
    private string? loadProblem;
    private string? saveProblem;
    private byte[]? originalBytes;
    private bool protectOriginal;
    private bool dirty;
    private bool disposed;
    private bool loaded;
    private int version;

    public ApprovalRules(string filePath, int debounceMilliseconds = DebounceMilliseconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        this.filePath = Path.GetFullPath(filePath);
        this.debounceMilliseconds = Math.Max(0, debounceMilliseconds);
        BackupPath = this.filePath + BackupSuffix;
    }

    /// <summary>Raised after a rule was added or removed, and once after the file was read. Any thread.</summary>
    public event Action? Changed;

    /// <summary>Where an unreadable rules file is preserved before the first write replaces it.</summary>
    public string BackupPath { get; }

    /// <summary>Null when the last read succeeded or the file did not exist.</summary>
    public string? LoadProblem
    {
        get
        {
            lock (gate)
            {
                return loadProblem;
            }
        }
    }

    /// <summary>Null when the last write succeeded; a failed write is reported here and traced.</summary>
    public string? SaveProblem
    {
        get
        {
            lock (gate)
            {
                return saveProblem;
            }
        }
    }

    /// <summary>Every remembered rule; a snapshot, so callers never hold the internal list.</summary>
    public IReadOnlyList<ApprovalRule> Rules
    {
        get
        {
            lock (gate)
            {
                return [.. rules];
            }
        }
    }

    /// <summary>True once the file was read; a caller that only needs the in-memory view can skip awaiting the load.</summary>
    public bool IsLoaded
    {
        get
        {
            lock (gate)
            {
                return loaded;
            }
        }
    }

    /// <summary>Whether a change is waiting for the debounce to write it.</summary>
    public bool IsDirty
    {
        get
        {
            lock (gate)
            {
                return dirty;
            }
        }
    }

    /// <summary>
    /// Reads the persisted rules once; every later call returns the same task. Approval handlers await it before they
    /// consult a rule, so a remembered allow is never missed just because the file read had not finished yet.
    /// </summary>
    public Task LoadAsync(CancellationToken ct)
    {
        Task task;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (loadTask is null)
            {
                // The caller's token only governs who waits: the one shared read runs to completion, so a cancelled
                // caller cannot leave the store half-loaded and a later caller never has to re-read the file.
                loadTask = Task.Run(LoadCore, CancellationToken.None);
            }

            task = loadTask;
        }

        return ct.CanBeCanceled ? task.WaitAsync(ct) : task;
    }

    /// <summary>An allow is remembered only when the tool/scope combination is complete and the tool may be remembered.</summary>
    public bool Allow(ApprovalRuleKey key)
    {
        if (!key.IsComplete || !IsRememberable(key.ToolName))
        {
            return false;
        }

        lock (gate)
        {
            if (disposed)
            {
                return false;
            }

            foreach (var existing in rules)
            {
                if (existing.Key == key)
                {
                    return false;
                }
            }

            rules.Add(new ApprovalRule(key, DateTimeOffset.UtcNow));
            version++;
            if (key.IsPersisted)
            {
                dirty = true;
            }
        }

        RaiseChanged();
        ScheduleWrite(key.IsPersisted);
        return true;
    }

    /// <summary>Removes one rule; returns false when it was not there.</summary>
    public bool Revoke(ApprovalRuleKey key)
    {
        bool removed;
        lock (gate)
        {
            if (disposed)
            {
                return false;
            }

            var index = rules.FindIndex(rule => rule.Key == key);
            if (index < 0)
            {
                return false;
            }

            removed = rules[index].Key.IsPersisted;
            rules.RemoveAt(index);
            version++;
            if (removed)
            {
                dirty = true;
            }
        }

        RaiseChanged();
        ScheduleWrite(removed);
        return true;
    }

    /// <summary>Drops every rule that names one of <paramref name="toolNames"/>, across all scopes. Returns how many.</summary>
    public int RemoveByToolNames(IReadOnlyCollection<string> toolNames)
    {
        ArgumentNullException.ThrowIfNull(toolNames);
        if (toolNames.Count == 0)
        {
            return 0;
        }

        var removedPersisted = false;
        var removed = 0;
        lock (gate)
        {
            if (disposed)
            {
                return 0;
            }

            for (var index = rules.Count - 1; index >= 0; index--)
            {
                if (!toolNames.Contains(rules[index].Key.ToolName))
                {
                    continue;
                }

                removedPersisted |= rules[index].Key.IsPersisted;
                rules.RemoveAt(index);
                version++;
                removed++;
            }

            if (removedPersisted)
            {
                dirty = true;
            }
        }

        if (removed == 0)
        {
            return 0;
        }

        RaiseChanged();
        ScheduleWrite(removedPersisted);
        return removed;
    }

    /// <summary>
    /// Whether this exact tool call has been remembered. Any scope that matches allows; <c>extension_delete</c> is never
    /// allowed from a rule, and a rule that only matches another project/session does not.
    /// </summary>
    public bool IsAllowed(string toolName, ToolSideEffect sideEffect, string sessionId, string? projectId)
    {
        if (string.IsNullOrWhiteSpace(toolName) || !IsRememberable(toolName))
        {
            return false;
        }

        lock (gate)
        {
            foreach (var rule in rules)
            {
                var key = rule.Key;
                if (key.SideEffect != sideEffect || !string.Equals(key.ToolName, toolName, StringComparison.Ordinal))
                {
                    continue;
                }

                var matches = key.Scope switch
                {
                    ApprovalScope.Session => string.Equals(key.ScopeId, sessionId, StringComparison.Ordinal),
                    ApprovalScope.Project => projectId is { Length: > 0 } &&
                                             string.Equals(key.ScopeId, projectId, StringComparison.Ordinal),
                    _ => true,
                };
                if (matches)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Writes a pending change now instead of waiting for the debounce; used by tests and on shutdown.</summary>
    public async Task SaveAsync(CancellationToken ct)
    {
        CancellationTokenSource? pending;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            pending = debounce;
            debounce = null;
        }

        Cancel(pending);
        await Task.Run(() => WriteCore(ct), ct).ConfigureAwait(false);
    }

    /// <summary>Flushes a pending write on the thread pool; a failed final write is traced, not thrown.</summary>
    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? pending;
        bool flush;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            pending = debounce;
            debounce = null;
            flush = dirty;
        }

        Cancel(pending);
        if (!flush)
        {
            return;
        }

        try
        {
            await Task.Run(() => WriteCore(CancellationToken.None), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            Trace.WriteLine("[approval-rules] the final save failed: " + exception.Message);
        }
    }

    /// <summary><c>extension_delete</c> is a deletion confirmation, never a remembered decision.</summary>
    public static bool IsRememberable(string toolName) =>
        !string.Equals(toolName, ExtensionToolNames.Delete, StringComparison.Ordinal);

    /// <summary>The scope a "remember" button of a tool card records.</summary>
    public static ApprovalRuleKey KeyFor(string toolName, ToolSideEffect sideEffect, ApprovalScope scope, string sessionId, string? projectId) =>
        scope switch
        {
            ApprovalScope.Session => ApprovalRuleKey.ForSession(toolName, sideEffect, sessionId),
            ApprovalScope.Project => ApprovalRuleKey.ForProject(toolName, sideEffect, projectId ?? string.Empty),
            _ => ApprovalRuleKey.Global(toolName, sideEffect),
        };

    private void LoadCore()
    {
        LoadOutcome outcome;
        try
        {
            outcome = ReadDocument();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A read that throws must never leave the shared load task faulted: every later approval awaits it. The file
            // is treated as unreadable (no rules, the original protected) and the failure is reported as the load problem.
            outcome = new LoadOutcome([], "approval-rules.json 无法读取：" + exception.Message, null, false);
        }

        lock (gate)
        {
            loadProblem = outcome.Problem;
            protectOriginal = outcome.Problem is not null;
            originalBytes = outcome.Protect ? outcome.Raw : null;
            loaded = true;

            // A rule the user granted while the file was being read wins: merging never drops an in-memory rule.
            foreach (var rule in outcome.Rules)
            {
                var known = false;
                foreach (var existing in rules)
                {
                    if (existing.Key == rule.Key)
                    {
                        known = true;
                        break;
                    }
                }

                if (!known)
                {
                    rules.Add(rule);
                }
            }
        }

        RaiseChanged();
    }

    private LoadOutcome ReadDocument()
    {
        byte[]? bytes;
        try
        {
            bytes = AtomicFile.ReadAllBytesShared(filePath, null, CancellationToken.None);
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            return new LoadOutcome([], "读取 approval-rules.json 失败：" + exception.Message, null, false);
        }

        if (bytes is null || bytes.Length == 0)
        {
            return new LoadOutcome([], null, null, false);
        }

        ApprovalRulesDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(bytes, ApprovalRulesJsonContext.Default.ApprovalRulesDocument);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return new LoadOutcome([], "approval-rules.json 不是有效的审批规则文件：" + exception.Message, bytes, true);
        }

        if (document is null)
        {
            return new LoadOutcome([], "approval-rules.json 为空。", bytes, true);
        }

        if (document.Version != DocumentVersion)
        {
            return new LoadOutcome(
                [], $"approval-rules.json 的版本 {document.Version} 无法识别。", bytes, true);
        }

        if (document.Rules is null)
        {
            return new LoadOutcome([], "approval-rules.json 缺少规则列表。", bytes, true);
        }

        var loaded = new List<ApprovalRule>(document.Rules.Count);
        foreach (var record in document.Rules)
        {
            if (!TryReadRule(record, out var rule))
            {
                continue;
            }

            loaded.Add(rule);
        }

        return new LoadOutcome(loaded, null, null, false);
    }

    /// <summary>Only a complete project or global allow is a rule; anything else is skipped, so a broken entry never grants.</summary>
    private static bool TryReadRule(ApprovalRuleRecord record, out ApprovalRule rule)
    {
        rule = null!;
        if (record is null || string.IsNullOrWhiteSpace(record.Tool) || !IsRememberable(record.Tool))
        {
            return false;
        }

        if (!Enum.TryParse<ToolSideEffect>(record.SideEffect, ignoreCase: true, out var sideEffect) ||
            !Enum.IsDefined(sideEffect))
        {
            return false;
        }

        if (!Enum.TryParse<ApprovalScope>(record.Scope, ignoreCase: true, out var scope) ||
            !Enum.IsDefined(scope) ||
            scope == ApprovalScope.Session)
        {
            return false;
        }

        var key = scope == ApprovalScope.Global
            ? ApprovalRuleKey.Global(record.Tool, sideEffect)
            : ApprovalRuleKey.ForProject(record.Tool, sideEffect, record.ScopeId ?? string.Empty);
        if (!key.IsComplete)
        {
            return false;
        }

        rule = new ApprovalRule(key, record.CreatedAt == default ? DateTimeOffset.UtcNow : record.CreatedAt);
        return true;
    }

    private void ScheduleWrite(bool persistedChanged)
    {
        if (!persistedChanged)
        {
            return;
        }

        CancellationToken token;
        CancellationTokenSource? previous;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            previous = debounce;
            var cts = new CancellationTokenSource();
            // Read the token while the lock still keeps a concurrent schedule from cancelling and disposing this source.
            token = cts.Token;
            debounce = cts;
        }

        Cancel(previous);
        _ = WriteAfterDelayAsync(token);
    }

    private async Task WriteAfterDelayAsync(CancellationToken ct)
    {
        try
        {
            if (debounceMilliseconds > 0)
            {
                await Task.Delay(debounceMilliseconds, ct).ConfigureAwait(false);
            }

            WriteCore(ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            lock (gate)
            {
                saveProblem = "写入 approval-rules.json 失败：" + exception.Message;
            }

            Trace.WriteLine("[approval-rules] save failed: " + exception.Message);
        }
    }

    /// <summary>The one write path: back up an unreadable file once, then replace it atomically with a fresh document.</summary>
    private void WriteCore(CancellationToken ct)
    {
        // Writers run one at a time and take their snapshot only after getting in, so the file written last always
        // reflects the newest state instead of an older snapshot overtaking a newer one.
        lock (writeGate)
        {
            WriteLocked(ct);
        }
    }

    private void WriteLocked(CancellationToken ct)
    {
        int snapshot;
        bool shouldBackUp;
        byte[]? retained;
        ApprovalRulesDocument document;
        lock (gate)
        {
            snapshot = version;
            shouldBackUp = protectOriginal;
            retained = originalBytes;
            document = new ApprovalRulesDocument
            {
                Version = DocumentVersion,
                Rules = [],
            };
            foreach (var rule in rules)
            {
                if (!rule.Key.IsPersisted)
                {
                    continue;
                }

                document.Rules.Add(new ApprovalRuleRecord
                {
                    Tool = rule.Key.ToolName,
                    SideEffect = rule.Key.SideEffect.ToString(),
                    Scope = rule.Key.Scope.ToString(),
                    ScopeId = rule.Key.ScopeId,
                    CreatedAt = rule.CreatedAt,
                });
            }
        }

        var existing = ReadExistingBytes(ct);
        var unreadable = shouldBackUp || IsUnreadable(existing);
        if (unreadable)
        {
            var source = existing is { Length: > 0 } ? existing : retained;
            if (source is { Length: > 0 })
            {
                AtomicFile.WriteAllBytes(BackupPath, source, null, ct);
            }
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, ApprovalRulesJsonContext.Default.ApprovalRulesDocument);
        var content = new byte[bytes.Length + 1];
        bytes.CopyTo(content, 0);
        content[^1] = (byte)'\n';
        AtomicFile.WriteAllBytes(filePath, content, null, ct);

        lock (gate)
        {
            if (snapshot == version)
            {
                dirty = false;
            }

            protectOriginal = false;
            originalBytes = null;
            saveProblem = null;
        }
    }

    /// <summary>Reads the current bytes so a file that turned invalid after the load is still backed up before it is replaced.</summary>
    private byte[]? ReadExistingBytes(CancellationToken ct)
    {
        try
        {
            return AtomicFile.ReadAllBytesShared(filePath, null, ct);
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            Trace.WriteLine("[approval-rules] the existing rules file could not be read: " + exception.Message);
            return null;
        }
    }

    private static bool IsUnreadable(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 })
        {
            return false;
        }

        try
        {
            var document = JsonSerializer.Deserialize(bytes, ApprovalRulesJsonContext.Default.ApprovalRulesDocument);
            return document is null || document.Version != DocumentVersion;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return true;
        }
    }

    private void RaiseChanged()
    {
        var handler = Changed;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Trace.WriteLine("[approval-rules] a change subscriber failed: " + exception.Message);
        }
    }

    private static void Cancel(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        cancellation.Dispose();
    }

    private static bool IsFileFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException
            or InvalidOperationException or NotSupportedException;

    private readonly record struct LoadOutcome(IReadOnlyList<ApprovalRule> Rules, string? Problem, byte[]? Raw, bool Protect);
}

/// <summary>On-disk shape of <c>approval-rules.json</c>.</summary>
internal sealed class ApprovalRulesDocument
{
    public int Version { get; set; }

    public List<ApprovalRuleRecord> Rules { get; set; } = [];
}

/// <summary>One persisted rule; scope and side effect are stored by name so the file stays readable.</summary>
internal sealed class ApprovalRuleRecord
{
    public string Tool { get; set; } = string.Empty;

    public string SideEffect { get; set; } = string.Empty;

    public string Scope { get; set; } = string.Empty;

    public string ScopeId { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>The rules file's source-generated context (no reflection, AOT safe).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(ApprovalRulesDocument))]
internal sealed partial class ApprovalRulesJsonContext : JsonSerializerContext
{
}
