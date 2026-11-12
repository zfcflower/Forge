using Forge.Abstractions;
using Forge.Ipc;
using Xunit;

namespace Forge.HostClient.Tests.Terminal;

/// <summary>terminal.start 的线形状、本地校验与 Host 错误码透传（任务条目 1–3）。</summary>
public sealed class TerminalStartProtocolTests
{
    [Fact]
    public async Task Start_sends_required_fields_with_values_and_camel_case_names()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();
        TerminalFixtures.AnswerStart(host, "t-shape");
        var directory = BoundRoots.AbsoluteDir("term-shape");

        var session = await client.StartAsync(
            TerminalFixtures.Request(sessionId: "sess-1", workingDirectory: directory, columns: 120, rows: 40),
            limit.Token);
        await session.DisposeAsync();

        var sent = Assert.Single(host.Requests(TerminalOps.Start)).Arguments!.Value;
        Assert.Equal("sess-1", sent.GetProperty("sessionId").GetString());
        Assert.Equal(directory, sent.GetProperty("workingDirectory").GetString());
        Assert.Equal(120, sent.GetProperty("columns").GetInt32());
        Assert.Equal(40, sent.GetProperty("rows").GetInt32());
        // camelCase 钉住：顺序与名字都按线协议，改名即 breaking。
        Assert.Equal(
            ["sessionId", "workingDirectory", "columns", "rows"],
            sent.EnumerateObject().Select(property => property.Name).ToArray());
    }

    [Fact]
    public async Task Start_with_shell_arguments_environment_sends_all_optional_fields()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();
        TerminalFixtures.AnswerStart(host, "t-full");

        var session = await client.StartAsync(
            TerminalFixtures.Request(
                shell: "/bin/bash",
                args: ["-l", "-c"],
                environment: new Dictionary<string, string> { ["FOO"] = "bar", ["BAZ"] = "qux" }),
            limit.Token);
        await session.DisposeAsync();

        var sent = Assert.Single(host.Requests(TerminalOps.Start)).Arguments!.Value;
        Assert.Equal("/bin/bash", sent.GetProperty("shell").GetString());
        Assert.Equal(["-l", "-c"], sent.GetProperty("arguments").EnumerateArray().Select(e => e.GetString()!).ToArray());
        var environment = sent.GetProperty("environment");
        Assert.Equal("bar", environment.GetProperty("FOO").GetString());
        Assert.Equal("qux", environment.GetProperty("BAZ").GetString());
        Assert.Equal(
            ["sessionId", "workingDirectory", "columns", "rows", "shell", "arguments", "environment"],
            sent.EnumerateObject().Select(property => property.Name).ToArray());
    }

    [Fact]
    public async Task Start_omits_shell_arguments_environment_when_the_caller_leaves_them_null()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();
        TerminalFixtures.AnswerStart(host, "t-minimal");

        var session = await client.StartAsync(TerminalFixtures.Request(), limit.Token);
        await session.DisposeAsync();

        var sent = Assert.Single(host.Requests(TerminalOps.Start)).Arguments!.Value;
        Assert.False(sent.TryGetProperty("shell", out _));
        Assert.False(sent.TryGetProperty("arguments", out _));
        Assert.False(sent.TryGetProperty("environment", out _));
    }

    [Fact]
    public async Task Start_result_id_becomes_the_session_id()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();
        TerminalFixtures.AnswerStart(host, "t-abc-123");

        var session = await client.StartAsync(TerminalFixtures.Request(), limit.Token);

        Assert.Equal("t-abc-123", session.Id);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Start_result_without_a_terminal_id_is_rejected_locally()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();
        host.Answer(TerminalOps.Start, """{"processId":1,"shell":"sh"}""");

        var failure = await Assert.ThrowsAsync<InvalidDataException>(
            () => client.StartAsync(TerminalFixtures.Request(), limit.Token).AsTask());

        Assert.Contains("terminal id", failure.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1001)]
    public async Task Start_rejects_out_of_range_columns_without_IPC(int columns)
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.StartAsync(TerminalFixtures.Request(columns: columns), limit.Token).AsTask());

        Assert.Empty(host.Requests(TerminalOps.Start));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(501)]
    public async Task Start_rejects_out_of_range_rows_without_IPC(int rows)
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.StartAsync(TerminalFixtures.Request(rows: rows), limit.Token).AsTask());

        Assert.Empty(host.Requests(TerminalOps.Start));
    }

    [Fact]
    public async Task Start_rejects_a_relative_working_directory_without_IPC()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.StartAsync(TerminalFixtures.Request(workingDirectory: "relative/path"), limit.Token).AsTask());

        Assert.Empty(host.Requests(TerminalOps.Start));
    }

    [Fact]
    public async Task Start_rejects_a_blank_session_id_without_IPC()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.StartAsync(TerminalFixtures.Request(sessionId: "  "), limit.Token).AsTask());

        Assert.Empty(host.Requests(TerminalOps.Start));
    }

    [Fact]
    public async Task Start_while_stopped_throws_and_sends_nothing()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        using var limit = TestLimit.Create();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.StartAsync(TerminalFixtures.Request(), limit.Token).AsTask());

        Assert.Empty(host.Requests(TerminalOps.Start));
    }

    [Fact]
    public async Task Start_while_failed_throws_and_sends_nothing_new()
    {
        await using var host = new FakeHost();
        await using var client = await host.StartTrackedAsync(out var statuses);
        await host.DropConnectionAsync();
        await statuses.UntilAsync(events => events.Any(e => e.Status == HostStatus.Failed));
        using var limit = TestLimit.Create();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.StartAsync(TerminalFixtures.Request(), limit.Token).AsTask());

        Assert.Empty(host.Requests(TerminalOps.Start));
    }

    [Theory]
    [InlineData("invalid_request")]
    [InlineData("terminal_limit")]
    [InlineData("terminal_start_failed")]
    public async Task Start_propagates_host_error_codes_to_the_caller(string code)
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();
        host.On(TerminalOps.Start, (_, _) => throw new FakeHostError(code, "refused by host"));

        var failure = await Assert.ThrowsAsync<IpcRemoteException>(
            () => client.StartAsync(TerminalFixtures.Request(), limit.Token).AsTask());

        Assert.Equal(code, failure.Code);
        Assert.Equal(TerminalOps.Start, failure.Operation);
    }
}
