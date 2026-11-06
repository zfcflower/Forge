using Forge.Abstractions;

namespace Forge.Core.Tests.Tools;

/// <summary>Argument JSON builders for the extension tools; every builder emits model-shaped call arguments.</summary>
internal static class ExtensionArgs
{
    public static readonly ToolContext Context = new("session", Path.GetTempPath());

    public static string PluginSubmit(
        string id = "demo",
        string version = "1.0.0",
        string fileName = "Demo.cs",
        string fileText = "public sealed class Demo {}",
        string? extraJson = null)
    {
        var files = $$"""{"{{fileName}}":{{Json(fileText)}}}""";
        return Submit(id, version, "plugin", files, extraJson);
    }

    public static string WorkflowSubmit(
        string id = "wf",
        string version = "1.0.0",
        string? extraJson = null)
    {
        const string tool = """{"name":"wf_tool","description":"Runs the workflow.","input_schema":{"type":"object"}}""";
        const string files = """{"workflow.json":"{\"version\":1,\"steps\":[]}"}""";
        return $$"""{"id":{{Json(id)}},"version":{{Json(version)}},"kind":"workflow","files":{{files}},"tool":{{tool}}{{extraJson ?? string.Empty}}}""";
    }

    public static string IdAndVersion(string id = "demo", string version = "1.0.0") =>
        $$"""{"id":{{Json(id)}},"version":{{Json(version)}}}""";

    public static ToolCall Call(ITool tool, string argumentsJson) =>
        new("call-1", tool.Definition.Name, argumentsJson);

    private static string Submit(string id, string version, string kind, string files, string? extraJson) =>
        $$"""{"id":{{Json(id)}},"version":{{Json(version)}},"kind":{{Json(kind)}},"files":{{files}}{{extraJson ?? string.Empty}}}""";

    private static string Json(string value) =>
        System.Text.Json.JsonSerializer.Serialize(value);
}
