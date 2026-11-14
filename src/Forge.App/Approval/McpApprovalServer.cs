namespace Forge.App.Approval;

/// <summary>
/// What an approval card may show about the MCP server the tool call belongs to. <see cref="EnvKeys"/> and
/// <see cref="HeaderKeys"/> carry the <em>names</em> only: the values are exactly what a user must not leak into a screenshot,
/// and they are not needed to judge whether the command line should run.
/// </summary>
/// <param name="Transport"><c>"stdio"</c> or <c>"http"</c>, untranslated; the card maps it to its own words.</param>
internal sealed record McpApprovalServer(
    string ServerName,
    string Transport,
    string? Command,
    IReadOnlyList<string> Args,
    string? WorkingDirectory,
    IReadOnlyList<string> EnvKeys,
    IReadOnlyList<string> HeaderKeys,
    string? Url);
