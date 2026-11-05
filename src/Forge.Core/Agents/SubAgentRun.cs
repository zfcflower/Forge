using System.Text;
using Forge.Abstractions;

namespace Forge.Core.Agents;

/// <summary>
/// Control and lifecycle state of one sub-agent run: the steering and follow-up queues the loop drains at its
/// turn boundaries, the stop request that cancels this run alone, and the events the UI needs. Checks on the
/// hot path read one counter and only take the lock when an instruction is actually pending.
/// </summary>
internal sealed class SubAgentRun : IDisposable
{
    private const string StoppedByUser = "stopped by the user";

    private readonly CancellationTokenSource cancellation;
    private readonly IAgentEventSink sink;
    private readonly Lock gate = new();
    private List<string>? steers;
    private List<string>? followUps;
    private StringBuilder? queuedText;
    private string? stopReason;
    private int steerCount;
    private int followUpCount;
    private bool running;
    private bool stopRequested;
    private bool finished;

    internal SubAgentRun(string agentId, IAgentEventSink sink, CancellationToken parentToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(sink);
        AgentId = agentId;
        this.sink = sink;
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
    }

    public string AgentId { get; }

    public CancellationToken Token => cancellation.Token;

    public bool StopRequested => Volatile.Read(ref stopRequested);

    public string StopReason
    {
        get
        {
            lock (gate)
            {
                return stopReason ?? StoppedByUser;
            }
        }
    }

    public void ReportQueued() => sink.Emit(new AgentSubAgentQueued(AgentId, DateTimeOffset.UtcNow));

    /// <summary>
    /// Marks the run as admitted and returns the instruction text that accumulated while it waited for a slot,
    /// so the loop can append it to the task it is about to send. Null when nothing arrived.
    /// </summary>
    public string? Admit()
    {
        string? appended;
        lock (gate)
        {
            if (finished)
            {
                return null;
            }

            running = true;
            appended = queuedText?.ToString();
            queuedText = null;
        }

        sink.Emit(new AgentSubAgentRunning(AgentId, DateTimeOffset.UtcNow));
        return appended;
    }

    public SubAgentControlResult Steer(string instruction)
    {
        ArgumentNullException.ThrowIfNull(instruction);
        var whileQueued = false;
        lock (gate)
        {
            if (finished)
            {
                return SubAgentControlResult.AlreadyFinished;
            }

            if (running)
            {
                (steers ??= []).Add(instruction);
                steerCount++;
            }
            else
            {
                AppendQueued(instruction);
                whileQueued = true;
            }
        }

        if (whileQueued)
        {
            sink.Emit(new AgentSubAgentSteered(AgentId, instruction, 0, DateTimeOffset.UtcNow));
        }

        return SubAgentControlResult.Accepted;
    }

    public SubAgentControlResult FollowUp(string instruction)
    {
        ArgumentNullException.ThrowIfNull(instruction);
        int pending;
        lock (gate)
        {
            if (finished)
            {
                return SubAgentControlResult.AlreadyFinished;
            }

            if (running)
            {
                (followUps ??= []).Add(instruction);
                followUpCount++;
                pending = followUpCount;
            }
            else
            {
                AppendQueued(instruction);
                pending = 1;
            }
        }

        sink.Emit(new AgentSubAgentFollowUpQueued(AgentId, instruction, pending, DateTimeOffset.UtcNow));
        return SubAgentControlResult.Accepted;
    }

    public SubAgentControlResult Stop(string? reason)
    {
        lock (gate)
        {
            if (finished)
            {
                return SubAgentControlResult.AlreadyFinished;
            }

            if (stopRequested)
            {
                return SubAgentControlResult.Accepted;
            }

            stopReason = string.IsNullOrWhiteSpace(reason) ? StoppedByUser : reason;
            Volatile.Write(ref stopRequested, true);
        }

        try
        {
            cancellation.Cancel();
        }
        catch (Exception exception) when (exception is ObjectDisposedException or AggregateException)
        {
        }

        return SubAgentControlResult.Accepted;
    }

    /// <summary>Instructions waiting for the next tool-batch boundary, joined in the order they arrived.</summary>
    public string? TakeSteering()
    {
        if (Volatile.Read(ref steerCount) == 0)
        {
            return null;
        }

        lock (gate)
        {
            if (steers is null || steers.Count == 0)
            {
                return null;
            }

            var text = steers.Count == 1 ? steers[0] : string.Join("\n\n", steers);
            steers.Clear();
            Volatile.Write(ref steerCount, 0);
            return text;
        }
    }

    /// <summary>Follow-ups to deliver instead of finishing, in the order they arrived.</summary>
    public List<string>? TakeFollowUps()
    {
        if (Volatile.Read(ref followUpCount) == 0)
        {
            return null;
        }

        lock (gate)
        {
            if (followUps is null || followUps.Count == 0)
            {
                return null;
            }

            var taken = new List<string>(followUps);
            followUps.Clear();
            Volatile.Write(ref followUpCount, 0);
            return taken;
        }
    }

    public void Finish(SubAgentStatus status, TokenUsage usage, TimeSpan elapsed, string? error)
    {
        lock (gate)
        {
            if (finished)
            {
                return;
            }

            finished = true;
        }

        sink.Emit(new AgentSubAgentFinished(AgentId, status, usage, elapsed, error, DateTimeOffset.UtcNow));
    }

    public void Dispose() => cancellation.Dispose();

    private void AppendQueued(string instruction)
    {
        queuedText ??= new StringBuilder(instruction.Length + 16);
        if (queuedText.Length > 0)
        {
            queuedText.Append("\n\n");
        }

        queuedText.Append(instruction);
    }
}
