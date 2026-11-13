using System.IO;
using Forge.Ipc;
using MessagePack;
using Xunit;
using static Forge.HostClient.Tests.AuthoringPayloads;
using Sdk = Forge.Plugin.Sdk;

namespace Forge.HostClient.Tests;

/// <summary>Field-by-field rejection of authoring answers. Every case throws <see cref="InvalidDataException"/>
/// naming the operation and the problem, records one <c>Diagnostics</c> entry, leaves the connection
/// <see cref="HostStatus.Running"/>, and the very next call with a usable answer succeeds. A nil member (a field the
/// host left out) or a member of the wrong MessagePack type makes the whole answer undecodable: the decoder validates
/// the SDK's nullable annotations, so no row-level message can name such a field. The typed serializer refuses to write
/// either, so both are spliced into raw payloads.</summary>
public sealed class ExtensionAuthoringResponseTests
{
    private const string Operation = Sdk.ExtensionProtocol.ExtensionsStage;

    /// <summary>Wire indices of <see cref="Sdk.StagedExtension"/> members.</summary>
    private const int PluginIdKey = 0;
    private const int VersionKey = 1;
    private const int KindKey = 2;
    private const int FilesKey = 3;
    private const int PermissionsKey = 4;
    private const int DependenciesKey = 5;
    private const int SourceHashKey = 6;
    private const int DependencyHashKey = 7;
    private const int SdkVersionKey = 8;
    private const int DiagnosticsKey = 9;

    /// <summary>Wire indices of <see cref="Sdk.ExtensionCommitResult"/> and <see cref="Sdk.PluginStatusInfo"/>
    /// members.</summary>
    private const int StatusKey = 0;
    private const int ToolNamesKey = 1;
    private const int StatusVersionKey = 1;
    private const int StatusPermissionsKey = 6;

    private const string StageUndecodable = "The staged extension cannot be decoded";
    private const string CommitUndecodable = "The commit result cannot be decoded";

    /// <summary>The MessagePack nil value.</summary>
    internal static readonly byte[] Nil = [0xc0];

    public static TheoryData<string> NonObjectAnswers => ["nil", "int", "string", "array"];

    [Theory]
    [MemberData(nameof(NonObjectAnswers))]
    public async Task Stage_rejects_a_non_object_answer(string shape)
    {
        var payload = shape switch
        {
            "nil" => HostWire.Nil,
            "int" => HostWire.Payload(42),
            "string" => HostWire.Payload("oops"),
            _ => new RawPayload(new byte[] { 0x92, 0x01, 0x02 }),
        };

        await AssertStageRejectedAsync(
            HostReply.Raw(payload), "The staged extension cannot be decoded", expectIdentity: false);
    }

    [Fact]
    public async Task Stage_rejects_an_answer_without_a_payload()
    {
        await AssertStageRejectedAsync(
            HostReply.Raw(RawPayload.None), "The staged extension is missing from the response", expectIdentity: false);
    }

    [Theory]
    [InlineData("", "has no plugin id")]
    [InlineData("   ", "has no plugin id")]
    public async Task Stage_rejects_a_blank_plugin_id(string pluginId, string problem)
    {
        await AssertStageRejectedAsync(ValidStaged with { PluginId = pluginId }, problem, expectIdentity: false);
    }

    [Fact]
    public async Task Stage_rejects_a_missing_plugin_id()
    {
        await AssertStageRejectedAsync(Staged((PluginIdKey, Nil)), StageUndecodable, expectIdentity: false);
    }

    [Theory]
    [InlineData("", "has no version")]
    [InlineData("   ", "has no version")]
    public async Task Stage_rejects_a_blank_version(string version, string problem)
    {
        await AssertStageRejectedAsync(ValidStaged with { Version = version }, problem);
    }

    [Fact]
    public async Task Stage_rejects_a_missing_version()
    {
        await AssertStageRejectedAsync(Staged((VersionKey, Nil)), StageUndecodable, expectIdentity: false);
    }

    [Theory]
    [InlineData("gadget", "'gadget'")]
    [InlineData("", "has no kind")]
    [InlineData("   ", "has no kind")]
    public async Task Stage_rejects_an_unknown_or_blank_kind(string kind, string problem)
    {
        await AssertStageRejectedAsync(ValidStaged with { Kind = kind }, problem);
    }

    [Fact]
    public async Task Stage_rejects_a_missing_kind()
    {
        await AssertStageRejectedAsync(Staged((KindKey, Nil)), StageUndecodable, expectIdentity: false);
    }

    public static TheoryData<string, string, bool> UnusableFiles => new()
    {
        { "nil", StageUndecodable, false },
        { "null entry", "unusable file at files[0]", true },
        { "blank path", "without a path at files[0]", true },
        { "nil path", StageUndecodable, false },
        { "nil text", StageUndecodable, false },
    };

    [Theory]
    [MemberData(nameof(UnusableFiles))]
    public async Task Stage_rejects_unusable_files(string shape, string problem, bool expectIdentity)
    {
        var files = shape switch
        {
            "nil" => Nil,
            "null entry" => ArrayOf(Nil),
            "blank path" => ArrayOf(IpcSerializer.Serialize(new Sdk.StagedFile("  ", "x"))),
            "nil path" => ArrayOf(Replace(new Sdk.StagedFile("Plugin.cs", "x"), (0, Nil))),
            _ => ArrayOf(Replace(new Sdk.StagedFile("Plugin.cs", "x"), (1, Nil))),
        };

        await AssertStageRejectedAsync(Staged((FilesKey, files)), problem, expectIdentity);
    }

    /// <summary>A map or a string where the file list belongs.</summary>
    [Theory]
    [InlineData(new byte[] { 0x80 })]
    [InlineData(new byte[] { 0xa1, (byte)'x' })]
    public async Task Stage_rejects_files_of_the_wrong_type(byte[] element)
    {
        await AssertStageRejectedAsync(
            Staged((FilesKey, element)), "The staged extension cannot be decoded", expectIdentity: false);
    }

    [Fact]
    public async Task Stage_accepts_an_empty_file_text()
    {
        var staged = await StageOnce(HostReply.Of(ValidStaged with { Files = [new Sdk.StagedFile("Plugin.cs", "")] }));

        Assert.Equal(string.Empty, Assert.Single(staged.Files).Text);
        Assert.Equal("s", staged.SourceHash);
    }

    public static TheoryData<string, string, bool> UnusablePermissions => new()
    {
        { "nil", StageUndecodable, false },
        { "null entry", "unusable permission at permissions[0]", true },
        { "negative kind with scope", "unusable permission at permissions[0]", true },
        { "undefined kind", "unusable permission at permissions[0]", true },
        { "empty scope", "blank scope at permissions[0]", true },
        { "blank scope", "blank scope at permissions[0]", true },
    };

    [Theory]
    [MemberData(nameof(UnusablePermissions))]
    public async Task Stage_rejects_unusable_permissions(string shape, string problem, bool expectIdentity)
    {
        var permissions = shape switch
        {
            "nil" => Nil,
            "null entry" => ArrayOf(Nil),
            "negative kind with scope" => Permissions(new Sdk.Permission((Sdk.PermissionKind)(-1), "x")),
            "undefined kind" => Permissions(new Sdk.Permission((Sdk.PermissionKind)99)),
            "empty scope" => Permissions(new Sdk.Permission(Sdk.PermissionKind.WriteFile, "")),
            _ => Permissions(new Sdk.Permission(Sdk.PermissionKind.WriteFile, "   ")),
        };

        await AssertStageRejectedAsync(Staged((PermissionsKey, permissions)), problem, expectIdentity);
    }

    [Fact]
    public async Task Stage_rejects_permissions_of_the_wrong_type()
    {
        await AssertStageRejectedAsync(
            Staged((PermissionsKey, [0x80])), "The staged extension cannot be decoded", expectIdentity: false);
    }

    [Theory]
    [InlineData("nil", StageUndecodable, false)]
    [InlineData("null entry", "unusable dependency at dependencies[0]", true)]
    public async Task Stage_rejects_unusable_dependencies(string shape, string problem, bool expectIdentity)
    {
        await AssertStageRejectedAsync(
            Staged((DependenciesKey, shape == "nil" ? Nil : ArrayOf(Nil))), problem, expectIdentity);
    }

    [Theory]
    [InlineData("nil", StageUndecodable, false)]
    [InlineData("null entry", "unusable diagnostic at diagnostics[1]", true)]
    public async Task Stage_rejects_unusable_diagnostics(string shape, string problem, bool expectIdentity)
    {
        var diagnostics = shape == "nil" ? Nil : ArrayOf(IpcSerializer.Serialize("ok"), Nil);

        await AssertStageRejectedAsync(Staged((DiagnosticsKey, diagnostics)), problem, expectIdentity);
    }

    [Theory]
    [InlineData(SourceHashKey, "empty", "has no source hash while reporting no diagnostics")]
    [InlineData(DependencyHashKey, "empty", "has no dependency hash while reporting no diagnostics")]
    [InlineData(SdkVersionKey, "empty", "has no SDK version while reporting no diagnostics")]
    [InlineData(DependencyHashKey, "nil", StageUndecodable)]
    [InlineData(SdkVersionKey, "nil", StageUndecodable)]
    public async Task Stage_rejects_a_missing_hash_without_diagnostics(int key, string shape, string problem)
    {
        await AssertStageRejectedAsync(
            Staged((key, shape == "nil" ? Nil : IpcSerializer.Serialize(""))), problem, expectIdentity: shape != "nil");
    }

    [Fact]
    public async Task Stage_rejects_a_missing_source_hash_without_diagnostics()
    {
        await AssertStageRejectedAsync(Staged((SourceHashKey, Nil)), StageUndecodable, expectIdentity: false);
    }

    /// <summary>A failed compile reports empty hashes; the decoder refuses nil ones (see the nil cases above).</summary>
    [Fact]
    public async Task Stage_with_diagnostics_accepts_missing_hashes_as_empty_strings()
    {
        var staged = await StageOnce(HostReply.Of(
            ValidStaged with { SourceHash = "", DependencyHash = "", SdkVersion = "", Diagnostics = ["boom"] }));

        Assert.Equal(["boom"], staged.Diagnostics);
        Assert.Equal(string.Empty, staged.SourceHash);
        Assert.Equal(string.Empty, staged.DependencyHash);
        Assert.Equal(string.Empty, staged.SdkVersion);
    }

    [Fact]
    public async Task Stage_with_diagnostics_and_hashes_keeps_both()
    {
        var staged = await StageOnce(HostReply.Of(ValidStaged with { Diagnostics = ["boom"] }));

        Assert.Equal(["boom"], staged.Diagnostics);
        Assert.Equal("s", staged.SourceHash);
        Assert.False(staged.IsUpdate);
    }

    [Fact]
    public async Task Stage_reports_an_update_once_a_live_version_exists()
    {
        var staged = await StageOnce(HostReply.Of(ValidStaged with { LiveVersion = "0.9.0" }));

        Assert.Equal("0.9.0", staged.LiveVersion);
        Assert.True(staged.IsUpdate);
    }

    [Fact]
    public async Task Stage_decodes_the_workflow_tool()
    {
        var staged = await StageOnce(HostReply.Of(
            ValidStaged with { Tool = new Sdk.ExtensionToolSpec("t", "d", "{\"type\":\"object\"}") }));

        Assert.Equal(new Sdk.ExtensionToolSpec("t", "d", "{\"type\":\"object\"}"), staged.Tool);
    }

    [Fact]
    public async Task Pending_with_two_usable_rows_returns_both()
    {
        await using var host = new FakeHost();
        host.Answer(
            Sdk.ExtensionProtocol.ExtensionsPending,
            new Sdk.StagedExtensionList([ValidStaged, ValidStaged with { PluginId = "second", LiveVersion = "0.9.0" }]));
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        var pending = await client.GetPendingExtensionsAsync(limit.Token);

        Assert.Equal(2, pending.Count);
        Assert.Equal("demo", pending[0].PluginId);
        Assert.False(pending[0].IsUpdate);
        Assert.Equal("second", pending[1].PluginId);
        Assert.True(pending[1].IsUpdate);
        Assert.Equal("Plugin.cs", Assert.Single(pending[0].Files).Path);
        Assert.Equal(HostStatus.Running, client.Status);
    }

    public static TheoryData<string, string, string> BrokenSecondRows => new()
    {
        { "file without a path", "items[1]", "without a path at files[0]" },
        { "null row", "items[1]", "instead of a staged extension object" },
        { "blank plugin id", "items[1]", "has no plugin id" },
    };

    [Theory]
    [MemberData(nameof(BrokenSecondRows))]
    public async Task Pending_with_a_broken_second_row_rejects_without_a_partial_list(
        string shape, string label, string problem)
    {
        var second = shape switch
        {
            "file without a path" => IpcSerializer.Serialize(SecondRow("second", [new Sdk.StagedFile(" ", "x")])),
            "null row" => Nil,
            _ => IpcSerializer.Serialize(SecondRow("  ", [])),
        };
        var broken = HostReply.Raw(new RawPayload(ArrayOf(ArrayOf(IpcSerializer.Serialize(ValidStaged), second))));
        await using var host = new FakeHost();
        var calls = 0;
        host.On(Sdk.ExtensionProtocol.ExtensionsPending, (_, _) =>
        {
            var call = Interlocked.Increment(ref calls);
            return Task.FromResult(call == 1 ? broken : HostReply.Of(new Sdk.StagedExtensionList([ValidStaged])));
        });
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => client.GetPendingExtensionsAsync(limit.Token));

        Assert.Contains(Sdk.ExtensionProtocol.ExtensionsPending, error.Message);
        Assert.Contains(label, error.Message);
        Assert.Contains(problem, error.Message);
        Assert.Equal(HostStatus.Running, client.Status);
        var diagnostic = Assert.Single(
            client.Diagnostics,
            entry => entry.Contains(Sdk.ExtensionProtocol.ExtensionsPending, StringComparison.Ordinal));
        Assert.Contains("unusable", diagnostic);
        Assert.Contains(label, diagnostic);

        var pending = await client.GetPendingExtensionsAsync(limit.Token);
        Assert.Equal(["demo"], pending.Select(row => row.PluginId));
        Assert.Equal(2, calls);
        Assert.Equal(HostStatus.Running, client.Status);
    }

    [Fact]
    public async Task Pending_with_an_empty_list_returns_empty()
    {
        await using var host = new FakeHost();
        host.Answer(Sdk.ExtensionProtocol.ExtensionsPending, new Sdk.StagedExtensionList([]));
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        var pending = await client.GetPendingExtensionsAsync(limit.Token);

        Assert.Empty(pending);
        Assert.Equal(HostStatus.Running, client.Status);
    }

    /// <summary>Answers without a usable item list: a nil <c>items</c> member, an empty array (no <c>items</c>
    /// member), a map where <c>items</c> belongs, a string, nil, and no payload at all.</summary>
    public static TheoryData<string> AnswersWithoutAList =>
        ["nil items", "empty array", "map items", "string", "nil", "no payload"];

    [Theory]
    [MemberData(nameof(AnswersWithoutAList))]
    public async Task Pending_without_a_list_is_rejected(string shape)
    {
        var first = shape switch
        {
            "nil items" => new RawPayload(ArrayOf(Nil)),
            "empty array" => new RawPayload(ArrayOf()),
            "map items" => new RawPayload(ArrayOf([0x80])),
            "string" => HostWire.Payload("items"),
            "nil" => HostWire.Nil,
            _ => RawPayload.None,
        };
        await using var host = new FakeHost();
        var calls = 0;
        host.On(Sdk.ExtensionProtocol.ExtensionsPending, (_, _) =>
        {
            var call = Interlocked.Increment(ref calls);
            return Task.FromResult(call == 1 ? HostReply.Raw(first) : HostReply.Of(new Sdk.StagedExtensionList([])));
        });
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => client.GetPendingExtensionsAsync(limit.Token));

        Assert.Contains(Sdk.ExtensionProtocol.ExtensionsPending, error.Message);
        Assert.Contains("no pending extension list", error.Message);
        Assert.Equal(HostStatus.Running, client.Status);
        var diagnostic = Assert.Single(
            client.Diagnostics,
            entry => entry.Contains(Sdk.ExtensionProtocol.ExtensionsPending, StringComparison.Ordinal));
        Assert.Contains("unusable", diagnostic);

        Assert.Empty(await client.GetPendingExtensionsAsync(limit.Token));
        Assert.Equal(2, calls);
        Assert.Equal(HostStatus.Running, client.Status);
    }

    [Fact]
    public async Task Commit_decodes_status_and_tool_names()
    {
        await using var host = new FakeHost();
        host.Answer(Sdk.ExtensionProtocol.ExtensionsCommit, ValidCommit);
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        var result = await client.CommitExtensionAsync(
            new Sdk.ExtensionCommitRequest("demo", "s", "d", "sdk-1"), limit.Token);

        Assert.Equal("demo", result.Status.PluginId);
        Assert.Equal("1.0.0", result.Status.Version);
        Assert.Equal("approved", result.Status.Status);
        Assert.Equal("s", result.Status.SourceHash);
        Assert.Equal(["demo-tool"], result.ToolNames);
        Assert.Equal(HostStatus.Running, client.Status);
    }

    [Theory]
    [InlineData("blank version", "status ('demo') has no version")]
    [InlineData("null permission", "status ('demo') has an unusable permission at permissions[0]")]
    public async Task Commit_rejects_an_unusable_status_naming_it(string shape, string problem)
    {
        var status = shape == "blank version"
            ? ValidCommitStatus with { Version = " " }
            : ValidCommitStatus with { Permissions = [null!] };

        await AssertCommitRejectedAsync(HostReply.Of(ValidCommit with { Status = status }), problem, expectStatusWord: true);
    }

    /// <summary>A status that left out its version or its permission list (nil members) cannot be decoded at all.</summary>
    [Theory]
    [InlineData(StatusVersionKey)]
    [InlineData(StatusPermissionsKey)]
    public async Task Commit_rejects_a_status_with_a_nil_member_as_undecodable(int missingKey)
    {
        await AssertCommitRejectedAsync(
            Commit((StatusKey, Replace(ValidCommitStatus, (missingKey, Nil)))), CommitUndecodable, expectStatusWord: false);
    }

    /// <summary>A string or an array where the status object belongs. The decoder rejects the whole answer, so the
    /// message names the commit result rather than its <c>status</c> member.</summary>
    [Theory]
    [InlineData(new byte[] { 0xa8, (byte)'a', (byte)'p', (byte)'p', (byte)'r', (byte)'o', (byte)'v', (byte)'e', (byte)'d' })]
    [InlineData(new byte[] { 0x91, 0x01 })]
    public async Task Commit_rejects_a_status_of_the_wrong_type(byte[] element)
    {
        await AssertCommitRejectedAsync(
            Commit((StatusKey, element)), CommitUndecodable, expectStatusWord: false);
    }

    /// <summary>A nil status cannot be decoded, so the message names the commit result, not its status.</summary>
    [Fact]
    public async Task Commit_rejects_a_missing_status_as_undecodable()
    {
        await AssertCommitRejectedAsync(Commit((StatusKey, Nil)), CommitUndecodable, expectStatusWord: false);
    }

    [Fact]
    public async Task Commit_rejects_an_answer_without_a_payload()
    {
        await AssertCommitRejectedAsync(
            HostReply.Raw(RawPayload.None), "The Extension Host returned no commit result", expectStatusWord: false);
    }

    /// <summary>An "invalid" row carries empty approval metadata; the decoder refuses nil members.</summary>
    [Fact]
    public async Task Commit_accepts_an_invalid_row_without_approval_metadata()
    {
        await using var host = new FakeHost();
        host.Answer(
            Sdk.ExtensionProtocol.ExtensionsCommit,
            new Sdk.ExtensionCommitResult(new Sdk.PluginStatusInfo("demo", "", "invalid", "", "", "", [], "boom"), []));
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        var result = await client.CommitExtensionAsync(
            new Sdk.ExtensionCommitRequest("demo", "s", "d", "sdk-1"), limit.Token);

        Assert.Equal("invalid", result.Status.Status);
        Assert.Equal(string.Empty, result.Status.Version);
        Assert.Equal(string.Empty, result.Status.SourceHash);
        Assert.Equal(string.Empty, result.Status.DependencyHash);
        Assert.Equal(string.Empty, result.Status.SdkVersion);
        Assert.Equal("boom", result.Status.Error);
        Assert.Empty(result.ToolNames);
        Assert.Equal(HostStatus.Running, client.Status);
    }

    [Theory]
    [InlineData("nil", CommitUndecodable)]
    [InlineData("blank", "unusable tool name at toolNames[0]")]
    [InlineData("null entry", "unusable tool name at toolNames[0]")]
    public async Task Commit_rejects_unusable_tool_names(string shape, string problem)
    {
        var toolNames = shape switch
        {
            "nil" => Nil,
            "blank" => ArrayOf(IpcSerializer.Serialize(" ")),
            _ => ArrayOf(Nil),
        };

        await AssertCommitRejectedAsync(Commit((ToolNamesKey, toolNames)), problem, expectStatusWord: false);
    }

    [Fact]
    public async Task Commit_rejects_tool_names_of_the_wrong_type()
    {
        await AssertCommitRejectedAsync(
            Commit((ToolNamesKey, [0x91, 0x01])), CommitUndecodable, expectStatusWord: false);
    }

    /// <summary><see cref="ValidStaged"/> with raw members spliced in.</summary>
    private static HostReply Staged(params (int Index, byte[] Element)[] edits) =>
        HostReply.Raw(new RawPayload(Replace(ValidStaged, edits)));

    /// <summary><see cref="ValidCommit"/> with raw members spliced in.</summary>
    private static HostReply Commit(params (int Index, byte[] Element)[] edits) =>
        HostReply.Raw(new RawPayload(Replace(ValidCommit, edits)));

    private static byte[] Permissions(params Sdk.Permission[] permissions) =>
        IpcSerializer.Serialize<IReadOnlyList<Sdk.Permission>>(permissions);

    private static Sdk.StagedExtension SecondRow(string pluginId, IReadOnlyList<Sdk.StagedFile> files) =>
        ValidStaged with
        {
            PluginId = pluginId,
            Version = "2.0.0",
            Files = files,
            Permissions = [],
            Dependencies = [],
            Diagnostics = ["x"],
        };

    private static Task AssertStageRejectedAsync(Sdk.StagedExtension answer, string problem, bool expectIdentity = true) =>
        AssertStageRejectedAsync(HostReply.Of(answer), problem, expectIdentity);

    private static async Task AssertStageRejectedAsync(HostReply answer, string problem, bool expectIdentity = true)
    {
        await using var host = new FakeHost();
        var calls = 0;
        host.On(Operation, (_, _) => Task.FromResult(
            Interlocked.Increment(ref calls) == 1 ? answer : HostReply.Of(ValidStaged)));
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => client.StageExtensionAsync(ExtensionAuthoringPayloadTests.ValidStageRequest(), limit.Token));

        Assert.Contains(Operation, error.Message);
        Assert.Contains(problem, error.Message);
        if (expectIdentity)
        {
            Assert.Contains("('demo')", error.Message);
        }

        Assert.Equal(HostStatus.Running, client.Status);
        var diagnostic = Assert.Single(
            client.Diagnostics, entry => entry.Contains(Operation, StringComparison.Ordinal));
        Assert.Contains("unusable", diagnostic);

        var staged = await client.StageExtensionAsync(
            ExtensionAuthoringPayloadTests.ValidStageRequest(), limit.Token);
        Assert.Equal("demo", staged.PluginId);
        Assert.Equal(2, calls);
        Assert.Equal(HostStatus.Running, client.Status);
        Assert.Single(client.Diagnostics);
    }

    private static async Task<Sdk.StagedExtension> StageOnce(HostReply answer)
    {
        await using var host = new FakeHost();
        host.On(Operation, (_, _) => Task.FromResult(answer));
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        var staged = await client.StageExtensionAsync(
            ExtensionAuthoringPayloadTests.ValidStageRequest(), limit.Token);

        Assert.Equal(HostStatus.Running, client.Status);
        return staged;
    }

    private static async Task AssertCommitRejectedAsync(HostReply answer, string problem, bool expectStatusWord)
    {
        const string operation = Sdk.ExtensionProtocol.ExtensionsCommit;
        await using var host = new FakeHost();
        var calls = 0;
        host.On(operation, (_, _) => Task.FromResult(
            Interlocked.Increment(ref calls) == 1 ? answer : HostReply.Of(ValidCommit)));
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => client.CommitExtensionAsync(ExtensionAuthoringPayloadTests.ValidCommitRequest(), limit.Token));

        Assert.Contains(operation, error.Message);
        Assert.Contains(problem, error.Message);
        if (expectStatusWord)
        {
            Assert.Contains("status", error.Message);
        }

        Assert.Equal(HostStatus.Running, client.Status);
        var diagnostic = Assert.Single(
            client.Diagnostics, entry => entry.Contains(operation, StringComparison.Ordinal));
        Assert.Contains("unusable", diagnostic);

        var result = await client.CommitExtensionAsync(
            ExtensionAuthoringPayloadTests.ValidCommitRequest(), limit.Token);
        Assert.Equal(["demo-tool"], result.ToolNames);
        Assert.Equal(2, calls);
        Assert.Equal(HostStatus.Running, client.Status);
        Assert.Single(client.Diagnostics);
    }

    /// <summary>Serializes <paramref name="value"/> (an array-keyed record) and replaces the members at the given
    /// indices with raw elements, each one complete MessagePack value (nil, or a value of another type) — payloads
    /// the typed serializer refuses to write.</summary>
    internal static byte[] Replace<T>(T value, params (int Index, byte[] Element)[] edits)
    {
        var bytes = IpcSerializer.Serialize(value);
        using var reader = new MessagePackReader((ReadOnlyMemory<byte>)bytes);
        var count = reader.ReadArrayHeader();
        var payload = new List<byte>(bytes[..(int)reader.BytesConsumed]);
        for (var position = 0; position < count; position++)
        {
            var start = (int)reader.BytesConsumed;
            reader.Skip();
            var edit = Array.FindIndex(edits, candidate => candidate.Index == position);
            payload.AddRange(edit >= 0 ? edits[edit].Element : bytes[start..(int)reader.BytesConsumed]);
        }

        return payload.ToArray();
    }

    /// <summary>A MessagePack array (fewer than 16 elements) of raw elements.</summary>
    internal static byte[] ArrayOf(params byte[][] elements)
    {
        Assert.True(elements.Length < 16);
        var payload = new List<byte> { (byte)(0x90 | elements.Length) };
        foreach (var element in elements)
        {
            payload.AddRange(element);
        }

        return payload.ToArray();
    }
}
