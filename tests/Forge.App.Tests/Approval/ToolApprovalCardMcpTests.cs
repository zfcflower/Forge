using Forge.Abstractions;
using Forge.App.Approval;
using Forge.App.Localization;
using Xunit;

namespace Forge.App.Tests.Approval;

/// <summary>
/// The MCP half of the tool approval card: the server block (name, transport text, key names only), the command line
/// formatting, and the revoke of the remembered rules that name this card's tool.
/// </summary>
public sealed class ToolApprovalCardMcpTests : IDisposable
{
    private const string Tool = "srv__edit";

    private readonly List<ApprovalCardViewModel> owned = [];

    public void Dispose()
    {
        foreach (var card in owned)
        {
            card.Dispose();
        }
    }

    private ToolApprovalCardViewModel Card(McpApprovalServer? mcp = null, ApprovalRules? rules = null, string tool = Tool)
    {
        var card = new ToolApprovalCardViewModel(
            new ToolApprovalCardRequest("s1", "Session s1", "p1", tool, ToolSideEffect.Execute, "/tmp", "{}", mcp),
            InlineDispatcher.Instance,
            rules);
        owned.Add(card);
        return card;
    }

    private static McpApprovalServer Server(
        string transport = "stdio",
        string? command = "node",
        IReadOnlyList<string>? args = null,
        string? workingDirectory = "/w",
        string? url = null,
        IReadOnlyList<string>? envKeys = null,
        IReadOnlyList<string>? headerKeys = null) =>
        new("srv", transport, command, args ?? [], workingDirectory, envKeys ?? [], headerKeys ?? [], url);

    [Fact]
    public void A_card_with_a_stdio_server_reports_the_server_and_the_stdio_transport_text()
    {
        var card = Card(Server());

        Assert.True(card.HasMcpServer);
        Assert.Equal("srv", card.McpServerName);
        Assert.Equal(Loc.Instance[ExtensionsStrings.ToolMcpTransportStdio], card.McpTransportText);
    }

    [Fact]
    public void An_http_server_uses_the_http_transport_text()
    {
        var card = Card(Server(transport: "http", command: null, workingDirectory: null, url: "https://mcp.example.com"));

        Assert.Equal(Loc.Instance[ExtensionsStrings.ToolMcpTransportHttp], card.McpTransportText);
    }

    [Fact]
    public void The_env_and_header_texts_are_exactly_the_key_names()
    {
        var card = Card(Server(envKeys: ["SECRET", "TOKEN"], headerKeys: ["X-Key", "Authorization"]));

        Assert.True(card.HasMcpEnvKeys);
        Assert.Equal("SECRET, TOKEN", card.McpEnvKeysText);
        Assert.True(card.HasMcpHeaderKeys);
        Assert.Equal("X-Key, Authorization", card.McpHeaderKeysText);
        Assert.True(card.McpHidesValues);
    }

    [Fact]
    public void Command_and_working_directory_presence_follow_the_server()
    {
        var with = Card(Server(command: "node", args: ["serve.js"], workingDirectory: "/w"));
        Assert.True(with.HasMcpCommand);
        Assert.True(with.HasMcpWorkingDirectory);
        Assert.Equal("/w", with.McpWorkingDirectory);

        var without = Card(Server(command: null, workingDirectory: ""));
        Assert.False(without.HasMcpCommand);
        Assert.False(without.HasMcpWorkingDirectory);
        Assert.Equal(string.Empty, without.McpWorkingDirectory);

        var argsOnly = Card(Server(command: null, args: ["serve.js"], workingDirectory: ""));
        Assert.True(argsOnly.HasMcpCommand);
    }

    [Fact]
    public void The_url_branch_shows_only_for_a_server_with_a_url()
    {
        var card = Card(Server(transport: "http", command: null, workingDirectory: null, url: "https://mcp.example.com/sse"));

        Assert.True(card.HasMcpUrl);
        Assert.Equal("https://mcp.example.com/sse", card.McpUrl);

        var without = Card(Server());
        Assert.False(without.HasMcpUrl);
        Assert.Equal(string.Empty, without.McpUrl);
    }

    [Fact]
    public void FormatMcpCommandLine_quotes_arguments_with_whitespace_or_nothing()
    {
        var server = Server(command: "cmd", args: ["a", "b c", "", "d\te"]);

        Assert.Equal("cmd a \"b c\" \"\" \"d\te\"", ToolApprovalCardViewModel.FormatMcpCommandLine(server));
    }

    [Fact]
    public void FormatMcpCommandLine_joins_arguments_when_there_is_no_command()
    {
        var server = Server(command: null, args: ["x", "y z"]);

        Assert.Equal("x \"y z\"", ToolApprovalCardViewModel.FormatMcpCommandLine(server));
    }

    [Fact]
    public void FormatMcpCommandLine_caps_pathological_lines()
    {
        var server = Server(command: new string('c', 4100));

        var text = ToolApprovalCardViewModel.FormatMcpCommandLine(server);

        Assert.Equal(4001, text.Length);
        Assert.StartsWith(new string('c', 4000), text);
        Assert.EndsWith("…", text);
    }

    [Fact]
    public async Task The_card_shows_the_formatted_command_line_once_formatting_finishes()
    {
        var card = Card(Server(command: "node", args: ["serve.js", "two words"]));

        await ApprovalFixtures.WaitUntilAsync(() => card.McpCommandLine.Length > 0, "the MCP command line to be formatted");

        Assert.Equal("node serve.js \"two words\"", card.McpCommandLine);
    }

    [Fact]
    public async Task Remembered_rules_are_counted_for_this_tool_across_scopes_and_revoked_together()
    {
        var dir = ApprovalFixtures.TempDir("forge-mcp-card-rules-");
        try
        {
            await using var rules = new ApprovalRules(Path.Combine(dir, ApprovalRules.FileName), debounceMilliseconds: 0);
            Assert.True(rules.Allow(ApprovalRuleKey.ForSession(Tool, ToolSideEffect.Execute, "s1")));
            Assert.True(rules.Allow(ApprovalRuleKey.Global(Tool, ToolSideEffect.Execute)));
            Assert.True(rules.Allow(ApprovalRuleKey.Global("other_tool", ToolSideEffect.Execute)));
            Assert.Equal(3, rules.Rules.Count);

            var card = Card(rules: rules);
            Assert.True(card.CanRevokeRemembered);
            Assert.NotEmpty(card.RememberedRulesText);
            Assert.True(card.McpRevokeCommand.CanExecute(null));

            card.McpRevokeCommand.Execute(null);

            Assert.False(card.CanRevokeRemembered);
            Assert.Equal(string.Empty, card.RememberedRulesText);
            Assert.False(card.McpRevokeCommand.CanExecute(null));
            Assert.False(card.IsDecided);
            Assert.Single(rules.Rules); // the other tool's rule survives
            Assert.Equal("other_tool", Assert.Single(rules.Rules).Key.ToolName);

            await rules.SaveAsync(CancellationToken.None);
            var text = File.ReadAllText(Path.Combine(dir, ApprovalRules.FileName));
            Assert.Contains("other_tool", text);
            Assert.DoesNotContain(Tool, text);
        }
        finally
        {
            ApprovalFixtures.DeleteTree(dir);
        }
    }

    [Fact]
    public void A_null_rule_store_never_allows_a_revoke()
    {
        var card = Card(rules: null);

        Assert.False(card.CanRevokeRemembered);
        Assert.Equal(string.Empty, card.RememberedRulesText);
        Assert.False(card.McpRevokeCommand.CanExecute(null));
    }

    [Fact]
    public void Without_an_mcp_server_the_block_is_hidden_and_every_mcp_property_is_empty()
    {
        var card = Card(mcp: null);

        Assert.False(card.HasMcpServer);
        Assert.Equal(string.Empty, card.McpServerName);
        Assert.Equal(string.Empty, card.McpTransportText);
        Assert.False(card.HasMcpCommand);
        Assert.Equal(string.Empty, card.McpCommandLine);
        Assert.False(card.HasMcpWorkingDirectory);
        Assert.Equal(string.Empty, card.McpWorkingDirectory);
        Assert.False(card.HasMcpUrl);
        Assert.Equal(string.Empty, card.McpUrl);
        Assert.False(card.HasMcpEnvKeys);
        Assert.Equal(string.Empty, card.McpEnvKeysText);
        Assert.False(card.HasMcpHeaderKeys);
        Assert.Equal(string.Empty, card.McpHeaderKeysText);
        Assert.True(card.McpHidesValues);
        Assert.False(card.CanRevokeRemembered);
    }
}
