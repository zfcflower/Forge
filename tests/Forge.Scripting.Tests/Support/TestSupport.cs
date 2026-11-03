using Forge.Plugin.Sdk;
using Xunit;

namespace Forge.Scripting.Tests.Support;

internal static class Build
{
    public static CompilationRequest Request(
        string source,
        string assemblyName = "DemoPlugin",
        params string[] references) =>
        new() { Source = source, AssemblyName = assemblyName, References = references };

    public static CompilationResult Direct(
        string source,
        string assemblyName = "DemoPlugin",
        ScriptCompilerOptions? options = null) =>
        new ScriptCompiler(options).CompileDirect(Request(source, assemblyName));

    public static byte[] Image(string source, string assemblyName = "DemoPlugin", params string[] references)
    {
        var result = new ScriptCompiler().CompileDirect(Request(source, assemblyName, references));
        Assert.True(result.Success, result.ErrorText);
        return result.AssemblyBytes!;
    }
}

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), "forge-scripting-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public void Dispose()
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (attempt < 20 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }
    }
}

internal sealed class RecordingRegistration : IRegistrationContext
{
    public List<IPluginTool> Tools { get; } = [];

    public void AddTool(IPluginTool tool) => Tools.Add(tool);

    public void AddCommand(CommandDeclaration command) => throw new NotSupportedException();

    public void AddWorkflow(WorkflowDeclaration workflow) => throw new NotSupportedException();

    public void AddPanel(PanelDeclaration panel) => throw new NotSupportedException();

    public void RequirePermission(Permission permission) => throw new NotSupportedException();
}

internal static class Position
{
    public static (int Line, int Column) Of(string source, string token)
    {
        var index = source.IndexOf(token, StringComparison.Ordinal);
        Assert.True(index >= 0, $"token '{token}' not found");
        var before = source[..index];
        var line = before.Count(c => c == '\n') + 1;
        var column = index - (before.LastIndexOf('\n') + 1) + 1;
        return (line, column);
    }
}
