using System.Buffers.Binary;
using System.Text.Json;
using Forge.Scripting;
using Xunit;

namespace Forge.Integration.Tests;

/// <summary>A plugin package that carries a native binary under a non-<c>.dll</c> name is rejected when the worker
/// loads it, whatever the format: verified bytes are only ever loaded as managed images from memory, so nothing a
/// native library header could be resolved through is accepted.</summary>
public sealed class NativeBinaryTests
{
    private const string NativeName = "native-payload.bin";

    [Theory]
    [InlineData("elf")]
    [InlineData("pe")]
    public async Task A_declared_native_file_with_a_non_dll_name_stops_the_load(string format)
    {
        await using var fx = new E2eFixture();
        var plugin = Compile(PluginSources.Standard("delta"), "delta");
        var native = format == "elf" ? ElfImage() : WithoutCliHeader(plugin);
        Assert.NotNull(ManagedImage.ReadNativeBinaryFormat(native));
        var output = WritePrebuiltPlugin(fx, "delta", plugin, new Dictionary<string, byte[]> { [NativeName] = native });

        await fx.StartAsync();
        await fx.ApproveAsync("delta");
        Assert.Empty(fx.ToolsOf("delta"));

        await fx.Client.RefreshPluginsAsync(fx.Ct);
        Assert.Empty(fx.ToolsOf("delta"));

        File.Delete(Path.Combine(output, NativeName));
        await fx.Client.RefreshPluginsAsync(fx.Ct);
        await fx.ApproveAsync("delta");
        await fx.WaitUntilAsync(
            () => fx.ToolsOf("delta").Count == PluginSources.AllTools.Length,
            "the delta tools once the native file is gone");
        var echo = await fx.InvokeAsync("delta", "echo", """{"text":"ok"}""");
        Assert.False(echo.IsError, echo.Content);
        Assert.Equal("echo:ok", echo.Content);
    }

    /// <summary>Writes a prebuilt (source-free) plugin package: <c>bin/&lt;version&gt;/plugin.dll</c> plus its declared
    /// files, so the host hashes exactly those files and hands them to the worker.</summary>
    private static string WritePrebuiltPlugin(
        E2eFixture fx,
        string id,
        byte[] pluginImage,
        IReadOnlyDictionary<string, byte[]> files,
        string version = "1.0.0")
    {
        var pluginDirectory = fx.PluginDirectory(id);
        var output = Path.Combine(pluginDirectory, "bin", version);
        Directory.CreateDirectory(output);
        File.WriteAllBytes(Path.Combine(output, "plugin.dll"), pluginImage);
        foreach (var (name, bytes) in files)
        {
            File.WriteAllBytes(Path.Combine(output, name), bytes);
        }

        File.WriteAllText(
            Path.Combine(pluginDirectory, "manifest.json"),
            JsonSerializer.Serialize(new { id, version, entryAssembly = "plugin.dll" }));
        return output;
    }

    private static byte[] Compile(string source, string assemblyName)
    {
        var result = new ScriptCompiler().CompileDirect(new CompilationRequest
        {
            Source = source,
            AssemblyName = assemblyName,
        });
        Assert.True(result.Success, result.ErrorText);
        return result.AssemblyBytes!;
    }

    /// <summary>A minimal ELF64 header: the signature and the class/encoding bytes a real Linux shared object starts
    /// with, padded past the header so it is a plausible file rather than four bytes.</summary>
    private static byte[] ElfImage() =>
        [0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 0, .. new byte[248]];

    /// <summary>A copy of a managed PE image with the CLI header directory cleared, so it parses as native code.</summary>
    private static byte[] WithoutCliHeader(byte[] image)
    {
        var bytes = (byte[])image.Clone();
        var peOffset = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0x3C));
        var optionalHeader = peOffset + 24;
        var magic = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(optionalHeader));
        var directories = optionalHeader + (magic == 0x20B ? 112 : 96);
        const int CliHeaderDirectory = 14;
        bytes.AsSpan(directories + (CliHeaderDirectory * 8), 8).Clear();
        return bytes;
    }
}
