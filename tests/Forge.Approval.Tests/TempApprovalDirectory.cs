namespace Forge.Approval.Tests;

internal sealed class TempApprovalDirectory : IDisposable
{
    public TempApprovalDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "forge-approval-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
