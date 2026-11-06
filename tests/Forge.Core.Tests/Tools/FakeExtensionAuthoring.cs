using Forge.Abstractions;
using Forge.Core.Tools;

namespace Forge.Core.Tests.Tools;

/// <summary>Configurable <see cref="IExtensionAuthoring"/> double that records every call.</summary>
internal sealed class FakeExtensionAuthoring : IExtensionAuthoring
{
    private readonly Lock gate = new();
    private readonly List<(ExtensionDraft Draft, ToolContext Context)> submitted = [];
    private readonly List<(string Id, string Version)> disabled = [];
    private readonly List<(string Id, string Version)> deleted = [];
    private int listCalls;
    private int submitCalls;
    private int disableCalls;
    private int deleteCalls;

    public Func<CancellationToken, Task<IReadOnlyList<ExtensionSummary>>> ListHandler { get; set; } =
        _ => Task.FromResult<IReadOnlyList<ExtensionSummary>>([]);

    public Func<ExtensionDraft, ToolContext, CancellationToken, Task<ExtensionSubmitResult>> SubmitHandler { get; set; } =
        (_, _, _) => Task.FromResult(new ExtensionSubmitResult(ExtensionSubmitOutcome.Approved, string.Empty, []));

    public Func<string, string, CancellationToken, Task<string>> DisableHandler { get; set; } =
        (id, version, _) => Task.FromResult($"Disabled '{id}' version '{version}'.");

    public Func<string, string, CancellationToken, Task<string>> DeleteHandler { get; set; } =
        (id, version, _) => Task.FromResult($"Deleted '{id}' version '{version}'.");

    public int ListCalls => Volatile.Read(ref listCalls);

    public int SubmitCalls => Volatile.Read(ref submitCalls);

    public int DisableCalls => Volatile.Read(ref disableCalls);

    public int DeleteCalls => Volatile.Read(ref deleteCalls);

    public IReadOnlyList<(ExtensionDraft Draft, ToolContext Context)> Submitted
    {
        get
        {
            lock (gate)
            {
                return [.. submitted];
            }
        }
    }

    public IReadOnlyList<(string Id, string Version)> Disabled
    {
        get
        {
            lock (gate)
            {
                return [.. disabled];
            }
        }
    }

    public IReadOnlyList<(string Id, string Version)> Deleted
    {
        get
        {
            lock (gate)
            {
                return [.. deleted];
            }
        }
    }

    public Task<IReadOnlyList<ExtensionSummary>> ListAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref listCalls);
        return ListHandler(ct);
    }

    public Task<ExtensionSubmitResult> SubmitAsync(ExtensionDraft draft, ToolContext context, CancellationToken ct)
    {
        Interlocked.Increment(ref submitCalls);
        lock (gate)
        {
            submitted.Add((draft, context));
        }

        return SubmitHandler(draft, context, ct);
    }

    public Task<string> DisableAsync(string id, string version, CancellationToken ct)
    {
        Interlocked.Increment(ref disableCalls);
        lock (gate)
        {
            disabled.Add((id, version));
        }

        return DisableHandler(id, version, ct);
    }

    public Task<string> DeleteAsync(string id, string version, CancellationToken ct)
    {
        Interlocked.Increment(ref deleteCalls);
        lock (gate)
        {
            deleted.Add((id, version));
        }

        return DeleteHandler(id, version, ct);
    }
}
