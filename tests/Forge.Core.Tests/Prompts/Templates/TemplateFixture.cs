using Forge.Abstractions;
using Forge.Core.Prompts.Templates;

namespace Forge.Core.Tests.Prompts.Templates;

/// <summary>A user directory ("global") and a workspace ("project") in a temp folder, plus a store over them.</summary>
internal sealed class TemplateFixture : IDisposable
{
    private readonly TempDirectory root = new();

    public TemplateFixture(PromptTemplateRegistry? registry = null)
    {
        User = Path.Combine(root.Path, "user");
        Workspace = Path.Combine(root.Path, "ws");
        Directory.CreateDirectory(User);
        Directory.CreateDirectory(Workspace);
        Store = new PromptTemplateStore(registry ?? PromptTemplateRegistry.Core, User);
    }

    public string User { get; }

    public string Workspace { get; }

    public PromptTemplateStore Store { get; }

    public string Global(PromptTemplateKind kind, string id, string text) =>
        Put(Path.Combine(User, PromptTemplateStore.SubDirectory(kind)), id + ".md", text);

    public string Project(PromptTemplateKind kind, string id, string text) =>
        Put(Path.Combine(Workspace, ".forge", PromptTemplateStore.SubDirectory(kind)), id + ".md", text);

    public string GlobalBytes(PromptTemplateKind kind, string id, byte[] bytes)
    {
        var directory = Path.Combine(User, PromptTemplateStore.SubDirectory(kind));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, id + ".md");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public PromptTemplateSet Resolve() => Store.Resolve(Workspace);

    public IEnumerable<PromptTemplateEntry> Entries(PromptTemplateKind kind, string id) =>
        Store.List(Workspace).Where(entry => entry.Ref.Kind == kind && entry.Ref.Id == id);

    public void Dispose() => root.Dispose();

    private static string Put(string directory, string fileName, string text)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, text);
        return path;
    }
}
