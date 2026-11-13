using System.IO;
using Forge.Abstractions;
using Forge.Ipc;
using Xunit;
using static Forge.HostClient.Tests.ExtensionAuthoringResponseTests;
using Sdk = Forge.Plugin.Sdk;

namespace Forge.HostClient.Tests;

/// <summary>Strict per-row validation of <c>plugins.status</c>: the first row is always a usable
/// "approved" entry, the second row carries exactly one defect, and the whole answer must be
/// rejected as "unknown" (<see cref="InvalidDataException"/> naming <c>plugins[1]</c> and, once
/// known, the plugin id; or, for a row of the wrong MessagePack type, reporting the answer undecodable) instead of
/// returning the valid prefix as a partial registry.
/// Every rejection also proves the connection stays <see cref="HostStatus.Running"/> with no
/// execution IPC and no resolver/approval involvement.</summary>
public sealed class PluginStatusRowValidationTests
{
    private static Sdk.PluginStatusInfo FirstRow { get; } =
        new("first", "1.0.0", "approved", "s1", "d1", "sdk-1", [], null);

    private static Sdk.PluginStatusInfo SecondRow { get; } =
        new("second", "2.0.0", "approved", "s2", "d2", "sdk-2", [], null);

    /// <summary>Second rows with exactly one defect after a known plugin id.</summary>
    public static TheoryData<string> DefectsWithKnownIdentity =>
    [
        "blank version", "empty source hash", "blank dependency hash", "empty sdk version", "null permission",
        "negative permission kind with scope", "undefined permission kind", "blank permission scope",
    ];

    [Theory]
    [MemberData(nameof(DefectsWithKnownIdentity))]
    public async Task Malformed_second_row_with_known_identity_rejects_the_whole_answer(string defect)
    {
        var second = defect switch
        {
            "blank version" => Row(SecondRow with { Version = "  " }),
            "empty source hash" => Row(SecondRow with { SourceHash = "" }),
            "blank dependency hash" => Row(SecondRow with { DependencyHash = "  " }),
            "empty sdk version" => Row(SecondRow with { SdkVersion = "" }),
            "null permission" => Replace(
                SecondRow,
                (PermissionsKey, ArrayOf(IpcSerializer.Serialize(new Sdk.Permission(Sdk.PermissionKind.ReadFile)), Nil))),
            "negative permission kind with scope" =>
                Row(SecondRow with { Permissions = [new Sdk.Permission((Sdk.PermissionKind)(-1), "x")] }),
            "undefined permission kind" =>
                Row(SecondRow with { Permissions = [new Sdk.Permission((Sdk.PermissionKind)99)] }),
            _ => Row(SecondRow with { Permissions = [new Sdk.Permission(Sdk.PermissionKind.ReadFile, "   ")] }),
        };

        await AssertSecondRowRejectedAsync(HostReply.Raw(RawAnswer(second)), knownIdentity: true);
    }

    [Theory]
    [InlineData("null row")]
    [InlineData("blank plugin id")]
    public async Task Malformed_second_row_without_known_identity_rejects_the_whole_answer(string defect)
    {
        var second = defect switch
        {
            "null row" => Nil,
            _ => Row(SecondRow with { PluginId = "   " }),
        };

        await AssertSecondRowRejectedAsync(HostReply.Raw(RawAnswer(second)), knownIdentity: false);
    }

    /// <summary>Second rows the decoder refuses, which no typed sender can produce: a row or member of the wrong
    /// MessagePack type, or a nil member (a field the host left out; the decoder validates the SDK's nullable
    /// annotations, even on an "invalid" row). The whole answer is rejected before any row is validated, so the message
    /// names no row.</summary>
    public static TheoryData<string> UndecodableSecondRows =>
    [
        "int", "string", "array of ints", "int plugin id", "string permission kind", "map permissions",
        "nil plugin id", "nil version", "nil source hash", "nil dependency hash", "nil sdk version", "nil permissions",
        "invalid row with nil metadata",
    ];

    [Theory]
    [MemberData(nameof(UndecodableSecondRows))]
    public async Task Undecodable_second_row_rejects_the_whole_answer(string shape)
    {
        byte[] second = shape switch
        {
            "nil plugin id" => Replace(SecondRow, (PluginIdKey, Nil)),
            "nil version" => Replace(SecondRow, (VersionKey, Nil)),
            "nil source hash" => Replace(SecondRow, (SourceHashKey, Nil)),
            "nil dependency hash" => Replace(SecondRow, (DependencyHashKey, Nil)),
            "nil sdk version" => Replace(SecondRow, (SdkVersionKey, Nil)),
            "nil permissions" => Replace(SecondRow, (PermissionsKey, Nil)),
            "invalid row with nil metadata" => Replace(
                SecondRow with { Status = "invalid", Error = "load failed" },
                (VersionKey, Nil),
                (SourceHashKey, Nil),
                (DependencyHashKey, Nil),
                (SdkVersionKey, Nil)),
            "int" => IpcSerializer.Serialize(42),
            "string" => IpcSerializer.Serialize("oops"),
            "array of ints" => [0x92, 0x01, 0x02],
            "int plugin id" => Replace(SecondRow, (PluginIdKey, IpcSerializer.Serialize(42))),
            "string permission kind" =>
                Replace(SecondRow, (PermissionsKey, ArrayOf(ArrayOf(IpcSerializer.Serialize("WriteFile"), Nil)))),
            _ => Replace(SecondRow, (PermissionsKey, [0x80])),
        };

        await AssertSecondRowRejectedAsync(HostReply.Raw(RawAnswer(second)), knownIdentity: null);
    }

    [Fact]
    public async Task Rejected_answer_leaves_no_partial_result_and_the_connection_stays_usable()
    {
        var resolver = new ResolverSpy();
        var approval = new ApprovalSpy();
        await using var host = new FakeHost();
        var calls = 0;
        host.On(Sdk.ExtensionProtocol.PluginsStatus, (_, _) =>
        {
            var call = Interlocked.Increment(ref calls);
            return Task.FromResult(call == 1
                ? HostReply.Of(Answer(FirstRow, SecondRow with { Version = " " }))
                : HostReply.Of(Answer(FirstRow, SecondRow)));
        });
        await using var client = await ClientFactory.StartAsync(
            host,
            resolver: resolver.Resolve,
            approval: approval.Approve);
        using var limit = TestLimit.Create();

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => client.GetPluginStatusesAsync(limit.Token));
        Assert.Contains("plugins[1]", error.Message);
        Assert.Contains("('second')", error.Message);

        var statuses = await client.GetPluginStatusesAsync(limit.Token);

        Assert.Equal(["first", "second"], statuses.Select(status => status.PluginId));
        Assert.Equal(2, calls);
        Assert.Equal(0, resolver.Calls);
        Assert.Equal(0, approval.Calls);
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
        var resolver = new ResolverSpy();
        var approval = new ApprovalSpy();
        await using var host = new FakeHost();
        host.Answer(
            Sdk.ExtensionProtocol.PluginsStatus,
            Answer(new Sdk.PluginStatusInfo(
                "alpha", "1.2.0", status, "s", "d", "sdk-2.0",
                [new Sdk.Permission(Sdk.PermissionKind.ExecuteCommand, "run")], null)));
        await using var client = await ClientFactory.StartAsync(
            host,
            resolver: resolver.Resolve,
            approval: approval.Approve);
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
        Assert.Equal(0, resolver.Calls);
        Assert.Equal(0, approval.Calls);
        Assert.Equal(HostStatus.Running, client.Status);
        AssertNoExecutionSideEffects(host);
    }

    [Theory]
    [InlineData("quarantined")]
    [InlineData("needs-review")]
    public async Task Unknown_nonblank_status_token_is_forwarded_verbatim(string status)
    {
        await using var host = new FakeHost();
        host.Answer(
            Sdk.ExtensionProtocol.PluginsStatus,
            Answer(new Sdk.PluginStatusInfo("alpha", "1.0", status, "s", "d", "sdk", [], null)));
        await using var client = await ClientFactory.StartAsync(host);
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
        host.Answer(Sdk.ExtensionProtocol.PluginsStatus, Answer(InvalidRow));
        await using var client = await ClientFactory.StartAsync(host);
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
        host.Answer(Sdk.ExtensionProtocol.PluginsStatus, Answer(InvalidRow));
        await using var client = await ClientFactory.StartAsync(host);
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
        host.Answer(
            Sdk.ExtensionProtocol.PluginsStatus,
            Answer(new Sdk.PluginStatusInfo(
                "alpha", "1.0", "approved", "s", "d", "sdk",
                [new Sdk.Permission(Sdk.PermissionKind.ReadFile), new Sdk.Permission(Sdk.PermissionKind.WriteFile, null)],
                null)));
        await using var client = await ClientFactory.StartAsync(host);
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
        var resolver = new ResolverSpy();
        var approval = new ApprovalSpy();
        await using var host = new FakeHost();
        host.Answer(Sdk.ExtensionProtocol.PluginsStatus, Answer());
        await using var client = await ClientFactory.StartAsync(
            host,
            resolver: resolver.Resolve,
            approval: approval.Approve);
        using var limit = TestLimit.Create();

        var statuses = await client.GetPluginStatusesAsync(limit.Token);

        Assert.Empty(statuses);
        Assert.Equal(0, resolver.Calls);
        Assert.Equal(0, approval.Calls);
        Assert.Equal(HostStatus.Running, client.Status);
        AssertNoExecutionSideEffects(host);
    }

    /// <summary>Wire indices of <see cref="Sdk.PluginStatusInfo"/> members.</summary>
    private const int PluginIdKey = 0;
    private const int VersionKey = 1;
    private const int SourceHashKey = 3;
    private const int DependencyHashKey = 4;
    private const int SdkVersionKey = 5;
    private const int PermissionsKey = 6;

    /// <summary>An "invalid" row without approval metadata: empty strings, since the decoder refuses nil members
    /// (<see cref="Undecodable_second_row_rejects_the_whole_answer"/>).</summary>
    private static Sdk.PluginStatusInfo InvalidRow { get; } =
        new("broken", "", "invalid", "", "", "", [], "load failed");

    /// <param name="knownIdentity">True: the message names the plugin; false: the row is rejected before its id is
    /// known; null: the answer cannot be decoded at all, so no row is named.</param>
    private static async Task AssertSecondRowRejectedAsync(HostReply answer, bool? knownIdentity)
    {
        var resolver = new ResolverSpy();
        var approval = new ApprovalSpy();
        await using var host = new FakeHost();
        var calls = 0;
        host.On(Sdk.ExtensionProtocol.PluginsStatus, (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(answer);
        });
        await using var client = await ClientFactory.StartAsync(
            host,
            resolver: resolver.Resolve,
            approval: approval.Approve);
        using var limit = TestLimit.Create();

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => client.GetPluginStatusesAsync(limit.Token));

        Assert.Contains("plugins.status", error.Message);
        Assert.Contains(knownIdentity is null ? "cannot be decoded" : "plugins[1]", error.Message);
        if (knownIdentity is true)
        {
            Assert.Contains("('second')", error.Message);
        }
        else
        {
            Assert.DoesNotContain("('", error.Message);
        }

        Assert.Equal(1, calls);
        Assert.Equal(0, resolver.Calls);
        Assert.Equal(0, approval.Calls);
        Assert.Equal(HostStatus.Running, client.Status);
        AssertNoExecutionSideEffects(host);
        var diagnostic = Assert.Single(
            client.Diagnostics, entry => entry.Contains("plugins.status", StringComparison.Ordinal));
        Assert.Contains("unusable", diagnostic);
        if (knownIdentity is not null)
        {
            Assert.Contains("plugins[1]", diagnostic);
        }
    }

    private static Sdk.PluginStatusResponse Answer(params Sdk.PluginStatusInfo[] rows) => new(rows);

    private static byte[] Row(Sdk.PluginStatusInfo row) => IpcSerializer.Serialize(row);

    /// <summary>A <c>plugins.status</c> answer <c>[[first, second]]</c> whose second row is raw bytes.</summary>
    private static RawPayload RawAnswer(byte[] second) => new(ArrayOf(ArrayOf(Row(FirstRow), second)));

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
