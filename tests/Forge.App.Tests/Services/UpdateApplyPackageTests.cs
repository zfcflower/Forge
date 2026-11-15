using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Forge.App.Services;
using Xunit;

namespace Forge.App.Tests.Services;

/// <summary>
/// The package formats a staged update can arrive in: a Windows <c>.zip</c>, a Unix <c>.tar.gz</c> whose execute bits must
/// survive the unpack (a <c>Forge.App</c> without them could not be started after the swap), and the archive safety bounds
/// that are checked even though the bytes were hash-verified — a manifest is a remote document.
/// </summary>
public sealed class UpdateApplyPackageTests
{
    [Fact]
    public void A_zip_package_is_unpacked_including_its_executable()
    {
        using var tree = new TempTree("forge-package-");
        var package = Path.Combine(tree.Root, "Forge-1.2.0-win-x64.zip");
        using (var stream = File.Create(package))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            Write(archive, "Forge.App.exe", "binary");
            Write(archive, "runtimes/lib.dll", "library");
        }

        var destination = Path.Combine(tree.Root, "payload");
        UpdatePackages.Extract(package, destination);

        Assert.Equal("binary", File.ReadAllText(Path.Combine(destination, "Forge.App.exe")));
        Assert.Equal("library", File.ReadAllText(Path.Combine(destination, "runtimes", "lib.dll")));
    }

    [Fact]
    public void A_zip_entry_that_escapes_the_destination_is_refused()
    {
        using var tree = new TempTree("forge-package-");
        var package = Path.Combine(tree.Root, "hostile.zip");
        using (var stream = File.Create(package))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            Write(archive, "../escaped.txt", "should never land here");
        }

        Assert.Throws<InvalidDataException>(() => UpdatePackages.Extract(package, Path.Combine(tree.Root, "payload")));
        Assert.False(File.Exists(Path.Combine(tree.Root, "escaped.txt")));
    }

    [Fact]
    public void A_tar_gz_package_is_unpacked_and_keeps_the_executable_bit()
    {
        using var tree = new TempTree("forge-package-");
        var package = Path.Combine(tree.Root, "Forge-1.2.0-linux-x64.tar.gz");
        var executable = OperatingSystem.IsWindows() ? "Forge.App.exe" : "Forge.App";
        using (var stream = File.Create(package))
        using (var gzip = new GZipStream(stream, CompressionLevel.Fastest))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            Write(writer, executable, "binary", ExecutableMode);
            Write(writer, "./lib/runtime.so", "library", UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var destination = Path.Combine(tree.Root, "payload");
        UpdatePackages.Extract(package, destination);

        var extracted = Path.Combine(destination, executable);
        Assert.Equal("binary", File.ReadAllText(extracted));
        Assert.Equal("library", File.ReadAllText(Path.Combine(destination, "lib", "runtime.so")));

        if (!OperatingSystem.IsWindows())
        {
            Assert.True(
                File.GetUnixFileMode(extracted).HasFlag(UnixFileMode.UserExecute),
                "the unpacked executable lost its execute bit");
        }
    }

    [Fact]
    public void A_package_with_another_name_is_refused()
    {
        using var tree = new TempTree("forge-package-");
        var package = Path.Combine(tree.Root, "Forge-1.2.0.msi");
        File.WriteAllText(package, "not an archive");

        var error = Assert.Throws<InvalidDataException>(() => UpdatePackages.Extract(package, Path.Combine(tree.Root, "payload")));
        Assert.Contains("neither", error.Message, StringComparison.Ordinal);
    }

    private static UnixFileMode ExecutableMode =>
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private static void Write(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }

    private static void Write(TarWriter writer, string name, string content, UnixFileMode mode)
    {
        var entry = new PaxTarEntry(TarEntryType.RegularFile, name) { Mode = mode };
        entry.DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        writer.WriteEntry(entry);
    }

    /// <summary>A throw-away directory tree with a best-effort teardown.</summary>
    private sealed class TempTree : IDisposable
    {
        public TempTree(string prefix) => Root = TestTempFiles.NewDirectory(prefix);

        public string Root { get; }

        public void Dispose() => TestTempFiles.DeleteTree(Root);
    }
}
