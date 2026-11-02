namespace Forge.Editor.Tests.Support;

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), "forge-editor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string PathOf(string name) => Path.Combine(Root, name);

    public string WriteBytes(string name, byte[] bytes)
    {
        var path = PathOf(name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public string WriteText(string name, string text) => WriteBytes(name, System.Text.Encoding.UTF8.GetBytes(text));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
