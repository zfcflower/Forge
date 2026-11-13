using Forge.Abstractions;
using Sdk = Forge.Plugin.Sdk;

namespace Forge.HostClient.Tests;

/// <summary>Client startups that recur across the host-client tests but add something a plain
/// <see cref="ClientFactory.StartAsync"/> call cannot: a status log attached before the first transition, and a
/// reconnect that returns only once the host accepted the new connection.</summary>
internal static class ClientSessions
{
    /// <summary>Creates a client for <paramref name="host"/>, records every status transition into
    /// <paramref name="statuses"/> from its first one on and returns it once connected. The caller disposes it
    /// before the host.</summary>
    public static Task<ExtensionHostClient> StartTrackedAsync(
        this FakeHost host,
        out EventLog<(HostStatus Status, string? Detail)> statuses,
        Func<string, ITool?>? resolver = null,
        Func<ApprovalRequest, CancellationToken, ValueTask<ApprovalDecision>>? approval = null,
        ExtensionHostClientOptions? options = null,
        TimeSpan? startTimeout = null)
    {
        var client = ClientFactory.Create(host, resolver, approval, options, startTimeout);
        statuses = client.TrackStatus();
        return ConnectAsync(client, host);
    }

    /// <summary>Starts <paramref name="client"/> again against <paramref name="host"/> and returns once the host accepted
    /// the new connection. The accept is initiated first, then the start is awaited under the test limit.</summary>
    public static async Task ReconnectAsync(this ExtensionHostClient client, FakeHost host, CancellationToken ct)
    {
        var accepting = host.AcceptAsync();
        await client.StartAsync(ct).Bounded().ConfigureAwait(false);
        await accepting.Bounded().ConfigureAwait(false);
    }

    private static async Task<ExtensionHostClient> ConnectAsync(ExtensionHostClient client, FakeHost host)
    {
        try
        {
            await client.StartConnectedAsync(host).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>A resolver for the single tool a case wires up, so the lambda and its name comparison are not repeated.</summary>
internal static class ToolWiring
{
    /// <summary>A resolver that answers <paramref name="tool"/> for its own definition name and <c>null</c> for every
    /// other name.</summary>
    public static Func<string, ITool?> Resolver(this ITool tool) =>
        name => string.Equals(name, tool.Definition.Name, StringComparison.Ordinal) ? tool : null;
}

/// <summary>An approval path that allows everything, for cases that only need to get past approval.</summary>
internal static class Approvals
{
    /// <summary>Allows every request without recording it.</summary>
    public static ValueTask<ApprovalDecision> AllowAll(ApprovalRequest request, CancellationToken ct) =>
        ValueTask.FromResult(ApprovalDecision.Allow);
}

/// <summary>Answers the requests of <paramref name="operation"/> with <paramref name="result"/>, so a case
/// declares only the payload it cares about.</summary>
internal static class HostAnswers
{
    public static void Answer<T>(this FakeHost host, string operation, T result) =>
        host.On(operation, (_, _) => Task.FromResult(result));

    /// <summary>Answers with a raw MessagePack payload (empty: a success response without a payload).</summary>
    public static void AnswerRaw(this FakeHost host, string operation, RawPayload payload) =>
        host.On(operation, (_, _) => Task.FromResult(HostReply.Raw(payload)));
}

internal static class CommandFixtures
{
    /// <summary>Answers <c>plugin.command</c> with <paramref name="commandResult"/> and registers one command of
    /// <paramref name="pluginId"/>; returns the proxy the client built for it.</summary>
    public static async Task<ExtensionHostCommand> RegisterCommandAsync(
        FakeHost host, ExtensionHostClient client, string pluginId, string name, Sdk.CommandOutput? commandResult = null)
    {
        host.Answer(Sdk.ExtensionProtocol.PluginCommand, commandResult ?? new Sdk.CommandOutput("hello", "next", false));
        await host.NotifyAndSyncAsync(
            Sdk.ExtensionProtocol.ContributionsRegister,
            HostWire.Contributions(
                pluginId, new Sdk.PluginContributionSet { Commands = [new Sdk.CommandDeclaration(name, "d", "")] }));
        return (ExtensionHostCommand)client.Commands.Single(c => c.Definition.Name == name);
    }
}
