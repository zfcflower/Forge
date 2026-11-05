using System.IO;
using Forge.Abstractions;
using Xunit;
using Sdk = Forge.Plugin.Sdk;

namespace Forge.HostClient.Tests;

/// <summary>Strict per-row validation of <c>plugins.status</c>: the first row is always a usable
/// "approved" entry, the second row carries exactly one defect, and the whole answer must be
/// rejected as "unknown" (<see cref="InvalidDataException"/> naming <c>plugins[1]</c> and, once
/// known, the plugin id) instead of returning the valid prefix as a partial registry.
/// Every rejection also proves the connection stays <see cref="HostStatus.Running"/> with no
/// execution IPC and no resolver/approval involvement.</summary>
public sealed class PluginStatusRowValidationTests
{
    private const string ValidFirstRow =
        "{\"pluginId\":\"first\",\"version\":\"1.0.0\",\"status\":\"approved\"," +
        "\"sourceHash\":\"s1\",\"dependencyHash\":\"d1\",\"sdkVersion\":\"sdk-1\"," +
        "\"permissions\":[],\"error\":null}";

    [Theory]
    [InlineData("{\"pluginId\":\"second\",\"status\":\"approved\",\"sourceHash\":\"s2\",\"dependencyHash\":\"d2\",\"sdkVersion\":\"sdk-2\",\"permissions\":[]}")]
    [InlineData("{\"pluginId\":\"second\",\"version\":\"  \",\"status\":\"approved\",\"sourceHash\":\"s2\",\"dependencyHash\":\"d2\",\"sdkVersion\":\"sdk-2\",\"permissions\":[]}")]
    [InlineData("{\"pluginId\":\"second\",\"version\":\"2.0.0\",\"status\":\"approved\",\"dependencyHash\":\"d2\",\"sdkVersion\":\"sdk-2\",\"permissions\":[]}")]
    [InlineData("{\"pluginId\":\"second\",\"version\":\"2.0.0\",\"status\":\"approved\",\"sourceHash\":\"\",\"dependencyHash\":\"d2\",\"sdkVersion\":\"sdk-2\",\"permissions\":[]}")]
    [InlineData("{\"pluginId\":\"second\",\"version\":\"2.0.0\",\"status\":\"approved\",\"sourceHash\":\"s2\",\"sdkVersion\":\"sdk-2\",\"permissions\":[]}")]
    [InlineData("{\"pluginId\":\"second\",\"version\":\"2.0.0\",\"status\":\"approved\",\"sourceHash\":\"s2\",\"dependencyHash\":\"  \",\"sdkVersion\":\"sdk-2\",\"permissions\":[]}")]
    [InlineData("{\"pluginId\":\"second\",\"version\":\"2.0.0\",\"status\":\"approved\",\"sourceHash\":\"s2\",\"dependencyHash\":\"d2\",\"permissions\":[]}")]
    [InlineData("{\"pluginId\":\"second\",\"version\":\"2.0.0\",\"status\":\"approved\",\"sourceHash\":\"s2\",\"dependencyHash\":\"d2\",\"sdkVersion\":\"\",\"permissions\":[]}")]
    [InlineData("{\"pluginId\":\"second\",\"version\":\"2.0.0\",\"status\":\"approved\",\"sourceHash\":\"s2\",\"dependencyHash\":\"d2\",\"sdkVersion\":\"sdk-2\"}")]
    [InlineData("{\"pluginId\":\"second\",\"version\":\"2.0.0\",\"status\":\"approved\",\"sourceHash\":\"s2\",\"dependencyHash\":\"d2\",\"sdkVersion\":\"sdk-2\",\"permissions\":[{\"kind\":0},null]}")]
    [InlineData("{\"pluginId\":\"second\",\"version\":\"2.0.0\",\"status\":\"approved\",\"sourceHash\":\"s2\",\"dependencyHash\":\"d2\",\"sdkVersion\":\"sdk-2\",\"permissions\":[{\"scope\":\"x\"}]}")]
    [InlineData("{\"pluginId\":\"second\",\"version\":\"2.0.0\",\"status\":\"approved\",\"sourceHash\":\"s2\",\"dependencyHash\":\"d2\",\"sdkVersion\":\"sdk-2\",\"permissions\":[{\"kind\":99}]}")]
    [InlineData("{\"pluginId\":\"second\",\"version\":\"2.0.0\",\"status\":\"approved\",\"sourceHash\":\"s2\",\"dependencyHash\":\"d2\",\"sdkVersion\":\"sdk-2\",\"permissions\":[{\"kind\":0,\"scope\":\"   \"}]}")]
    public async Task Malformed_second_row_with_known_identity_rejects_the_whole_answer(string secondRow)
    {
        await AssertSecondRowRejectedAsync(secondRow, knownIdentity: true);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"oops\"")]
    [InlineData("[1,2]")]
    [InlineData("{\"version\":\"2.0.0\",\"status\":\"approved\",\"sourceHash\":\"s2\",\"dependencyHash\":\"d2\",\"sdkVersion\":\"sdk-2\",\"permissions\":[]}")]
    [InlineData("{\"pluginId\":\"   \",\"version\":\"2.0.0\",\"status\":\"approved\",\"sourceHash\":\"s2\",\"dependencyHash\":\"d2\",\"sdkVersion\":\"sdk-2\",\"permissions\":[]}")]
    [InlineData("{\"pluginId\":42,\"version\":\"2.0.0\",\"status\":\"approved\",\"sourceHash\":\"s2\",\"dependencyHash\":\"d2\",\"sdkVersion\":\"sdk-2\",\"permissions\":[]}")]
    [InlineData("{\"pluginId\":\"second\",\"version\":\"2.0.0\",\"status\":\"approved\",\"sourceHash\":\"s2\",\"dependencyHash\":\"d2\",\"sdkVersion\":\"sdk-2\",\"permissions\":[{\"kind\":\"WriteFile\"}]}")]
    [InlineData("{\"pluginId\":\"second\",\"version\":\"2.0.0\",\"status\":\"approved\",\"sourceHash\":\"s2\",\"dependencyHash\":\"d2\",\"sdkVersion\":\"sdk-2\",\"permissions\":{}}")]
    public async Task Malformed_second_row_without_known_identity_rejects_the_whole_answer(string secondRow)
    {
        await AssertSecondRowRejectedAsync(secondRow, knownIdentity: false);
    }

    [Fact]
    public async Task Rejected_answer_leaves_no_partial_result_and_the_connection_stays_usable()
    {
        const string malformedSecond =
            "{\"pluginId\":\"second\",\"status\":\"approved\",\"sourceHash\":\"s2\"," +
            "\"dependencyHash\":\"d2\",\"sdkVersion\":\"sdk-2\",\"permissions\":[]}";
        const string validAnswer =
            "{\"plugins\":[" + ValidFirstRow + "," +
            "{\"pluginId\":\"second\",\"version\":\"2.0.0\",\"status\":\"approved\",\"sourceHash\":\"s2\"," +
            "\"dependencyHash\":\"d2\",\"sdkVersion\":\"sdk-2\",\"permissions\":[],\"error\":null}]}";
        var resolved = 0;
        var approved = 0;
        await using var host = new FakeHost();
        var calls = 0;
        host.On("plugins.status", (_, _) =>
        {
            var call = Interlocked.Increment(ref calls);
            return Task.FromResult(Json.Element(call == 1
                ? "{\"plugins\":[" + ValidFirstRow + "," + malformedSecond + "]}"
                : validAnswer));
        });
        await using var client = ClientFactory.Create(
            host,
            resolver: _ =>
            {
                Interlocked.Increment(ref resolved);
                return null;
            },
            approval: (_, _) =>
            {
                Interlocked.Increment(ref approved);
                return ValueTask.FromResult(ApprovalDecision.Allow);
            });
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => client.GetPluginStatusesAsync(limit.Token));
        Assert.Contains("plugins[1]", error.Message);
        Assert.Contains("('second')", error.Message);

        var statuses = await client.GetPluginStatusesAsync(limit.Token);

        Assert.Equal(["first", "second"], statuses.Select(status => status.PluginId));
        Assert.Equal(2, calls);
        Assert.Equal(0, resolved);
        Assert.Equal(0, approved);
        Assert.Equal(HostStatus.Running, client.Status);
        AssertNoExecutionSideEffects(host);
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("approved")]
    [InlineData("disabled")]
    [InlineData("uninstalled")]
    public async Task Documented_status_with_complete_metadata_is_valid(string status)
    {
        var resolved = 0;
        var approved = 0;
        await using var host = new FakeHost();
        host.On("plugins.status", (_, _) => Task.FromResult(Json.Element(
            "{\"plugins\":[{\"pluginId\":\"alpha\",\"version\":\"1.2.0\",\"status\":\"" + status + "\"," +
            "\"sourceHash\":\"s\",\"dependencyHash\":\"d\",\"sdkVersion\":\"sdk-2.0\"," +
            "\"permissions\":[{\"kind\":2,\"scope\":\"run\"}],\"error\":null}]}")));
        await using var client = ClientFactory.Create(
            host,
            resolver: _ =>
            {
                Interlocked.Increment(ref resolved);
                return null;
            },
            approval: (_, _) =>
            {
                Interlocked.Increment(ref approved);
                return ValueTask.FromResult(ApprovalDecision.Allow);
            });
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();

        var statuses = await client.GetPluginStatusesAsync(limit.Token);

        var single = Assert.Single(statuses);
        Assert.Equal("alpha", single.PluginId);
        Assert.Equal(status, single.Status);
        Assert.Equal("1.2.0", single.Version);
        Assert.Equal("s", single.SourceHash);
        Assert.Equal("d", single.DependencyHash);
        Assert.Equal("sdk-2.0", single.SdkVersion);
        Assert.Equal([new Sdk.Permission(Sdk.PermissionKind.ExecuteCommand, "run")], single.Permissions);
        Assert.Equal(0, resolved);
        Assert.Equal(0, approved);
        Assert.Equal(HostStatus.Running, client.Status);
        AssertNoExecutionSideEffects(host);
    }

    [Theory]
    [InlineData("quarantined")]
    [InlineData("needs-review")]
    public async Task Unknown_nonblank_status_token_is_forwarded_verbatim(string status)
    {
        await using var host = new FakeHost();
        host.On("plugins.status", (_, _) => Task.FromResult(Json.Element(
            "{\"plugins\":[{\"pluginId\":\"alpha\",\"version\":\"1.0\",\"status\":\"" + status + "\"," +
            "\"sourceHash\":\"s\",\"dependencyHash\":\"d\",\"sdkVersion\":\"sdk\"," +
            "\"permissions\":[],\"error\":null}]}")));
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();

        var statuses = await client.GetPluginStatusesAsync(limit.Token);

        var single = Assert.Single(statuses);
        Assert.Equal(status, single.Status);
        Assert.NotEqual("approved", single.Status);
        Assert.Equal(HostStatus.Running, client.Status);
    }

    [Fact]
    public async Task Invalid_row_without_approval_metadata_remains_visible_with_its_error()
    {
        await using var host = new FakeHost();
        host.On("plugins.status", (_, _) => Task.FromResult(Json.Element(
            "{\"plugins\":[{\"pluginId\":\"broken\",\"status\":\"invalid\"," +
            "\"permissions\":[],\"error\":\"load failed\"}]}")));
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();

        var statuses = await client.GetPluginStatusesAsync(limit.Token);

        var single = Assert.Single(statuses);
        Assert.Equal("broken", single.PluginId);
        Assert.Equal("invalid", single.Status);
        Assert.Equal("load failed", single.Error);
        Assert.Equal(HostStatus.Running, client.Status);
    }

    [Fact]
    public async Task Invalid_row_missing_metadata_exposes_empty_strings_not_null()
    {
        await using var host = new FakeHost();
        host.On("plugins.status", (_, _) => Task.FromResult(Json.Element(
            "{\"plugins\":[{\"pluginId\":\"broken\",\"status\":\"invalid\"," +
            "\"permissions\":[],\"error\":\"load failed\"}]}")));
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();

        var statuses = await client.GetPluginStatusesAsync(limit.Token);

        var single = Assert.Single(statuses);
        Assert.NotNull(single.Version);
        Assert.NotNull(single.SourceHash);
        Assert.NotNull(single.DependencyHash);
        Assert.NotNull(single.SdkVersion);
        Assert.Equal(string.Empty, single.Version);
        Assert.Equal(string.Empty, single.SourceHash);
        Assert.Equal(string.Empty, single.DependencyHash);
        Assert.Equal(string.Empty, single.SdkVersion);
    }

    [Fact]
    public async Task Explicit_ReadFile_kind_and_null_scope_are_accepted()
    {
        await using var host = new FakeHost();
        host.On("plugins.status", (_, _) => Task.FromResult(Json.Element(
            "{\"plugins\":[{\"pluginId\":\"alpha\",\"version\":\"1.0\",\"status\":\"approved\"," +
            "\"sourceHash\":\"s\",\"dependencyHash\":\"d\",\"sdkVersion\":\"sdk\"," +
            "\"permissions\":[{\"kind\":0},{\"kind\":1,\"scope\":null}],\"error\":null}]}")));
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();

        var statuses = await client.GetPluginStatusesAsync(limit.Token);

        Assert.Equal(
            [new Sdk.Permission(Sdk.PermissionKind.ReadFile, null), new Sdk.Permission(Sdk.PermissionKind.WriteFile, null)],
            Assert.Single(statuses).Permissions);
        Assert.Equal(HostStatus.Running, client.Status);
    }

    [Fact]
    public async Task Empty_registry_decodes_to_an_empty_list()
    {
        var resolved = 0;
        var approved = 0;
        await using var host = new FakeHost();
        host.On("plugins.status", (_, _) => Task.FromResult(Json.Element("{\"plugins\":[]}")));
        await using var client = ClientFactory.Create(
            host,
            resolver: _ =>
            {
                Interlocked.Increment(ref resolved);
                return null;
            },
            approval: (_, _) =>
            {
                Interlocked.Increment(ref approved);
                return ValueTask.FromResult(ApprovalDecision.Allow);
            });
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();

        var statuses = await client.GetPluginStatusesAsync(limit.Token);

        Assert.Empty(statuses);
        Assert.Equal(0, resolved);
        Assert.Equal(0, approved);
        Assert.Equal(HostStatus.Running, client.Status);
        AssertNoExecutionSideEffects(host);
    }

    private static async Task AssertSecondRowRejectedAsync(string secondRow, bool knownIdentity)
    {
        var resolved = 0;
        var approved = 0;
        await using var host = new FakeHost();
        var calls = 0;
        host.On("plugins.status", (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(Json.Element("{\"plugins\":[" + ValidFirstRow + "," + secondRow + "]}"));
        });
        await using var client = ClientFactory.Create(
            host,
            resolver: _ =>
            {
                Interlocked.Increment(ref resolved);
                return null;
            },
            approval: (_, _) =>
            {
                Interlocked.Increment(ref approved);
                return ValueTask.FromResult(ApprovalDecision.Allow);
            });
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => client.GetPluginStatusesAsync(limit.Token));

        Assert.Contains("plugins.status", error.Message);
        Assert.Contains("plugins[1]", error.Message);
        if (knownIdentity)
        {
            Assert.Contains("('second')", error.Message);
        }
        else
        {
            Assert.DoesNotContain("('", error.Message);
        }

        Assert.Equal(1, calls);
        Assert.Equal(0, resolved);
        Assert.Equal(0, approved);
        Assert.Equal(HostStatus.Running, client.Status);
        AssertNoExecutionSideEffects(host);
        var diagnostic = Assert.Single(
            client.Diagnostics, entry => entry.Contains("plugins.status", StringComparison.Ordinal));
        Assert.Contains("unusable", diagnostic);
        Assert.Contains("plugins[1]", diagnostic);
    }

    private static void AssertNoExecutionSideEffects(FakeHost host)
    {
        Assert.Empty(host.Requests("plugin.execute"));
        Assert.Empty(host.Requests("code.execute"));
        Assert.Empty(host.Requests("plugin.command"));
        Assert.Empty(host.Requests("plugin.prompt"));
        Assert.Empty(host.Requests("plugin.approve"));
        Assert.Empty(host.Requests("plugin.revoke"));
    }
}
